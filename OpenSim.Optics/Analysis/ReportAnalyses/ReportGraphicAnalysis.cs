// OP-44
using System.ComponentModel;
using OpenSim.Core.PostProcessing;

namespace OpenSim.Optics.Analysis;

/// <summary>The number of positions of a <see cref="ReportGraphicAnalysis"/> page (OpticStudio's
/// 2x2, 2x3 and 3x3 reports).</summary>
public enum ReportGraphicLayout
{
    TwoByTwo,
    TwoByThree,
    ThreeByThree,
}

/// <summary>Settings of the <see cref="ReportGraphicAnalysis"/>: the layout and the saved id of
/// the analysis at each position (blank for an empty position).</summary>
public sealed record ReportGraphicSettings
{
    [DisplayName("Layout")]
    [Description("2x2 (4 positions), 2x3 (6) or 3x3 (9).")]
    public ReportGraphicLayout Layout { get; init; } = ReportGraphicLayout.TwoByTwo;

    [DisplayName("Position 1")] [Description("Saved id of the analysis at this position; blank for none.")]
    public string Position1 { get; init; } = SystemSummaryGraphicAnalysis.SavedId;
    [DisplayName("Position 2")] [Description("Saved id of the analysis at this position; blank for none.")]
    public string Position2 { get; init; } = GaussianBeamAnalysis.SavedId;
    [DisplayName("Position 3")] [Description("Saved id of the analysis at this position; blank for none.")]
    public string Position3 { get; init; } = CardinalPointsAnalysis.SavedId;
    [DisplayName("Position 4")] [Description("Saved id of the analysis at this position; blank for none.")]
    public string Position4 { get; init; } = SingleRayTraceAnalysis.SavedId;
    [DisplayName("Position 5")] [Description("Saved id of the analysis at this position; blank for none.")]
    public string Position5 { get; init; } = "";
    [DisplayName("Position 6")] [Description("Saved id of the analysis at this position; blank for none.")]
    public string Position6 { get; init; } = "";
    [DisplayName("Position 7")] [Description("Saved id of the analysis at this position; blank for none.")]
    public string Position7 { get; init; } = "";
    [DisplayName("Position 8")] [Description("Saved id of the analysis at this position; blank for none.")]
    public string Position8 { get; init; } = "";
    [DisplayName("Position 9")] [Description("Saved id of the analysis at this position; blank for none.")]
    public string Position9 { get; init; } = "";

    /// <summary>The ids of the positions the layout shows, in row order.</summary>
    public IReadOnlyList<string> Positions() =>
        new[] { Position1, Position2, Position3, Position4, Position5, Position6, Position7, Position8, Position9 }
            .Take(Layout switch { ReportGraphicLayout.TwoByTwo => 4, ReportGraphicLayout.TwoByThree => 6, _ => 9 })
            .Select(p => (p ?? "").Trim()).ToArray();
}

/// <summary>
/// OpticStudio's Report Graphic (2025 R2 help, "Report Graphic": "simultaneously displays 4, 6,
/// or 9 analyses", graphic or text, chosen per position, "an analysis does not need to be open in
/// a separate window"): each position's analysis is computed with the settings it was last used
/// with in this project (or its defaults), and its plots and sections are gathered in position
/// order, each titled with its position (docs/Optics-Conventions.md §29). A position whose
/// analysis fails shows the failure instead of a result. The page arrangement (rows and columns,
/// OpticStudio's Top Left / Centered / Clustered layout modes), per-position settings within the
/// report, and saving a named report (.22, .23, .33 files) are not built: the window lists the
/// plots in position order.
/// </summary>
public sealed class ReportGraphicAnalysis : OpticsAnalysis<ReportGraphicSettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "ReportGraphic";

    private readonly Func<string, IOpticsAnalysis?> _resolve;

    /// <param name="resolve">Finds an analysis by its saved id (the registry), null when none has it.</param>
    public ReportGraphicAnalysis(Func<string, IOpticsAnalysis?> resolve)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    public override string Id => SavedId;
    public override string Name => "Report graphic";
    public override string Category => "Reports";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, ReportGraphicSettings settings,
        CancellationToken cancellation)
    {
        var positions = settings.Positions();
        var plots = new List<AnalysisPlot>();
        var sections = new List<ReportSection>();
        var assumptions = new List<string>();
        var index = new List<IReadOnlyList<string>>();
        for (int i = 0; i < positions.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            string id = positions[i];
            string where = $"Position {i + 1}";
            if (id.Length == 0)
            {
                index.Add(new[] { (i + 1).ToString(ReportText.Ci), "—", "empty" });
                continue;
            }
            if (id == SavedId)
                throw new ArgumentException($"Report graphic, {where}: a report graphic cannot contain itself.");
            var analysis = _resolve(id)
                ?? throw new ArgumentException($"Report graphic, {where}: no analysis has the saved id '{id}'.");
            object analysisSettings = analysis.CreateDefaultSettings();
            string settingsNote = "default settings";
            if (context.System.Analyses?.LastSettings is { } last && last.TryGetValue(id, out var saved))
            {
                analysisSettings = OpticsAnalysisSettingsCodec.Load(analysis, saved, out string? note);
                settingsNote = note ?? "the settings it was last used with";
            }
            OpticsAnalysisResult result;
            try
            {
                result = analysis.Compute(context, analysisSettings, cancellation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string message = ex.Message.Replace("\r", "").Replace('\n', ' ');
                sections.Add(new ReportSection($"{i + 1}. {analysis.Name}") { Lines = new[] { "Failed: " + message } });
                index.Add(new[] { (i + 1).ToString(ReportText.Ci), analysis.Name, "failed" });
                assumptions.Add($"{where} ({analysis.Name}) failed: {message}");
                continue;
            }
            index.Add(new[] { (i + 1).ToString(ReportText.Ci), analysis.Name,
                $"{result.Plots.Count} plot(s), {result.Sections.Count} section(s); {settingsNote}" });
            foreach (var plot in result.Plots) plots.Add(plot with { Title = $"{i + 1}. {analysis.Name}: {plot.Title}" });
            foreach (var section in result.Sections) sections.Add(section with { Heading = $"{i + 1}. {analysis.Name}: {section.Heading}" });
            foreach (var a in result.Assumptions) if (!assumptions.Contains(a)) assumptions.Add(a);
        }
        sections.Insert(0, new ReportSection("Positions")
        {
            TableHeaders = new[] { "Position", "Analysis", "Result" },
            TableRows = index,
            Lines = new[] { $"Layout {LayoutName(settings.Layout)}, positions in row order; the window lists the plots in this order." },
        });
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions, plots);
    }

    private static string LayoutName(ReportGraphicLayout layout) => layout switch
    {
        ReportGraphicLayout.TwoByTwo => "2x2",
        ReportGraphicLayout.TwoByThree => "2x3",
        _ => "3x3",
    };
}
