using System.ComponentModel;
using OpenSim.Core.Numerics;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using static OpenSim.Optics.Analysis.RayAnalysisSupport;

namespace OpenSim.Optics.Analysis.StrayLight;

/// <summary>The data a power map shows (help, "Power Pupil Map" and "Power Field Map", Data).</summary>
public enum PowerMapData
{
    Spherical = 0,
    Cylinder = 1,
    Maximum = 2,
    Minimum = 3,
    Tangential = 4,
    Sagittal = 5,
    YDirection = 6,
    XDirection = 7,
    Astigmatic = 8,
}

/// <summary>How a power map is drawn (help, Show As: the surface plot, grey scale and scan are not built).</summary>
public enum PowerMapStyle
{
    FalseColor = 0,
    Contour = 1,
}

/// <summary>
/// The local power of a lens along one real ray (OP-46): what the PAL/Freeform power maps
/// evaluate at each pupil or field point.
/// <para>
/// The help says "the method used is to trace a ring of real rays around a central reference ray
/// ... The ray data are used to determine the focal length", without the formula (2025 R2 help,
/// "Power Pupil Map", plans/sources/optics/op-46-help/). This program's construction: ring rays
/// leave from the reference ray's launch point offset by ±δ·ê(θ) at right angles to it, parallel
/// to it, for θ = kπ/m (k = 0 … m − 1; ê(0) is the pupil's x direction and ê(π/2) its y
/// direction, made perpendicular to the ray). After refraction at the evaluation surface,
/// P(θ) = −n′ (d′₊ − d′₋)·ê′ / (2δ), with d′ the global direction cosines, n′ the index after the
/// surface and ê′ the part of ê(θ) at right angles to the reference ray's exit direction: the
/// convergence the surfaces give a narrow parallel pencil in the direction θ, in diopters when
/// lengths are meters (a thin lens of focal length f in air gives 1/f). Being the curvature of a
/// quadratic form, P(θ) = A + B cos 2θ + C sin 2θ; A, B and C are fitted by least squares to the m
/// samples (exact for that form), so the spherical power (the help's average over the ring) is A,
/// the maximum and minimum are A ± √(B² + C²), the cylinder their difference, and the directional
/// powers the form at the direction's angle. Tangential is the plane of the reference ray and the
/// object-space z axis (the y direction for a ray along z), sagittal at right angles to it (help).
/// Because the pencil is parallel, the result does not depend on where the object is: it is the
/// lens's power along the ray, not a vergence change for the actual object point (§10 item 45).
/// </para>
/// </summary>
public static class LocalPower
{
    /// <summary>The fitted form P(θ) = A + B cos 2θ + C sin 2θ (1/m) and the tangential angle in the
    /// ring basis.</summary>
    public readonly record struct Form(double A, double B, double C, double TangentialAngle)
    {
        public double At(double theta) => A + B * Math.Cos(2 * theta) + C * Math.Sin(2 * theta);
        public double Maximum => A + Math.Sqrt(B * B + C * C);
        public double Minimum => A - Math.Sqrt(B * B + C * C);

        /// <summary>The value of <paramref name="data"/>: a power (1/m), or with
        /// <paramref name="focalLength"/> a focal length (m) by the help's rules: the cylinder EFL
        /// is the difference of the maximum and minimum focal lengths, the astigmatic EFL that of
        /// the x and y focal lengths; every other EFL is 1/power.</summary>
        public double Value(PowerMapData data, bool focalLength)
        {
            double t = TangentialAngle;
            double x = At(0), y = At(Math.PI / 2);
            if (!focalLength)
                return data switch
                {
                    PowerMapData.Spherical => A,
                    PowerMapData.Cylinder => Maximum - Minimum,
                    PowerMapData.Maximum => Maximum,
                    PowerMapData.Minimum => Minimum,
                    PowerMapData.Tangential => At(t),
                    PowerMapData.Sagittal => At(t + Math.PI / 2),
                    PowerMapData.YDirection => y,
                    PowerMapData.XDirection => x,
                    PowerMapData.Astigmatic => x - y,
                    _ => throw new ArgumentOutOfRangeException(nameof(data)),
                };
            return data switch
            {
                PowerMapData.Spherical => 1 / A,
                PowerMapData.Cylinder => Math.Abs(1 / Minimum - 1 / Maximum),
                PowerMapData.Maximum => 1 / Maximum,
                PowerMapData.Minimum => 1 / Minimum,
                PowerMapData.Tangential => 1 / At(t),
                PowerMapData.Sagittal => 1 / At(t + Math.PI / 2),
                PowerMapData.YDirection => 1 / y,
                PowerMapData.XDirection => 1 / x,
                PowerMapData.Astigmatic => 1 / x - 1 / y,
                _ => throw new ArgumentOutOfRangeException(nameof(data)),
            };
        }
    }

    /// <summary>
    /// The form along <paramref name="reference"/> (a launched ray in the global frame) evaluated
    /// after surface <paramref name="surface"/>, with <paramref name="angles"/> ring directions
    /// (at least 3) and offset <paramref name="delta"/> (m). Null with <paramref name="failure"/>
    /// set when the reference or a ring ray fails before the surface.
    /// </summary>
    public static Form? Evaluate(SequentialTrace trace, Ray reference, int surface, int angles, double delta, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (angles < 3) throw new ArgumentException($"Local power: at least 3 ring directions are needed (it is {angles}).");
        if (!(delta > 0)) throw new ArgumentException($"Local power: the ring offset must be positive (it is {delta}).");
        failure = null;
        var d0 = reference.Direction;
        var ex = Perpendicular(Vector3D.UnitX, d0);
        var ey = Vector3D.Cross(d0, ex);
        ey = ey * (1 / ey.Length);
        // Tangential: the plane of the ray and the object-space z axis.
        var radial = new Vector3D(d0.X, d0.Y, 0);
        var et = radial.Length < 1e-12 ? ey : Perpendicular(radial * (1 / radial.Length), d0);
        double tangential = Math.Atan2(Vector3D.Dot(et, ey), Vector3D.Dot(et, ex));

        var refExit = Exit(trace, reference, surface, out failure);
        if (refExit is null) { failure = "reference ray: " + failure; return null; }
        double nAfter = trace.Indices[surface];
        var thetas = new double[angles];
        var p = new double[angles];
        for (int k = 0; k < angles; k++)
        {
            double theta = Math.PI * k / angles;
            var e = ex * Math.Cos(theta) + ey * Math.Sin(theta);
            var plus = Exit(trace, reference with { Position = reference.Position + e * delta }, surface, out var f1);
            var minus = Exit(trace, reference with { Position = reference.Position - e * delta }, surface, out var f2);
            if (plus is null || minus is null) { failure = $"ring ray at {theta * 180 / Math.PI:g4}°: {f1 ?? f2}"; return null; }
            var eOut = Perpendicular(e, refExit.Value);
            thetas[k] = theta;
            p[k] = -nAfter * Vector3D.Dot(plus.Value - minus.Value, eOut) / (2 * delta);
        }
        var (a, b, c) = Fit(thetas, p);
        return new Form(a, b, c, tangential);
    }

    /// <summary>Least squares of A + B cos 2θ + C sin 2θ to the samples (normal equations, 3 × 3).</summary>
    private static (double A, double B, double C) Fit(double[] theta, double[] p)
    {
        var m = new double[3, 3];
        var r = new double[3];
        for (int k = 0; k < theta.Length; k++)
        {
            double[] f = { 1, Math.Cos(2 * theta[k]), Math.Sin(2 * theta[k]) };
            for (int i = 0; i < 3; i++)
            {
                r[i] += f[i] * p[k];
                for (int j = 0; j < 3; j++) m[i, j] += f[i] * f[j];
            }
        }
        // Gaussian elimination with partial pivoting.
        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int row = col + 1; row < 3; row++) if (Math.Abs(m[row, col]) > Math.Abs(m[piv, col])) piv = row;
            if (piv != col)
            {
                for (int j = 0; j < 3; j++) (m[col, j], m[piv, j]) = (m[piv, j], m[col, j]);
                (r[col], r[piv]) = (r[piv], r[col]);
            }
            for (int row = col + 1; row < 3; row++)
            {
                double f = m[row, col] / m[col, col];
                for (int j = col; j < 3; j++) m[row, j] -= f * m[col, j];
                r[row] -= f * r[col];
            }
        }
        var x = new double[3];
        for (int i = 2; i >= 0; i--)
        {
            double s = r[i];
            for (int j = i + 1; j < 3; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
        }
        return (x[0], x[1], x[2]);
    }

    private static Vector3D Perpendicular(Vector3D v, Vector3D d)
    {
        var w = v - d * Vector3D.Dot(v, d);
        double length = w.Length;
        if (length < 1e-12) throw new InvalidOperationException("Local power: the ring direction is parallel to the ray.");
        return w * (1 / length);
    }

    /// <summary>The global direction after <paramref name="surface"/>, or null with the failure.</summary>
    private static Vector3D? Exit(SequentialTrace trace, Ray ray, int surface, out string? failure)
    {
        failure = null;
        var path = trace.TracePath(ray);
        if (path.Hits.Count <= surface || path.Hits[surface].Status != RayStatus.Ok)
        {
            failure = $"{RayStatuses.Label(path.Status).ToLowerInvariant()} at surface {path.FailedAtSurface}";
            return null;
        }
        return trace.GlobalPlacement(surface).ApplyDirection(path.Hits[surface].Direction);
    }
}

/// <summary>Settings of the <see cref="PowerPupilMapAnalysis"/> (help, "Power Pupil Map").</summary>
public sealed record PowerPupilMapSettings
{
    [DisplayName("Sampling"), Description("Grid points across the pupil diameter (at least 3).")]
    public int Sampling { get; init; } = 33;

    [DisplayName("Field"), Description("Field number, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Offset"), Description("Added to every computed value (diopters, or lens units for EFL).")]
    public double Offset { get; init; }

    [DisplayName("Wavelength"), Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Surface"), Description("The last surface with optical power; 0 for the image surface (the help recommends setting it).")]
    public int Surface { get; init; }

    [DisplayName("Data"), Description("Spherical, cylinder, maximum, minimum, tangential, sagittal, Y, X or astigmatic (X − Y).")]
    public PowerMapData Data { get; init; } = PowerMapData.Spherical;

    [DisplayName("Effective focal length"), Description("Show the effective focal length in lens units instead of the optical power in diopters.")]
    public bool EffectiveFocalLength { get; init; }

    [DisplayName("Show as"), Description("False colour map or contour map.")]
    public PowerMapStyle ShowAs { get; init; } = PowerMapStyle.FalseColor;
}

/// <summary>Settings of the <see cref="PowerFieldMapAnalysis"/> (help, "Power Field Map").</summary>
public sealed record PowerFieldMapSettings
{
    [DisplayName("Sampling"), Description("Grid points across the field width (at least 3).")]
    public int Sampling { get; init; } = 21;

    [DisplayName("Field width"), Description("The full width of the field in field units; 0 for twice the largest field.")]
    public double FieldWidth { get; init; }

    [DisplayName("Offset"), Description("Added to every computed value (diopters, or lens units for EFL).")]
    public double Offset { get; init; }

    [DisplayName("Wavelength"), Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Surface"), Description("The last surface with optical power; 0 for the image surface (the help recommends setting it).")]
    public int Surface { get; init; }

    [DisplayName("Use tangent of field angle"), Description("With angle fields, space the grid evenly in the tangent of the angle.")]
    public bool UseTangent { get; init; }

    [DisplayName("Data"), Description("Spherical, cylinder, maximum, minimum, tangential, sagittal, Y, X or astigmatic (X − Y).")]
    public PowerMapData Data { get; init; } = PowerMapData.Spherical;

    [DisplayName("Effective focal length"), Description("Show the effective focal length in lens units instead of the optical power in diopters.")]
    public bool EffectiveFocalLength { get; init; }

    [DisplayName("Show as"), Description("False colour map or contour map.")]
    public PowerMapStyle ShowAs { get; init; } = PowerMapStyle.FalseColor;
}

/// <summary>What the two power maps share.</summary>
internal static class PowerMapSupport
{
    internal const int RingAngles = 8;

    internal static string DataLabel(PowerMapData d) => d switch
    {
        PowerMapData.YDirection => "Y direction",
        PowerMapData.XDirection => "X direction",
        PowerMapData.Astigmatic => "Astigmatic (X − Y)",
        _ => d.ToString(),
    };

    /// <summary>The ring offset: 10⁻⁴ of the entrance pupil radius, or 1 µm when the pupil is at infinity.</summary>
    internal static double Delta(RayLauncher launcher) =>
        launcher.Paraxial.EntrancePupilDiameter is double epd && epd > 0 ? 1e-4 * epd / 2 : 1e-6;

    internal static double Convert(LocalPower.Form form, PowerMapData data, bool efl, LensUnit unit, double offset)
    {
        double v = form.Value(data, efl);
        return (efl ? OpticsConventions.FromMeters(v, unit) : v) + offset;
    }

    internal static List<string> Assumptions(OpticalSystemData system, int wave, int surface, bool efl, double offset, double delta)
    {
        var list = new List<string>
        {
            $"Real rays at {WaveLabel(system, wave)}, directions taken after refraction at surface {surface} (help: set the last surface with power).",
            $"Local power along each reference ray from a parallel ring of {2 * RingAngles} rays offset by ±{StudyReport.Number(delta * 1e6, "g4")} µm in {RingAngles} directions; the fitted form A + B cos 2θ + C sin 2θ gives the average (spherical), maximum, minimum and directional powers (Conventions §49).",
            efl ? "Values are effective focal lengths in lens units: 1/power, the cylinder EFL the difference of the extreme focal lengths, the astigmatic EFL that of the x and y focal lengths (help)."
                : "Values are optical powers in diopters (1/m); the cylinder is maximum minus minimum and is never negative (help).",
            "The pencil is parallel, so the power is the lens's along the ray whatever the object distance.",
        };
        if (offset != 0) list.Add($"An offset of {StudyReport.Number(offset, "g6")} is added to every value (help).");
        list.Add("Show As: the surface plot, grey scale and the X/Y scan are not built; the false colour and contour maps are.");
        return list;
    }
}

/// <summary>PAL/Freeform Power Pupil Map (OP-46): the local power along the rays of one field
/// across the pupil (help, "Power Pupil Map").</summary>
public sealed class PowerPupilMapAnalysis : OpticsAnalysis<PowerPupilMapSettings>
{
    public const string SavedId = "PowerPupilMap";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public PowerPupilMapAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Power pupil map";
    public override string Category => "PAL/Freeform";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, PowerPupilMapSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        CheckDensity(Name, settings.Sampling, 3, "sampling");
        if (settings.Field < 1 || settings.Field > system.Fields.Points.Count)
            throw new ArgumentException($"{Name}: the field must be 1 to {system.Fields.Points.Count} (it is {settings.Field}).");
        int field = settings.Field - 1;
        int wave = OneWavelength(Name, settings.Wavelength, system);
        int surface = Surface(Name, settings.Surface, lens);
        IReadOnlyList<double>? sd = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            sd = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        var trace = SequentialTrace.Create(lens, system, wave, sd);
        var launcher = new RayLauncher(lens, system);
        double delta = PowerMapSupport.Delta(launcher);
        var (hx, hy) = system.Fields.Normalized(field);
        int n = settings.Sampling;
        var values = new double[n, n];
        int failed = 0; string? firstFailure = null;
        var unit = system.Units.LensUnit;
        for (int r = 0; r < n; r++)
        {
            cancellation.ThrowIfCancellationRequested();
            double py = -1 + 2.0 * r / (n - 1);
            for (int col = 0; col < n; col++)
            {
                double px = -1 + 2.0 * col / (n - 1);
                values[r, col] = double.NaN;
                if (px * px + py * py > 1 + 1e-12) continue;
                var reference = launcher.Launch(trace, hx, hy, px, py);
                var form = reference.IsOk ? LocalPower.Evaluate(trace, reference, surface, PowerMapSupport.RingAngles, delta, out var why) : null;
                if (form is null)
                {
                    failed++;
                    firstFailure ??= $"pupil ({px:g4}, {py:g4}): " + (reference.IsOk ? "a ray failed" : $"{RayStatuses.Label(reference.Status).ToLowerInvariant()} at surface {reference.FailedAtSurface}");
                    continue;
                }
                values[r, col] = PowerMapSupport.Convert(form.Value, settings.Data, settings.EffectiveFocalLength, unit, settings.Offset);
            }
        }
        string valueUnit = settings.EffectiveFocalLength ? ParaxialReport.UnitName(unit) : "D";
        string title = $"{PowerMapSupport.DataLabel(settings.Data)} {(settings.EffectiveFocalLength ? "EFL" : "power")}, {FieldLabel(system, field)}";
        var grid = new GridField(values, -1, 1, -1, 1);
        var plot = new AnalysisMapPlot(title, "Px", "Py", grid, valueUnit)
        {
            Style = settings.ShowAs == PowerMapStyle.Contour ? AnalysisMapStyle.Contour : AnalysisMapStyle.Image,
        };
        var (min, max) = grid.FiniteRange();
        var center = values[n / 2, n / 2];
        var lines = new List<string>
        {
            $"Minimum {StudyReport.Number(min, "g8")} {valueUnit}, maximum {StudyReport.Number(max, "g8")} {valueUnit}" + (n % 2 == 1 ? $", centre {StudyReport.Number(center, "g8")} {valueUnit}." : "."),
        };
        if (failed > 0) lines.Add($"{failed} grid point{(failed == 1 ? "" : "s")} left blank because a ray failed (first: {firstFailure}).");
        var sections = new[] { new ReportSection(title) { Lines = lines } };
        var assumptions = PowerMapSupport.Assumptions(system, wave, surface, settings.EffectiveFocalLength, settings.Offset, delta);
        assumptions.Insert(0, $"{n} × {n} grid over the pupil square, points outside the unit circle blank; reference rays launched at the field's normalised coordinates.");
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(launcher.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>PAL/Freeform Power Field Map (OP-46): the local power along the chief ray across the
/// field (help, "Power Field Map").</summary>
public sealed class PowerFieldMapAnalysis : OpticsAnalysis<PowerFieldMapSettings>
{
    public const string SavedId = "PowerFieldMap";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public PowerFieldMapAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Power field map";
    public override string Category => "PAL/Freeform";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, PowerFieldMapSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        CheckDensity(Name, settings.Sampling, 3, "sampling");
        int wave = OneWavelength(Name, settings.Wavelength, system);
        int surface = Surface(Name, settings.Surface, lens);
        var scale = system.Fields.Scale;
        if (!(scale.X > 0) || !(scale.Y > 0))
            throw new ArgumentException($"{Name}: every field is on the axis (or on one axis with rectangular normalisation), so field coordinates cannot be normalised; add a field point off the axis.");
        bool angle = FieldTypes.IsAngle(system.Fields.Type);
        var unit = system.Units.LensUnit;
        double width = settings.FieldWidth;
        if (width < 0) throw new ArgumentException($"{Name}: the field width must be zero or positive (it is {width}).");
        // Field width in the field type's own unit: degrees, or lens units for heights.
        double widthNative = width == 0 ? 2 * Math.Max(scale.X, scale.Y) : angle ? width : OpticsConventions.ToMeters(width, unit);
        bool tangent = settings.UseTangent && angle;
        if (tangent && !(widthNative / 2 < 90)) throw new ArgumentException($"{Name}: with the tangent of the field angle the half width must be below 90° (it is {widthNative / 2}°).");
        double half = tangent ? Math.Tan(widthNative / 2 * Math.PI / 180) : widthNative / 2;

        IReadOnlyList<double>? sd = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            sd = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        var trace = SequentialTrace.Create(lens, system, wave, sd);
        var launcher = new RayLauncher(lens, system);
        double delta = PowerMapSupport.Delta(launcher);
        int n = settings.Sampling;
        var values = new double[n, n];
        int failed = 0; string? firstFailure = null;
        for (int r = 0; r < n; r++)
        {
            cancellation.ThrowIfCancellationRequested();
            double gy = -half + 2 * half * r / (n - 1);
            for (int col = 0; col < n; col++)
            {
                double gx = -half + 2 * half * col / (n - 1);
                double fx = tangent ? Math.Atan(gx) * 180 / Math.PI : gx, fy = tangent ? Math.Atan(gy) * 180 / Math.PI : gy;
                values[r, col] = double.NaN;
                Ray reference;
                try { reference = launcher.Launch(trace, fx / scale.X, fy / scale.Y, 0, 0); }
                catch (InvalidOperationException e)
                {
                    failed++; firstFailure ??= $"field ({fx:g4}, {fy:g4}): {e.Message}"; continue;
                }
                var form = reference.IsOk ? LocalPower.Evaluate(trace, reference, surface, PowerMapSupport.RingAngles, delta, out _) : null;
                if (form is null)
                {
                    failed++;
                    firstFailure ??= $"field ({fx:g4}, {fy:g4}): " + (reference.IsOk ? "a ring ray failed" : $"{RayStatuses.Label(reference.Status).ToLowerInvariant()} at surface {reference.FailedAtSurface}");
                    continue;
                }
                values[r, col] = PowerMapSupport.Convert(form.Value, settings.Data, settings.EffectiveFocalLength, unit, settings.Offset);
            }
        }
        string fieldUnit = angle ? (tangent ? "tan" : "°") : ParaxialReport.UnitName(unit);
        double axis = angle ? half : OpticsConventions.FromMeters(half, unit);
        string valueUnit = settings.EffectiveFocalLength ? ParaxialReport.UnitName(unit) : "D";
        string title = $"{PowerMapSupport.DataLabel(settings.Data)} {(settings.EffectiveFocalLength ? "EFL" : "power")} over the field";
        var grid = new GridField(values, -axis, axis, -axis, axis);
        var plot = new AnalysisMapPlot(title, tangent ? "tan(X field)" : $"X field ({fieldUnit})", tangent ? "tan(Y field)" : $"Y field ({fieldUnit})", grid, valueUnit)
        {
            Style = settings.ShowAs == PowerMapStyle.Contour ? AnalysisMapStyle.Contour : AnalysisMapStyle.Image,
        };
        var (min, max) = grid.FiniteRange();
        var lines = new List<string>
        {
            $"Minimum {StudyReport.Number(min, "g8")} {valueUnit}, maximum {StudyReport.Number(max, "g8")} {valueUnit}" + (n % 2 == 1 ? $", on axis {StudyReport.Number(values[n / 2, n / 2], "g8")} {valueUnit}." : "."),
        };
        if (failed > 0) lines.Add($"{failed} grid point{(failed == 1 ? "" : "s")} left blank because a ray failed (first: {firstFailure}).");
        var sections = new[] { new ReportSection(title) { Lines = lines } };
        var assumptions = PowerMapSupport.Assumptions(system, wave, surface, settings.EffectiveFocalLength, settings.Offset, delta);
        assumptions.Insert(0, $"{n} × {n} grid of fields over a full width of {StudyReport.Number(angle ? widthNative : OpticsConventions.FromMeters(widthNative, unit), "g6")} {(angle ? "°" : ParaxialReport.UnitName(unit))}"
            + (tangent ? ", evenly spaced in the tangent of the angle" : "") + (width == 0 ? " (twice the largest field)" : "") + "; the reference ray is each field's chief ray (pupil centre).");
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(launcher.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }
}
