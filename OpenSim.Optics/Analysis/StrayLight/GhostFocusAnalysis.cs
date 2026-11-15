using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using static OpenSim.Optics.Analysis.RayAnalysisSupport;

namespace OpenSim.Optics.Analysis.StrayLight;

/// <summary>Single- or double-bounce ghosts (help, "Ghost Focus Generator", Bounces).</summary>
public enum GhostBounces
{
    Double = 0,
    Single = 1,
}

/// <summary>Settings of the <see cref="GhostFocusAnalysis"/>; the help's settings plus the wavelength and the real-ray grid.</summary>
public sealed record GhostFocusSettings
{
    [DisplayName("Bounces"), Description("Single-bounce or double-bounce analysis.")]
    public GhostBounces Bounces { get; init; } = GhostBounces.Double;

    [DisplayName("First surface"), Description("The first surface to consider reflections from.")]
    public int FirstSurface { get; init; } = 1;

    [DisplayName("Last surface"), Description("The last surface to consider reflections from; 0 for the image surface.")]
    public int LastSurface { get; init; }

    [DisplayName("Image surface only"), Description("Double bounce: list only the image surface data of each ghost.")]
    public bool ImageSurfaceOnly { get; init; }

    [DisplayName("Ghost reflector coating"), Description("A coating applied to every bounce surface instead of its own (e.g. I.99 reflects 1 %); blank for the surfaces' own coatings or uncoated Fresnel.")]
    public string GhostReflectorCoating { get; init; } = "";

    [DisplayName("Wavelength"), Description("Row of the wavelength table, 1 for the first; 0 for the primary.")]
    public int Wavelength { get; init; }

    [DisplayName("Pupil grid"), Description("Rays across the pupil of the on-axis real-ray grid used for the RMS spot radius.")]
    public int PupilGrid { get; init; } = 10;
}

/// <summary>
/// The Ghost Focus Generator as an analysis (OP-46; help, "Ghost Focus Generator"): a summary
/// row per ghost (bounce reflectances, relative intensity R1·R2, focus distance from the image,
/// pupil ghost size, ghost EFL and ghost pupil), the help's per-surface listing for each ghost,
/// and a scatter plot of relative intensity against focus distance. Not built: Save Files (the
/// GHfffsss.ZMX files are not written; their names are listed).
/// </summary>
public sealed class GhostFocusAnalysis : OpticsAnalysis<GhostFocusSettings>
{
    public const string SavedId = "GhostFocusGenerator";
    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    public GhostFocusAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null) =>
        _lensSource = lensSource ?? ProjectLens.Sequential;

    public override string Id => SavedId;
    public override string Name => "Ghost focus generator";
    public override string Category => "Stray light";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, GhostFocusSettings settings, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        if (_lensSource(context) is not { } lens) return NoSurfaces(Name);
        int wave = OneWavelength(Name, settings.Wavelength, system);
        int last = Surface(Name, settings.LastSurface, lens);
        if (settings.FirstSurface < 1 || settings.FirstSurface > last)
            throw new ArgumentException($"{Name}: the first surface must be 1 to the last surface {last} (it is {settings.FirstSurface}).");
        var catalog = CoatingCatalogSettings.FromSettings(context.System.Coatings, notes);
        IReadOnlyList<double>? semiDiameters = null;
        if (lens.Surfaces.Any(s => s.Aperture is FloatingAperture && s.SemiDiameter is null))
            semiDiameters = SemiDiameterSizing.Compute(lens, system, cancellation: cancellation).Clear;
        bool twice = settings.Bounces == GhostBounces.Double;
        var run = GhostFocusGenerator.Run(lens, system, new GhostFocusOptions
        {
            DoubleBounce = twice, FirstSurface = settings.FirstSurface, LastSurface = last,
            WavelengthRow = wave, ReflectorCoating = settings.GhostReflectorCoating, PupilGrid = settings.PupilGrid,
        }, catalog, semiDiameters, cancellation);

        var unit = system.Units.LensUnit;
        string u = ParaxialReport.UnitName(unit);
        double perMeter = OpticsConventions.FromMeters(1, unit);
        string L(double? m) => m is double v ? Lens(v, unit) : "∞";
        string N(double? v, string f = "g6") => v is double x ? StudyReport.Number(x, f) : "—";

        var sections = new List<ReportSection>();
        var summary = new List<IReadOnlyList<string>>();
        var lines = new List<string>
        {
            $"Candidate surfaces: {(run.Candidates.Count == 0 ? "none" : string.Join(", ", run.Candidates.Select(j => j == lens.ImageIndex ? "IMA" : j.ToString())))}; " +
            (twice ? $"{run.Ghosts.Count} double-bounce ghost{(run.Ghosts.Count == 1 ? "" : "s")} (n(n − 1)/2 for n = {run.Candidates.Count})." : $"{run.Ghosts.Count} single-bounce ghost{(run.Ghosts.Count == 1 ? "" : "s")}."),
        };
        var points = new List<AnalysisPoint>();
        foreach (var g in run.Ghosts)
        {
            if (g.Error is not null)
            {
                summary.Add(new[] { g.Label, "—", "—", "—", "—", "—", "—", "—", "—", "—", "failed" });
                lines.Add($"Ghost {g.Label}: {g.Error}");
                continue;
            }
            var internalFocus = g.Surfaces.Where(s => s.InternalFocus).Select(s => GhostFocusGenerator.PassLabel(s.Origin, false)).ToArray();
            summary.Add(new[]
            {
                g.Label, N(g.FirstReflectance), N(g.SecondReflectance), N(g.RelativeIntensity), N(g.PathTransmittance),
                L(g.FocusFromImage), Lens(g.BeamDiameterAtImage, unit), L(g.EffectiveFocalLength), L(g.PupilFromImage), N(g.PupilMagnification),
                g.RaysFailed == 0 ? "" : $"{g.RaysFailed}/{g.RaysLaunched} rays failed",
            });
            if (internalFocus.Length > 0) lines.Add($"Ghost {g.Label}: possible internal focus in the glass after {string.Join("; ", internalFocus)}.");
            foreach (var f in g.Failures) lines.Add($"Ghost {g.Label}: {f}");
            if (g.FocusFromImage is double z) points.Add(new AnalysisPoint(z * perMeter, g.RelativeIntensity, g.Label));
        }
        sections.Add(new ReportSection("Ghosts")
        {
            TableHeaders = new[]
            {
                "Ghost", "R first", "R second", "R1·R2", "With transmission", $"Focus from image ({u})", $"Pupil ghost size ({u})",
                $"Ghost EFL ({u})", $"Ghost pupil from image ({u})", "Ghost pupil magnification", "Rays",
            },
            TableRows = summary, Lines = lines,
        });

        foreach (var g in run.Ghosts)
        {
            if (g.Error is not null || g.Ghost is null) continue;
            cancellation.ThrowIfCancellationRequested();
            var rows = new List<IReadOnlyList<string>>();
            foreach (var s in g.Surfaces)
            {
                bool isImage = s.GhostSurface == g.Ghost.Lens.ImageIndex;
                if (twice && settings.ImageSurfaceOnly && !isImage) continue;
                rows.Add(new[]
                {
                    s.GhostSurface.ToString(), GhostFocusGenerator.PassLabel(s.Origin, isImage),
                    Lens(s.MarginalHeight, unit), Lens(s.ChiefHeight, unit),
                    double.IsInfinity(s.FNumber) ? "∞" : StudyReport.Number(s.FNumber, "g6"),
                    double.IsNaN(s.RmsRadius) ? "—" : Lens(s.RmsRadius, unit), s.InternalFocus ? "internal focus" : "",
                });
            }
            sections.Add(new ReportSection($"Ghost {g.Label} ({g.Ghost.FileStem})")
            {
                TableHeaders = new[] { "Ghost surface", "Original", $"Marginal y ({u})", $"Chief y ({u})", "Paraxial F/#", $"RMS spot radius ({u})", "" },
                TableRows = rows,
                Lines = new[]
                {
                    $"Image surface: marginal y {Lens(g.MarginalAtImage, unit)} {u}, chief y {Lens(g.ChiefAtImage, unit)} {u}; ghost focus {L(g.FocusFromImage)} {u} from the image surface; ghost EFL {L(g.EffectiveFocalLength)} {u}.",
                },
            });
        }

        var plots = new List<AnalysisPlot>();
        if (points.Count > 0)
            plots.Add(new AnalysisScatterPlot("Ghost intensity vs focus", $"Ghost focus from image surface ({u})", "Relative intensity R1·R2",
                new[] { new AnalysisScatterSeries("Ghosts", points) }));

        var assumptions = new List<string>
        {
            $"Current configuration, {WaveLabel(system, wave)}; afocal image space is not used (the help turns it off).",
            "Each ghost is its own lens built by the double-pass construction (Conventions §49): the bounce surfaces become mirrors and the surfaces between them are copied in reverse.",
            "Paraxial data: the original's marginal ray and full-field chief ray, as they enter surface 1, traced through the ghost (so the entrance pupil is the original's wherever the stop is). F/# = 1/(2|n·u|) of the marginal ray in each space.",
            "Focus and ghost pupil positions are along z from the ghost's image surface (for a single bounce, the object plane, or surface 1's vertex plane when the object is at infinity); positive toward +z.",
            "Pupil ghost size: the diameter 2|y| of the paraxial marginal ray on the image surface. Ghost pupil magnification: the ghost exit pupil diameter over the entrance pupil diameter.",
            $"RMS spot radius: real rays on axis on a {settings.PupilGrid} × {settings.PupilGrid} grid inside the pupil, about their centroid on each surface; failed rays are counted and named.",
            "Relative intensity R1·R2: unpolarized reflectance at normal incidence of each bounce, from the surface's OP-15 coating or the uncoated Fresnel interface"
                + (string.IsNullOrWhiteSpace(settings.GhostReflectorCoating) ? "." : $", with the ghost reflector coating '{settings.GhostReflectorCoating.Trim()}' on every bounce.")
                + " 'With transmission' multiplies in every other surface the path crosses (normal incidence, own coatings); bulk absorption is not included.",
            "Candidates: surfaces with an index change (mirrors and dummies excepted) and the image surface (help). Automatic floating apertures do not clip ghost rays; fixed ones do.",
            "Save Files is not built: no GHfffsss.ZMX files are written.",
        };
        assumptions.AddRange(LaunchAssumptions(system));
        assumptions.AddRange(run.Notes);
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }
}
