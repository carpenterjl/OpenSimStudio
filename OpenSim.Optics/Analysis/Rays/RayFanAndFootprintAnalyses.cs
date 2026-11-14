using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using static OpenSim.Optics.Analysis.RayAnalysisSupport;

namespace OpenSim.Optics.Analysis;

/// <summary>Which component of the transverse aberration a ray fan plots.</summary>
public enum FanComponent
{
    /// <summary>EY, the y component.</summary>
    Y = 0,
    /// <summary>EX, the x component.</summary>
    X = 1,
}

/// <summary>Settings of the <see cref="RayAberrationAnalysis"/>.</summary>
public sealed record RayAberrationSettings
{
    [DisplayName("Number of rays")]
    [Description("Rays on each side of the origin of each fan; at least 1.")]
    public int RaysEachSide { get; init; } = 20;

    [DisplayName("Field")]
    [Description("Row of the field table, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Tangential")]
    [Description("The component plotted against Py (EY by default).")]
    public FanComponent Tangential { get; init; } = FanComponent.Y;

    [DisplayName("Sagittal")]
    [Description("The component plotted against Px (EX by default).")]
    public FanComponent Sagittal { get; init; } = FanComponent.X;

    [DisplayName("Check apertures")]
    [Description("Rays stopped by a surface aperture are not drawn; off traces through every aperture.")]
    public bool CheckApertures { get; init; } = true;
}

/// <summary>
/// OpticStudio's Ray Aberration plot, the ray fan (OP-34; Ansys OpticStudio 2025 R2 help, "Ray
/// Aberration (rays and spots)"): for each field, the tangential fan (pupil (0, Py)) and the
/// sagittal fan (pupil (Px, 0)), the transverse aberration on the image surface against the
/// normalised pupil coordinate, in µm. "The data being plotted is the difference between the ray
/// intercept coordinate and the chief ray intercept coordinate"; "If 'All' wavelengths are shown,
/// then the plot is referenced to the primary wavelength chief ray. If monochromatic, then the
/// chief ray for the selected wavelength is used". A ray that fails is left out of its curve and
/// counted.
/// </summary>
public sealed class RayAberrationAnalysis : OpticsAnalysis<RayAberrationSettings>
{
    public const string SavedId = "RayAberration";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public RayAberrationAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Ray aberration (ray fan)";
    public override string Category => "Rays and spots";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, RayAberrationSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        CheckDensity(Name, settings.RaysEachSide, 1, "number of rays");
        var fields = Fields(Name, settings.Field, system);
        var waves = Wavelengths(Name, settings.Wavelength, system);
        var fan = PupilSampling.Fan(settings.RaysEachSide);
        var pupil = fan.Select(p => (0.0, p)).Concat(fan.Select(p => (p, 0.0))).ToArray();
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest
        {
            Fields = fields, Wavelengths = waves, Pupil = pupil, IgnoreApertures = !settings.CheckApertures,
        }, cancellation);
        int n = fan.Count;
        bool mono = waves.Count == 1;
        string tName = settings.Tangential == FanComponent.Y ? "EY" : "EX";
        string sName = settings.Sagittal == FanComponent.Y ? "EY" : "EX";

        var plots = new List<AnalysisPlot>();
        var sections = new List<ReportSection>();
        var summary = new List<IReadOnlyList<string>>();
        for (int fi = 0; fi < fields.Count; fi++)
        {
            cancellation.ThrowIfCancellationRequested();
            string label = FieldLabel(system, fields[fi]);
            var chief = mono ? sample.ChiefAt(fi, 0) : sample.PrimaryChiefs[fi];
            if (chief.AtSurface is null)
            {
                sections.Add(new ReportSection(label)
                {
                    Lines = new[] { $"No fan: the reference chief ray failed ({RayStatuses.Label(chief.Final).ToLowerInvariant()} at surface {chief.FinalSurface}), apertures ignored." }
                });
                continue;
            }
            var tCurves = new List<AnalysisCurve>();
            var sCurves = new List<AnalysisCurve>();
            var tableRows = new List<IReadOnlyList<string>>();
            var values = new double[waves.Count, 2 * n];
            for (int wi = 0; wi < waves.Count; wi++)
            {
                var tx = new List<double>(); var ty = new List<double>();
                var sx = new List<double>(); var sy = new List<double>();
                for (int p = 0; p < 2 * n; p++)
                {
                    var ray = sample.At(fi, wi, p);
                    bool tangential = p < n;
                    double e = double.NaN;
                    if (ray.Passed)
                    {
                        var component = tangential ? settings.Tangential : settings.Sagittal;
                        e = (component == FanComponent.Y ? ray.Position.Y - chief.Position.Y : ray.Position.X - chief.Position.X) * MicrometersPerMeter;
                        (tangential ? tx : sx).Add(tangential ? ray.Py : ray.Px);
                        (tangential ? ty : sy).Add(e);
                    }
                    values[wi, p] = e;
                }
                string wl = WaveLabel(system, waves[wi]);
                tCurves.Add(new AnalysisCurve(wl, tx, ty));
                sCurves.Add(new AnalysisCurve(wl, sx, sy));
                var failures = RaySampler.DescribeFailures(sample.Set(fi, wi));
                summary.Add(new[]
                {
                    label, wl,
                    Max(values, wi, 0, n), Max(values, wi, n, 2 * n),
                    failures.Count == 0 ? "none" : string.Join("; ", failures),
                });
            }
            for (int k = 0; k < n; k++)
            {
                var row = new List<string> { StudyReport.Number(fan[k], "g6") };
                for (int wi = 0; wi < waves.Count; wi++)
                {
                    row.Add(Cell(values[wi, k]));
                    row.Add(Cell(values[wi, n + k]));
                }
                tableRows.Add(row);
            }
            var headers = new List<string> { "Pupil" };
            foreach (int w in waves)
            {
                headers.Add($"{tName} tangential, {WaveLabel(system, w)} (µm)");
                headers.Add($"{sName} sagittal, {WaveLabel(system, w)} (µm)");
            }
            sections.Add(new ReportSection(label) { TableHeaders = headers, TableRows = tableRows, Lines = new[] { "— marks a ray that failed." } });
            plots.Add(new LinePlot($"{label}: tangential fan", "Py", $"{tName} (µm)", tCurves));
            plots.Add(new LinePlot($"{label}: sagittal fan", "Px", $"{sName} (µm)", sCurves));
        }
        sections.Insert(0, new ReportSection("Summary")
        {
            TableHeaders = new[] { "Field", "Wavelength", $"Largest |{tName}| tangential (µm)", $"Largest |{sName}| sagittal (µm)", "Rays not drawn" },
            TableRows = summary,
        });
        var assumptions = new List<string>
        {
            $"{n} rays per fan, pupil coordinates k/{settings.RaysEachSide}, k = −{settings.RaysEachSide} … {settings.RaysEachSide}; the tangential fan is (0, Py), the sagittal (Px, 0), in normalised pupil coordinates after any vignetting factors remap them.",
            mono ? "Aberrations are relative to the chief ray of the wavelength shown, on the image surface's local axes."
                : "Aberrations are relative to the primary wavelength's chief ray, on the image surface's local axes.",
            "The reference chief ray is traced with apertures ignored.",
            settings.CheckApertures ? "Apertures are checked: rays stopped by an aperture are left out of the curves and counted."
                : "Apertures are NOT checked: rays pass through every aperture.",
            "The fan is evaluated on the image surface only; OpticStudio's evaluation at intermediate surfaces and its vignetted-pupil scaling are not built.",
        };
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(sample.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }

    private static string Cell(double v) => double.IsNaN(v) ? "—" : StudyReport.Number(v, "g8");

    private static string Max(double[,] values, int w, int from, int to)
    {
        double max = double.NaN;
        for (int p = from; p < to; p++)
            if (!double.IsNaN(values[w, p])) max = double.IsNaN(max) ? Math.Abs(values[w, p]) : Math.Max(max, Math.Abs(values[w, p]));
        return Cell(max);
    }
}

/// <summary>Settings of the <see cref="FootprintDiagramAnalysis"/>.</summary>
public sealed record FootprintDiagramSettings
{
    [DisplayName("Ray density")]
    [Description("n: a (2n + 1) × (2n + 1) grid of rays, those inside the pupil traced; at least 1.")]
    public int RayDensity { get; init; } = 10;

    [DisplayName("Surface")]
    [Description("The surface to show the beam footprint on; 0 for the image surface.")]
    public int Surface { get; init; } = 1;

    [DisplayName("Field")]
    [Description("Row of the field table, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Delete vignetted")]
    [Description("Leave out rays stopped at this surface or a later one.")]
    public bool DeleteVignetted { get; init; }
}

/// <summary>
/// OpticStudio's Footprint Diagram (OP-34; help, "Footprint Diagram"): where a grid of rays
/// meets a surface, in its local frame and lens units, over the circle of its semi-diameter.
/// "Rays which are vignetted by surfaces prior to the surface shown are not drawn. Rays which
/// are vignetted by the surface or subsequent surfaces are not drawn if 'delete vignetted' is
/// checked, otherwise, they are drawn"; "The number of rays shown divided by the total number of
/// rays launched is listed as the percentage of rays through".
/// </summary>
public sealed class FootprintDiagramAnalysis : OpticsAnalysis<FootprintDiagramSettings>
{
    public const string SavedId = "FootprintDiagram";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public FootprintDiagramAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Footprint diagram";
    public override string Category => "Rays and spots";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, FootprintDiagramSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        CheckDensity(Name, settings.RayDensity, 1, "ray density");
        var fields = Fields(Name, settings.Field, system);
        var waves = Wavelengths(Name, settings.Wavelength, system);
        int surface = Surface(Name, settings.Surface, lens);
        var pupil = PupilSampling.HalfPupilGrid(settings.RayDensity);
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest { Fields = fields, Wavelengths = waves, Pupil = pupil, Surface = surface }, cancellation);
        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        double perMeter = OpticsConventions.FromMeters(1, unit);

        var rows = new List<IReadOnlyList<string>>();
        var series = new List<AnalysisScatterSeries>();
        var lines = new List<string>();
        int shownTotal = 0, launchedTotal = 0;
        double maxRadius = 0;
        for (int fi = 0; fi < fields.Count; fi++)
        {
            string label = FieldLabel(system, fields[fi]);
            var pts = new List<AnalysisPoint>();
            for (int wi = 0; wi < waves.Count; wi++)
            {
                cancellation.ThrowIfCancellationRequested();
                int shown = 0;
                double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity, r = 0;
                foreach (var ray in sample.Set(fi, wi))
                {
                    if (!Shown(ray, settings.DeleteVignetted)) continue;
                    shown++;
                    double x = ray.Position.X, y = ray.Position.Y;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    r = Math.Max(r, Math.Sqrt(x * x + y * y));
                    pts.Add(new AnalysisPoint(x * perMeter, y * perMeter, ""));
                }
                shownTotal += shown;
                launchedTotal += pupil.Count;
                maxRadius = Math.Max(maxRadius, r);
                string wl = WaveLabel(system, waves[wi]);
                rows.Add(shown == 0
                    ? new[] { label, wl, $"0 of {pupil.Count}", "—", "—", "—", "—", "—" }
                    : new[] { label, wl, $"{shown} of {pupil.Count}", Lens(minX, unit), Lens(maxX, unit), Lens(minY, unit), Lens(maxY, unit), Lens(r, unit) });
                var failures = RaySampler.DescribeFailures(sample.Set(fi, wi), countLaterFailures: settings.DeleteVignetted);
                if (failures.Count > 0) lines.Add($"{label}, {wl}: not drawn: {string.Join("; ", failures)}.");
            }
            series.Add(new AnalysisScatterSeries(label, pts));
        }

        double semi = lens.Surfaces[surface].SemiDiameter ?? double.NaN;
        string semiSource = "the surface's semi-diameter";
        if (!double.IsFinite(semi))
        {
            semi = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear[surface];
            semiSource = "the automatic clear semi-diameter";
        }
        if (double.IsFinite(semi) && semi > 0)
            series.Add(new AnalysisScatterSeries("Semi-diameter", Circle(0, 0, semi * perMeter)));
        double percent = launchedTotal == 0 ? 0 : 100.0 * shownTotal / launchedTotal;
        lines.Insert(0, $"Rays through: {StudyReport.Number(percent, "g6")} % ({shownTotal} of {launchedTotal} launched). Largest radial height of a drawn ray: {Lens(maxRadius, unit)} {u}; {semiSource}: {(double.IsFinite(semi) ? Lens(semi, unit) + " " + u : "none")}.");
        var plots = new List<AnalysisPlot>
        {
            new AnalysisScatterPlot($"Footprint on {SurfaceName(lens, surface)}: {StudyReport.Number(percent, "g4")} % through", $"X ({u})", $"Y ({u})", series),
        };
        var sections = new[]
        {
            new ReportSection("Footprint")
            {
                TableHeaders = new[] { "Field", "Wavelength", "Rays drawn", $"Min X ({u})", $"Max X ({u})", $"Min Y ({u})", $"Max Y ({u})", $"Largest radius ({u})" },
                TableRows = rows, Lines = lines,
            },
        };
        var assumptions = new List<string>
        {
            $"A (2n + 1) × (2n + 1) grid, n = {settings.RayDensity}, the {pupil.Count} points (i/n, k/n) inside the unit pupil, per field and wavelength.",
            $"Positions on {SurfaceName(lens, surface)}, in its local frame. Rays stopped before it are never drawn; " + (settings.DeleteVignetted
                ? "rays stopped at it or later are left out (delete vignetted)." : "rays stopped at it (where they meet it) or later are drawn."),
            lens.Surfaces[surface].Aperture is null ? "The circle is the semi-diameter." : "The circle is the semi-diameter; the surface's own aperture shape is not drawn (not built).",
            "The help's \"Ring\" density (360 marginal rays, the unvignetted radius found automatically) is not built.",
        };
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(sample.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }

    /// <summary>Drawn: the ray met the surface (passed it, or stopped there by its aperture), and
    /// with "delete vignetted" passed it and every later surface.</summary>
    private static bool Shown(SampledRay ray, bool deleteVignetted) =>
        ray.AtSurface is not null && (deleteVignetted ? ray.Passed && ray.Final == RayStatus.Ok : true);
}

/// <summary>Settings of the <see cref="YYbarAnalysis"/>.</summary>
public sealed record YYbarSettings
{
    [DisplayName("First surface")]
    [Description("The first surface plotted; at least 1.")]
    public int FirstSurface { get; init; } = 1;

    [DisplayName("Last surface")]
    [Description("The last surface plotted; 0 for the image surface.")]
    public int LastSurface { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }
}

/// <summary>
/// OpticStudio's Y-Ybar Drawing, the Delano diagram (OP-34; help, "Y-Ybar Drawing": "a plot of
/// marginal ray height as a function of chief ray height ... at every surface in the lens"): the
/// paraxial marginal ray height y against the full-field paraxial chief ray height ȳ at each
/// surface (<see cref="ParaxialSystem.MarginalRay"/>, <see cref="ParaxialSystem.ChiefRay"/>).
/// </summary>
public sealed class YYbarAnalysis : OpticsAnalysis<YYbarSettings>
{
    public const string SavedId = "YYbarDrawing";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public YYbarAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Y-Ybar drawing";
    public override string Category => "Rays and spots";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, YYbarSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        int wave = OneWavelength(Name, settings.Wavelength, system);
        int last = Surface(Name, settings.LastSurface, lens);
        if (settings.FirstSurface < 1 || settings.FirstSurface > last)
            throw new ArgumentException($"{Name}: the first surface must be 1 to the last surface {last} (it is {settings.FirstSurface}).");
        var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), system, wave);
        cancellation.ThrowIfCancellationRequested();
        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        double perMeter = OpticsConventions.FromMeters(1, unit);
        var x = new List<double>(); var y = new List<double>();
        var rows = new List<IReadOnlyList<string>>();
        for (int j = settings.FirstSurface; j <= last; j++)
        {
            double yb = paraxial.ChiefRay.Heights[j], ym = paraxial.MarginalRay.Heights[j];
            x.Add(yb * perMeter); y.Add(ym * perMeter);
            rows.Add(new[] { j == lens.ImageIndex ? "IMA" : j == lens.StopIndex ? $"{j} (stop)" : j.ToString(), Lens(ym, unit), Lens(yb, unit) });
        }
        var plots = new AnalysisPlot[] { new LinePlot("Y-Ybar", $"Chief ray height ȳ ({u})", $"Marginal ray height y ({u})", new[] { new AnalysisCurve("Surfaces", x, y) }) };
        var sections = new[]
        {
            new ReportSection("Paraxial heights")
            {
                TableHeaders = new[] { "Surface", $"Marginal y ({u})", $"Chief ȳ ({u})" },
                TableRows = rows,
            },
        };
        var assumptions = new List<string>
        {
            $"Paraxial marginal ray and the paraxial chief ray of the full field (Hy = 1) at {WaveLabel(system, wave)}, from the paraxial engine (docs/Optics-Conventions.md §11), in the y–z plane.",
            "The help's paraxial skew ray is read as this pair, which it equals for a rotationally symmetric system; tilts and decenters are not in the paraxial model.",
        };
        if (system.Fields.Points.All(p => p.X == 0 && p.Y == 0)) assumptions.Add("Every field is on the axis, so the chief ray heights are all zero.");
        assumptions.AddRange(paraxial.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}

/// <summary>Settings of the <see cref="IncidentAngleAnalysis"/>.</summary>
public sealed record IncidentAngleSettings
{
    [DisplayName("Field density")]
    [Description("Fields from the axis to the largest +y field, evenly spaced; at least 2.")]
    public int FieldDensity { get; init; } = 11;

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }
}

/// <summary>
/// OpticStudio's Incident Angle vs. Image Height (OP-34; help, "Incident Angle vs. Image
/// Height"): the "(signed) angle of incidence on the image surface (which is assumed to be
/// flat)" of the lower marginal (0, −1), chief (0, 0) and upper marginal (0, 1) rays at fields
/// Hy from 0 to 1, against the chief ray's image height. Here the signed angle is
/// atan2(M, |N|) of the ray's direction in the image surface's local frame, degrees: positive
/// when the ray travels toward +y.
/// </summary>
public sealed class IncidentAngleAnalysis : OpticsAnalysis<IncidentAngleSettings>
{
    public const string SavedId = "IncidentAngleVsImageHeight";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public IncidentAngleAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Incident angle vs. image height";
    public override string Category => "Rays and spots";

    /// <summary>The signed angle of a direction on a flat surface, degrees: atan2(M, |N|).</summary>
    public static double SignedAngleDegrees(OpenSim.Core.Numerics.Vector3D direction) =>
        Math.Atan2(direction.Y, Math.Abs(direction.Z)) * (180 / Math.PI);

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, IncidentAngleSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        CheckDensity(Name, settings.FieldDensity, 2, "field density");
        int wave = OneWavelength(Name, settings.Wavelength, system);
        IReadOnlyList<double>? semiDiameters = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semiDiameters = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        var trace = SequentialTrace.Create(lens, system, wave, semiDiameters);
        var launcher = new RayLauncher(lens, system);
        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        double perMeter = OpticsConventions.FromMeters(1, unit);
        var names = new[] { "Lower marginal", "Chief", "Upper marginal" };
        var pys = new[] { -1.0, 0.0, 1.0 };
        var xs = names.Select(_ => new List<double>()).ToArray();
        var ys = names.Select(_ => new List<double>()).ToArray();
        var rows = new List<IReadOnlyList<string>>();
        var lines = new List<string>();
        for (int i = 0; i < settings.FieldDensity; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            double hy = (double)i / (settings.FieldDensity - 1);
            var rays = pys.Select(py => trace.Trace(launcher.Launch(trace, 0, hy, 0, py))).ToArray();
            string h = StudyReport.Number(hy, "g6");
            if (!rays[1].IsOk)
            {
                rows.Add(new[] { h, "—", "—", "—", "—" });
                lines.Add($"Hy = {h}: the chief ray failed ({RayStatuses.Label(rays[1].Status).ToLowerInvariant()} at surface {rays[1].FailedAtSurface}), so there is no image height.");
                continue;
            }
            double height = rays[1].Position.Y;
            var row = new List<string> { h, Lens(height, unit) };
            for (int k = 0; k < 3; k++)
            {
                if (!rays[k].IsOk)
                {
                    row.Add("—");
                    lines.Add($"Hy = {h}: the {names[k].ToLowerInvariant()} ray failed ({RayStatuses.Label(rays[k].Status).ToLowerInvariant()} at surface {rays[k].FailedAtSurface}).");
                    continue;
                }
                double angle = SignedAngleDegrees(rays[k].Direction);
                xs[k].Add(height * perMeter); ys[k].Add(angle);
                row.Add(StudyReport.Number(angle, "g8"));
            }
            rows.Add(row);
        }
        if (lines.Count == 0) lines.Add("Every ray reached the image surface.");
        var plots = new AnalysisPlot[]
        {
            new LinePlot("Incident angle vs. image height", $"Image height ({u})", "Angle of incidence (°)",
                names.Select((n, k) => new AnalysisCurve(n, xs[k], ys[k])).ToArray()),
        };
        var sections = new[]
        {
            new ReportSection("Angles")
            {
                TableHeaders = new[] { "Hy", $"Image height ({u})", "Lower marginal (°)", "Chief (°)", "Upper marginal (°)" },
                TableRows = rows, Lines = lines,
            },
        };
        var assumptions = new List<string>
        {
            $"{settings.FieldDensity} fields Hy = 0 … 1 (Hx = 0), {WaveLabel(system, wave)}; pupil points (0, −1), (0, 0), (0, 1); apertures checked.",
            "Angle = atan2(M, |N|) of the ray in the image surface's local frame, as if the image surface were flat (help); image height is the chief ray's local y.",
            "The help: meant for rotationally symmetric systems without vignetting.",
        };
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(launcher.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}
