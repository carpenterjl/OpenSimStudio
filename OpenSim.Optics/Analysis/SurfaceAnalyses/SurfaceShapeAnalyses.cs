using System.ComponentModel;
using System.Globalization;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using static OpenSim.Optics.Analysis.SurfaceQuantities;

namespace OpenSim.Optics.Analysis;

/// <summary>What a surface map or cross-section subtracts first (OpticStudio's "Remove"; its
/// "Base Sag" and "Composite Sag" need STAR data and composite surfaces, which this program does
/// not have).</summary>
public enum SurfaceRemove
{
    None,
    /// <summary>The sphere of the surface's vertex radius.</summary>
    BaseRadius,
    /// <summary>The best-fit sphere by the chosen criterion (<see cref="BestFitSphere"/>).</summary>
    BestFitSphere,
}

/// <summary>How a surface map is drawn (OpticStudio's "Show As": its surface plot and grey
/// scale are not built).</summary>
public enum SurfaceMapShowAs
{
    FalseColor,
    Contour,
}

/// <summary>The settings every surface map and cross-section shares.</summary>
public abstract record SurfaceShapeSettingsBase
{
    [DisplayName("Surface")]
    [Description("The surface number (1 to the image surface; not a coordinate break).")]
    public int Surface { get; init; } = 1;

    [DisplayName("Remove")]
    [Description("Subtract nothing, the sphere of the vertex radius, or the best-fit sphere first.")]
    public SurfaceRemove Remove { get; init; } = SurfaceRemove.None;

    [DisplayName("Criterion")]
    [Description("The best-fit sphere's criterion, when the best-fit sphere is removed.")]
    public BestFitSphereCriterion Criterion { get; init; } = BestFitSphereCriterion.MinimumVolume;

    [DisplayName("Reverse direction")]
    [Description("Minimum volume: take the other side of the surface as the air side.")]
    public bool ReverseDirection { get; init; }
}

/// <summary>The settings of the surface maps.</summary>
public abstract record SurfaceMapSettingsBase : SurfaceShapeSettingsBase
{
    [DisplayName("Sampling")]
    [Description("n × n points across the clear diameter; odd, 5 to 1025 (OpticStudio offers 33, 65, …).")]
    public int Sampling { get; init; } = 65;

    [DisplayName("Show as")]
    [Description("False-colour map or contour map.")]
    public SurfaceMapShowAs ShowAs { get; init; } = SurfaceMapShowAs.FalseColor;

    [DisplayName("Contour format")]
    [Description("Blank: default contours; one value: the contour interval; several values: only those contours. In the map's units.")]
    public string ContourFormat { get; init; } = "";
}

/// <summary>The settings of the cross-sections.</summary>
public abstract record SurfaceCrossSectionSettingsBase : SurfaceShapeSettingsBase
{
    [DisplayName("Sampling")]
    [Description("Points across the clear diameter; odd, 5 to 4097 (OpticStudio offers 33, 65, …).")]
    public int Sampling { get; init; } = 65;

    [DisplayName("Angle (°)")]
    [Description("The cross-section's angle from the local x axis.")]
    public double Angle { get; init; } = 90;
}

public sealed record SurfaceSagSettings : SurfaceMapSettingsBase;

public sealed record SurfaceSlopeSettings : SurfaceMapSettingsBase
{
    [DisplayName("Data")]
    [Description("Tangential, sagittal, x or y slope, or the modulus |∇z|.")]
    public SurfaceSlopeData Data { get; init; } = SurfaceSlopeData.Tangential;
}

public sealed record SurfaceCurvatureSettings : SurfaceMapSettingsBase
{
    [DisplayName("Data")]
    [Description("Tangential, sagittal, x or y curvature, or the modulus (larger principal curvature magnitude).")]
    public SurfaceCurvatureData Data { get; init; } = SurfaceCurvatureData.Tangential;
}

public sealed record SurfaceSagCrossSectionSettings : SurfaceCrossSectionSettingsBase;

public sealed record SurfaceSlopeCrossSectionSettings : SurfaceCrossSectionSettingsBase
{
    [DisplayName("Data")]
    [Description("Tangential, sagittal, x or y slope, or the modulus |∇z|.")]
    public SurfaceSlopeData Data { get; init; } = SurfaceSlopeData.Tangential;
}

public sealed record SurfaceCurvatureCrossSectionSettings : SurfaceCrossSectionSettingsBase
{
    [DisplayName("Data")]
    [Description("Tangential, sagittal, x or y curvature, or the modulus (larger principal curvature magnitude).")]
    public SurfaceCurvatureData Data { get; init; } = SurfaceCurvatureData.Tangential;
}

/// <summary>Settings of the <see cref="SagTableAnalysis"/> (the help's "Sag Table" settings).</summary>
public sealed record SagTableSettings
{
    [DisplayName("Surface")]
    [Description("The surface number (1 to the image surface; not a coordinate break).")]
    public int Surface { get; init; } = 1;

    [DisplayName("Step size")]
    [Description("The y step between rows from the vertex, lens units; 0 chooses one (the fit range's maximum over 50, rounded down to 1, 2 or 5 × 10ⁿ).")]
    public double StepSize { get; init; }

    [DisplayName("Best fit sphere criterion")]
    [Description("Minimum volume, minimum RMS with the vertices together, or minimum RMS with a vertex offset.")]
    public BestFitSphereCriterion Criterion { get; init; } = BestFitSphereCriterion.MinimumVolume;

    [DisplayName("Reverse direction")]
    [Description("Take the other side of the surface as the air side.")]
    public bool ReverseDirection { get; init; }

    [DisplayName("Minimum radius")]
    [Description("The fit's inner radius, lens units; with the maximum both 0, 0 to the clear semi-diameter.")]
    public double MinimumRadius { get; init; }

    [DisplayName("Maximum radius")]
    [Description("The fit's outer radius, lens units; with the minimum both 0, the clear semi-diameter.")]
    public double MaximumRadius { get; init; }
}

/// <summary>A sphere removed from a surface quantity: curvature (1/m) and vertex offset (m).</summary>
public sealed record SurfaceReference(double Curvature, double VertexOffset, string Description, BestFitSphereFit? Fit);

/// <summary>What a surface shape analysis resolves once: the lens, the surface, its probe and clear semi-diameter.</summary>
public sealed record SurfaceAnalysisSetup(SequentialLens Lens, OpticalSystemData System, int Index, SequentialSurface Surface,
    SurfaceProbe Probe, double SemiDiameter, bool SemiDiameterAutomatic, LensUnit Unit, List<string> Notes)
{
    public string U => ParaxialReport.UnitName(Unit);
    public double Shown(double meters) => OpticsConventions.FromMeters(meters, Unit);
    public double ShownCurvature(double perMeter) => perMeter * OpticsConventions.MetersPer(Unit);
    public double Meters(double lensUnits) => OpticsConventions.ToMeters(lensUnits, Unit);
}

/// <summary>
/// What every surface shape analysis shares (OP-43, docs/Optics-Conventions.md §28): the lens
/// from <see cref="ProjectLens"/>, the surface checked, its clear semi-diameter, and the sphere
/// to remove.
/// </summary>
public abstract class SurfaceShapeAnalysisBase<TSettings> : OpticsAnalysis<TSettings> where TSettings : class, new()
{
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    protected SurfaceShapeAnalysisBase(Func<OpticsAnalysisContext, SequentialLens?>? lensSource) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Category => "Surface";

    /// <summary>The second derivatives' difference step as a fraction of the clear semi-diameter.</summary>
    public const double DifferenceStepFraction = 1e-3;

    /// <summary>The setup, or null when the project has no surfaces.</summary>
    protected SurfaceAnalysisSetup? Prepare(OpticsAnalysisContext context, int surface, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return null;
        if (surface < 1 || surface > lens.ImageIndex)
            throw new ArgumentException($"{Name}: the surface must be 1 to {lens.ImageIndex} (it is {surface}).");
        var s = lens.Surfaces[surface];
        if (s.IsCoordinateBreak)
            throw new ArgumentException($"{Name}: surface {surface} is a coordinate break, which has no shape.");
        double sd;
        bool automatic = s.SemiDiameter is null;
        if (s.SemiDiameter is { } given) sd = given;
        else
        {
            var report = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation);
            sd = report.Clear[surface];
            notes.AddRange(report.Notes);
        }
        if (!(sd > 0) || !double.IsFinite(sd))
            throw new ArgumentException($"{Name}: surface {surface} has a clear semi-diameter of {sd} m; it must be positive to analyse its shape.");
        var probe = new SurfaceProbe(s, DifferenceStepFraction * sd);
        return new SurfaceAnalysisSetup(lens, system, surface, s, probe, sd, automatic, system.Units.LensUnit, notes);
    }

    protected OpticsAnalysisResult NoSurfaces() => OpticsAnalysisResult.FromSections(Name, new[]
    {
        new ReportSection("No surfaces")
        {
            Lines = new[] { "The system has no sequential surfaces: add them in the Lens Data Editor, so there is a surface to analyse." }
        }
    }, new[] { "Nothing is computed." });

    /// <summary>The sphere to remove, or null for none. The best-fit sphere is fitted over 0 to
    /// the clear semi-diameter.</summary>
    protected SurfaceReference? Reference(SurfaceAnalysisSetup setup, SurfaceShapeSettingsBase settings, CancellationToken cancellation)
    {
        switch (settings.Remove)
        {
            case SurfaceRemove.None: return null;
            case SurfaceRemove.BaseRadius:
                return new SurfaceReference(setup.Surface.Curvature, 0,
                    $"The sphere of the vertex radius ({Radius(setup, setup.Surface.Curvature)}) was removed.", null);
            case SurfaceRemove.BestFitSphere:
            {
                int air = BestFitSphere.AirSign(setup.Lens, setup.System, setup.Index, settings.ReverseDirection, out string airNote);
                var fit = BestFitSphere.Fit(setup.Probe, 0, setup.SemiDiameter, settings.Criterion, air,
                    $"{Name}, surface {setup.Index}", cancellation);
                setup.Notes.Add(airNote);
                return new SurfaceReference(fit.Curvature, fit.VertexOffset,
                    $"The best-fit sphere ({Criterion(settings.Criterion)}, fitted to the y profile over 0 to the clear semi-diameter) was removed: radius {Radius(setup, fit.Curvature)}, vertex offset {StudyReport.Number(setup.Shown(fit.VertexOffset), "g10")} {setup.U}.",
                    fit);
            }
            default: throw new ArgumentOutOfRangeException(nameof(settings), settings.Remove, "Unknown remove option.");
        }
    }

    internal static string Criterion(BestFitSphereCriterion c) => c switch
    {
        BestFitSphereCriterion.MinimumVolume => "minimum volume",
        BestFitSphereCriterion.MinimumRmsNoOffset => "minimum RMS, no offset",
        BestFitSphereCriterion.MinimumRmsWithOffset => "minimum RMS, with offset",
        _ => c.ToString(),
    };

    internal static string Radius(SurfaceAnalysisSetup setup, double curvature) => curvature == 0 ? "infinite"
        : $"{StudyReport.Number(setup.Shown(1 / curvature), "g10")} {setup.U}";

    /// <summary>The common assumption lines.</summary>
    protected List<string> CommonAssumptions(SurfaceAnalysisSetup setup, SurfaceReference? reference, bool secondDerivatives)
    {
        var list = new List<string>
        {
            $"Surface {setup.Index} ({setup.Probe.TypeName}), in its local frame: vertex at the origin, +z along the local axis (docs/Optics-Conventions.md §3, §28).",
            setup.SemiDiameterAutomatic
                ? $"Clear semi-diameter {StudyReport.Number(setup.Shown(setup.SemiDiameter), "g8")} {setup.U}, sized from rays (automatic)."
                : $"Clear semi-diameter {StudyReport.Number(setup.Shown(setup.SemiDiameter), "g8")} {setup.U}, as entered.",
            "Off-axis coordinates are not built: the samples cover the clear semi-diameter round the vertex, masked by the surface's aperture if it has one.",
        };
        if (secondDerivatives)
        {
            list.Add(setup.Probe.AnalyticSecondDerivatives
                ? "Curvatures from the conic's analytic second derivatives."
                : $"Curvatures from central differences of the analytic slopes, Richardson-extrapolated from h = {StudyReport.Number(setup.Shown(setup.Probe.DifferenceStep), "g4")} {setup.U} (10⁻³ of the clear semi-diameter) and h/2: truncation error −h⁴/480 times the sag's sixth derivative, rounding about 10⁻¹⁶·|slope|/h.");
        }
        if (setup.Surface.Shape is { } shape && shape is not Sequential.Aspheres.SagSurfaceShape)
            list.Add($"A {shape.TypeName} surface's slope is read from its normal, the one the trace refracts with.");
        if (reference is not null) list.Add(reference.Description);
        if (reference?.Fit is not null && !setup.Probe.IsRotationallySymmetric)
            list.Add("The best-fit sphere uses the y profile only (as OpticStudio's Sag Table does); this surface type may not be rotationally symmetric.");
        list.AddRange(setup.Notes);
        return list;
    }

    // ---- Shared sampling ----

    protected static void CheckSampling(string who, int sampling, int maximum)
    {
        if (sampling < 5 || sampling > maximum || sampling % 2 == 0)
            throw new ArgumentException($"{who}: the sampling must be odd, 5 to {maximum} (it is {sampling}).");
    }

    /// <summary>True when (x, y) is inside the clear semi-diameter and passes the surface's aperture.</summary>
    protected static bool Inside(SurfaceAnalysisSetup setup, double x, double y)
    {
        double sd = setup.SemiDiameter;
        if (x * x + y * y > sd * sd * (1 + 1e-12)) return false;
        return setup.Surface.Aperture is not { } aperture || aperture.Passes(x, y, sd);
    }
}

/// <summary>
/// OpticStudio's Sag Table (OP-43): the sag of a surface along +y from the vertex, its best-fit
/// sphere by one of three criteria, the difference, the depth to remove from the sphere, and
/// the slopes of both. Columns and figures as the help defines them (Ansys OpticStudio 2025 R2
/// help, "Sag Table", The Tolerance Tab » Manufacturing Drawings and Data Group; saved under
/// plans/sources/optics/op-43-help/Sag_Table_1.html): Y-Coord, Sag, BFS Sag, Deviation ("the
/// difference between the BFS Sag and the Sag, ignoring any vertex offset"), Remove, Slope,
/// BFS Slope, Slope Delta. The fit is <see cref="BestFitSphere"/>.
/// </summary>
public sealed class SagTableAnalysis : SurfaceShapeAnalysisBase<SagTableSettings>
{
    public const string SavedId = "SagTable";

    public SagTableAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }

    public override string Id => SavedId;
    public override string Name => "Sag table";

    /// <summary>One row, meters (slopes dimensionless).</summary>
    public sealed record Row(double Y, double Sag, double BfsSag, double Deviation, double Remove, double Slope, double BfsSlope, double SlopeDelta);

    /// <summary>The table as numbers.</summary>
    public sealed record Table(BestFitSphereFit Fit, IReadOnlyList<Row> Rows, double Step, string AirNote);

    /// <summary>The table for one surface of <paramref name="lens"/>, lengths in meters (the
    /// settings' lengths are lens units of <paramref name="unit"/>).</summary>
    public static Table Compute(SequentialLens lens, OpticalSystemData system, SurfaceProbe probe, int surface, double semiDiameter,
        SagTableSettings settings, LensUnit unit, CancellationToken cancellation = default)
    {
        string where = $"Sag table, surface {surface}";
        double rMin = OpticsConventions.ToMeters(settings.MinimumRadius, unit);
        double rMax = OpticsConventions.ToMeters(settings.MaximumRadius, unit);
        if (rMin == 0 && rMax == 0) rMax = semiDiameter;
        if (!(rMin >= 0) || !(rMax > rMin))
            throw new ArgumentException($"{where}: the radii must satisfy 0 ≤ minimum < maximum (they are {settings.MinimumRadius} and {settings.MaximumRadius} lens units).");
        if (settings.StepSize < 0 || !double.IsFinite(settings.StepSize))
            throw new ArgumentException($"{where}: the step size must be 0 (automatic) or positive (it is {settings.StepSize}).");
        int air = BestFitSphere.AirSign(lens, system, surface, settings.ReverseDirection, out string airNote);
        var fit = BestFitSphere.Fit(probe, rMin, rMax, settings.Criterion, air, where, cancellation);

        double step = settings.StepSize > 0 ? OpticsConventions.ToMeters(settings.StepSize, unit)
            : OpticsConventions.ToMeters(NiceStep(OpticsConventions.FromMeters(rMax, unit) / 50), unit);
        double count = Math.Floor(rMax / step * (1 + 1e-12));
        if (count > 100000)
            throw new ArgumentException($"{where}: a step of {settings.StepSize} lens units gives more than 100000 rows; take a larger step.");
        var ys = new List<double>();
        for (int i = 0; i <= (int)count; i++) ys.Add(Math.Min(i * step, rMax));
        if (rMax - ys[^1] > 1e-9 * rMax) ys.Add(rMax);

        var rows = new List<Row>(ys.Count);
        foreach (double y in ys)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!probe.TrySlope(0, y, out double z, out _, out double slope))
                throw new ArgumentException($"{where}: the sag or slope is not defined at y = {OpticsConventions.FromMeters(y, unit):g8} lens units.");
            double bfs = fit.SphereSag(y), bfsSlope = BestFitSphere.SphereSlope(fit.Curvature, y);
            rows.Add(new Row(y, z, bfs, bfs - z, air * (z - bfs - fit.VertexOffset), slope, bfsSlope, slope - bfsSlope));
        }
        return new Table(fit, rows, step, airNote);
    }

    /// <summary>The largest 1, 2 or 5 × 10ⁿ not above <paramref name="value"/>.</summary>
    internal static double NiceStep(double value)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double m = value / p;
        return (m >= 5 ? 5 : m >= 2 ? 2 : 1) * p;
    }

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, SagTableSettings settings, CancellationToken cancellation)
    {
        var setup = Prepare(context, settings.Surface, cancellation);
        if (setup is null) return NoSurfaces();
        var table = Compute(setup.Lens, setup.System, setup.Probe, setup.Index, setup.SemiDiameter, settings, setup.Unit, cancellation);
        var fit = table.Fit;
        string u = setup.U;
        string L(double m) => StudyReport.Number(setup.Shown(m), "g10");
        string N(double v) => StudyReport.Number(v, "g10");

        var pairs = new List<(string, string)>
        {
            ("Surface", $"{setup.Index} ({setup.Probe.TypeName})"),
            ("Clear semi-diameter", $"{L(setup.SemiDiameter)} {u}"),
            ("Fit range", $"{L(fit.MinRadius)} to {L(fit.MaxRadius)} {u}"),
            ("Method", Criterion(fit.Criterion)),
            ("Best fit sphere radius", fit.Curvature == 0 ? "infinite" : $"{L(fit.Radius)} {u}"),
            ("Best fit sphere vertex offset", $"{L(fit.VertexOffset)} {u}"),
        };
        if (fit.Criterion == BestFitSphereCriterion.MinimumVolume)
        {
            pairs.Add(("Maximum depth to remove", $"{L(fit.MaxDepthToRemove)} {u}"));
            pairs.Add(("RMS of depth to remove", $"{L(fit.RmsDepthToRemove)} {u}"));
        }
        else pairs.Add(("RMS difference", $"{L(fit.Objective)} {u}"));
        pairs.Add(("Maximum slope deviation", N(fit.MaxSlopeDeviation)));
        pairs.Add(("RMS slope deviation", N(fit.RmsSlopeDeviation)));
        if (fit.Criterion == BestFitSphereCriterion.MinimumVolume)
        {
            double perUnit = OpticsConventions.MetersPer(setup.Unit);
            pairs.Add(("Volume to remove", $"{N(fit.VolumeToRemove / (perUnit * perUnit * perUnit))} {u}³"));
        }

        var rows = table.Rows.Select(r => (IReadOnlyList<string>)new[]
        {
            L(r.Y), L(r.Sag), L(r.BfsSag), L(r.Deviation), L(r.Remove), N(r.Slope), N(r.BfsSlope), N(r.SlopeDelta),
        }).ToList();
        var plot = new LinePlot("Sag and best-fit sphere", $"Y ({u})", $"Sag ({u})", new[]
        {
            new AnalysisCurve("Sag", table.Rows.Select(r => setup.Shown(r.Y)).ToArray(), table.Rows.Select(r => setup.Shown(r.Sag)).ToArray()),
            new AnalysisCurve("BFS sag + offset", table.Rows.Select(r => setup.Shown(r.Y)).ToArray(),
                table.Rows.Select(r => setup.Shown(r.BfsSag + fit.VertexOffset)).ToArray(), Dashed: true),
        });
        var sections = new[]
        {
            new ReportSection("Best fit sphere") { Pairs = pairs },
            new ReportSection("Sag table")
            {
                TableHeaders = new[] { $"Y-coord ({u})", $"Sag ({u})", $"BFS sag ({u})", $"Deviation ({u})", $"Remove ({u})", "Slope", "BFS slope", "Slope delta" },
                TableRows = rows,
                Lines = new[]
                {
                    "Deviation is BFS sag − sag, ignoring the vertex offset; Remove is the depth from the sphere placed at its vertex offset to the surface, measured into the material (positive = material to remove).",
                    "Slopes are dz/dr along +y, dimensionless.",
                }
            },
        };
        var assumptions = CommonAssumptions(setup, null, false);
        assumptions.Add("Only the y profile x = 0, y ≥ 0 is used (the help: \"Only the Y-coordinate of the surface is considered\").");
        if (!setup.Probe.IsRotationallySymmetric)
            assumptions.Add($"A {setup.Probe.TypeName} surface may not be rotationally symmetric; the help says the sag table should not be used then.");
        assumptions.Add(table.AirNote);
        assumptions.Add($"Best-fit sphere: the y profile sampled at {BestFitSphere.ProfileIntervals + 1} equally spaced radii, integrals by Simpson's rule with the area weight 2πr; RMS values are area-weighted. Slope deviations over {BestFitSphere.SlopePoints} equally spaced y.");
        if (settings.StepSize == 0)
            assumptions.Add($"Step {L(table.Step)} {u}, chosen as the fit range's maximum over 50 rounded down to 1, 2 or 5 × 10ⁿ (OpenSim's rule; OpticStudio's automatic step is not documented).");
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>The three surface maps (Surface Sag, Surface Slope, Surface Curvature) share this.</summary>
public abstract class SurfaceMapAnalysisBase<TSettings> : SurfaceShapeAnalysisBase<TSettings> where TSettings : SurfaceMapSettingsBase, new()
{
    protected SurfaceMapAnalysisBase(Func<OpticsAnalysisContext, SequentialLens?>? lensSource) : base(lensSource) { }


    private protected abstract SurfaceQuantity QuantityOf(TSettings settings);

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, TSettings settings, CancellationToken cancellation)
    {
        var setup = Prepare(context, settings.Surface, cancellation);
        if (setup is null) return NoSurfaces();
        CheckSampling(Name, settings.Sampling, 1025);
        var q = QuantityOf(settings);
        var reference = Reference(setup, settings, cancellation);
        var sphere = reference is null ? null : SurfaceProbe.Sphere(reference.Curvature);

        int n = settings.Sampling;
        double sd = setup.SemiDiameter;
        var values = new double[n, n];
        int undefined = 0, inside = 0;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int row = 0; row < n; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            double y = row == n - 1 ? sd : -sd + 2 * sd * row / (n - 1);
            for (int col = 0; col < n; col++)
            {
                double x = col == n - 1 ? sd : -sd + 2 * sd * col / (n - 1);
                if (!Inside(setup, x, y)) { values[row, col] = double.NaN; continue; }
                inside++;
                double v = EvaluateRemoved(setup.Probe, sphere, reference, q, x, y);
                if (!double.IsFinite(v)) { undefined++; values[row, col] = double.NaN; continue; }
                v = Shown(setup, q, v);
                values[row, col] = v;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }
        double sdShown = setup.Shown(sd);
        var field = new GridField(values, -sdShown, sdShown, -sdShown, sdShown);
        string unit = UnitOf(setup, q), quantity = NameOf(q);
        var notes = new List<string>();
        var levels = settings.ShowAs == SurfaceMapShowAs.Contour ? ContourLevels(Name, settings.ContourFormat, min, max, notes) : null;
        string title = reference is null ? $"{quantity}, surface {setup.Index}" : $"{quantity} less the {(reference.Fit is null ? "base sphere" : "best-fit sphere")}, surface {setup.Index}";
        var plot = new AnalysisMapPlot(title, $"X ({setup.U})", $"Y ({setup.U})", field, unit)
        {
            Style = settings.ShowAs == SurfaceMapShowAs.Contour ? AnalysisMapStyle.Contour : AnalysisMapStyle.Image,
            Levels = levels,
        };

        string N(double v) => StudyReport.Number(v, "g10");
        var pairs = new List<(string, string)>
        {
            ("Surface", $"{setup.Index} ({setup.Probe.TypeName})"),
            ("Data", $"{quantity} ({unit})"),
            ("Sampling", $"{n} × {n} over ±{N(sdShown)} {setup.U}"),
            ("Samples inside the clear aperture", inside.ToString(CultureInfo.InvariantCulture)),
            ("Minimum", inside > undefined ? N(min) : "—"),
            ("Maximum", inside > undefined ? N(max) : "—"),
            ("Peak to valley", inside > undefined ? N(max - min) : "—"),
        };
        var lines = new List<string>();
        if (undefined > 0)
            lines.Add($"{undefined} samples inside the clear aperture could not be evaluated (the sag, the removed sphere or a difference stencil is not defined there); they are left blank.");
        var sections = new List<ReportSection> { new("Map") { Pairs = pairs, Lines = lines } };
        if (n <= 129)
        {
            var headers = new List<string> { $"Y \\ X ({setup.U})" };
            for (int col = 0; col < n; col++) headers.Add(N(field.XAt(col)));
            var rows = new List<IReadOnlyList<string>>();
            for (int row = n - 1; row >= 0; row--)
            {
                var cells = new List<string> { N(field.YAt(row)) };
                for (int col = 0; col < n; col++) cells.Add(double.IsFinite(values[row, col]) ? N(values[row, col]) : "—");
                rows.Add(cells);
            }
            sections.Add(new ReportSection("Values") { TableHeaders = headers, TableRows = rows });
        }
        else sections.Add(new ReportSection("Values") { Lines = new[] { $"The {n} × {n} values are not listed (only maps of 129 × 129 or fewer are)." } });

        var assumptions = CommonAssumptions(setup, reference, q.Kind == 2);
        assumptions.AddRange(MeaningOf(q));
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }

    /// <summary>What the quantity means, one line or none.</summary>
    internal static IEnumerable<string> MeaningOf(SurfaceQuantity q)
    {
        if (q.Kind == 1)
            yield return "Slope: dz along the named direction of the vertex plane (tangential = radial from the vertex, sagittal = at right angles to it; +x on the axis), dimensionless; the modulus is |∇z|.";
        if (q.Kind == 2)
            yield return "Curvature: the normal curvature of the surface in the named direction (tangential = radial from the vertex, sagittal = at right angles to it; +x on the axis), positive when the surface bends toward +z; the modulus is the larger magnitude of the two principal curvatures.";
    }

    /// <summary>
    /// The help's contour format string ("The Contour Format String", Ansys OpticStudio 2025 R2
    /// help, "Surface Sag"): blank for the default; one value for the interval; several
    /// values for only those contours. An interval giving more than 100 contours falls back to
    /// the default (the help: "If the specified contour settings result in too many contours to
    /// be drawn properly, the default number of contours will be selected"; 100 is this
    /// program's limit) and a note says so.
    /// </summary>
    public static IReadOnlyList<double>? ContourLevels(string who, string? format, double min, double max, ICollection<string> notes)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        var parts = format.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                throw new ArgumentException($"{who}: the contour format \"{format}\" is not a list of numbers ('{parts[i]}').");
        if (values.Length > 1) return values.OrderBy(v => v).ToArray();
        double interval = values[0];
        if (!(interval > 0))
            throw new ArgumentException($"{who}: a single contour format value is the interval and must be positive (it is {interval}).");
        if (!double.IsFinite(min) || !double.IsFinite(max)) return null;
        double first = Math.Ceiling(min / interval), last = Math.Floor(max / interval);
        if (last - first + 1 > 100)
        {
            notes.Add($"An interval of {interval} gives more than 100 contours; the default contours are drawn.");
            return null;
        }
        var levels = new List<double>();
        for (double k = first; k <= last; k++) levels.Add(k * interval);
        return levels;
    }
}

/// <summary>OpticStudio's Surface Sag (OP-43): the sag of a surface as a false-colour or contour
/// map (Ansys OpticStudio 2025 R2 help, "Surface Sag", Analyze » Surface).</summary>
public sealed class SurfaceSagAnalysis : SurfaceMapAnalysisBase<SurfaceSagSettings>
{
    public const string SavedId = "SurfaceSag";
    public SurfaceSagAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Surface sag";
    private protected override SurfaceQuantity QuantityOf(SurfaceSagSettings settings) => SurfaceQuantity.Sag;
}

/// <summary>OpticStudio's Surface Slope (OP-43): tangential, sagittal, x or y slope or its
/// modulus as a map (Ansys OpticStudio 2025 R2 help, "Surface Slope").</summary>
public sealed class SurfaceSlopeAnalysis : SurfaceMapAnalysisBase<SurfaceSlopeSettings>
{
    public const string SavedId = "SurfaceSlope";
    public SurfaceSlopeAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Surface slope";
    private protected override SurfaceQuantity QuantityOf(SurfaceSlopeSettings settings) => SurfaceQuantity.Of(settings.Data);
}

/// <summary>OpticStudio's Surface Curvature (OP-43): tangential, sagittal, x or y curvature or
/// its modulus as a map (Ansys OpticStudio 2025 R2 help, "Surface Curvature").</summary>
public sealed class SurfaceCurvatureAnalysis : SurfaceMapAnalysisBase<SurfaceCurvatureSettings>
{
    public const string SavedId = "SurfaceCurvature";
    public SurfaceCurvatureAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Surface curvature";
    private protected override SurfaceQuantity QuantityOf(SurfaceCurvatureSettings settings) => SurfaceQuantity.Of(settings.Data);
}

/// <summary>The three cross-sections share this: the quantity along a line through the vertex
/// at an angle from the local x axis, across the full clear diameter (the help's "across the
/// full clear diameter or full diameter of the surface").</summary>
public abstract class SurfaceCrossSectionAnalysisBase<TSettings> : SurfaceShapeAnalysisBase<TSettings> where TSettings : SurfaceCrossSectionSettingsBase, new()
{
    protected SurfaceCrossSectionAnalysisBase(Func<OpticsAnalysisContext, SequentialLens?>? lensSource) : base(lensSource) { }

    private protected abstract SurfaceQuantity QuantityOf(TSettings settings);

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, TSettings settings, CancellationToken cancellation)
    {
        var setup = Prepare(context, settings.Surface, cancellation);
        if (setup is null) return NoSurfaces();
        CheckSampling(Name, settings.Sampling, 4097);
        if (!double.IsFinite(settings.Angle))
            throw new ArgumentException($"{Name}: the angle must be finite (it is {settings.Angle}).");
        var q = QuantityOf(settings);
        var reference = Reference(setup, settings, cancellation);
        var sphere = reference is null ? null : SurfaceProbe.Sphere(reference.Curvature);
        var (sin, cos) = OpticsConventions.SinCosDegrees(settings.Angle);

        int n = settings.Sampling;
        double sd = setup.SemiDiameter;
        var t = new List<double>();
        var v = new List<double>();
        var rows = new List<IReadOnlyList<string>>();
        int undefined = 0;
        string N(double x) => StudyReport.Number(x, "g10");
        for (int i = 0; i < n; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            double s = i == n - 1 ? sd : -sd + 2 * sd * i / (n - 1);
            double x = s * cos, y = s * sin;
            double value = double.NaN;
            if (Inside(setup, x, y))
            {
                value = EvaluateRemoved(setup.Probe, sphere, reference, q, x, y);
                if (!double.IsFinite(value)) { undefined++; value = double.NaN; }
                else value = Shown(setup, q, value);
            }
            t.Add(setup.Shown(s));
            v.Add(value);
            rows.Add(new[] { N(setup.Shown(s)), N(setup.Shown(x)), N(setup.Shown(y)), double.IsFinite(value) ? N(value) : "—" });
        }
        string unit = UnitOf(setup, q), quantity = NameOf(q);
        var plot = new LinePlot($"{quantity}, surface {setup.Index}, at {N(settings.Angle)}°", $"Position along the section ({setup.U})",
            $"{quantity} ({unit})", new[] { new AnalysisCurve(quantity, t, v) });
        var lines = new List<string>
        {
            $"Positions along the line through the vertex at {N(settings.Angle)}° from the local x axis, positive toward that angle.",
        };
        if (undefined > 0)
            lines.Add($"{undefined} samples inside the clear aperture could not be evaluated (the sag, the removed sphere or a difference stencil is not defined there); they are left blank.");
        var sections = new[]
        {
            new ReportSection("Cross-section")
            {
                TableHeaders = new[] { $"Position ({setup.U})", $"X ({setup.U})", $"Y ({setup.U})", $"{quantity} ({unit})" },
                TableRows = rows, Lines = lines,
            },
        };
        var assumptions = CommonAssumptions(setup, reference, q.Kind == 2);
        assumptions.AddRange(SurfaceMapAnalysisBase<SurfaceSagSettings>.MeaningOf(q));
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, new AnalysisPlot[] { plot });
    }
}

/// <summary>OpticStudio's Surface Sag Cross Section (OP-43).</summary>
public sealed class SurfaceSagCrossSectionAnalysis : SurfaceCrossSectionAnalysisBase<SurfaceSagCrossSectionSettings>
{
    public const string SavedId = "SurfaceSagCrossSection";
    public SurfaceSagCrossSectionAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Surface sag cross section";
    private protected override SurfaceQuantity QuantityOf(SurfaceSagCrossSectionSettings settings) => SurfaceQuantity.Sag;
}

/// <summary>OpticStudio's Surface Slope Cross Section (OP-43).</summary>
public sealed class SurfaceSlopeCrossSectionAnalysis : SurfaceCrossSectionAnalysisBase<SurfaceSlopeCrossSectionSettings>
{
    public const string SavedId = "SurfaceSlopeCrossSection";
    public SurfaceSlopeCrossSectionAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Surface slope cross section";
    private protected override SurfaceQuantity QuantityOf(SurfaceSlopeCrossSectionSettings settings) => SurfaceQuantity.Of(settings.Data);
}

/// <summary>OpticStudio's Surface Curvature Cross Section (OP-43).</summary>
public sealed class SurfaceCurvatureCrossSectionAnalysis : SurfaceCrossSectionAnalysisBase<SurfaceCurvatureCrossSectionSettings>
{
    public const string SavedId = "SurfaceCurvatureCrossSection";
    public SurfaceCurvatureCrossSectionAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) : base(lensSource) { }
    public override string Id => SavedId;
    public override string Name => "Surface curvature cross section";
    private protected override SurfaceQuantity QuantityOf(SurfaceCurvatureCrossSectionSettings settings) => SurfaceQuantity.Of(settings.Data);
}

/// <summary>Which quantity a surface map or cross-section samples: 0 sag, 1 slope, 2 curvature.</summary>
internal readonly record struct SurfaceQuantity(int Kind, SurfaceSlopeData Slope, SurfaceCurvatureData Curvature)
{
    public static SurfaceQuantity Sag => new(0, default, default);
    public static SurfaceQuantity Of(SurfaceSlopeData d) => new(1, d, default);
    public static SurfaceQuantity Of(SurfaceCurvatureData d) => new(2, default, d);
}

/// <summary>Sampling helpers shared by the surface maps and cross-sections.</summary>
internal static class SurfaceQuantities
{
    /// <summary>The quantity at (x, y), meters, dimensionless or 1/m; NaN where not defined.</summary>
    internal static double Evaluate(SurfaceProbe probe, SurfaceQuantity q, double x, double y)
    {
        switch (q.Kind)
        {
            case 0: return probe.Sag(x, y);
            case 1:
                return probe.TrySlope(x, y, out double z, out double zx, out double zy)
                    ? SurfaceProbe.Slope(new SurfaceDerivatives(z, zx, zy, 0, 0, 0), q.Slope, x, y) : double.NaN;
            default:
                return probe.TryDerivatives(x, y, out var d) ? SurfaceProbe.Curvature(d, q.Curvature, x, y) : double.NaN;
        }
    }

    /// <summary>The quantity less the reference sphere's (the sag less the offset too); NaN
    /// where either is not defined.</summary>
    internal static double EvaluateRemoved(SurfaceProbe probe, SurfaceProbe? sphere, SurfaceReference? reference, SurfaceQuantity q,
        double x, double y)
    {
        double v = Evaluate(probe, q, x, y);
        if (sphere is null || reference is null) return v;
        double r = Evaluate(sphere, q, x, y);
        if (q.Kind == 0) r += reference.VertexOffset;
        return v - r;
    }

    /// <summary>Converts a quantity from meters or 1/m to the shown unit.</summary>
    internal static double Shown(SurfaceAnalysisSetup setup, SurfaceQuantity q, double v) => q.Kind switch
    {
        0 => setup.Shown(v),
        1 => v,
        _ => setup.ShownCurvature(v),
    };

    internal static string UnitOf(SurfaceAnalysisSetup setup, SurfaceQuantity q) => q.Kind switch
    {
        0 => setup.U,
        1 => "dimensionless",
        _ => $"1/{setup.U}",
    };

    internal static string NameOf(SurfaceQuantity q) => q.Kind switch
    {
        0 => "Sag",
        1 => q.Slope switch
        {
            SurfaceSlopeData.Modulus => "Slope modulus",
            var d => $"{d} slope",
        },
        _ => q.Curvature switch
        {
            SurfaceCurvatureData.Modulus => "Curvature modulus",
            var d => $"{d} curvature",
        },
    };

}
