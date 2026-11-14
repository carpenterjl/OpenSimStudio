using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

// OP-37: Grid Distortion.

/// <summary>Settings of the <see cref="GridDistortionAnalysis"/>.</summary>
public sealed record GridDistortionSettings
{
    [DisplayName("Grid size")]
    [Description("Points on each side of the square grid of fields; 3 to 101.")]
    public int GridSize { get; init; } = 9;

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Reference field")]
    [Description("The field the linear (ABCD) model is taken at: 0 for the axis, 1 for field 1 and so on.")]
    public int ReferenceField { get; init; }

    [DisplayName("Scale")]
    [Description("Exaggeration of the real points' departure from the grid on the plot (not in the text); may be negative.")]
    public double Scale { get; init; } = 1;

    [DisplayName("H/W aspect")]
    [Description("Height of the field grid over its width (y over x).")]
    public double Aspect { get; init; } = 1;

    [DisplayName("Symmetric magnification")]
    [Description("Force the x magnification to equal the y magnification (a rotation and a scale only).")]
    public bool SymmetricMagnification { get; init; }

    [DisplayName("Field width")]
    [Description("Full width of the x field in field units (degrees or lens units); 0 puts the grid's corner at the largest radial field.")]
    public double FieldWidth { get; init; }
}

/// <summary>One grid point.</summary>
public sealed record GridDistortionPoint
{
    /// <summary>Linear field coordinates: tangents of the field angles, or heights in meters.</summary>
    public required double Fx { get; init; }
    public required double Fy { get; init; }
    /// <summary>Predicted and real image coordinates relative to the reference field's image point, meters.</summary>
    public required double PredictedX { get; init; }
    public required double PredictedY { get; init; }
    public required double RealX { get; init; }
    public required double RealY { get; init; }
    /// <summary>100·|real − predicted|/|predicted|, negative when |real| &lt; |predicted|; NaN at the reference point.</summary>
    public required double Percent { get; init; }
    /// <summary>Null when the chief ray reached the image surface; otherwise why not.</summary>
    public string? Failure { get; init; }
}

/// <summary>The grid distortion's numbers.</summary>
public sealed record GridDistortionResult
{
    public required IReadOnlyList<GridDistortionPoint> Points { get; init; }
    /// <summary>The linear model [x_p, y_p] = [A B; C D]·[f_x, f_y], meters per field unit.</summary>
    public required double A { get; init; }
    public required double B { get; init; }
    public required double C { get; init; }
    public required double D { get; init; }
    /// <summary>The grid's half-widths in linear field units.</summary>
    public required double HalfWidthX { get; init; }
    public required double HalfWidthY { get; init; }
    /// <summary>SMIA-TV distortion, percent, or null with <see cref="SmiaTvFailure"/> saying why.</summary>
    public double? SmiaTv { get; init; }
    public string? SmiaTvFailure { get; init; }
}

/// <summary>
/// OpticStudio's Grid Distortion (Ansys OpticStudio 2025 R2 help, "Grid Distortion"), docs/Optics-
/// Conventions.md §27. The help: chief ray coordinates on the image surface "follow a linear
/// relationship with the field coordinate" [x_p; y_p] = [A B; C D]·[f_x; f_y], "where x_p and y_p
/// are the predicted image coordinates relative to a reference image point and f_x and f_y are the
/// linear coordinates on the object surface relative to a reference point. For optical systems
/// using angles as a field definition, f_x and f_y are the tangents of the field angles"; the
/// matrix comes from rays "over a very small region centered upon the reference field position";
/// "the corner of the field grid in object space" is "at the maximum radial field distance"; the
/// percent distortion is P = 100%·R_distorted/R_predicted with R_real = √(x_r² + y_r²),
/// R_predicted = √(x_p² + y_p²), R_distorted = √((x_p − x_r)² + (y_p − y_r)²), and "if R_real is
/// less than R_predicted, then the sign of P is changed to be negative"; SMIA-TV distortion is
/// P_TV = 100%·(A − B)/B, A = ½(A1 + A2), the distances between the top and bottom corners at the
/// left and right sides of the image and B the distance between the top and bottom of the image
/// at the x-field centre.
/// <para>
/// This program's readings: the matrix is from central differences at ±10⁻⁴ of the grid's
/// half-width about the reference field; "symmetric magnification" replaces it by its nearest
/// rotation-and-scale, A = D = (A + D)/2, C = −B = (C − B)/2; a ray that fails is listed, not
/// dropped, and SMIA-TV is then reported as not computed (the help lists it as zero).
/// </para>
/// </summary>
public static class GridDistortion
{
    public const double SmallStep = 1e-4;

    internal static GridDistortionResult Compute(FieldScanSetup setup, int row, GridDistortionSettings settings, CancellationToken cancellation)
    {
        const string who = "Grid distortion";
        var system = setup.System;
        var type = system.Fields.Type;
        bool angle = type == FieldType.Angle;
        if (type == FieldType.TheodoliteAngle)
            throw new ArgumentException($"{who}: theodolite fields are not linear in the field coordinates; grid distortion is built for angle and height fields. Convert the fields to Angle.");
        if (!(settings.Aspect > 0) || !double.IsFinite(settings.Aspect))
            throw new ArgumentException($"{who}: the H/W aspect must be positive (it is {settings.Aspect}).");
        if (!double.IsFinite(settings.Scale))
            throw new ArgumentException($"{who}: the scale must be a number (it is {settings.Scale}).");
        if (!double.IsFinite(settings.FieldWidth) || settings.FieldWidth < 0)
            throw new ArgumentException($"{who}: the field width must be zero or positive (it is {settings.FieldWidth}).");
        int count = system.Fields.Points.Count;
        if (settings.ReferenceField < 0 || settings.ReferenceField > count)
            throw new ArgumentException($"{who}: the reference field must be 0 (the axis) or 1 to {count} (it is {settings.ReferenceField}).");

        // Linear field coordinates and their half-widths.
        double radial = system.Fields.Points.Select(p => Math.Sqrt(p.X * p.X + p.Y * p.Y)).Max();
        double wx;
        if (settings.FieldWidth > 0)
        {
            double half = settings.FieldWidth / 2;
            if (angle && !(half < 90))
                throw new ArgumentException($"{who}: a field width of {settings.FieldWidth}° reaches 90°; grid distortion needs angles below 90°.");
            wx = angle ? Math.Tan(half * Math.PI / 180) : OpticsConventions.ToMeters(half, setup.Unit);
        }
        else
        {
            if (!(radial > 0))
                throw new ArgumentException($"{who}: every field is on the axis; add an off-axis field or give a field width.");
            if (angle && !(radial < 90))
                throw new ArgumentException($"{who}: the largest radial field is {radial}°; grid distortion needs angles below 90° (the help's limitation).");
            double r = angle ? Math.Tan(radial * Math.PI / 180) : radial;
            wx = r / Math.Sqrt(1 + settings.Aspect * settings.Aspect);
        }
        double wy = settings.Aspect * wx;
        if (angle && !(Math.Atan(Math.Sqrt(wx * wx + wy * wy)) < Math.PI / 2 * (89.999 / 90)))
            throw new ArgumentException($"{who}: the grid's corner reaches 90°; reduce the field width or the aspect.");

        var trace = setup.Trace(row);
        var launcher = setup.Launcher;
        var scale = system.Fields.Scale;
        if (!(scale.X > 0))
            throw new ArgumentException($"{who}: every field is on the axis, so the fields cannot be normalised; add an off-axis field.");
        string wl = FieldScanCommon.WavelengthLabel(system, row);

        Ray Chief(double fx, double fy)
        {
            cancellation.ThrowIfCancellationRequested();
            var field = angle ? (Math.Atan(fx) * 180 / Math.PI, Math.Atan(fy) * 180 / Math.PI) : (fx, fy);
            var (hx, hy) = OpticsConventions.NormalizeField(field, scale);
            return trace.Trace(launcher.Launch(trace, hx, hy, 0, 0));
        }

        double refX = 0, refY = 0;
        if (settings.ReferenceField > 0)
        {
            var p = system.Fields.Points[settings.ReferenceField - 1];
            refX = angle ? Math.Tan(p.X * Math.PI / 180) : p.X;
            refY = angle ? Math.Tan(p.Y * Math.PI / 180) : p.Y;
        }
        var origin = Chief(refX, refY);
        double step = SmallStep * Math.Max(wx, wy);
        var xp = Chief(refX + step, refY); var xm = Chief(refX - step, refY);
        var yp = Chief(refX, refY + step); var ym = Chief(refX, refY - step);
        foreach (var r in new[] { origin, xp, xm, yp, ym })
            if (!r.IsOk)
                throw new InvalidOperationException($"{who}: a chief ray at the reference field ({wl}) does not reach the image surface ({FieldScanCommon.Failure(r)}), so the linear model cannot be found.");
        double a = (xp.Position.X - xm.Position.X) / (2 * step), b = (yp.Position.X - ym.Position.X) / (2 * step);
        double c = (xp.Position.Y - xm.Position.Y) / (2 * step), d = (yp.Position.Y - ym.Position.Y) / (2 * step);
        if (settings.SymmetricMagnification)
        {
            double m = (a + d) / 2, rot = (c - b) / 2;
            (a, b, c, d) = (m, -rot, rot, m);
        }
        if (!(Math.Abs(a * d - b * c) > 0))
            throw new InvalidOperationException($"{who}: the linear model of the image against the field is singular ({wl}); the image does not change in two independent directions with field.");

        int n = settings.GridSize;
        var points = new List<GridDistortionPoint>();
        for (int i = 0; i < n; i++)
        {
            double fy = wy - 2 * wy * i / (n - 1);
            for (int k = 0; k < n; k++)
            {
                double fx = -wx + 2 * wx * k / (n - 1);
                double px = a * (fx - refX) + b * (fy - refY), py = c * (fx - refX) + d * (fy - refY);
                var ray = Chief(fx, fy);
                if (!ray.IsOk)
                {
                    points.Add(new GridDistortionPoint { Fx = fx, Fy = fy, PredictedX = px, PredictedY = py, RealX = double.NaN, RealY = double.NaN, Percent = double.NaN, Failure = FieldScanCommon.Failure(ray) });
                    continue;
                }
                double rx = ray.Position.X - origin.Position.X, ry = ray.Position.Y - origin.Position.Y;
                double rReal = Math.Sqrt(rx * rx + ry * ry), rPred = Math.Sqrt(px * px + py * py);
                double rDist = Math.Sqrt((px - rx) * (px - rx) + (py - ry) * (py - ry));
                double pct = rPred > 0 ? 100 * rDist / rPred * (rReal < rPred ? -1 : 1) : double.NaN;
                points.Add(new GridDistortionPoint { Fx = fx, Fy = fy, PredictedX = px, PredictedY = py, RealX = rx, RealY = ry, Percent = pct });
            }
        }

        // SMIA-TV: corners and the top and bottom of the centre column.
        double? smia = null;
        string? smiaFailure = null;
        var corners = new[] { Chief(-wx, wy), Chief(-wx, -wy), Chief(wx, wy), Chief(wx, -wy), Chief(0, wy), Chief(0, -wy) };
        if (corners.Any(r => !r.IsOk))
            smiaFailure = $"not computed: a corner or edge chief ray failed ({FieldScanCommon.Failure(corners.First(r => !r.IsOk))}).";
        else
        {
            static double Dist(Ray p, Ray q) => Math.Sqrt((p.Position.X - q.Position.X) * (p.Position.X - q.Position.X) + (p.Position.Y - q.Position.Y) * (p.Position.Y - q.Position.Y));
            double a1 = Dist(corners[0], corners[1]), a2 = Dist(corners[2], corners[3]), bb = Dist(corners[4], corners[5]);
            smia = 100 * ((a1 + a2) / 2 - bb) / bb;
        }
        return new GridDistortionResult
        {
            Points = points, A = a, B = b, C = c, D = d, HalfWidthX = wx, HalfWidthY = wy, SmiaTv = smia, SmiaTvFailure = smiaFailure,
        };
    }
}

/// <summary>OpticStudio's Grid Distortion (OP-37); see <see cref="GridDistortion"/>. The help's
/// "Display" choice (crosses or vectors) is not built: the plot shows the grid and the real points
/// as two point series.</summary>
public sealed class GridDistortionAnalysis : OpticsAnalysis<GridDistortionSettings>
{
    public const string SavedId = "GridDistortion";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public GridDistortionAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Grid distortion";
    public override string Category => "Aberrations";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, GridDistortionSettings settings, CancellationToken cancellation)
    {
        const string who = "Grid distortion";
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return FieldScanCommon.NoSurfaces(Name);
        FieldScanCommon.CheckCount(settings.GridSize, 3, 101, who, "grid size");
        FieldScanCommon.RefuseAfocal(system, who);
        int row = FieldScanCommon.Rows(settings.Wavelength, system, who, zeroIsAll: false)[0];
        system = FieldScanCommon.ScanSystem(VignettingTools.Clear(system), realImageHeightAsParaxial: true, notes);
        var setup = FieldScanCommon.Prepare(lens, system, notes, cancellation);
        var r = GridDistortion.Compute(setup, row, settings, cancellation);

        bool angle = system.Fields.Type == FieldType.Angle;
        string u = setup.UnitName;
        string fu = angle ? "tan θ" : u;
        double F(double v) => angle ? v : setup.Shown(v);
        var rows = r.Points.Select(p => (IReadOnlyList<string>)new[]
        {
            FieldScanCommon.N(F(p.Fx), "g6"), FieldScanCommon.N(F(p.Fy), "g6"),
            FieldScanCommon.N(setup.Shown(p.PredictedX)), FieldScanCommon.N(setup.Shown(p.PredictedY)),
            p.Failure ?? FieldScanCommon.N(setup.Shown(p.RealX)), p.Failure is null ? FieldScanCommon.N(setup.Shown(p.RealY)) : "—",
            FieldScanCommon.N(p.Percent, "g6"),
        }).ToList();

        // The plot is in field space: the grid, and each real point taken back through the
        // inverse of the linear model, its departure from its grid point multiplied by the scale.
        double det = r.A * r.D - r.B * r.C;
        var grid = new List<AnalysisPoint>();
        var real = new List<AnalysisPoint>();
        double refX = 0, refY = 0;
        if (settings.ReferenceField > 0)
        {
            var fp = system.Fields.Points[settings.ReferenceField - 1];
            refX = angle ? Math.Tan(fp.X * Math.PI / 180) : fp.X;
            refY = angle ? Math.Tan(fp.Y * Math.PI / 180) : fp.Y;
        }
        foreach (var p in r.Points)
        {
            grid.Add(new AnalysisPoint(F(p.Fx), F(p.Fy), ""));
            if (p.Failure is not null) continue;
            double ox = refX + (r.D * p.RealX - r.B * p.RealY) / det, oy = refY + (-r.C * p.RealX + r.A * p.RealY) / det;
            real.Add(new AnalysisPoint(F(p.Fx + settings.Scale * (ox - p.Fx)), F(p.Fy + settings.Scale * (oy - p.Fy)), ""));
        }
        var plot = new AnalysisScatterPlot("Grid distortion", $"X field ({fu})", $"Y field ({fu})", new[]
        {
            new AnalysisScatterSeries("Linear grid", grid),
            new AnalysisScatterSeries("Real chief rays", real),
        });
        var finite = r.Points.Where(p => double.IsFinite(p.Percent)).ToArray();
        var worst = finite.Length == 0 ? double.NaN : finite.MaxBy(p => Math.Abs(p.Percent))!.Percent;
        double unitScale = angle ? 1 : OpticsConventions.MetersPer(setup.Unit);
        var sections = new[]
        {
            new ReportSection("Linear model")
            {
                Pairs = new[]
                {
                    ("Wavelength", $"{row + 1} ({FieldScanCommon.WavelengthLabel(system, row)})"),
                    ("Reference field", settings.ReferenceField == 0 ? "axis" : settings.ReferenceField.ToString()),
                    ($"A, B ({u} per {fu})", $"{FieldScanCommon.N(setup.Shown(r.A) * unitScale)}, {FieldScanCommon.N(setup.Shown(r.B) * unitScale)}"),
                    ($"C, D ({u} per {fu})", $"{FieldScanCommon.N(setup.Shown(r.C) * unitScale)}, {FieldScanCommon.N(setup.Shown(r.D) * unitScale)}"),
                    ("Grid half-widths", $"{FieldScanCommon.N(F(r.HalfWidthX), "g6")}, {FieldScanCommon.N(F(r.HalfWidthY), "g6")} {fu}"),
                    ("Largest distortion", $"{FieldScanCommon.N(worst, "g6")} %"),
                    ("SMIA-TV distortion", r.SmiaTv is { } s ? $"{FieldScanCommon.N(s, "g6")} %" : r.SmiaTvFailure!),
                },
            },
            new ReportSection("Grid")
            {
                TableHeaders = new[] { $"X field ({fu})", $"Y field ({fu})", $"Predicted x ({u})", $"Predicted y ({u})", $"Real x ({u})", $"Real y ({u})", "Distortion (%)" },
                TableRows = rows,
                Lines = new[] { "Image coordinates are relative to the reference field's real chief ray on the image surface." },
            },
        };
        var assumptions = new List<string>
        {
            $"Linear field coordinates: {(angle ? "tangents of the field angles" : "field heights")}; the linear model by central differences at ±{GridDistortion.SmallStep:g2} of the grid's half-width about the reference field{(settings.SymmetricMagnification ? ", made a rotation and scale (symmetric magnification)" : "")}.",
            "Vignetting factors are ignored (cleared): chief rays go through the pupil centre.",
            $"The plot is in field space; the real points' departures from the grid are multiplied by {settings.Scale:g6}.",
            $"Ray aiming: {RayAimingModes.Names.Label(system.RayAiming.Mode)}.",
        };
        assumptions.AddRange(setup.Launcher.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }
}
