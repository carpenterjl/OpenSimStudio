using System.ComponentModel;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Analysis;

// OP-36: Full-Field Aberration (Ansys OpticStudio 2025 R2 help, "Full-Field Aberration", saved under
// plans/sources/optics/op-36-help/).

/// <summary>The shape of the field sampling grid ("Field Shape").</summary>
public enum FullFieldShape
{
    Rectangular = 0,
    /// <summary>Points outside the ellipse of the two half-widths are not computed.</summary>
    Elliptical = 1,
}

/// <summary>What is plotted over the field ("Aberration").</summary>
public enum FullFieldAberration
{
    /// <summary>Z4.</summary>
    Defocus = 0,
    /// <summary>√(Z5² + Z6²), angle ½·atan2(−Z5, −Z6).</summary>
    PrimaryAstigmatism = 1,
    /// <summary>√(Z7² + Z8²), angle atan2(−Z7, −Z8).</summary>
    PrimaryComa = 2,
    /// <summary>One Zernike Standard term, <see cref="FullFieldAberrationSettings.ZernikeTerm"/>.</summary>
    ZernikeTerm = 3,
}

/// <summary>"Display": the value as computed, less its average over the grid, or the average.</summary>
public enum FullFieldDisplay
{
    Absolute = 0,
    Relative = 1,
    Average = 2,
}

/// <summary>Settings of the <see cref="FullFieldAberrationAnalysis"/>.</summary>
public sealed record FullFieldAberrationSettings
{
    [DisplayName("Field"), Description("Field row the grid is centred on, 1 for the first.")]
    public int Field { get; init; } = 1;

    [DisplayName("Field shape"), Description("Rectangular, or elliptical (points outside the ellipse of the two half-widths are not computed).")]
    public FullFieldShape Shape { get; init; } = FullFieldShape.Rectangular;

    [DisplayName("Wavelength"), Description("Wavelength row, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("X field width"), Description("Half-width of the grid in x, in degrees or lens units as the fields are; 0 for the largest radial field.")]
    public double XFieldWidth { get; init; }

    [DisplayName("Y field width"), Description("Half-width of the grid in y, in degrees or lens units as the fields are; 0 for the largest radial field.")]
    public double YFieldWidth { get; init; }

    [DisplayName("X field sampling"), Description("Field points across the grid in x; 2 to 65.")]
    public int XFieldSampling { get; init; } = 9;

    [DisplayName("Y field sampling"), Description("Field points across the grid in y; 2 to 65.")]
    public int YFieldSampling { get; init; } = 9;

    [DisplayName("Maximum term"), Description("Zernike Standard terms fitted, 1 to this; 4 to 231 (8 or more for astigmatism and coma).")]
    public int MaximumTerm { get; init; } = 37;

    [DisplayName("Aberration"), Description("Defocus (Z4), primary astigmatism (Z5, Z6), primary coma (Z7, Z8) or one Zernike term.")]
    public FullFieldAberration Aberration { get; init; } = FullFieldAberration.PrimaryAstigmatism;

    [DisplayName("Zernike term"), Description("The Zernike Standard term plotted when the aberration is a Zernike term; at most the maximum term.")]
    public int ZernikeTerm { get; init; } = 4;

    [DisplayName("Pupil sampling"), Description("n: the wavefront is fitted on an n × n grid of pupil nodes; 9 to 257.")]
    public int PupilSampling { get; init; } = 33;

    [DisplayName("Display"), Description("Absolute, relative (less the average over the grid) or the average.")]
    public FullFieldDisplay Display { get; init; } = FullFieldDisplay.Absolute;

    [DisplayName("Show as"), Description("False-colour image or contours.")]
    public AnalysisMapStyle ShowAs { get; init; } = AnalysisMapStyle.Image;

    [DisplayName("Allow OPD limitations"), Description("Compute even when a surface's shape makes the OPD through it approximate or undefined.")]
    public bool AllowOpdLimitations { get; init; }
}

/// <summary>The Zernike Standard fit of the wavefront at one field point.</summary>
public sealed record FieldZernikeFit(double FieldX, double FieldY, double Hx, double Hy, ZernikeFitResult? Fit, string? Failure)
{
    /// <summary>Coefficient of Zernike Standard term <paramref name="j"/> (1-based), waves.</summary>
    public double Z(int j) => Fit is { } f ? f.Coefficients[j - 1] : double.NaN;
}

/// <summary>
/// The wavefront at any point of the field, fitted with Zernike Standard terms (OP-36). The point
/// is appended to the system's field list (its normalised coordinates are inside the
/// normalisation boundary, so the field scale does not change) and its OPD computed by
/// <see cref="OpdEngine"/> on the exit-pupil reference sphere centred on the chief ray; the
/// coefficients are the least-squares fit (<see cref="ZernikeFit"/>) over the rays that reached
/// the reference. Vignetting factors are cleared (an added point has none to give it).
/// </summary>
public static class FieldZernike
{
    /// <summary>The system with vignetting factors cleared and one field point per entry of
    /// <paramref name="points"/> (field-type units) appended after the system's own.</summary>
    public static OpticalSystemData WithPoints(OpticalSystemData system, IReadOnlyList<(double X, double Y)> points)
    {
        ArgumentNullException.ThrowIfNull(system);
        var cleared = VignettingTools.Clear(system);
        var all = cleared.Fields.Points.Concat(points.Select(p => new FieldPoint { X = p.X, Y = p.Y })).ToArray();
        return cleared with { Fields = cleared.Fields with { Points = all } };
    }

    /// <summary>
    /// The fit at field row <paramref name="row"/> of <paramref name="system"/> (0-based), wavelength
    /// row <paramref name="wavelength"/>, over <paramref name="pupil"/>. A failed chief ray, a
    /// refused OPD or a rank-deficient fit gives a result with its <see cref="FieldZernikeFit.Failure"/>.
    /// </summary>
    public static FieldZernikeFit Fit(SequentialLens lens, OpticalSystemData system, int row, int wavelength, ZernikeBasis basis,
        IReadOnlyList<(double Px, double Py)> pupil, OpdOptions options, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(basis);
        var point = system.Fields.Points[row];
        var (hx, hy) = system.Fields.Normalized(row);
        OpdResult r;
        try { r = OpdEngine.Compute(lens, system, row, wavelength, pupil, options, cancellation); }
        catch (InvalidOperationException e) { return new FieldZernikeFit(point.X, point.Y, hx, hy, null, e.Message); }
        var valid = r.Samples.Select(s => s.IsValid).ToArray();
        int used = valid.Count(v => v);
        if (used < basis.TermCount)
            return new FieldZernikeFit(point.X, point.Y, hx, hy, null, $"{used} rays reached the reference, too few for {basis.TermCount} terms");
        var fit = ZernikeFit.Fit(basis, r.Samples.Select(s => s.Px).ToArray(), r.Samples.Select(s => s.Py).ToArray(),
            r.Samples.Select(s => s.Waves).ToArray(), mask: valid);
        if (!fit.IsFullRank)
            return new FieldZernikeFit(point.X, point.Y, hx, hy, null, $"the {used} valid rays cannot determine the {basis.TermCount} terms (rank {fit.Rank})");
        return new FieldZernikeFit(point.X, point.Y, hx, hy, fit, null);
    }
}

/// <summary>
/// OpticStudio's Full-Field Aberration (OP-36): Zernike Standard coefficients across a grid of
/// field points centred on a chosen field, "the wavefront … decomposed using the Zernike Standard
/// Coefficient analysis", and one aberration plotted over the field: "Defocus = Z4; Primary
/// Astigmatism: Magnitude = sqrt (Z5^2 + Z6^2), Angle = (1/2)*atan2(y = -Z5, x = -Z6); Primary
/// Coma: Magnitude = sqrt (Z7^2 + Z8^2), Angle = atan2(y = -Z7, x = -Z8)", or one term (help
/// page "Full-Field Aberration"). "Sampling points falling outside of the Normalization boundary
/// … will not be plotted"; an elliptical shape leaves out the points outside the ellipse.
/// Relative subtracts the average over the computed points, Average shows it. Here the
/// magnitude is what the map shows, so Relative and Average act on the magnitude; the help's
/// per-term averaging belongs to its icon display, which is not built (nor the grey-scale and
/// inverse displays; the map is a false-colour image or contours).
/// </summary>
public sealed class FullFieldAberrationAnalysis : WavefrontAnalysisBase<FullFieldAberrationSettings>
{
    public const string SavedId = "FullFieldAberration";

    public FullFieldAberrationAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Full-field aberration";
    public override string Category => "Aberrations";

    /// <summary>The magnitude and angle (degrees; NaN for a scalar) of the chosen aberration from a fit.</summary>
    internal static (double Value, double AngleDegrees) Evaluate(FieldZernikeFit f, FullFieldAberration aberration, int term)
    {
        const double deg = 180 / Math.PI;
        switch (aberration)
        {
            case FullFieldAberration.Defocus: return (f.Z(4), double.NaN);
            case FullFieldAberration.PrimaryAstigmatism:
            {
                double z5 = f.Z(5), z6 = f.Z(6);
                return (Math.Sqrt(z5 * z5 + z6 * z6), 0.5 * Math.Atan2(-z5, -z6) * deg);
            }
            case FullFieldAberration.PrimaryComa:
            {
                double z7 = f.Z(7), z8 = f.Z(8);
                return (Math.Sqrt(z7 * z7 + z8 * z8), Math.Atan2(-z7, -z8) * deg);
            }
            default: return (f.Z(term), double.NaN);
        }
    }

    internal static string Label(FullFieldAberration a, int term) => a switch
    {
        FullFieldAberration.Defocus => "Defocus Z4",
        FullFieldAberration.PrimaryAstigmatism => "Primary astigmatism √(Z5² + Z6²)",
        FullFieldAberration.PrimaryComa => "Primary coma √(Z7² + Z8²)",
        _ => $"Zernike term Z{term}",
    };

    protected override OpticsAnalysisResult Compute(SequentialLens lens, OpticalSystemData system, List<string> notes,
        FullFieldAberrationSettings settings, CancellationToken cancellation)
    {
        int nx = settings.XFieldSampling, ny = settings.YFieldSampling;
        Check(nx >= 2 && nx <= 65 && ny >= 2 && ny <= 65, $"{Name}: the field sampling must be 2 to 65 in x and y (it is {nx} × {ny}).");
        Check(settings.MaximumTerm >= 4 && settings.MaximumTerm <= 231, $"{Name}: the maximum term must be 4 to 231 (it is {settings.MaximumTerm}).");
        int needed = settings.Aberration switch
        {
            FullFieldAberration.Defocus => 4,
            FullFieldAberration.PrimaryAstigmatism => 6,
            FullFieldAberration.PrimaryComa => 8,
            _ => settings.ZernikeTerm,
        };
        Check(settings.ZernikeTerm >= 1 || settings.Aberration != FullFieldAberration.ZernikeTerm, $"{Name}: the Zernike term must be 1 or more (it is {settings.ZernikeTerm}).");
        Check(needed <= settings.MaximumTerm, $"{Name}: {Label(settings.Aberration, settings.ZernikeTerm)} needs term {needed}, above the maximum term {settings.MaximumTerm}.");
        Check(settings.PupilSampling >= 9 && settings.PupilSampling <= 257, $"{Name}: the pupil sampling must be 9 to 257 (it is {settings.PupilSampling}).");
        Check(double.IsFinite(settings.XFieldWidth) && settings.XFieldWidth >= 0 && double.IsFinite(settings.YFieldWidth) && settings.YFieldWidth >= 0,
            $"{Name}: the field widths must be zero (the largest field) or positive (they are {settings.XFieldWidth}, {settings.YFieldWidth}).");
        int f = Rows(settings.Field, system.Fields.Points.Count, false, "field")[0];
        int w = WavelengthRow(settings.Wavelength, system);

        bool angle = FieldTypes.IsAngle(system.Fields.Type);
        var unit = system.Units.LensUnit;
        double perUnit = angle ? 1 : OpticsConventions.ToMeters(1, unit);   // field-type unit per shown unit
        string fieldUnit = angle ? "°" : ParaxialReport.UnitName(unit);
        var scale = system.Fields.Scale;
        double radial = system.Fields.Points.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
        double wx = settings.XFieldWidth > 0 ? settings.XFieldWidth * perUnit : radial;
        double wy = settings.YFieldWidth > 0 ? settings.YFieldWidth * perUnit : radial;
        Check(radial > 0, $"{Name}: every field is on the axis, so the field normalisation boundary is a point and no grid point lies inside it; add an off-axis field.");
        if (!settings.AllowOpdLimitations && OpdEngine.Limitations(lens) is { Count: > 0 } limits)
            throw new InvalidOperationException($"{Name} refused: the OPD through this lens is approximate or undefined:" + Environment.NewLine
                + string.Join(Environment.NewLine, limits) + Environment.NewLine + "Turn on \"Allow OPD limitations\" to compute it anyway.");
        var centre = system.Fields.Points[f];

        // The grid, in field-type units; points outside the boundary or the ellipse are left out.
        var xs = FieldScanCommon.Even(nx).Select(t => centre.X - wx + 2 * wx * t).ToArray();
        var ys = FieldScanCommon.Even(ny).Select(t => centre.Y - wy + 2 * wy * t).ToArray();
        var grid = new List<(int Row, int Column, double X, double Y)>();
        int outside = 0;
        for (int i = 0; i < ny; i++)
            for (int k = 0; k < nx; k++)
            {
                double x = xs[k], y = ys[i];
                if (settings.Shape == FullFieldShape.Elliptical)
                {
                    double ex = (x - centre.X) / wx, ey = (y - centre.Y) / wy;
                    if (ex * ex + ey * ey > 1) continue;
                }
                var (hx, hy) = OpticsConventions.NormalizeField((x, y), scale);
                bool inside = system.Fields.Normalization == FieldNormalization.Radial ? hx * hx + hy * hy <= 1 : Math.Abs(hx) <= 1 && Math.Abs(hy) <= 1;
                if (!inside) { outside++; continue; }
                grid.Add((i, k, x, y));
            }
        Check(grid.Count > 0, $"{Name}: no grid point lies inside the field's normalisation boundary.");

        var data = FieldZernike.WithPoints(system, grid.Select(g => (g.X, g.Y)).ToArray());
        int first = system.Fields.Points.Count;
        var basis = new ZernikeBasis(ZernikeOrdering.Standard, settings.MaximumTerm);
        var (pupil, _) = MapNodes(settings.PupilSampling);
        var options = Options(OpdReference.ExitPupil, OpdReferencePoint.ChiefRay, false, true, settings.AllowOpdLimitations);
        var fits = new FieldZernikeFit[grid.Count];
        for (int g = 0; g < grid.Count; g++)
        {
            cancellation.ThrowIfCancellationRequested();
            fits[g] = FieldZernike.Fit(lens, data, first + g, w, basis, pupil, options, cancellation);
        }

        var values = fits.Select(x => x.Fit is null ? (double.NaN, double.NaN) : Evaluate(x, settings.Aberration, settings.ZernikeTerm)).ToArray();
        var ok = values.Where(v => double.IsFinite(v.Item1)).Select(v => v.Item1).ToArray();
        double average = ok.Length > 0 ? ok.Average() : double.NaN;
        double Shown(double v) => settings.Display switch
        {
            FullFieldDisplay.Relative => v - average,
            FullFieldDisplay.Average => double.IsFinite(v) ? average : double.NaN,
            _ => v,
        };

        var map = new double[ny, nx];
        for (int i = 0; i < ny; i++) for (int k = 0; k < nx; k++) map[i, k] = double.NaN;
        for (int g = 0; g < grid.Count; g++) map[grid[g].Row, grid[g].Column] = Shown(values[g].Item1);

        string label = Label(settings.Aberration, settings.ZernikeTerm);
        double Show(double v) => v / perUnit;
        var plot = new AnalysisMapPlot($"{label}, {settings.Display.ToString().ToLowerInvariant()}", $"X field ({fieldUnit})", $"Y field ({fieldUnit})",
            new GridField(map, Show(xs[0]), Show(xs[^1]), Show(ys[0]), Show(ys[^1])), "waves") { Style = settings.ShowAs };

        bool vector = settings.Aberration is FullFieldAberration.PrimaryAstigmatism or FullFieldAberration.PrimaryComa;
        var headers = new List<string> { $"X field ({fieldUnit})", $"Y field ({fieldUnit})", "Hx", "Hy", "Z4", "Z5", "Z6", "Z7", "Z8" };
        if (settings.Aberration == FullFieldAberration.ZernikeTerm && settings.ZernikeTerm is < 4 or > 8) headers.Add($"Z{settings.ZernikeTerm}");
        headers.Add($"{label} (waves)");
        if (vector) headers.Add("Angle (°)");
        var rows = new List<IReadOnlyList<string>>();
        var failures = new List<string>();
        for (int g = 0; g < grid.Count; g++)
        {
            var fz = fits[g];
            var cells = new List<string> { W(Show(fz.FieldX)), W(Show(fz.FieldY)), W(fz.Hx), W(fz.Hy) };
            if (fz.Fit is null)
            {
                failures.Add($"Field ({W(Show(fz.FieldX))}, {W(Show(fz.FieldY))}) {fieldUnit}: {fz.Failure}.");
                cells.AddRange(Enumerable.Repeat("failed", headers.Count - 4));
            }
            else
            {
                for (int j = 4; j <= 8; j++) cells.Add(j <= settings.MaximumTerm ? W(fz.Z(j)) : "—");
                if (settings.Aberration == FullFieldAberration.ZernikeTerm && settings.ZernikeTerm is < 4 or > 8) cells.Add(W(fz.Z(settings.ZernikeTerm)));
                cells.Add(W(Shown(values[g].Item1)));
                if (vector) cells.Add(W(values[g].Item2));
            }
            rows.Add(cells);
        }
        var sections = new List<ReportSection>
        {
            new("Zernike Standard coefficients over the field (waves)") { TableHeaders = headers, TableRows = rows },
            new("Summary")
            {
                Pairs = new (string, string)[]
                {
                    ($"Average of {label} (waves)", W(average)),
                    ("Field points computed", $"{ok.Length} of {grid.Count} inside the boundary ({outside} outside it not computed)"),
                },
                Lines = failures,
            },
        };
        var assumptions = new List<string>
        {
            $"Grid of {nx} × {ny} field points centred on field {f + 1} ({W(Show(centre.X))}, {W(Show(centre.Y))}) {fieldUnit}, half-widths {W(Show(wx))} and {W(Show(wy))} {fieldUnit}{(settings.Shape == FullFieldShape.Elliptical ? ", elliptical" : "")}; points outside the field normalisation boundary are not computed.",
            $"Zernike Standard (Noll) terms 1–{settings.MaximumTerm} fitted by least squares to the OPD on {settings.PupilSampling} × {settings.PupilSampling} pupil nodes inside the unit circle, at {W(system.Wavelengths.Wavelengths[w].Micrometers)} µm; failed rays are left out of the fit.",
            "OPD on the exit-pupil reference sphere centred on each point's chief ray (docs/Optics-Conventions.md §26); angles ½·atan2(−Z5, −Z6) and atan2(−Z7, −Z8) in degrees, as the help defines them.",
            settings.Display switch
            {
                FullFieldDisplay.Relative => "Relative: the plotted value less its average over the computed points.",
                FullFieldDisplay.Average => "Average: the average over the computed points, shown at every computed point.",
                _ => "Absolute: the value as fitted.",
            },
            "Vignetting factors are cleared for every grid point.",
            $"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.",
        };
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }
}
