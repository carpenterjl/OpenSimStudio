// OP-44
using System.ComponentModel;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="SurfaceDataAnalysis"/> (OpticStudio's Surface setting).</summary>
public sealed record SurfaceDataSettings
{
    [DisplayName("Surface")]
    [Description("The surface to list the data of.")]
    public int Surface { get; init; } = 1;
}

/// <summary>
/// OpticStudio's Surface Data report (2025 R2 help, "Surface Data"): for one surface its shape,
/// thickness and media, surface and element powers and focal lengths, edge thickness, sag at the
/// semi-diameter, index of refraction at each wavelength and its global position
/// (docs/Optics-Conventions.md §29). The "best fit glass" OpticStudio lists for a model glass is
/// not built.
/// </summary>
public sealed class SurfaceDataAnalysis : OpticsAnalysis<SurfaceDataSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "SurfaceData";

    private readonly Func<OpticsAnalysisContext, SequentialLens?> _lensSource;

    /// <param name="lensSource">Where the surfaces come from; null for the project's saved surfaces.</param>
    public SurfaceDataAnalysis(Func<OpticsAnalysisContext, SequentialLens?>? lensSource = null)
    {
        _lensSource = lensSource ?? ProjectLens.Sequential;
    }

    public override string Id => SavedId;
    public override string Name => "Surface data";
    public override string Category => "Reports";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, SurfaceDataSettings settings,
        CancellationToken cancellation)
    {
        var notes = new List<string>();
        var system = SystemDataSettings.FromSettings(context.System.SystemData, notes);
        var lens = _lensSource(context);
        if (lens is null) return ReportText.NoSurfaces(Name);
        int j = settings.Surface;
        if (j < 1 || j > lens.LastSurfaceIndex)
            throw new ArgumentException($"Surface data: the surface must be 1 to {lens.LastSurfaceIndex} (it is {j}).");
        var unit = system.Units.LensUnit;
        string u = ConventionNames.Symbol(unit);
        string L(double meters) => ReportText.Length(meters, unit);
        var s = lens.Surfaces[j];
        var geometry = LensGeometry.Create(lens, system, cancellation);
        var media = ReportText.SpaceMedia(lens);
        var sections = new List<ReportSection>();
        var assumptions = new List<string>();

        // ---- The surface ----
        string type = s.IsCoordinateBreak ? SurfaceTypes.CoordinateBreak : s.Shape?.TypeName ?? SavedSurfaces.StandardType;
        var pairs = new List<(string, string)>
        {
            ("Surface", ReportText.Label(lens, j)),
            ("Type", type),
            ("Comment", s.Comment.Length == 0 ? "—" : s.Comment),
            ("Radius", s.Curvature == 0 ? "Infinity" : $"{L(1 / s.Curvature)} {u}"),
            ("Curvature", $"{StudyReport.Number(s.Curvature * OpticsConventions.MetersPer(unit), "g10")} 1/{u}"),
            ("Conic", StudyReport.Number(s.Conic, "g10")),
            ("Thickness", $"{L(s.Thickness)} {u}"),
            ("Medium before", ReportText.Describe(media[j - 1])),
            ("Medium after", s.IsMirror ? "mirror (the medium before)" : ReportText.Describe(media[j])),
        };
        double a = geometry.SemiDiameter(j);
        pairs.Add(("Clear semi-diameter", double.IsFinite(a) ? $"{L(a)} {u}" + (geometry.IsAutomatic(j) ? " (automatic)" : "")
            : "not sized" + (geometry.SizingError is null ? "" : ": " + geometry.SizingError)));
        if (double.IsFinite(a) && !s.IsCoordinateBreak)
        {
            double sagX = ElementVolume.Sag(s, a, 0), sagY = ElementVolume.Sag(s, 0, a);
            pairs.Add(("Sag at (a, 0)", double.IsFinite(sagX) ? $"{L(sagX)} {u}" : "undefined"));
            pairs.Add(("Sag at (0, a)", double.IsFinite(sagY) ? $"{L(sagY)} {u}" : "undefined"));
        }
        var edge = geometry.EdgeThickness(j, out string? why);
        pairs.Add(("Edge thickness X", edge is { } e1 ? $"{L(e1.X)} {u}" : "— (" + why + ")"));
        pairs.Add(("Edge thickness Y", edge is { } e2 ? $"{L(e2.Y)} {u}" : "— (" + why + ")"));
        var place = geometry.Placement.Global(j).Translation;
        pairs.Add(("Global vertex (launch frame)", $"({L(place.X)}, {L(place.Y)}, {L(place.Z)}) {u}"));
        sections.Add(new ReportSection("Surface") { Pairs = pairs });

        // ---- Powers and focal lengths ----
        try
        {
            var model = ParaxialSystem.Create(lens.ToParaxialLens(), system);
            double per = OpticsConventions.MetersPer(unit);
            double phi = model.Powers[j];
            double n = model.SignedIndices[j - 1], np = model.SignedIndices[j];
            var power = new List<(string, string)>
            {
                ("Wavelength", $"{StudyReport.Number(model.WavelengthMicrometers)} µm (primary)"),
                ("Surface power", $"{StudyReport.Number(phi * per, "g10")} 1/{u}"),
                ("Surface focal length (image space, n′/φ)", phi == 0 ? "infinite" : $"{L(np / phi)} {u}"),
                ("Surface focal length (object space, −n/φ)", phi == 0 ? "infinite" : $"{L(-n / phi)} {u}"),
            };
            if (!ReportText.IsAir(media[j]) && !s.IsMirror && j + 1 <= lens.LastSurfaceIndex)
            {
                var element = CardinalPlanes.Compute(model, j, j + 1);
                power.Add(("Element", $"surfaces {j} to {j + 1} in {ReportText.Describe(media[j])}"));
                power.Add(("Element power", $"{StudyReport.Number(element.Power * per, "g10")} 1/{u}"));
                power.Add(("Element focal length (1/φ)", element.IsAfocal ? "infinite (afocal)" : $"{L(element.EffectiveFocalLength!.Value)} {u}"));
            }
            sections.Add(new ReportSection("Powers and focal lengths")
            {
                Pairs = power,
                Lines = new[] { "φ = c·(n′ − n) with signed indices (or the special surface's paraxial equivalent); an element is the space after this surface to the next one, its power −C of their matrix with the media on either side." }
            });
            assumptions.Add("Powers are first-order (paraxial) at the primary wavelength.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            sections.Add(new ReportSection("Powers and focal lengths") { Lines = new[] { "Not computed: " + ex.Message.Replace("\r", "").Replace('\n', ' ') } });
        }
        cancellation.ThrowIfCancellationRequested();

        // ---- Index ----
        var rows = new List<IReadOnlyList<string>>();
        foreach (var w in system.Wavelengths.Wavelengths)
        {
            string Index(ParaxialMedium m)
            {
                try { return StudyReport.Number(m.IndexAt(w.Micrometers, system.Environment), "F8"); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return "error: " + ex.Message; }
            }
            rows.Add(new[] { StudyReport.Number(w.Micrometers, "g8"), Index(media[j - 1]), Index(media[j]) });
        }
        sections.Add(new ReportSection("Index of refraction")
        {
            TableHeaders = new[] { "Wavelength (µm)", "Before", "After" },
            TableRows = rows,
            Lines = new[] { "Relative to the system's air." }
        });

        // ---- Element volume of the element this surface fronts ----
        var element2 = geometry.Elements(cancellation).FirstOrDefault(r => r.Front == j);
        if (element2 is not null)
        {
            sections.Add(LensGeometry.ElementSection(new[] { element2 }, "Element volume"));
            assumptions.Add(LensGeometry.VolumeAssumption);
        }
        sections.Add(new ReportSection("Not in this report")
        {
            Lines = new[] { "Best fit glass for a model glass (OpticStudio lists the catalog glass of least RMS index difference): not built." }
        });
        assumptions.Add("Edge thickness: the axial distance to the next surface plus its sag minus this surface's sag at this surface's clear semi-diameter.");
        assumptions.AddRange(notes);
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions);
    }
}
