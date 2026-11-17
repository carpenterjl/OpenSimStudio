// OP-85
using System.ComponentModel;
using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Sources;

namespace OpenSim.Optics.NonSequential.Lightning;

/// <summary>The LightningTrace sampling levels (help, "Lightning Trace": "varies from "Low (1X)" to
/// "1024X""; NSLT: "0 = Low (1X), 1 = 4X, 2 = 16X, 3 = 64X, 4 = 256X, 5 = 1024X").</summary>
public enum NscLightningSampling
{
    Low1X = 0,
    X4 = 1,
    X16 = 2,
    X64 = 3,
    X256 = 4,
    X1024 = 5,
}

/// <summary>Settings of the <see cref="NscLightningTraceAnalysis"/>.</summary>
public sealed record NscLightningTraceSettings
{
    [DisplayName("Ray sampling"), Description("The initial mesh of rays over each source's far field: Low (1X) is 32 × 32 cells, each step doubles the cells per side (4X the rays).")]
    public NscLightningSampling RaySampling { get; init; } = NscLightningSampling.Low1X;

    [DisplayName("Edge sampling"), Description("How many times a mesh cell is halved where its corner rays follow different paths (object and detector edges) or its image folds.")]
    public NscLightningSampling EdgeSampling { get; init; } = NscLightningSampling.X16;

    [DisplayName("Source"), Description("The source object to trace; 0 for every source.")]
    public int Source { get; init; }

    [DisplayName("Detector"), Description("The Detector Rectangle or Detector Color to show (its object number); 0 for the first.")]
    public int Detector { get; init; }

    [DisplayName("Show as"), Description("Image, contours, a row or column cross section, or (Detector Color) true colour or a chromaticity chart.")]
    public NscDetectorShow ShowAs { get; init; } = NscDetectorShow.Image;

    [DisplayName("Row/Column"), Description("The row or column of a cross section, 1 for the first; 0 for the centre.")]
    public int RowOrColumn { get; init; }

    [DisplayName("Smoothing"), Description("How many times every pixel is replaced by the average of itself and its neighbours.")]
    public int Smoothing { get; init; }

    [DisplayName("Scale"), Description("Linear, or logarithmic over 5, 10 or 15 decades.")]
    public NscDetectorScale Scale { get; init; } = NscDetectorScale.Linear;

    [DisplayName("Photometric"), Description("Lumens instead of watts (the sources' spectra averaged).")]
    public bool Photometric { get; init; }
}

/// <summary>
/// The LightningTrace analysis (help, "Lightning Trace"; plan item OP-85;
/// docs/Optics-Conventions.md §79): traces the mesh rays of the project's sources
/// (<see cref="ProjectNscLightning.Trace(OpticsAnalysisContext, int, int, IReadOnlyList{int}?, int?, CancellationToken)"/>)
/// and shows one flat detector through the Detector Viewer (<see cref="NscDetectorViewer.Render"/>),
/// with the mesh's statistics.
/// </summary>
public sealed class NscLightningTraceAnalysis : OpticsAnalysis<NscLightningTraceSettings>
{
    public const string SavedId = "NscLightningTrace";
    public override string Id => SavedId;
    public override string Name => "Lightning trace";
    public override string Category => "Non-sequential";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscLightningTraceSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var unit = SystemDataSettings.FromSettings(context.System.SystemData, notes).Units.LensUnit;
        if (settings.Source < 0) throw new ArgumentException($"{Name}: the source is {settings.Source}; it must be 0 (every source) or a source object.");
        var result = ProjectNscLightning.Trace(context, (int)settings.RaySampling, (int)settings.EdgeSampling,
            settings.Source == 0 ? null : new[] { settings.Source }, cancellation: cancellation);
        if (result.Detectors.Count == 0)
            throw new ArgumentException($"{Name}: the scene has no Detector Rectangle or Detector Color to show LightningTrace results on (\"Results of the LightningTrace analysis may be observed on Detector Rectangle, Detector Color, and Detector Polar objects\"; Detector Polar is not built for it).");
        var data = settings.Detector == 0 ? result.Detectors[0] : result.Detectors.FirstOrDefault(d => d.ObjectNumber == settings.Detector)
            ?? throw new ArgumentException($"{Name}: object {settings.Detector} is not a Detector Rectangle or Detector Color; those are object{(result.Detectors.Count == 1 ? "" : "s")} {string.Join(", ", result.Detectors.Select(d => d.ObjectNumber))}.");
        var viewer = new NscDetectorViewerSettings
        {
            ShowAs = settings.ShowAs, RowOrColumn = settings.RowOrColumn, Smoothing = settings.Smoothing, Scale = settings.Scale, Photometric = settings.Photometric,
        };
        var shown = NscDetectorViewer.Render(data, viewer, unit, Name);
        var sections = new List<ReportSection>(shown.Sections) { Statistics(result, data) };
        return OpticsAnalysisResult.FromSections(Name, sections, shown.Assumptions.Concat(notes).Concat(result.Notes).ToArray(), shown.Plots);
    }

    internal static ReportSection Statistics(NscLightningResult r, NscDetectorData shown)
    {
        string E(double v) => StudyReport.Number(v, "g8") + " " + r.Unit;
        double onDetector = shown is NscFlatDetectorData f ? f.Flux.Sum() : double.NaN;
        var pairs = new List<(string, string)>
        {
            ("Mesh", $"{r.CellsPerSide} × {r.CellsPerSide} cells per source element, up to {r.EdgeDepth} halvings"),
            ("Mesh rays traced", r.Rays.ToString(CultureInfo.InvariantCulture)),
            ("Cells refined", r.RefinedCells.ToString(CultureInfo.InvariantCulture)),
            ("Folded cells at the finest level", r.FoldedCells.ToString(CultureInfo.InvariantCulture)),
            ("Emitted", E(r.Emitted)),
            ($"On detector object {shown.ObjectNumber}", E(onDetector)),
            ("Reaching no detector", E(r.Missed)),
            ("Unresolved (corner rays at the finest level)", E(r.Unresolved)),
            ("  of which lost to trace errors", E(r.Errors)),
        };
        foreach (var s in r.Sources)
            pairs.Add(($"Source object {s.ObjectNumber}", $"{s.TypeName}, {s.Elements} element(s), {StudyReport.Number(s.Power, "g8")} {r.Unit}, traced at {StudyReport.Number(s.WavelengthMicrometers, "g6")} µm; {s.Chart}"));
        return new ReportSection("LightningTrace") { Pairs = pairs };
    }
}

/// <summary>Settings of the <see cref="NscSourceIlluminationMapAnalysis"/> (help, "Source Illumination Map").</summary>
public sealed record NscSourceIlluminationMapSettings
{
    [DisplayName("File name"), Description("An IESNA, EULUMDAT or SDF source file: not built (plan item OP-78); leave blank and list source objects instead.")]
    public string FileName { get; init; } = "";

    [DisplayName("Sources"), Description("The source objects to display (1 to 8 object numbers, separated by commas or spaces); blank for every source of the scene. Each keeps its own spectrum, position and orientation.")]
    public string Sources { get; init; } = "";

    [DisplayName("X half width"), Description("The X half width of the screen, lens units. The screen lies at the global origin, normal to z.")]
    public double XHalfWidth { get; init; } = 1000;

    [DisplayName("Y half width"), Description("The Y half width of the screen, lens units.")]
    public double YHalfWidth { get; init; } = 1000;

    [DisplayName("# X pixels"), Description("The number of pixels in X on the screen.")]
    public int XPixels { get; init; } = 100;

    [DisplayName("# Y pixels"), Description("The number of pixels in Y on the screen.")]
    public int YPixels { get; init; } = 100;

    [DisplayName("Ray sampling"), Description("The mesh over each source's far field (as LightningTrace's Ray Sampling).")]
    public NscLightningSampling RaySampling { get; init; } = NscLightningSampling.X16;

    [DisplayName("Edge sampling"), Description("Halvings of the mesh at the screen's edges (as LightningTrace's Edge Sampling).")]
    public NscLightningSampling EdgeSampling { get; init; } = NscLightningSampling.X16;

    [DisplayName("Show as"), Description("True colour (the help's display), an image, contours, a cross section or a chromaticity chart.")]
    public NscDetectorShow ShowAs { get; init; } = NscDetectorShow.TrueColor;

    [DisplayName("Photometric"), Description("Illuminance (lumens per area) instead of irradiance.")]
    public bool Photometric { get; init; } = true;

    [DisplayName("Suppress frame"), Description("The help's \"the frame is not drawn\": stored; the window draws its plots the same either way.")]
    public bool SuppressFrame { get; init; }
}

/// <summary>
/// Source Illumination Map (help, "Source Illumination Map"; plan item OP-85;
/// docs/Optics-Conventions.md §79): "a true color image of the illumination pattern formed by one
/// or more sources" on a screen at the global origin with normal (0, 0, 1); "The far-field
/// luminous intensity data ... along with the solid angle subtended by each pixel on the screen,
/// determines the luminous flux for each pixel". The help's sources are IES, LDT or SDF files,
/// whose readers are plan item OP-78 and not built: a file name is refused by name, and the sources
/// are the project's own source objects instead (a reading, Conventions §10 item 75), each with its
/// own spectrum, position and orientation. The map is LightningTrace on a scene holding only those
/// sources and an absorbing Detector Color as the screen, so each pixel receives the source's power
/// in the solid angle the pixel subtends from the source's centre, exactly the help's far-field
/// definition.
/// </summary>
public sealed class NscSourceIlluminationMapAnalysis : OpticsAnalysis<NscSourceIlluminationMapSettings>
{
    public const string SavedId = "NscSourceIlluminationMap";
    public override string Id => SavedId;
    public override string Name => "Source illumination map";
    public override string Category => "Non-sequential";

    /// <summary>"No. of Sources: The number of sources to be displayed. May be any number from 1 to 8."</summary>
    public const int MaxSources = 8;

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, NscSourceIlluminationMapSettings settings, CancellationToken cancellation)
    {
        if (!string.IsNullOrWhiteSpace(settings.FileName))
            throw new NotSupportedException($"{Name}: source files (IESNA, EULUMDAT, SDF: '{settings.FileName}') are not built; their readers are plan item OP-78. Leave the file name blank and list the project's source objects under Sources.");
        var notes = new List<string>();
        var unit = SystemDataSettings.FromSettings(context.System.SystemData, notes).Units.LensUnit;
        var scene = ProjectNonSequential.Scene(context, unit)
            ?? throw new ArgumentException($"{Name}: the system has no non-sequential objects; add the source objects to display.");
        var chosen = Chosen(settings.Sources, scene);
        var screenScene = ScreenScene(scene, chosen, settings, unit, out int screen);
        var result = ProjectNscLightning.Trace(context, screenScene, (int)settings.RaySampling, (int)settings.EdgeSampling, chosen, null, cancellation, notes);
        var data = result.Detectors.Single(d => d.ObjectNumber == screen);
        var viewer = new NscDetectorViewerSettings { ShowAs = settings.ShowAs, Photometric = settings.Photometric };
        var shown = NscDetectorViewer.Render(data, viewer, unit, Name);
        var sections = new List<ReportSection>(shown.Sections) { NscLightningTraceAnalysis.Statistics(result, data) };
        var assumptions = shown.Assumptions.Concat(notes).Concat(result.Notes).Append(
            "Each source is its far field from its centre onto a screen at the global origin, normal to z; the other objects of the scene are left out (help: \"this analysis only considers the far-field distribution of the sources\"). Source files (IES, LDT, SDF) are not built (OP-78); the sources are the project's own objects.").ToArray();
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, shown.Plots);
    }

    /// <summary>The listed source objects, or every source of the scene; 1 to 8 of them.</summary>
    internal static IReadOnlyList<int> Chosen(string? text, NscScene scene)
    {
        var all = Enumerable.Range(1, scene.Objects.Count).Where(i => scene.Objects[i - 1].Geometry is NscSource).ToList();
        List<int> chosen;
        if (string.IsNullOrWhiteSpace(text)) chosen = all;
        else
        {
            chosen = new List<int>();
            foreach (var part in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k))
                    throw new ArgumentException($"Source illumination map: '{part}' in Sources is not an object number.");
                if (!all.Contains(k))
                    throw new ArgumentException($"Source illumination map: object {k} is not a source object; the sources are {(all.Count == 0 ? "none" : string.Join(", ", all))}.");
                if (chosen.Contains(k)) throw new ArgumentException($"Source illumination map: source {k} is listed twice.");
                chosen.Add(k);
            }
        }
        if (chosen.Count == 0) throw new ArgumentException("Source illumination map: the scene has no source objects.");
        if (chosen.Count > MaxSources)
            throw new ArgumentException($"Source illumination map: {chosen.Count} sources; the help allows 1 to {MaxSources} (\"No. of Sources ... May be any number from 1 to 8\"). List the ones to show under Sources.");
        return chosen;
    }

    /// <summary>The scene with every object but the chosen sources replaced by an object that no
    /// ray meets (so references and numbering hold), those sources taken out of any "Inside Of",
    /// and the screen appended as the last object.</summary>
    internal static NscScene ScreenScene(NscScene scene, IReadOnlyList<int> chosen, NscSourceIlluminationMapSettings s, LensUnit unit, out int screen)
    {
        if (!(s.XHalfWidth > 0) || !(s.YHalfWidth > 0) || !double.IsFinite(s.XHalfWidth + s.YHalfWidth))
            throw new ArgumentException($"Source illumination map: the screen half widths must be positive; they are {s.XHalfWidth} and {s.YHalfWidth}.");
        var objects = new List<NscObject>();
        for (int i = 0; i < scene.Objects.Count; i++)
        {
            var o = scene.Objects[i];
            objects.Add(chosen.Contains(i + 1)
                ? o with { InsideOf = 0 }
                : new NscObject { Geometry = NscNothing.Instance, Placement = o.Placement, ReferenceObject = o.ReferenceObject, Comment = o.Comment });
        }
        var detector = new NscDetectorColor(OpticsConventions.ToMeters(s.XHalfWidth, unit), OpticsConventions.ToMeters(s.YHalfWidth, unit), s.XPixels, s.YPixels);
        objects.Add(new NscObject { Geometry = detector, Material = NscMaterial.Absorb, Comment = "Source illumination map screen" });
        screen = objects.Count;
        return new NscScene { Objects = objects, Background = Paraxial.ParaxialMedium.Air };
    }
}

/// <summary>An object no ray meets (the Source Illumination Map's stand-in for objects it leaves out).</summary>
internal sealed class NscNothing : NscGeometry
{
    public static NscNothing Instance { get; } = new();
    private static readonly IReadOnlyDictionary<int, string> FaceNames = new Dictionary<int, string> { [0] = "None" };
    public override string TypeName => "Left out";
    public override bool IsVolume => false;
    public override IReadOnlyDictionary<int, string> Faces => FaceNames;
    public override Aabb LocalBounds => new(Vector3D.Zero, Vector3D.Zero);
    public override void Intersect(Vector3D origin, Vector3D direction, double tMin, double tMax, List<NscLocalHit> hits) { }
    public override bool IsInside(Vector3D point) => false;
    public override string Describe(Func<double, string> length) => "left out of the analysis";
}
