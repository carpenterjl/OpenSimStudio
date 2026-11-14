using OpenSim.Core.PostProcessing;

namespace OpenSim.Optics.Analysis;

/// <summary>
/// What an analysis produces: its text listing (the window's Text tab), its plots as data (the
/// window draws them; the command line can write them), and the same content as report sections
/// with the assumptions it rests on (plan rule 7), for the composite report.
/// </summary>
public sealed record OpticsAnalysisResult
{
    /// <summary>The window's Text tab: a plain listing a reader can copy.</summary>
    public required string Text { get; init; }

    public IReadOnlyList<AnalysisPlot> Plots { get; init; } = Array.Empty<AnalysisPlot>();

    /// <summary>The result as report sections, for the composite report page.</summary>
    public IReadOnlyList<ReportSection> Sections { get; init; } = Array.Empty<ReportSection>();

    /// <summary>What the numbers assume: sampling, reference point, index model.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();

    /// <summary>A result whose text is its sections and assumptions rendered as plain text, the
    /// way the report renders them — so the Text tab and the report can never disagree.</summary>
    public static OpticsAnalysisResult FromSections(string title, IReadOnlyList<ReportSection> sections,
        IReadOnlyList<string>? assumptions = null, IReadOnlyList<AnalysisPlot>? plots = null)
    {
        var assumed = assumptions ?? Array.Empty<string>();
        var text = new StudyReport { Title = title, Sections = sections, Assumptions = assumed }.ToText();
        return new OpticsAnalysisResult
        {
            Text = text, Sections = sections, Assumptions = assumed,
            Plots = plots ?? Array.Empty<AnalysisPlot>()
        };
    }
}
