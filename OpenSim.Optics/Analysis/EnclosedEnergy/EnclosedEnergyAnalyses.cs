using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis.EnclosedEnergy;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// ---------------------------------------------------------------------------------------------
// OP-41: the enclosed-energy analyses. Each reads the project's saved lens (ProjectLens) unless a
// lens source is given and states its sampling, reference point and warnings in the result. The
// settings and their meaning come from the Ansys OpticStudio 2025 R2 help pages "Diffraction
// (enclosed energy)", "Geometric", "Geometric Line/Edge Spread" and "Extended Source", saved under
// plans/sources/optics/op-41-help/ (docs/Optics-Conventions.md §39).
// ---------------------------------------------------------------------------------------------

/// <summary>What the geometric line/edge spread draws (help: "line and edge, line only, or edge only").</summary>
public enum LineEdgeSpreadType
{
    LineAndEdge = 0,
    LineOnly = 1,
    EdgeOnly = 2,
}

/// <summary>The extended source's analysis types (help, "Extended Source": encircled, X-only,
/// Y-only, ensquared, and "X- or Y-distributions, which show the energy distribution in either x
/// or y directions").</summary>
public enum ExtendedSourceType
{
    Encircled = 0,
    XOnly = 1,
    YOnly = 2,
    Ensquared = 3,
    XDistribution = 4,
    YDistribution = 5,
}

/// <summary>Settings of the <see cref="DiffractionEnclosedEnergyAnalysis"/>.</summary>
public sealed record DiffractionEnclosedEnergySettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil nodes; 16 to 512.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Type"), Description("Encircled (radius), x-only or y-only (a slit infinite in the other direction), or ensquared (half-width).")]
    public EnclosedEnergyType Type { get; init; } = EnclosedEnergyType.Encircled;

    [DisplayName("Maximum distance (µm)"), Description("The largest radius or half-width plotted; 0 for the default. At most half the PSF's period.")]
    public double MaximumDistanceMicrometers { get; init; }

    [DisplayName("Refer to"), Description("The point the regions are centred on: chief ray, centroid or vertex.")]
    public EnclosedEnergyReference Reference { get; init; } = EnclosedEnergyReference.ChiefRay;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (polychromatic).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Show diffraction limit"), Description("Add the aberration-free curve at the reference field (the (0, 0) field, else field 1).")]
    public bool ShowDiffractionLimit { get; init; }

    [DisplayName("Check apertures"), Description("Rays stopped by a surface aperture carry no light.")]
    public bool CheckApertures { get; init; } = true;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>Settings of the <see cref="GeometricEnclosedEnergyAnalysis"/>.</summary>
public sealed record GeometricEnclosedEnergySettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil rays; 8 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Type"), Description("Encircled (radius), x-only or y-only (a slit infinite in the other direction), or ensquared (half-width).")]
    public EnclosedEnergyType Type { get; init; } = EnclosedEnergyType.Encircled;

    [DisplayName("Maximum distance (µm)"), Description("The largest radius or half-width plotted; 0 for the distance that holds every ray.")]
    public double MaximumDistanceMicrometers { get; init; }

    [DisplayName("Refer to"), Description("Chief ray, centroid, vertex, or middle (the centre of the smallest circle holding every ray).")]
    public EnclosedEnergyReference Reference { get; init; } = EnclosedEnergyReference.ChiefRay;

    [DisplayName("Surface"), Description("The surface the rays are read at; 0 for the image surface.")]
    public int Surface { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (weighted by the wavelength weights).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Multiply by diffraction limit"), Description("Encircled only, image surface only: multiply by the Airy disk's enclosed energy 1 − J0² − J1² at the reference field's working F/#.")]
    public bool MultiplyByDiffractionLimit { get; init; }
}

/// <summary>Settings of the <see cref="GeometricLineEdgeSpreadAnalysis"/>.</summary>
public sealed record GeometricLineEdgeSpreadSettings
{
    [DisplayName("Sampling"), Description("n: an n × n grid of pupil rays; 8 to 1024.")]
    public int Sampling { get; init; } = 64;

    [DisplayName("Maximum radius (µm)"), Description("Half-width of the plot about the chief ray; 0 for the farthest ray.")]
    public double MaximumRadiusMicrometers { get; init; }

    [DisplayName("Surface"), Description("The surface the rays are read at; 0 for the image surface.")]
    public int Surface { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (weighted by the wavelength weights).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("Field row, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Type"), Description("Line and edge, line only, or edge only.")]
    public LineEdgeSpreadType Type { get; init; } = LineEdgeSpreadType.LineAndEdge;
}

/// <summary>Settings of the <see cref="ExtendedSourceEnclosedEnergyAnalysis"/>.</summary>
public sealed record ExtendedSourceEnclosedEnergySettings
{
    [DisplayName("File"), Description("The full path of the .IMA source image (text digits 0–9, or binary bytes).")]
    public string File { get; init; } = "";

    [DisplayName("Field size"), Description("The image's full width in field units: degrees for angle fields, lens units for height fields.")]
    public double FieldSize { get; init; } = 0.1;

    [DisplayName("Rays x 1000"), Description("About 1000 times this many rays; every lit pixel traces at least one per wavelength. 1 to 1000.")]
    public int RaysThousands { get; init; } = 5;

    [DisplayName("Type"), Description("Encircled, x-only, y-only, ensquared, or the x or y distribution (energy per narrow strip, with the FWHM).")]
    public ExtendedSourceType Type { get; init; } = ExtendedSourceType.Encircled;

    [DisplayName("Refer to"), Description("Chief ray of the centre field, centroid, or vertex.")]
    public EnclosedEnergyReference Reference { get; init; } = EnclosedEnergyReference.ChiefRay;

    [DisplayName("Surface"), Description("The surface the rays are read at; 0 for the image surface.")]
    public int Surface { get; init; }

    [DisplayName("Multiply by diffraction limit"), Description("Encircled only, image surface only: multiply by the Airy disk's enclosed energy at the reference field's working F/#.")]
    public bool MultiplyByDiffractionLimit { get; init; }

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for all (rays shared by the wavelength weights).")]
    public int Wavelength { get; init; }

    [DisplayName("Field"), Description("The field row the image is centred on, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Maximum distance (µm)"), Description("The largest radius, half-width or distribution half-width plotted; 0 for the distance that holds every ray.")]
    public double MaximumDistanceMicrometers { get; init; }

    [DisplayName("Remove vignetting factors"), Description("Replace the fields' vignetting factors by surface apertures (needed when the fields have them).")]
    public bool RemoveVignettingFactors { get; init; }
}

/// <summary>What the enclosed-energy analyses share.</summary>
internal static class EnclosedEnergyRuns
{
    public const int Points = 100;

    public static string Um(double meters) => StudyReport.Number(meters * 1e6, "g6");

    /// <summary>The reference field of the diffraction limit (help, "Diffraction Limited": the
    /// field at (0, 0) when one is defined, else field 1), 0-based.</summary>
    public static int ReferenceField(OpticalSystemData system)
    {
        for (int i = 0; i < system.Fields.Points.Count; i++)
            if (system.Fields.Points[i].X == 0 && system.Fields.Points[i].Y == 0) return i;
        return 0;
    }

    /// <summary>λ (meters) and the real working F/# of the Airy curve: the selected wavelength
    /// (the primary when several) at the reference field.</summary>
    public static (double Lambda, double FNumber, string Note) AiryParameters(SequentialLens lens, OpticalSystemData system, IReadOnlyList<int> waves, string who)
    {
        int wave = waves.Count == 1 ? waves[0] : system.Wavelengths.PrimaryIndex;
        int field = ReferenceField(system);
        var trace = SequentialTrace.Create(lens, system, wave);
        var launcher = new RayLauncher(lens, system);
        var (hx, hy) = system.Fields.Normalized(field);
        var w = RealWorkingFNumber.Compute(trace, launcher, hx, hy);
        if (!w.Succeeded)
            throw new InvalidOperationException($"{who}: no diffraction limit: the working F/# at field {field + 1} could not be found ({w.Failure}). Turn off \"Multiply by diffraction limit\".");
        double lambda = trace.AirMicrometers * 1e-6;
        return (lambda, w.Value, $"Multiplied by the diffraction limit: the Airy disk's enclosed energy 1 − J₀(v)² − J₁(v)², v = π·r/(λ·F), λ = {trace.AirMicrometers:g6} µm ({(waves.Count == 1 ? "the selected wavelength" : "the primary wavelength")}), F = {w.Value:g6} the real working F/# at field {field + 1} (the reference field of \"Diffraction Limited\"; the help: the approximation \"ignores the change in F/# with field\").");
    }

    public static ReportSection? WarningSection(IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? null : new ReportSection("Sampling and validity warnings") { Lines = warnings };

    public static IReadOnlyList<ReportSection> Sections(IReadOnlyList<string> warnings, params ReportSection[] main) =>
        WarningSection(warnings) is { } w ? new[] { w }.Concat(main).ToArray() : main;

    /// <summary>A table of size against one column per curve.</summary>
    public static ReportSection Table(string heading, string sizeHeader, IReadOnlyList<double> sizes, IReadOnlyList<AnalysisCurve> curves, IReadOnlyList<(string, string)> pairs, IReadOnlyList<string> lines)
    {
        var headers = new List<string> { sizeHeader };
        headers.AddRange(curves.Select(c => c.Name));
        var rows = new List<IReadOnlyList<string>>();
        for (int i = 0; i < sizes.Count; i++)
        {
            var row = new List<string> { StudyReport.Number(sizes[i] * 1e6, "g6") };
            foreach (var c in curves) row.Add(i < c.Y.Count ? StudyReport.Number(c.Y[i], "g6") : "—");
            rows.Add(row);
        }
        return new ReportSection(heading) { TableHeaders = headers, TableRows = rows, Pairs = pairs, Lines = lines };
    }

    /// <summary>The smallest size at which the curve reaches <paramref name="fraction"/>
    /// (linear between samples), or NaN when it does not.</summary>
    public static double SizeAt(IReadOnlyList<double> sizes, IReadOnlyList<double> values, double fraction)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] < fraction) continue;
            if (i == 0) return sizes[0];
            double t = (fraction - values[i - 1]) / (values[i] - values[i - 1]);
            return sizes[i - 1] + t * (sizes[i] - sizes[i - 1]);
        }
        return double.NaN;
    }

    public static string FieldLabel(OpticalSystemData system, int f) => RayAnalysisSupport.FieldLabel(system, f);

    public static string WaveLabel(OpticalSystemData system, IReadOnlyList<int> waves) =>
        waves.Count > 1 ? "polychromatic" : RayAnalysisSupport.WaveLabel(system, waves[0]);

    /// <summary>The lines a geometric result states about its rays.</summary>
    public static List<string> RayLines(GeometricEnclosedEnergySpot spot, string label)
    {
        var lines = new List<string>();
        int passed = spot.Points.Count;
        lines.Add($"{label}: {passed} of {spot.Launched} rays reached surface {spot.Surface}{(spot.Failures.Count > 0 ? "; not counted: " + string.Join("; ", spot.Failures) : "")}.");
        lines.Add($"{label}: reference ({StudyReport.Number(spot.ReferenceX * 1e3, "g8")}, {StudyReport.Number(spot.ReferenceY * 1e3, "g8")}) mm, the {EnclosedEnergyRegions.ReferenceLabel(spot.Reference)}.");
        return lines;
    }
}

/// <summary>
/// OpticStudio's Diffraction Enclosed Energy (OP-41): the fraction of a point image's energy
/// within a circle, slit or square about the chief ray, the centroid or the vertex, from OP-38's
/// pupil function, exact for the sampled pupil (<see cref="DiffractionEnclosedEnergyKernel"/>).
/// Not built: the Huygens PSF option, polarization, intermediate surfaces.
/// </summary>
public sealed class DiffractionEnclosedEnergyAnalysis : WavefrontAnalysisBase<DiffractionEnclosedEnergySettings>
{
    public const string SavedId = "DiffractionEnclosedEnergy";

    public DiffractionEnclosedEnergyAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Diffraction enclosed energy";
    public override string Category => "Enclosed energy";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        DiffractionEnclosedEnergySettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 16 && settings.Sampling <= 512, $"{Name}: the sampling must be 16 to 512 (it is {settings.Sampling}).");
        Check(settings.MaximumDistanceMicrometers >= 0 && double.IsFinite(settings.MaximumDistanceMicrometers), $"{Name}: the maximum distance must be 0 or positive.");
        Check(settings.Reference != EnclosedEnergyReference.Middle, $"{Name}: the middle reference belongs to the geometric enclosed energy; choose the chief ray, the centroid or the vertex.");
        var fields = Rows(settings.Field, system.Fields.Points.Count, true, "field");
        var waves = PsfRuns.Waves(settings.Wavelength, system, Name);
        var setups = new List<DiffractionEnclosedEnergySetup>();
        foreach (int f in fields)
        {
            cancellation.ThrowIfCancellationRequested();
            setups.Add(DiffractionEnclosedEnergy.Prepare(lens, system, f, waves, settings.Sampling, settings.Reference,
                settings.CheckApertures, settings.AllowOpdLimitations, null, cancellation));
        }
        double allowed = setups.Min(s => s.MaximumSize(settings.Type));
        double max;
        if (settings.MaximumDistanceMicrometers > 0)
        {
            max = settings.MaximumDistanceMicrometers * 1e-6;
            Check(max <= allowed * (1 + 1e-12),
                $"{Name}: the maximum distance {settings.MaximumDistanceMicrometers:g6} µm is beyond half the PSF's period ({allowed * 1e6:g6} µm), where the regions would overlap the PSF's periodic copies. Raise the sampling or lower the maximum distance.");
        }
        else max = Math.Min(allowed, setups.Max(s => s.DefaultSize));
        var sizes = EnclosedEnergyRegions.Sizes(max, EnclosedEnergyRuns.Points);
        var x = sizes.Select(s => s * 1e6).ToArray();

        var curves = new List<AnalysisCurve>();
        var pairs = new List<(string, string)>();
        var warnings = new List<string>();
        for (int i = 0; i < setups.Count; i++)
        {
            var values = setups[i].Fractions(settings.Type, sizes, null, cancellation);
            string label = EnclosedEnergyRuns.FieldLabel(system, fields[i]);
            curves.Add(new AnalysisCurve(label, x, values));
            warnings.AddRange(setups[i].Warnings.Select(w => $"{label}: {w}"));
            foreach (double level in new[] { 0.5, 0.8 })
            {
                double at = EnclosedEnergyRuns.SizeAt(sizes, values, level);
                pairs.Add(($"{label}: size holding {level:P0}", double.IsFinite(at) ? $"{EnclosedEnergyRuns.Um(at)} µm" : $"beyond {EnclosedEnergyRuns.Um(max)} µm"));
            }
        }
        var assumptions = new List<string>(setups[0].Notes)
        {
            $"Type: {EnclosedEnergyRegions.TypeLabel(settings.Type)}; the x axis is the {EnclosedEnergyRegions.SizeLabel(settings.Type).ToLowerInvariant()}. Each curve is the fraction of the PSF's energy in the region (help, \"Diffraction (enclosed energy)\").",
            settings.MaximumDistanceMicrometers > 0 ? $"Maximum distance {settings.MaximumDistanceMicrometers:g6} µm (chosen)." : $"Maximum distance {EnclosedEnergyRuns.Um(max)} µm: the program's default, the smaller of half the PSF's period and the larger of 5·λ·F and 1.5 × the rays' spread (the help gives no default).",
        };
        if (settings.ShowDiffractionLimit)
        {
            int rf = EnclosedEnergyRuns.ReferenceField(system);
            cancellation.ThrowIfCancellationRequested();
            var reference = DiffractionEnclosedEnergy.Prepare(lens, system, rf, waves, settings.Sampling, EnclosedEnergyReference.ChiefRay,
                settings.CheckApertures, settings.AllowOpdLimitations, null, cancellation);
            var limit = DiffractionEnclosedEnergy.Unaberrated(reference, null, cancellation);
            double limitMax = limit.MaximumSize(settings.Type);
            var limitSizes = sizes.Where(s => s <= limitMax * (1 + 1e-12)).ToArray();
            curves.Add(new AnalysisCurve("Diffraction limit", limitSizes.Select(s => s * 1e6).ToArray(), limit.Fractions(settings.Type, limitSizes, null, cancellation), Dashed: true));
            assumptions.Add($"Diffraction limit: field {rf + 1}'s pupil (apodization, vignetting, apertures) with the OPD set to zero, about its chief ray (help, \"Diffraction Limited\": the reference field is the (0, 0) field when defined, else field 1).");
        }
        assumptions.AddRange(warnings.Select(w => "Warning: " + w));
        assumptions.Add("Not built: the Huygens PSF option (image sampling, image delta), polarization, intermediate surfaces, dashed-line styling.");
        assumptions.Add($"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.");
        assumptions.AddRange(notes);
        var plot = new LinePlot($"Diffraction {EnclosedEnergyRegions.TypeLabel(settings.Type)} energy, {EnclosedEnergyRuns.WaveLabel(system, waves)}",
            EnclosedEnergyRegions.SizeLabel(settings.Type), "Fraction of enclosed energy", curves);
        var main = EnclosedEnergyRuns.Table("Diffraction enclosed energy", "Size (µm)", sizes, curves, pairs, Array.Empty<string>());
        return OpticsAnalysisResult.FromSections(Name, EnclosedEnergyRuns.Sections(warnings, main), assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>
/// OpticStudio's Geometric Enclosed Energy (OP-41): the fraction of the traced rays' weight within
/// a circle, slit or square about the chief ray, the centroid, the vertex or the middle
/// (<see cref="GeometricEnclosedEnergy"/>), optionally multiplied by the Airy disk's curve.
/// Not built: polarization, scattering, dashed-line styling.
/// </summary>
public sealed class GeometricEnclosedEnergyAnalysis : WavefrontAnalysisBase<GeometricEnclosedEnergySettings>
{
    public const string SavedId = "GeometricEnclosedEnergy";

    public GeometricEnclosedEnergyAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Geometric enclosed energy";
    public override string Category => "Enclosed energy";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        GeometricEnclosedEnergySettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 8 && settings.Sampling <= 1024, $"{Name}: the sampling must be 8 to 1024 (it is {settings.Sampling}).");
        Check(settings.MaximumDistanceMicrometers >= 0 && double.IsFinite(settings.MaximumDistanceMicrometers), $"{Name}: the maximum distance must be 0 or positive.");
        FieldScanCommon.RefuseAfocal(system, Name);
        var fields = Rows(settings.Field, system.Fields.Points.Count, true, "field");
        var waves = PsfRuns.Waves(settings.Wavelength, system, Name);
        int surface = RayAnalysisSupport.Surface(Name, settings.Surface, lens);
        bool multiply = settings.MultiplyByDiffractionLimit;
        Check(!multiply || settings.Type == EnclosedEnergyType.Encircled, $"{Name}: \"Multiply by diffraction limit\" applies to the encircled type only (the Airy curve is radial).");
        Check(!multiply || surface == lens.ImageIndex, $"{Name}: \"Multiply by diffraction limit\" applies at the image surface only.");

        var spots = new List<GeometricEnclosedEnergySpot>();
        foreach (int f in fields)
        {
            cancellation.ThrowIfCancellationRequested();
            spots.Add(GeometricEnclosedEnergy.Trace(lens, system, f, waves, settings.Sampling, settings.Reference, surface, null, cancellation));
        }
        double max = settings.MaximumDistanceMicrometers > 0 ? settings.MaximumDistanceMicrometers * 1e-6 : spots.Max(s => s.MaximumDistance(settings.Type));
        if (!(max > 0)) max = 1e-9;
        var sizes = EnclosedEnergyRegions.Sizes(max, EnclosedEnergyRuns.Points);
        var x = sizes.Select(s => s * 1e6).ToArray();
        (double Lambda, double F, string Note)? airy = multiply ? EnclosedEnergyRuns.AiryParameters(lens, system, waves, Name) : null;

        var curves = new List<AnalysisCurve>();
        var pairs = new List<(string, string)>();
        var lines = new List<string>();
        for (int i = 0; i < spots.Count; i++)
        {
            var values = spots[i].Fractions(settings.Type, sizes);
            if (airy is { } a)
                for (int j = 0; j < sizes.Length; j++) values[j] *= AiryEnclosedEnergy.AtRadius(sizes[j], a.Lambda, a.F);
            string label = EnclosedEnergyRuns.FieldLabel(system, fields[i]);
            curves.Add(new AnalysisCurve(label, x, values));
            lines.AddRange(EnclosedEnergyRuns.RayLines(spots[i], label));
            foreach (double level in new[] { 0.5, 0.8 })
            {
                double at = EnclosedEnergyRuns.SizeAt(sizes, values, level);
                pairs.Add(($"{label}: size holding {level:P0}", double.IsFinite(at) ? $"{EnclosedEnergyRuns.Um(at)} µm" : $"beyond {EnclosedEnergyRuns.Um(max)} µm"));
            }
        }
        var assumptions = new List<string>(RayAnalysisSupport.LaunchAssumptions(system).Where(l => !l.StartsWith("Pupil apodization", StringComparison.Ordinal)))
        {
            $"Rays on a {settings.Sampling} × {settings.Sampling} grid of pupil nodes inside the unit circle (equal pupil areas); each ray that passes the surface weighs its wavelength's weight (1 for one wavelength) times the pupil apodization's intensity A(ρ)². Rays that fail or are vignetted carry no light and are counted.",
            $"Type: {EnclosedEnergyRegions.TypeLabel(settings.Type)}; the fraction at a size is the weight of the rays at a distance at most that size from the {EnclosedEnergyRegions.ReferenceLabel(settings.Reference)} over the weight of every ray that passed (help, \"Geometric\").",
            settings.Reference == EnclosedEnergyReference.ChiefRay ? $"The chief ray is {(waves.Count > 1 ? "the primary wavelength's" : "the selected wavelength's")}, traced with apertures ignored." : "",
            settings.MaximumDistanceMicrometers > 0 ? $"Maximum distance {settings.MaximumDistanceMicrometers:g6} µm (chosen)." : $"Maximum distance {EnclosedEnergyRuns.Um(max)} µm: the distance that holds every ray (the program's default; the help gives none).",
            "The help: \"The geometric encircled energy is not a good indicator of performance if the system is close to diffraction limited.\"",
        };
        assumptions.RemoveAll(string.IsNullOrEmpty);
        if (airy is { } air) assumptions.Add(air.Note);
        assumptions.Add("Not built: polarization, scattering, dashed-line styling.");
        assumptions.AddRange(notes);
        var plot = new LinePlot($"Geometric {EnclosedEnergyRegions.TypeLabel(settings.Type)} energy, {EnclosedEnergyRuns.WaveLabel(system, waves)}",
            EnclosedEnergyRegions.SizeLabel(settings.Type), "Fraction of enclosed energy", curves);
        var main = EnclosedEnergyRuns.Table("Geometric enclosed energy", "Size (µm)", sizes, curves, pairs, lines);
        return OpticsAnalysisResult.FromSections(Name, new[] { main }, assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>
/// OpticStudio's Geometric Line/Edge Spread (OP-41): the rays' weight across a line or an edge
/// parallel to x and to y, about the chief ray (<see cref="GeometricEnclosedEnergy.LineEdgeSpread"/>).
/// The diffraction form is OP-38's FFT line/edge spread. Not built: polarization.
/// </summary>
public sealed class GeometricLineEdgeSpreadAnalysis : WavefrontAnalysisBase<GeometricLineEdgeSpreadSettings>
{
    public const string SavedId = "GeometricLineEdgeSpread";

    /// <summary>The bins across ±the maximum radius (the help gives no count).</summary>
    public const int Bins = 100;

    public GeometricLineEdgeSpreadAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Geometric line/edge spread";
    public override string Category => "Enclosed energy";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        GeometricLineEdgeSpreadSettings settings, CancellationToken cancellation)
    {
        Check(settings.Sampling >= 8 && settings.Sampling <= 1024, $"{Name}: the sampling must be 8 to 1024 (it is {settings.Sampling}).");
        Check(settings.MaximumRadiusMicrometers >= 0 && double.IsFinite(settings.MaximumRadiusMicrometers), $"{Name}: the maximum radius must be 0 or positive.");
        FieldScanCommon.RefuseAfocal(system, Name);
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        var waves = PsfRuns.Waves(settings.Wavelength, system, Name);
        int surface = RayAnalysisSupport.Surface(Name, settings.Surface, lens);
        var spot = GeometricEnclosedEnergy.Trace(lens, system, f, waves, settings.Sampling, EnclosedEnergyReference.ChiefRay, surface, null, cancellation);
        double max = settings.MaximumRadiusMicrometers > 0
            ? settings.MaximumRadiusMicrometers * 1e-6
            : spot.Points.Max(p => Math.Max(Math.Abs(p.X - spot.ReferenceX), Math.Abs(p.Y - spot.ReferenceY)));
        if (!(max > 0)) max = 1e-9;
        var curves = new List<AnalysisCurve>();
        foreach (bool parallelToX in new[] { true, false })
        {
            var (coordinate, line, edge) = GeometricEnclosedEnergy.LineEdgeSpread(spot.Points, spot.ReferenceX, spot.ReferenceY, parallelToX, max, Bins);
            var xs = coordinate.Select(c => c * 1e6).ToArray();
            string o = parallelToX ? "X" : "Y";
            if (settings.Type != LineEdgeSpreadType.EdgeOnly) curves.Add(new AnalysisCurve($"Line, {o} orientation", xs, line));
            if (settings.Type != LineEdgeSpreadType.LineOnly) curves.Add(new AnalysisCurve($"Edge, {o} orientation", xs, edge, Dashed: true));
        }
        string label = EnclosedEnergyRuns.FieldLabel(system, f);
        var lines = EnclosedEnergyRuns.RayLines(spot, label);
        var assumptions = new List<string>(RayAnalysisSupport.LaunchAssumptions(system).Where(l => !l.StartsWith("Pupil apodization", StringComparison.Ordinal)))
        {
            $"Rays on a {settings.Sampling} × {settings.Sampling} grid of pupil nodes inside the unit circle, each weighing its wavelength's weight times A(ρ)².",
            "X orientation: a line or edge parallel to the x axis, so the curve runs along y (each ray at y − y_chief); Y orientation runs along x (help, \"Geometric Line/Edge Spread\": \"The zero coordinate is referenced to the chief ray\").",
            $"The line spread is the rays' weight in each of {Bins} equal bins across ±{EnclosedEnergyRuns.Um(max)} µm, over the largest bin's; the edge spread at a bin's centre is the weight of the rays at or below it over the total, so it runs from 0 to 1 across every ray (the help gives no normalisation).",
            settings.MaximumRadiusMicrometers > 0 ? $"Maximum radius {settings.MaximumRadiusMicrometers:g6} µm (chosen)." : $"Maximum radius {EnclosedEnergyRuns.Um(max)} µm: the farthest ray from the chief ray along x or y (the program's default).",
            "Not built: polarization.",
        };
        assumptions.AddRange(notes);
        var plot = new LinePlot($"Geometric line/edge spread, {label}, {EnclosedEnergyRuns.WaveLabel(system, waves)}", "Distance from the chief ray (µm)", "Relative value", curves);
        var main = new ReportSection("Geometric line/edge spread")
        {
            Lines = lines,
            TableHeaders = new[] { "Distance (µm)" }.Concat(curves.Select(c => c.Name)).ToArray(),
            TableRows = Enumerable.Range(0, Bins).Select(b => (IReadOnlyList<string>)new[] { W(curves[0].X[b]) }.Concat(curves.Select(c => W(c.Y[b]))).ToArray()).ToArray(),
        };
        return OpticsAnalysisResult.FromSections(Name, new[] { main }, assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>
/// OpticStudio's Extended Source enclosed energy (OP-41): rays from every lit pixel of an IMA
/// source image (<see cref="ExtendedSourceEnclosedEnergy"/>), as enclosed energy or as x or y
/// distributions with the geometric FWHM. Not built: BIM files, polarization, dashed-line styling.
/// </summary>
public sealed class ExtendedSourceEnclosedEnergyAnalysis : WavefrontAnalysisBase<ExtendedSourceEnclosedEnergySettings>
{
    public const string SavedId = "ExtendedSourceEnclosedEnergy";

    /// <summary>The strips across ±the maximum distance of a distribution (the help gives no count).</summary>
    public const int Bins = 100;

    public ExtendedSourceEnclosedEnergyAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Extended source enclosed energy";
    public override string Category => "Enclosed energy";

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        ExtendedSourceEnclosedEnergySettings settings, CancellationToken cancellation)
    {
        Check(settings.RaysThousands >= 1 && settings.RaysThousands <= 1000, $"{Name}: Rays x 1000 must be 1 to 1000 (it is {settings.RaysThousands}).");
        Check(settings.FieldSize > 0 && double.IsFinite(settings.FieldSize), $"{Name}: the field size must be positive (it is {settings.FieldSize:g6}).");
        Check(settings.MaximumDistanceMicrometers >= 0 && double.IsFinite(settings.MaximumDistanceMicrometers), $"{Name}: the maximum distance must be 0 or positive.");
        bool distribution = settings.Type is ExtendedSourceType.XDistribution or ExtendedSourceType.YDistribution;
        bool multiply = settings.MultiplyByDiffractionLimit;
        Check(!multiply || settings.Type == ExtendedSourceType.Encircled, $"{Name}: \"Multiply by diffraction limit\" is \"only available for Type = encircled\" (help, \"Extended Source\").");
        var image = ImaFile.Read(settings.File);
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        var waves = PsfRuns.Waves(settings.Wavelength, system, Name);
        int surface = RayAnalysisSupport.Surface(Name, settings.Surface, lens);
        Check(!multiply || surface == lens.ImageIndex, $"{Name}: \"Multiply by diffraction limit\" applies at the image surface only.");
        var setup = RmsCore.Prepare(lens, system, settings.RemoveVignettingFactors, Name, notes, cancellation);
        bool angle = FieldTypes.IsAngle(system.Fields.Type);
        double size = angle ? settings.FieldSize : OpticsConventions.ToMeters(settings.FieldSize, system.Units.LensUnit);
        var spot = ExtendedSourceEnclosedEnergy.Trace(setup.Lens, setup.System, new ExtendedSourceRequest
        {
            Image = image, Field = f, FieldSize = size, Wavelengths = waves, RaysThousands = settings.RaysThousands,
            Reference = settings.Reference, Surface = surface,
        }, cancellation);

        string label = EnclosedEnergyRuns.FieldLabel(system, f);
        var lines = EnclosedEnergyRuns.RayLines(spot, label);
        var pairs = new List<(string, string)>();
        var curves = new List<AnalysisCurve>();
        double[] sizes;
        string xLabel, yLabel, title;
        if (distribution)
        {
            bool alongX = settings.Type == ExtendedSourceType.XDistribution;
            double max = settings.MaximumDistanceMicrometers > 0 ? settings.MaximumDistanceMicrometers * 1e-6
                : spot.Points.Max(p => Math.Abs(alongX ? p.X - spot.ReferenceX : p.Y - spot.ReferenceY));
            if (!(max > 0)) max = 1e-9;
            var (centres, values) = Distribution(spot, alongX, max);
            sizes = centres;
            curves.Add(new AnalysisCurve(label, centres.Select(c => c * 1e6).ToArray(), values));
            double fwhm = Fwhm(centres, values);
            pairs.Add(("Geometric FWHM", double.IsFinite(fwhm) ? $"{EnclosedEnergyRuns.Um(fwhm)} µm" : "not found: the half maximum is not crossed on both sides within the plot"));
            xLabel = $"{(alongX ? "x" : "y")} from the reference (µm)";
            yLabel = "Fraction of the energy per strip";
            title = $"Extended source {(alongX ? "x" : "y")} distribution, {EnclosedEnergyRuns.WaveLabel(system, waves)}";
            lines.Add($"Each strip is {EnclosedEnergyRuns.Um(2 * max / Bins)} µm wide in {(alongX ? "x" : "y")} and infinite in {(alongX ? "y" : "x")} (help: \"a pixel which is narrow in one direction and infinite in the other direction\"); {Bins} strips across ±{EnclosedEnergyRuns.Um(max)} µm. FWHM: the width between the half-maximum crossings either side of the largest strip, linear between strip centres.");
        }
        else
        {
            var type = (EnclosedEnergyType)(int)settings.Type;
            double max = settings.MaximumDistanceMicrometers > 0 ? settings.MaximumDistanceMicrometers * 1e-6 : spot.MaximumDistance(type);
            if (!(max > 0)) max = 1e-9;
            sizes = EnclosedEnergyRegions.Sizes(max, EnclosedEnergyRuns.Points);
            var values = spot.Fractions(type, sizes);
            if (multiply)
            {
                var a = EnclosedEnergyRuns.AiryParameters(setup.Lens, setup.System, waves, Name);
                for (int j = 0; j < sizes.Length; j++) values[j] *= AiryEnclosedEnergy.AtRadius(sizes[j], a.Lambda, a.FNumber);
                lines.Add(a.Note);
            }
            curves.Add(new AnalysisCurve(label, sizes.Select(s => s * 1e6).ToArray(), values));
            foreach (double level in new[] { 0.5, 0.8 })
            {
                double at = EnclosedEnergyRuns.SizeAt(sizes, values, level);
                pairs.Add(($"Size holding {level:P0}", double.IsFinite(at) ? $"{EnclosedEnergyRuns.Um(at)} µm" : $"beyond {EnclosedEnergyRuns.Um(max)} µm"));
            }
            xLabel = EnclosedEnergyRegions.SizeLabel(type);
            yLabel = "Fraction of enclosed energy";
            title = $"Extended source {EnclosedEnergyRegions.TypeLabel(type)} energy, {EnclosedEnergyRuns.WaveLabel(system, waves)}";
        }
        string unit = angle ? "°" : " " + ParaxialReport.UnitName(system.Units.LensUnit);
        var assumptions = new List<string>(RayAnalysisSupport.LaunchAssumptions(system).Where(l => !l.StartsWith("Pupil apodization", StringComparison.Ordinal)))
        {
            $"Source: {Path.GetFileName(settings.File)}, a {(image.Binary ? "binary" : "text")} IMA image of {image.Size} × {image.Size} pixels{(image.Maps.Count > 1 ? $" with {image.Maps.Count} maps (one per wavelength)" : "")}, {settings.FieldSize:g6}{unit} wide, centred on field {f + 1}; row 1 is the top (+y) of the image and column 1 its left (−x) (help, \"Geometric Image Analysis\").",
            $"About {settings.RaysThousands * 1000} rays shared over the lit pixels by pixel value × wavelength weight, at least one per lit pixel and wavelength ({spot.Launched} traced); each ray weighs its pixel's value × wavelength weight over the pixel's ray count, times A(ρ)². Positions in the pixel and the pupil from a 4-D Sobol sequence instead of the help's random draws, so the result repeats exactly.",
            "Field points between the defined fields are normalised against the defined fields plus the image's corners.",
            "The help: \"The geometric encircled energy is not a good indicator of performance if the system is close to diffraction limited.\"",
            "Not built: BIM files, polarization, dashed-line styling.",
        };
        if (setup.ReplacedVignetting) assumptions.Add("The fields' vignetting factors were replaced by surface apertures (Remove vignetting factors).");
        assumptions.AddRange(notes.Distinct());
        var plot = new LinePlot(title, xLabel, yLabel, curves);
        var main = EnclosedEnergyRuns.Table("Extended source enclosed energy", distribution ? "Strip centre (µm)" : "Size (µm)", sizes, curves, pairs, lines);
        return OpticsAnalysisResult.FromSections(Name, new[] { main }, assumptions, new AnalysisPlot[] { plot });
    }

    /// <summary>The fraction of the weight in each of <see cref="Bins"/> strips across ±max.</summary>
    internal static (double[] Centres, double[] Values) Distribution(GeometricEnclosedEnergySpot spot, bool alongX, double max)
    {
        double width = 2 * max / Bins;
        var centres = new double[Bins];
        var values = new double[Bins];
        for (int b = 0; b < Bins; b++) centres[b] = -max + (b + 0.5) * width;
        double total = 0;
        foreach (var p in spot.Points)
        {
            total += p.Weight;
            double u = alongX ? p.X - spot.ReferenceX : p.Y - spot.ReferenceY;
            int b = (int)Math.Floor((u + max) / width);
            if (b == Bins && u <= max) b = Bins - 1;
            if (b >= 0 && b < Bins) values[b] += p.Weight;
        }
        for (int b = 0; b < Bins; b++) values[b] /= total;
        return (centres, values);
    }

    /// <summary>The full width at half maximum: the half-maximum crossings either side of the
    /// largest value, linear between samples; NaN when one side does not cross.</summary>
    internal static double Fwhm(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int peak = 0;
        for (int i = 1; i < y.Count; i++) if (y[i] > y[peak]) peak = i;
        double half = y[peak] / 2;
        if (!(half > 0)) return double.NaN;
        double left = double.NaN, right = double.NaN;
        for (int i = peak; i > 0; i--)
            if (y[i - 1] < half) { left = x[i - 1] + (half - y[i - 1]) / (y[i] - y[i - 1]) * (x[i] - x[i - 1]); break; }
        for (int i = peak; i < y.Count - 1; i++)
            if (y[i + 1] < half) { right = x[i] + (y[i] - half) / (y[i] - y[i + 1]) * (x[i + 1] - x[i]); break; }
        return right - left;
    }
}
