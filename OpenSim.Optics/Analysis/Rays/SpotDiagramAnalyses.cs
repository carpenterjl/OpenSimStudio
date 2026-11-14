using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis.Rays;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using static OpenSim.Optics.Analysis.RayAnalysisSupport;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="StandardSpotAnalysis"/>.</summary>
public sealed record StandardSpotSettings
{
    [DisplayName("Pattern")]
    [Description("Hexapolar, square or dithered rays on the pupil.")]
    public PupilPattern Pattern { get; init; } = PupilPattern.Hexapolar;

    [DisplayName("Ray density")]
    [Description("Hexapolar rings (hexapolar, dithered) or rays across (square); at least 3.")]
    public int RayDensity { get; init; } = 6;

    [DisplayName("Field")]
    [Description("Row of the field table, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Surface")]
    [Description("The surface the rays are read at; 0 for the image surface.")]
    public int Surface { get; init; }

    [DisplayName("Refer to")]
    [Description("The point the RMS and GEO radii are measured from.")]
    public SpotReference Reference { get; init; } = SpotReference.ChiefRay;

    [DisplayName("Airy disk")]
    [Description("Draw the Airy disk, 1.22 λ times the working F/#, about the reference.")]
    public bool ShowAiryDisk { get; init; } = true;
}

/// <summary>
/// OpticStudio's Standard Spot Diagram (OP-34; Ansys OpticStudio 2025 R2 help, "Standard Spot
/// Diagram"): for each field, the rays of the chosen pupil pattern (<see cref="PupilSampling"/>)
/// where they meet a surface, drawn in µm about the reference point, with the RMS and GEO radii
/// (<see cref="SpotStatistics"/>) and the Airy disk of the working F/#
/// (<see cref="RealWorkingFNumber"/>). Rays that fail are counted, not drawn.
/// </summary>
public sealed class StandardSpotAnalysis : OpticsAnalysis<StandardSpotSettings>
{
    public const string SavedId = "StandardSpot";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public StandardSpotAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Standard spot diagram";
    public override string Category => "Rays and spots";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, StandardSpotSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        CheckDensity(Name, settings.RayDensity, PupilSampling.MinimumSpotDensity, "ray density");
        var fields = Fields(Name, settings.Field, system);
        var waves = Wavelengths(Name, settings.Wavelength, system);
        int surface = Surface(Name, settings.Surface, lens);
        var pupil = PupilSampling.Points(settings.Pattern, settings.RayDensity);
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest { Fields = fields, Wavelengths = waves, Pupil = pupil, Surface = surface }, cancellation);
        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);

        // The Airy disk: the primary wavelength when polychromatic, else the one shown (help).
        int airyWave = waves.Count == 1 ? waves[0] : system.Wavelengths.PrimaryIndex;
        SequentialTrace? airyTrace = null;
        RayLauncher? launcher = null;
        bool airy = settings.ShowAiryDisk && surface == lens.ImageIndex;
        if (airy)
        {
            airyTrace = SequentialTrace.Create(lens, system, airyWave);
            launcher = new RayLauncher(lens, system);
        }

        var rows = new List<IReadOnlyList<string>>();
        var plots = new List<AnalysisPlot>();
        var lines = new List<string>();
        var allWaves = Enumerable.Range(0, waves.Count).ToArray();
        for (int fi = 0; fi < fields.Count; fi++)
        {
            cancellation.ThrowIfCancellationRequested();
            var spot = SpotCore.Compute(sample, system, fi, allWaves, settings.Reference, 0, null);
            string label = FieldLabel(system, fields[fi]);
            string airyText = "—", fText = "—";
            double airyRadius = double.NaN;
            if (airy)
            {
                var (hx, hy) = system.Fields.Normalized(fields[fi]);
                var w = RealWorkingFNumber.Compute(airyTrace!, launcher!, hx, hy);
                if (w.Succeeded)
                {
                    airyRadius = RealWorkingFNumber.AiryRadius(airyTrace!.AirMicrometers * 1e-6, w.Value);
                    airyText = Um(airyRadius);
                    fText = StudyReport.Number(w.Value, "g6");
                }
                else lines.Add($"{label}: no Airy disk, {w.Failure}.");
            }
            int passed = spot.Points.Count;
            if (spot.Statistics is not { } st)
            {
                rows.Add(new[] { label, "—", "—", "—", "—", "—", "—", airyText, fText, $"{passed} of {spot.Launched}" });
                lines.Add($"{label}: {spot.Failure}.");
            }
            else
            {
                rows.Add(new[]
                {
                    label, Lens(st.ReferenceX, unit), Lens(st.ReferenceY, unit), Um(st.Rms), Um(st.Geo), Um(st.RmsX), Um(st.RmsY),
                    airyText, fText, $"{passed} of {spot.Launched}",
                });
                var series = SpotCore.Series(spot, system, st.ReferenceX, st.ReferenceY, MicrometersPerMeter).ToList();
                if (double.IsFinite(airyRadius))
                    series.Add(new AnalysisScatterSeries("Airy disk", Circle(0, 0, airyRadius * MicrometersPerMeter)));
                plots.Add(new AnalysisScatterPlot($"{label}: RMS {Um(st.Rms)} µm, GEO {Um(st.Geo)} µm", "X from reference (µm)", "Y from reference (µm)", series));
            }
            if (spot.Failures.Count > 0) lines.Add($"{label}: rays not drawn: {string.Join("; ", spot.Failures)}.");
        }
        if (lines.Count == 0) lines.Add("Every ray launched reached the surface.");
        var sections = new[]
        {
            new ReportSection("Spot")
            {
                TableHeaders = new[] { "Field", $"Reference X ({u})", $"Reference Y ({u})", "RMS (µm)", "GEO (µm)", "RMS X (µm)", "RMS Y (µm)", "Airy radius (µm)", "Working F/#", "Rays passed" },
                TableRows = rows, Lines = lines,
            },
        };
        var assumptions = new List<string>
        {
            PupilSampling.Describe(settings.Pattern, settings.RayDensity, pupil.Count),
            $"Rays read at {SurfaceName(lens, surface)}, in its local frame; wavelengths {string.Join(", ", waves.Select(w => WaveLabel(system, w)))}.",
            SpotCore.ReferenceAssumption(settings.Reference),
            SpotCore.CountingAssumption,
            airy ? $"Airy radius 1.22 λ W at {WaveLabel(system, airyWave)} (in the system's air), W the real working F/# averaged over the four marginal rays of the vignetted pupil, apertures ignored (help, \"Working F/#\"; §25). It is drawn as a circle, not the help's ellipse."
                : settings.ShowAiryDisk ? "No Airy disk: it is drawn only at the image surface." : "No Airy disk drawn.",
        };
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(sample.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}

/// <summary>Settings of the <see cref="ThroughFocusSpotAnalysis"/>.</summary>
public sealed record ThroughFocusSpotSettings
{
    [DisplayName("Pattern")]
    [Description("Hexapolar, square or dithered rays on the pupil.")]
    public PupilPattern Pattern { get; init; } = PupilPattern.Hexapolar;

    [DisplayName("Ray density")]
    [Description("Hexapolar rings (hexapolar, dithered) or rays across (square); at least 3.")]
    public int RayDensity { get; init; } = 6;

    [DisplayName("Field")]
    [Description("Row of the field table, 1 for the first; 0 for every field.")]
    public int Field { get; init; }

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Refer to")]
    [Description("The point the RMS and GEO radii are measured from.")]
    public SpotReference Reference { get; init; } = SpotReference.ChiefRay;

    [DisplayName("Delta focus (µm)")]
    [Description("Spacing of the five focal planes along the image surface's z: −2, −1, 0, 1, 2 times this.")]
    public double DeltaFocusMicrometers { get; init; } = 50;
}

/// <summary>
/// OpticStudio's Through Focus Spot Diagram (OP-34; help, "Through Focus Spot Diagram"): five
/// spots per field at −2, −1, 0, 1 and 2 times the delta focus from the image surface, each
/// ray carried in a straight line along its image-space direction to the plane z = dz of the
/// image surface's local frame. Radii as the standard spot diagram.
/// </summary>
public sealed class ThroughFocusSpotAnalysis : OpticsAnalysis<ThroughFocusSpotSettings>
{
    public const string SavedId = "ThroughFocusSpot";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public ThroughFocusSpotAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Through focus spot diagram";
    public override string Category => "Rays and spots";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, ThroughFocusSpotSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        CheckDensity(Name, settings.RayDensity, PupilSampling.MinimumSpotDensity, "ray density");
        if (!(Math.Abs(settings.DeltaFocusMicrometers) > 0) || !double.IsFinite(settings.DeltaFocusMicrometers))
            throw new ArgumentException($"{Name}: the delta focus must be a non-zero number of micrometers (it is {settings.DeltaFocusMicrometers}).");
        var fields = Fields(Name, settings.Field, system);
        var waves = Wavelengths(Name, settings.Wavelength, system);
        var pupil = PupilSampling.Points(settings.Pattern, settings.RayDensity);
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest { Fields = fields, Wavelengths = waves, Pupil = pupil }, cancellation);
        var allWaves = Enumerable.Range(0, waves.Count).ToArray();
        double delta = settings.DeltaFocusMicrometers / MicrometersPerMeter;

        var rows = new List<IReadOnlyList<string>>();
        var plots = new List<AnalysisPlot>();
        var lines = new List<string>();
        for (int fi = 0; fi < fields.Count; fi++)
        {
            string label = FieldLabel(system, fields[fi]);
            for (int k = -2; k <= 2; k++)
            {
                cancellation.ThrowIfCancellationRequested();
                double dz = k * delta;
                var spot = SpotCore.Compute(sample, system, fi, allWaves, settings.Reference, dz, null);
                string focus = StudyReport.Number(dz * MicrometersPerMeter, "g6");
                if (spot.Statistics is not { } st)
                {
                    rows.Add(new[] { label, focus, "—", "—", $"{spot.Points.Count} of {spot.Launched}" });
                    lines.Add($"{label}, defocus {focus} µm: {spot.Failure}.");
                    continue;
                }
                rows.Add(new[] { label, focus, Um(st.Rms), Um(st.Geo), $"{spot.Points.Count} of {spot.Launched}" });
                plots.Add(new AnalysisScatterPlot($"{label}, defocus {focus} µm: RMS {Um(st.Rms)} µm, GEO {Um(st.Geo)} µm",
                    "X from reference (µm)", "Y from reference (µm)",
                    SpotCore.Series(spot, system, st.ReferenceX, st.ReferenceY, MicrometersPerMeter).ToList()));
            }
            var failures = SpotCore.Compute(sample, system, fi, allWaves, SpotReference.Vertex, 0, null).Failures;
            if (failures.Count > 0) lines.Add($"{label}: rays not drawn: {string.Join("; ", failures)}.");
        }
        if (lines.Count == 0) lines.Add("Every ray launched reached the image surface.");
        var sections = new[]
        {
            new ReportSection("Through focus")
            {
                TableHeaders = new[] { "Field", "Defocus (µm)", "RMS (µm)", "GEO (µm)", "Rays passed" },
                TableRows = rows, Lines = lines,
            },
        };
        var assumptions = new List<string>
        {
            PupilSampling.Describe(settings.Pattern, settings.RayDensity, pupil.Count),
            "Each ray (and the chief ray) is carried in a straight line from the image surface along its direction to the plane z = dz of the image surface's local frame; the image space is taken as homogeneous.",
            SpotCore.ReferenceAssumption(settings.Reference),
            SpotCore.CountingAssumption,
        };
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(sample.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}

/// <summary>Settings of the <see cref="FullFieldSpotAnalysis"/>.</summary>
public sealed record FullFieldSpotSettings
{
    [DisplayName("Pattern")]
    [Description("Hexapolar, square or dithered rays on the pupil.")]
    public PupilPattern Pattern { get; init; } = PupilPattern.Hexapolar;

    [DisplayName("Ray density")]
    [Description("Hexapolar rings (hexapolar, dithered) or rays across (square); at least 3.")]
    public int RayDensity { get; init; } = 6;

    [DisplayName("Wavelength")]
    [Description("Row of the wavelength table, 1 for the first; 0 for every wavelength.")]
    public int Wavelength { get; init; }

    [DisplayName("Surface")]
    [Description("The surface the rays are read at; 0 for the image surface.")]
    public int Surface { get; init; }

    [DisplayName("Refer to")]
    [Description("Each field's reference point; the first field's is the common origin.")]
    public SpotReference Reference { get; init; } = SpotReference.ChiefRay;

    [DisplayName("Exaggerate")]
    [Description("Factor on every ray's distance from its own field's reference.")]
    public double Exaggerate { get; init; } = 1;
}

/// <summary>
/// OpticStudio's Full Field Spot Diagram (OP-34; help, "Full Field Spot Diagram"): every field's
/// spot on one plot in lens units, all relative to the first field's reference point ("If chief
/// ray is selected as the reference point, then the chief ray for field position 1 will be
/// used"). A ray is drawn at R_f + E·(p − R_f) − R_1, R_f its own field's reference, E the
/// exaggeration ("magnifies the transverse aberrations").
/// </summary>
public sealed class FullFieldSpotAnalysis : OpticsAnalysis<FullFieldSpotSettings>
{
    public const string SavedId = "FullFieldSpot";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public FullFieldSpotAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Full field spot diagram";
    public override string Category => "Rays and spots";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, FullFieldSpotSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        CheckDensity(Name, settings.RayDensity, PupilSampling.MinimumSpotDensity, "ray density");
        if (!(settings.Exaggerate > 0) || !double.IsFinite(settings.Exaggerate))
            throw new ArgumentException($"{Name}: the exaggeration must be a positive number (it is {settings.Exaggerate}).");
        var fields = Fields(Name, 0, system);
        var waves = Wavelengths(Name, settings.Wavelength, system);
        int surface = Surface(Name, settings.Surface, lens);
        var pupil = PupilSampling.Points(settings.Pattern, settings.RayDensity);
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest { Fields = fields, Wavelengths = waves, Pupil = pupil, Surface = surface }, cancellation);
        var allWaves = Enumerable.Range(0, waves.Count).ToArray();
        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        double perMeter = OpticsConventions.FromMeters(1, unit);

        var spots = new List<Spot>();
        for (int fi = 0; fi < fields.Count; fi++)
        {
            cancellation.ThrowIfCancellationRequested();
            spots.Add(SpotCore.Compute(sample, system, fi, allWaves, settings.Reference, 0, null));
        }
        int origin = spots.FindIndex(s => s.Statistics is not null);
        var rows = new List<IReadOnlyList<string>>();
        var lines = new List<string>();
        var series = new List<AnalysisScatterSeries>();
        if (origin != 0)
            lines.Add(origin < 0 ? "No field has a spot to draw." : $"Field 1 has no spot ({spots[0].Failure}); the origin is {FieldLabel(system, fields[origin])}'s reference.");
        double ox = origin >= 0 ? spots[origin].Statistics!.ReferenceX : 0, oy = origin >= 0 ? spots[origin].Statistics!.ReferenceY : 0;
        for (int fi = 0; fi < fields.Count; fi++)
        {
            var spot = spots[fi];
            string label = FieldLabel(system, fields[fi]);
            if (spot.Failures.Count > 0) lines.Add($"{label}: rays not drawn: {string.Join("; ", spot.Failures)}.");
            if (spot.Statistics is not { } st)
            {
                rows.Add(new[] { label, "—", "—", "—", "—", $"{spot.Points.Count} of {spot.Launched}" });
                lines.Add($"{label}: {spot.Failure}.");
                continue;
            }
            rows.Add(new[] { label, Lens(st.ReferenceX, unit), Lens(st.ReferenceY, unit), Um(st.Rms), Um(st.Geo), $"{spot.Points.Count} of {spot.Launched}" });
            double e = settings.Exaggerate;
            var pts = spot.Points.Select(p => new AnalysisPoint(
                (st.ReferenceX + e * (p.X - st.ReferenceX) - ox) * perMeter,
                (st.ReferenceY + e * (p.Y - st.ReferenceY) - oy) * perMeter, "")).ToList();
            series.Add(new AnalysisScatterSeries(label, pts));
        }
        var plots = new List<AnalysisPlot>();
        if (series.Count > 0)
            plots.Add(new AnalysisScatterPlot("Full field spot diagram", $"X from field {fields[Math.Max(origin, 0)] + 1}'s reference ({u})", $"Y ({u})", series));
        if (lines.Count == 0) lines.Add("Every ray launched reached the surface.");
        var sections = new[]
        {
            new ReportSection("Spots")
            {
                TableHeaders = new[] { "Field", $"Reference X ({u})", $"Reference Y ({u})", "RMS (µm)", "GEO (µm)", "Rays passed" },
                TableRows = rows, Lines = lines,
            },
        };
        var assumptions = new List<string>
        {
            PupilSampling.Describe(settings.Pattern, settings.RayDensity, pupil.Count),
            $"Rays read at {SurfaceName(lens, surface)}. Drawn at R_f + E·(p − R_f) relative to the first field's reference, E = {StudyReport.Number(settings.Exaggerate)}; the radii are not exaggerated.",
            SpotCore.ReferenceAssumption(settings.Reference),
            SpotCore.CountingAssumption,
        };
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(sample.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}

/// <summary>Settings of the <see cref="MatrixSpotAnalysis"/>.</summary>
public sealed record MatrixSpotSettings
{
    [DisplayName("Pattern")]
    [Description("Hexapolar, square or dithered rays on the pupil.")]
    public PupilPattern Pattern { get; init; } = PupilPattern.Hexapolar;

    [DisplayName("Ray density")]
    [Description("Hexapolar rings (hexapolar, dithered) or rays across (square); at least 3.")]
    public int RayDensity { get; init; } = 6;

    [DisplayName("Surface")]
    [Description("The surface the rays are read at; 0 for the image surface.")]
    public int Surface { get; init; }

    [DisplayName("Refer to")]
    [Description("The point the RMS and GEO radii are measured from.")]
    public SpotReference Reference { get; init; } = SpotReference.ChiefRay;

    [DisplayName("Ignore lateral color")]
    [Description("Reference each field and wavelength to its own point instead of the field's.")]
    public bool IgnoreLateralColor { get; init; }
}

/// <summary>
/// OpticStudio's Matrix Spot Diagram (OP-34; help, "Matrix Spot Diagram"): one spot per field
/// and wavelength. The reference is the field's (its primary-wavelength chief ray, or the
/// centroid or middle of all its wavelengths) unless "Ignore Lateral Color", which "will
/// reference each spot diagram to the reference point for each field and wavelength
/// independently" (the chief ray of that wavelength, or that spot's own centroid or middle).
/// </summary>
public sealed class MatrixSpotAnalysis : OpticsAnalysis<MatrixSpotSettings>
{
    public const string SavedId = "MatrixSpot";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public MatrixSpotAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Matrix spot diagram";
    public override string Category => "Rays and spots";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, MatrixSpotSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        CheckDensity(Name, settings.RayDensity, PupilSampling.MinimumSpotDensity, "ray density");
        var fields = Fields(Name, 0, system);
        var waves = Wavelengths(Name, 0, system);
        int surface = Surface(Name, settings.Surface, lens);
        var pupil = PupilSampling.Points(settings.Pattern, settings.RayDensity);
        var sample = RaySampler.Trace(lens, system, new RaySampleRequest { Fields = fields, Wavelengths = waves, Pupil = pupil, Surface = surface }, cancellation);
        var allWaves = Enumerable.Range(0, waves.Count).ToArray();

        var rows = new List<IReadOnlyList<string>>();
        var plots = new List<AnalysisPlot>();
        var lines = new List<string>();
        for (int fi = 0; fi < fields.Count; fi++)
        {
            string label = FieldLabel(system, fields[fi]);
            var whole = SpotCore.Compute(sample, system, fi, allWaves, settings.Reference, 0, null);
            if (whole.Failures.Count > 0) lines.Add($"{label}: rays not drawn: {string.Join("; ", whole.Failures)}.");
            for (int wi = 0; wi < waves.Count; wi++)
            {
                cancellation.ThrowIfCancellationRequested();
                string wave = WaveLabel(system, waves[wi]);
                Spot cell;
                (double X, double Y)? reference;
                if (settings.IgnoreLateralColor)
                {
                    cell = SpotCore.Compute(sample, system, fi, new[] { wi }, settings.Reference, 0, wi);
                    reference = cell.Statistics is { } s ? (s.ReferenceX, s.ReferenceY) : null;
                }
                else
                {
                    var own = SpotCore.Compute(sample, system, fi, new[] { wi }, SpotReference.Vertex, 0, null);
                    reference = whole.Statistics is { } s ? (s.ReferenceX, s.ReferenceY) : null;
                    cell = own.Points.Count == 0 || reference is null ? own with { Statistics = null, Failure = own.Failure ?? whole.Failure }
                        : own with { Statistics = SpotStatistics.Compute(own.Points, SpotReference.ChiefRay, reference.Value) };
                }
                if (cell.Statistics is not { } st || reference is null)
                {
                    rows.Add(new[] { label, wave, "—", "—", $"{cell.Points.Count} of {cell.Launched}" });
                    lines.Add($"{label}, {wave}: {cell.Failure ?? "no reference point"}.");
                    continue;
                }
                rows.Add(new[] { label, wave, Um(st.Rms), Um(st.Geo), $"{cell.Points.Count} of {cell.Launched}" });
                plots.Add(new AnalysisScatterPlot($"{label}, {wave}: RMS {Um(st.Rms)} µm", "X from reference (µm)", "Y from reference (µm)",
                    SpotCore.Series(cell, system, reference.Value.X, reference.Value.Y, MicrometersPerMeter).ToList()));
            }
        }
        if (lines.Count == 0) lines.Add("Every ray launched reached the surface.");
        var sections = new[]
        {
            new ReportSection("Spots")
            {
                TableHeaders = new[] { "Field", "Wavelength", "RMS (µm)", "GEO (µm)", "Rays passed" },
                TableRows = rows, Lines = lines,
            },
        };
        var assumptions = new List<string>
        {
            PupilSampling.Describe(settings.Pattern, settings.RayDensity, pupil.Count),
            $"Rays read at {SurfaceName(lens, surface)}. " + (settings.IgnoreLateralColor
                ? "Each field and wavelength is referred to its own point (the chief ray of that wavelength, or that spot's centroid or middle)."
                : "Each field's spots are referred to the field's point over all wavelengths (the primary wavelength's chief ray, or the centroid or middle of every wavelength's rays)."),
            SpotCore.ReferenceAssumption(settings.Reference),
            SpotCore.CountingAssumption,
            "No Airy disk is drawn on the matrix spot diagram (not built).",
        };
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(sample.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}
