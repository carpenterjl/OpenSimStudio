using System.ComponentModel;
using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Diffraction;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Diffractive;
using static OpenSim.Optics.Analysis.RayAnalysisSupport;

namespace OpenSim.Optics.Analysis;

// OP-47: diffraction efficiency (docs/Optics-Conventions.md §61). OpticStudio's three sequential
// Diffraction Efficiency analyses (help pages "Diffraction Efficiency Analyses (sequential ui mode)",
// "Diffraction Efficiency (sequential ui mode)", "Efficiency vs. Angle (sequential ui mode)",
// "Efficiency vs. Wavelength (sequential ui mode)" and "Calculation Assumptions and Limitations",
// 2025 R2, saved under plans/sources/optics/op-47-help/) are for volume holograms by Kogelnik's
// method; the Grating Efficiency analysis is this program's own front end to the scalar and RCWA
// solvers of OpenSim.Optics/Diffraction.

/// <summary>How the Diffraction Efficiency map is drawn ("Show As").</summary>
public enum EfficiencyMapStyle
{
    Image,
    Contour,
}

/// <summary>Settings of the volume-hologram efficiency analyses; each analysis uses the ones its
/// help page lists.</summary>
public sealed record VolumeHologramEfficiencySettings
{
    [DisplayName("Sampling")]
    [Description("Samples along each scanned axis (\"33, 65, etc.\"); at least 2.")]
    public int Sampling { get; init; } = 33;

    [DisplayName("Surface")]
    [Description("The volume hologram surface; 0 for the first surface set up as a volume hologram.")]
    public int Surface { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Field")]
    [Description("Row of the field table, 1 for the first; its X angle is kept and its Y angle scanned.")]
    public int Field { get; init; } = 1;

    [DisplayName("Show as")]
    [Description("Diffraction Efficiency map only: image or contour.")]
    public EfficiencyMapStyle ShowAs { get; init; } = EfficiencyMapStyle.Image;

    [DisplayName("Minimum angle (°)")]
    [Description("Smallest field Y angle; with the maximum also 0, 99% of the field's Y.")]
    public double MinimumAngle { get; init; }

    [DisplayName("Maximum angle (°)")]
    [Description("Largest field Y angle; with the minimum also 0, 101% of the field's Y.")]
    public double MaximumAngle { get; init; }

    [DisplayName("Minimum wavelength (µm)")]
    [Description("Smallest wavelength; with the maximum also 0, 99% of the selected wavelength.")]
    public double MinimumWavelength { get; init; }

    [DisplayName("Maximum wavelength (µm)")]
    [Description("Largest wavelength; with the minimum also 0, 101% of the selected wavelength.")]
    public double MaximumWavelength { get; init; }
}

/// <summary>One sample of a volume-hologram scan: field Y angle, wavelength, and the efficiency
/// (NaN when the chief ray did not reach the surface).</summary>
public readonly record struct VolumeHologramSample(double FieldAngle, double Micrometers, double Diffracted, double Undiffracted,
    double TE, double TM, double IncidenceDegrees, string? Failure);

/// <summary>
/// The engine of the three volume-hologram analyses: for each field Y angle and wavelength, the
/// chief ray is traced from the object to the hologram and Kogelnik's efficiency is evaluated at
/// its intersection (<see cref="VolumeHologramEfficiencies"/>), with the system polarization's
/// Jones vector on the surface's local x and y (unpolarized: TE and TM averaged).
/// <para>
/// The chief ray: for an object at infinity it is the ray of the field's direction
/// (tan θx, tan θy, 1)/|…| that crosses the stop surface at its vertex, found by
/// <see cref="DampedNewton2D"/> on its crossing of the plane through surface 1's vertex — so lenses
/// whose paraxial model is refused (an off-axis hologram's phase has a slope at the vertex) can be
/// analysed; for a finite object the <see cref="RayLauncher"/> launches it, which needs the paraxial
/// model.
/// </para>
/// </summary>
public static class VolumeHologramScan
{
    /// <summary>The first volume hologram surface, or the given one checked.</summary>
    public static int FindSurface(string analysis, SequentialLens lens, int setting)
    {
        if (setting < 0 || setting >= lens.ImageIndex)
            throw new ArgumentException($"{analysis}: the surface must be 0 (the first volume hologram) or 1 to {lens.ImageIndex - 1} (it is {setting}).");
        if (setting == 0)
        {
            for (int j = 1; j < lens.ImageIndex; j++)
                if (lens.Surfaces[j].Shape is TwoPointHologram { Volume: not null }) return j;
            throw new ArgumentException($"{analysis}: no surface is a volume hologram. Set a Hologram 1, Hologram 2 or Toroidal Hologram surface's \"Volume hologram?\" parameter to 1 and give its volume data.");
        }
        if (lens.Surfaces[setting].Shape is not TwoPointHologram { Volume: not null })
            throw new ArgumentException($"{analysis}: surface {setting} is not a volume hologram (these analyses take a Hologram 1, Hologram 2 or Toroidal Hologram with \"Volume hologram?\" = 1).");
        return setting;
    }

    /// <summary>Evaluates one sample.</summary>
    public static VolumeHologramSample Sample(SequentialLens lens, OpticalSystemData system, int surface, double fieldX, double fieldY,
        double micrometers, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var one = system with
        {
            Fields = system.Fields with { Type = FieldType.Angle, Points = new[] { new FieldPoint { X = fieldX, Y = fieldY } } },
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = micrometers } }, PrimaryIndex = 0 },
        };
        var trace = SequentialTrace.Create(lens, one, 0);
        Ray launch;
        if (lens.ObjectAtInfinity)
        {
            if (!TryAimChief(lens, trace, fieldX, fieldY, out launch, out string? why))
                return Failed(fieldY, micrometers, why!);
        }
        else
        {
            var launcher = new RayLauncher(lens, one);
            var (hx, hy) = one.Fields.Normalized(0);
            launch = launcher.Launch(trace, hx, hy, 0, 0);
        }
        var path = trace.TracePath(launch, new TraceOptions { IgnoreApertures = true });
        var hit = path.Hits.FirstOrDefault(h => h.Surface == surface);
        if (hit is null || hit.Status != RayStatus.Ok)
            return Failed(fieldY, micrometers, $"the chief ray stopped before surface {surface} ({RayStatuses.Label(path.Status).ToLowerInvariant()} at surface {path.FailedAtSurface})");
        var hologram = (TwoPointHologram)lens.Surfaces[surface].Shape!;
        (Complex, Complex)? jones = system.Polarization.Unpolarized ? null
            : (Complex.FromPolarCoordinates(system.Polarization.Jx, system.Polarization.XPhaseDegrees * Math.PI / 180),
               Complex.FromPolarCoordinates(system.Polarization.Jy, system.Polarization.YPhaseDegrees * Math.PI / 180));
        var e = VolumeHologramEfficiencies.At(hologram, hit.Position, hit.IncomingDirection, hit.Normal, hit.IndexBefore,
            OpticsConventions.MicrometersToMeters(trace.AirMicrometers), jones, surface);
        return new VolumeHologramSample(fieldY, micrometers, e.Diffracted, e.Undiffracted, e.TE.Diffracted, e.TM.Diffracted, e.IncidenceDegrees, null);
    }

    private static VolumeHologramSample Failed(double angle, double micrometers, string why) =>
        new(angle, micrometers, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, why);

    /// <summary>The ray of the field direction through the stop's vertex (object at infinity).</summary>
    private static bool TryAimChief(SequentialLens lens, SequentialTrace trace, double fieldX, double fieldY, out Ray ray, out string? why)
    {
        var direction = new Vector3D(Math.Tan(fieldX * Math.PI / 180), Math.Tan(fieldY * Math.PI / 180), 1);
        direction /= direction.Length;
        int stop = lens.StopIndex;
        var options = new TraceOptions { IgnoreApertures = true };
        Ray Make(double a1, double a2)
        {
            var p = new Vector3D(a1, a2, 0);
            return trace.NewRay(p - direction * Vector3D.Dot(p, direction), direction);
        }
        bool Residual(double a1, double a2, out double fx, out double fy)
        {
            fx = fy = double.NaN;
            var path = trace.TracePath(Make(a1, a2), options);
            var at = path.Hits.FirstOrDefault(h => h.Surface == stop);
            if (at is null) return false;
            fx = at.Position.X;
            fy = at.Position.Y;
            return double.IsFinite(fx) && double.IsFinite(fy);
        }
        var solved = DampedNewton2D.Solve(Residual, 0, 0, 1e-7, 1e-13, 1e-9);
        if (!solved.Converged)
        {
            ray = default;
            why = $"the chief ray could not be aimed at the stop's vertex ({solved.Failure})";
            return false;
        }
        ray = Make(solved.X, solved.Y);
        why = null;
        return true;
    }

    /// <summary>The scanned range: the given one, or 99% to 101% of <paramref name="nominal"/> when both are 0.</summary>
    public static (double Min, double Max) Range(string analysis, string what, double minimum, double maximum, double nominal)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            throw new ArgumentException($"{analysis}: the {what} range must be finite.");
        if (minimum == 0 && maximum == 0)
        {
            minimum = 0.99 * nominal;
            maximum = 1.01 * nominal;
        }
        if (!(maximum > minimum))
            throw new ArgumentException($"{analysis}: the {what} range is empty ({minimum:g6} to {maximum:g6}); give a minimum below the maximum{(nominal == 0 ? $" (the default, 99% to 101% of the selection, is empty because it is 0)" : "")}.");
        return (minimum, maximum);
    }

    internal static double At(double min, double max, int i, int n) => i == n - 1 ? max : min + (max - min) * i / (n - 1);

    internal static string[] Assumptions(OpticalSystemData system, int surface, TwoPointHologram hologram) => new[]
    {
        $"Kogelnik's coupled-wave theory for thick holograms (Bell Syst. Tech. J. 48, 2909, 1969), the help's only method; surface {surface} ({hologram.TypeName}), order {hologram.Order:g6}, thickness {hologram.Volume!.Thickness * hologram.Volume.ThicknessScale:g6} m after shrinkage, emulsion index {hologram.Volume.MeanIndex + hologram.Volume.IndexShift:g6} on playback, dn {hologram.Volume.Modulation:g6}.",
        "Fringes from the two construction beams refracted into the emulsion from indices n1 and n2; diffracted wave σ = ρ + m·K; Fresnel losses at the emulsion's faces are not included (docs/Optics-Conventions.md §61; §10 item 57).",
        system.Polarization.Unpolarized
            ? "Unpolarized: the mean of the TE and TM efficiencies."
            : $"Polarization: the system Jones vector ({system.Polarization.Jx:g4}, {system.Polarization.Jy:g4}) taken on the surface's local x and y and split into TE (along ρ × σ) and TM; the changes of polarization at the surfaces before the hologram are not traced.",
        "The chief ray of each field angle: through the stop's vertex (object at infinity), or the ray launcher's (finite object). The field X angle is kept and the Y angle scanned (help). Apertures are not applied to it.",
        "Diffracted: η = |c_S|/c_R·|S|² (eq. 40); undiffracted: the power left in the incident wave.",
    };
}

/// <summary>Shared checks of the three volume-hologram analyses.</summary>
public abstract class VolumeHologramAnalysisBase : OpticsAnalysis<VolumeHologramEfficiencySettings>
{
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    protected VolumeHologramAnalysisBase(Func<OpticsAnalysisContext, SequentialLens?>? lensSource) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Category => "Polarization";

    protected sealed override OpticsAnalysisResult Compute(OpticsAnalysisContext context, VolumeHologramEfficiencySettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        if (system.Fields.Type != FieldType.Angle)
            throw new ArgumentException($"{Name}: the field type must be Angle (help: \"The Field Type must be Angle in the System Explorer\").");
        CheckDensity(Name, settings.Sampling, 2, "sampling");
        int surface = VolumeHologramScan.FindSurface(Name, lens, settings.Surface);
        int count = system.Fields.Points.Count;
        if (settings.Field < 1 || settings.Field > count)
            throw new ArgumentException($"{Name}: the field must be 1 to {count} (it is {settings.Field}).");
        var field = system.Fields.Points[settings.Field - 1];
        int wave = OneWavelength(Name, settings.Wavelength, system);
        double micrometers = system.Wavelengths.Wavelengths[wave].Micrometers;
        var result = Scan(lens, system, surface, field, micrometers, settings, cancellation);
        var assumptions = new List<string>(VolumeHologramScan.Assumptions(system, surface, (TwoPointHologram)lens.Surfaces[surface].Shape!));
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, result.Sections, assumptions, result.Plots);
    }

    protected abstract (IReadOnlyList<ReportSection> Sections, IReadOnlyList<AnalysisPlot> Plots) Scan(SequentialLens lens,
        OpticalSystemData system, int surface, FieldPoint field, double micrometers, VolumeHologramEfficiencySettings settings, CancellationToken cancellation);

    protected static ReportSection Table(IEnumerable<VolumeHologramSample> samples)
    {
        var rows = new List<IReadOnlyList<string>>();
        var lines = new List<string>();
        foreach (var s in samples)
        {
            string a = StudyReport.Number(s.FieldAngle, "g8"), w = StudyReport.Number(s.Micrometers, "g8");
            if (s.Failure is not null)
            {
                rows.Add(new[] { a, w, "—", "—", "—", "—", "—" });
                lines.Add($"Field Y {a}°, {w} µm: {s.Failure}.");
                continue;
            }
            rows.Add(new[] { a, w, StudyReport.Number(s.IncidenceDegrees, "g6"), StudyReport.Number(s.Diffracted, "g8"),
                StudyReport.Number(s.Undiffracted, "g8"), StudyReport.Number(s.TE, "g8"), StudyReport.Number(s.TM, "g8") });
        }
        return new ReportSection("Efficiency")
        {
            TableHeaders = new[] { "Field Y (°)", "Wavelength (µm)", "Incidence in emulsion (°)", "Diffracted", "Undiffracted", "TE diffracted", "TM diffracted" },
            TableRows = rows,
            Lines = lines,
        };
    }
}

/// <summary>OpticStudio's Diffraction Efficiency (sequential): the efficiency of a volume hologram
/// against field Y angle and wavelength, as a map.</summary>
public sealed class DiffractionEfficiencyAnalysis : VolumeHologramAnalysisBase
{
    public const string SavedId = "DiffractionEfficiency";

    public DiffractionEfficiencyAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Diffraction efficiency";

    protected override (IReadOnlyList<ReportSection>, IReadOnlyList<AnalysisPlot>) Scan(SequentialLens lens, OpticalSystemData system,
        int surface, FieldPoint field, double micrometers, VolumeHologramEfficiencySettings settings, CancellationToken cancellation)
    {
        var (a0, a1) = VolumeHologramScan.Range(Name, "angle", settings.MinimumAngle, settings.MaximumAngle, field.Y);
        var (w0, w1) = VolumeHologramScan.Range(Name, "wavelength", settings.MinimumWavelength, settings.MaximumWavelength, micrometers);
        int n = settings.Sampling;
        var values = new double[n, n];
        var samples = new List<VolumeHologramSample>();
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var s = VolumeHologramScan.Sample(lens, system, surface, field.X, VolumeHologramScan.At(a0, a1, i, n), VolumeHologramScan.At(w0, w1, j, n), cancellation);
                values[i, j] = s.Diffracted;
                samples.Add(s);
            }
        var plot = new AnalysisMapPlot("Diffraction efficiency", "Wavelength (µm)", "Field Y angle (°)", new GridField(values, w0, w1, a0, a1), "efficiency")
        {
            Style = settings.ShowAs == EfficiencyMapStyle.Contour ? AnalysisMapStyle.Contour : AnalysisMapStyle.Image,
            Minimum = 0,
            Maximum = 1,
        };
        return (new[] { Table(samples) }, new AnalysisPlot[] { plot });
    }
}

/// <summary>OpticStudio's Efficiency vs. Angle (sequential).</summary>
public sealed class EfficiencyVsAngleAnalysis : VolumeHologramAnalysisBase
{
    public const string SavedId = "EfficiencyVsAngle";

    public EfficiencyVsAngleAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Efficiency vs. angle";

    protected override (IReadOnlyList<ReportSection>, IReadOnlyList<AnalysisPlot>) Scan(SequentialLens lens, OpticalSystemData system,
        int surface, FieldPoint field, double micrometers, VolumeHologramEfficiencySettings settings, CancellationToken cancellation)
    {
        var (a0, a1) = VolumeHologramScan.Range(Name, "angle", settings.MinimumAngle, settings.MaximumAngle, field.Y);
        int n = settings.Sampling;
        var samples = Enumerable.Range(0, n)
            .Select(i => VolumeHologramScan.Sample(lens, system, surface, field.X, VolumeHologramScan.At(a0, a1, i, n), micrometers, cancellation))
            .ToArray();
        var ok = samples.Where(s => s.Failure is null).ToArray();
        var plot = new LinePlot("Efficiency vs. angle", "Field Y angle (°)", "Efficiency", new[]
        {
            new AnalysisCurve("Diffracted", ok.Select(s => s.FieldAngle).ToArray(), ok.Select(s => s.Diffracted).ToArray()),
            new AnalysisCurve("Undiffracted", ok.Select(s => s.FieldAngle).ToArray(), ok.Select(s => s.Undiffracted).ToArray(), Dashed: true),
        });
        return (new[] { Table(samples) }, new AnalysisPlot[] { plot });
    }
}

/// <summary>OpticStudio's Efficiency vs. Wavelength (sequential).</summary>
public sealed class EfficiencyVsWavelengthAnalysis : VolumeHologramAnalysisBase
{
    public const string SavedId = "EfficiencyVsWavelength";

    public EfficiencyVsWavelengthAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Efficiency vs. wavelength";

    protected override (IReadOnlyList<ReportSection>, IReadOnlyList<AnalysisPlot>) Scan(SequentialLens lens, OpticalSystemData system,
        int surface, FieldPoint field, double micrometers, VolumeHologramEfficiencySettings settings, CancellationToken cancellation)
    {
        var (w0, w1) = VolumeHologramScan.Range(Name, "wavelength", settings.MinimumWavelength, settings.MaximumWavelength, micrometers);
        int n = settings.Sampling;
        var samples = Enumerable.Range(0, n)
            .Select(i => VolumeHologramScan.Sample(lens, system, surface, field.X, field.Y, VolumeHologramScan.At(w0, w1, i, n), cancellation))
            .ToArray();
        var ok = samples.Where(s => s.Failure is null).ToArray();
        var plot = new LinePlot("Efficiency vs. wavelength", "Wavelength (µm)", "Efficiency", new[]
        {
            new AnalysisCurve("Diffracted", ok.Select(s => s.Micrometers).ToArray(), ok.Select(s => s.Diffracted).ToArray()),
            new AnalysisCurve("Undiffracted", ok.Select(s => s.Micrometers).ToArray(), ok.Select(s => s.Undiffracted).ToArray(), Dashed: true),
        });
        return (new[] { Table(samples) }, new AnalysisPlot[] { plot });
    }
}

// ---------------- Grating efficiency (scalar and RCWA) ----------------

/// <summary>The solver of the <see cref="GratingEfficiencyAnalysis"/>.</summary>
public enum GratingEfficiencyMethod
{
    Rcwa,
    Scalar,
}

/// <summary>The relief profile of the <see cref="GratingEfficiencyAnalysis"/>.</summary>
public enum GratingProfileKind
{
    Lamellar,
    Blazed,
    Multilevel,
    Sinusoidal,
}

/// <summary>The incident polarization of the <see cref="GratingEfficiencyAnalysis"/>.</summary>
public enum GratingPolarizationSetting
{
    TE,
    TM,
    Unpolarized,
}

/// <summary>What the <see cref="GratingEfficiencyAnalysis"/> scans.</summary>
public enum GratingSweep
{
    None,
    Angle,
    Wavelength,
}

/// <summary>Settings of the <see cref="GratingEfficiencyAnalysis"/>.</summary>
public sealed record GratingEfficiencySettings
{
    [DisplayName("Method")]
    [Description("RCWA (rigorous) or Scalar (thin element, transmitted orders only).")]
    public GratingEfficiencyMethod Method { get; init; } = GratingEfficiencyMethod.Rcwa;

    [DisplayName("Profile")]
    [Description("Lamellar (binary), Blazed (sawtooth, staircase in RCWA), Multilevel (N-level staircase) or Sinusoidal.")]
    public GratingProfileKind Profile { get; init; } = GratingProfileKind.Lamellar;

    [DisplayName("Surface")]
    [Description("A Diffraction Grating or Toroidal Grating surface whose lines/µm give the period; 0 to use the period below.")]
    public int Surface { get; init; }

    [DisplayName("Period (µm)")]
    [Description("Grating period when Surface is 0.")]
    public double PeriodMicrometers { get; init; } = 1;

    [DisplayName("Depth (µm)")]
    [Description("Relief depth (for Multilevel, the continuous depth H; the highest step is (N − 1)H/N).")]
    public double DepthMicrometers { get; init; } = 0.5;

    [DisplayName("Fill factor")]
    [Description("Lamellar: fraction of the period that is ridge.")]
    public double FillFactor { get; init; } = 0.5;

    [DisplayName("Levels")]
    [Description("Multilevel: number of phase levels N.")]
    public int Levels { get; init; } = 8;

    [DisplayName("Slices")]
    [Description("Blazed and Sinusoidal in RCWA: staircase slices.")]
    public int Slices { get; init; } = 16;

    [DisplayName("Ridge index n")]
    public double RidgeIndex { get; init; } = 1.5;

    [DisplayName("Ridge extinction k")]
    public double RidgeExtinction { get; init; }

    [DisplayName("Groove index n")]
    public double GrooveIndex { get; init; } = 1;

    [DisplayName("Cover index")]
    [Description("The lossless medium light comes from.")]
    public double CoverIndex { get; init; } = 1;

    [DisplayName("Substrate index n")]
    public double SubstrateIndex { get; init; } = 1.5;

    [DisplayName("Substrate extinction k")]
    public double SubstrateExtinction { get; init; }

    [DisplayName("Angle (°)")]
    [Description("Angle of incidence in the cover, in the plane of the grating vector.")]
    public double AngleDegrees { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Polarization")]
    public GratingPolarizationSetting Polarization { get; init; } = GratingPolarizationSetting.TE;

    [DisplayName("Harmonics")]
    [Description("RCWA: orders −N … N retained.")]
    public int Harmonics { get; init; } = 15;

    [DisplayName("Sweep")]
    public GratingSweep Sweep { get; init; } = GratingSweep.None;

    [DisplayName("Sweep minimum")]
    [Description("Degrees or micrometers.")]
    public double SweepMinimum { get; init; }

    [DisplayName("Sweep maximum")]
    public double SweepMaximum { get; init; }

    [DisplayName("Sweep samples")]
    public int SweepSamples { get; init; } = 41;

    [DisplayName("Order")]
    [Description("The order plotted against the sweep.")]
    public int Order { get; init; } = 1;
}

/// <summary>The efficiencies of one evaluation: per order, reflected and transmitted.</summary>
public sealed record GratingEfficiencyPoint(IReadOnlyList<GratingOrderEfficiency> Orders, double TotalReflected, double TotalTransmitted,
    double Absorbed, bool TransmissionDefined);

/// <summary>
/// The efficiency of a one-dimensional grating per order, by RCWA (<see cref="Rcwa1D"/>) or the
/// thin-element theory (<see cref="ScalarGratingEfficiency"/>), optionally against angle or
/// wavelength. This program's own analysis (OpticStudio's RCWA is a non-sequential diffraction
/// DLL); the period can come from a Diffraction Grating or Toroidal Grating surface.
/// </summary>
public sealed class GratingEfficiencyAnalysis : OpticsAnalysis<GratingEfficiencySettings>
{
    public const string SavedId = "GratingEfficiency";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public GratingEfficiencyAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Grating efficiency (RCWA / scalar)";
    public override string Category => "Polarization";

    /// <summary>One evaluation at an angle (degrees) and wavelength (meters) for the settings.</summary>
    public static GratingEfficiencyPoint Evaluate(GratingEfficiencySettings s, double periodMeters, double angleDegrees, double wavelength,
        CancellationToken cancellation = default)
    {
        const string name = "Grating efficiency";
        double depth = s.DepthMicrometers * 1e-6;
        if (!(depth > 0) || !double.IsFinite(depth)) throw new ArgumentException($"{name}: the depth is {s.DepthMicrometers} µm; it must be positive.");
        var ridge = new Complex(s.RidgeIndex, s.RidgeExtinction);
        var groove = new Complex(s.GrooveIndex, 0);
        if (s.Method == GratingEfficiencyMethod.Scalar)
        {
            if (s.RidgeExtinction != 0) throw new ArgumentException($"{name}: the scalar method takes lossless media; use RCWA for an absorbing ridge.");
            if (angleDegrees != 0) throw new ArgumentException($"{name}: the scalar method is for normal incidence (the angle is {angleDegrees}°); use RCWA.");
            double alpha = ScalarGratingEfficiency.DepthInWaves(s.RidgeIndex, s.GrooveIndex, depth, wavelength);
            int reach = Math.Max(s.Harmonics, 1);
            var orders = new List<GratingOrderEfficiency>();
            double sum = 0;
            for (int m = -reach; m <= reach; m++)
            {
                double eta = s.Profile switch
                {
                    GratingProfileKind.Lamellar => ScalarGratingEfficiency.Binary(m, 2 * Math.PI * alpha, s.FillFactor),
                    GratingProfileKind.Blazed => ScalarGratingEfficiency.Blazed(m, alpha),
                    GratingProfileKind.Multilevel => ScalarGratingEfficiency.Multilevel(m, alpha, s.Levels),
                    _ => ScalarGratingEfficiency.Sinusoidal(m, Math.PI * alpha),
                };
                orders.Add(new GratingOrderEfficiency(m, 0, eta, false, true, double.NaN, double.NaN));
                sum += eta;
            }
            return new GratingEfficiencyPoint(orders, 0, sum, 1 - sum, true);
        }
        IReadOnlyList<GratingLayer> layers = s.Profile switch
        {
            GratingProfileKind.Lamellar => new[] { GratingLayer.Lamellar(depth, s.FillFactor, ridge, groove) },
            GratingProfileKind.Blazed => GratingProfiles.Blazed(depth, s.Slices, ridge, groove),
            GratingProfileKind.Multilevel => GratingProfiles.Multilevel(depth, s.Levels, ridge, groove),
            _ => GratingProfiles.Sinusoidal(depth, s.Slices, ridge, groove),
        };
        var structure = new GratingStructure(periodMeters, s.CoverIndex, layers, new Complex(s.SubstrateIndex, s.SubstrateExtinction));
        GratingEfficiencyResult Run(GratingPolarization p) => Rcwa1D.Solve(structure, new RcwaIncidence(wavelength, angleDegrees, p, s.Harmonics), cancellation);
        if (s.Polarization != GratingPolarizationSetting.Unpolarized)
        {
            var r = Run(s.Polarization == GratingPolarizationSetting.TE ? GratingPolarization.TE : GratingPolarization.TM);
            return new GratingEfficiencyPoint(r.Orders, r.TotalReflected, r.TotalTransmitted, r.Absorbed, r.TransmissionDefined);
        }
        var te = Run(GratingPolarization.TE);
        var tm = Run(GratingPolarization.TM);
        var mean = te.Orders.Zip(tm.Orders, (a, b) => a with { Reflected = 0.5 * (a.Reflected + b.Reflected), Transmitted = 0.5 * (a.Transmitted + b.Transmitted) }).ToArray();
        return new GratingEfficiencyPoint(mean, 0.5 * (te.TotalReflected + tm.TotalReflected), 0.5 * (te.TotalTransmitted + tm.TotalTransmitted),
            0.5 * (te.Absorbed + tm.Absorbed), te.TransmissionDefined);
    }

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, GratingEfficiencySettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        double period;
        string periodSource;
        if (settings.Surface != 0)
        {
            if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
            if (settings.Surface < 1 || settings.Surface >= lens.ImageIndex)
                throw new ArgumentException($"{Name}: the surface must be 0 or 1 to {lens.ImageIndex - 1} (it is {settings.Surface}).");
            double lines = lens.Surfaces[settings.Surface].Shape switch
            {
                DiffractionGrating g => g.LinesPerMicrometer,
                ToroidalGrating t => t.LinesPerMicrometer,
                _ => throw new ArgumentException($"{Name}: surface {settings.Surface} is not a Diffraction Grating or Toroidal Grating, so it has no period; set Surface to 0 and give the period."),
            };
            if (lines == 0) throw new ArgumentException($"{Name}: surface {settings.Surface} has 0 lines/µm, so no period.");
            period = 1e-6 / Math.Abs(lines);
            periodSource = $"surface {settings.Surface}'s {Math.Abs(lines):g6} lines/µm";
        }
        else
        {
            if (!(settings.PeriodMicrometers > 0)) throw new ArgumentException($"{Name}: the period is {settings.PeriodMicrometers} µm; it must be positive.");
            period = settings.PeriodMicrometers * 1e-6;
            periodSource = "the period setting";
        }
        int wave = OneWavelength(Name, settings.Wavelength, system);
        double micrometers = system.Wavelengths.Wavelengths[wave].Micrometers;
        double wavelength = micrometers * 1e-6;
        var point = Evaluate(settings, period, settings.AngleDegrees, wavelength, cancellation);
        string N(double v) => StudyReport.Number(v, "g8");
        var rows = point.Orders.Where(o => o.Reflected > 0 || o.Transmitted > 0 || o.ReflectedPropagates || o.TransmittedPropagates)
            .Select(o => (IReadOnlyList<string>)new[]
            {
                o.Order.ToString(System.Globalization.CultureInfo.InvariantCulture), N(o.Reflected), N(o.Transmitted),
                double.IsNaN(o.ReflectedAngleDegrees) ? "—" : StudyReport.Number(o.ReflectedAngleDegrees, "g6"),
                double.IsNaN(o.TransmittedAngleDegrees) ? "—" : StudyReport.Number(o.TransmittedAngleDegrees, "g6"),
            }).ToArray();
        var sections = new List<ReportSection>
        {
            new("Efficiency by order")
            {
                Pairs = new (string, string)[]
                {
                    ("Method", settings.Method == GratingEfficiencyMethod.Rcwa ? $"RCWA, orders −{settings.Harmonics} … {settings.Harmonics}" : "Scalar (thin element)"),
                    ("Period", $"{N(period * 1e6)} µm ({periodSource})"),
                    ("Wavelength", $"{N(micrometers)} µm"),
                    ("Angle", $"{N(settings.AngleDegrees)}°"),
                    ("Polarization", settings.Polarization.ToString()),
                    ("Total reflected", N(point.TotalReflected)),
                    ("Total transmitted", point.TransmissionDefined ? N(point.TotalTransmitted) : "not defined (absorbing substrate)"),
                    ("Absorbed (1 − sum)", N(point.Absorbed)),
                },
                TableHeaders = new[] { "Order", "Reflected", "Transmitted", "Reflected angle (°)", "Transmitted angle (°)" },
                TableRows = rows,
            },
        };
        var plots = new List<AnalysisPlot>
        {
            new LinePlot("Efficiency by order", "Order", "Efficiency", new[]
            {
                new AnalysisCurve("Reflected", point.Orders.Select(o => (double)o.Order).ToArray(), point.Orders.Select(o => o.Reflected).ToArray()),
                new AnalysisCurve("Transmitted", point.Orders.Select(o => (double)o.Order).ToArray(), point.Orders.Select(o => o.Transmitted).ToArray()),
            }),
        };
        if (settings.Sweep != GratingSweep.None)
        {
            CheckDensity(Name, settings.SweepSamples, 2, "number of sweep samples");
            if (!(settings.SweepMaximum > settings.SweepMinimum))
                throw new ArgumentException($"{Name}: the sweep runs from {settings.SweepMinimum} to {settings.SweepMaximum}; give a minimum below the maximum.");
            int n = settings.SweepSamples;
            var xs = new double[n];
            var rs = new double[n];
            var ts = new double[n];
            var sweepRows = new List<IReadOnlyList<string>>();
            for (int i = 0; i < n; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                double x = VolumeHologramScan.At(settings.SweepMinimum, settings.SweepMaximum, i, n);
                var p = settings.Sweep == GratingSweep.Angle
                    ? Evaluate(settings, period, x, wavelength, cancellation)
                    : Evaluate(settings, period, settings.AngleDegrees, x * 1e-6, cancellation);
                var o = p.Orders.FirstOrDefault(e => e.Order == settings.Order);
                xs[i] = x;
                rs[i] = o?.Reflected ?? 0;
                ts[i] = o?.Transmitted ?? 0;
                sweepRows.Add(new[] { N(x), N(rs[i]), N(ts[i]), N(p.TotalReflected + p.TotalTransmitted) });
            }
            string axis = settings.Sweep == GratingSweep.Angle ? "Angle (°)" : "Wavelength (µm)";
            plots.Add(new LinePlot($"Order {settings.Order} vs. {(settings.Sweep == GratingSweep.Angle ? "angle" : "wavelength")}", axis, "Efficiency", new[]
            {
                new AnalysisCurve("Reflected", xs, rs),
                new AnalysisCurve("Transmitted", xs, ts),
            }));
            sections.Add(new ReportSection($"Order {settings.Order} sweep")
            {
                TableHeaders = new[] { axis, "Reflected", "Transmitted", "Sum over orders" },
                TableRows = sweepRows,
            });
        }
        var assumptions = new List<string>
        {
            settings.Method == GratingEfficiencyMethod.Rcwa
                ? "RCWA in the planar mounting (Moharam et al. 1995, enhanced transmittance matrix; Li's factorisation for TM); the conical mounting is not built. Efficiencies are fractions of the incident power through the grating plane; absorption = 1 − sum (docs/Optics-Conventions.md §61)."
                : "Scalar thin-element theory: |Fourier coefficient|² of the transmittance e^{−iφ(x)}, φ = 2π(n_ridge − n_groove)h(x)/λ; no reflection, Fresnel loss or polarization; transmitted orders only.",
            "Order m leaves at n₂ sin θ₂ = n₁ sin θ₁ + mλ/Λ (OpticStudio's grating equation, M = m); a profile thicker toward +x favours positive orders.",
            "Indices are relative to the system's air; the wavelength is the table's (air) wavelength.",
        };
        if (settings.Profile is GratingProfileKind.Blazed or GratingProfileKind.Sinusoidal && settings.Method == GratingEfficiencyMethod.Rcwa)
            assumptions.Add($"The continuous profile is cut into {settings.Slices} lamellar slices of equal thickness.");
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}
