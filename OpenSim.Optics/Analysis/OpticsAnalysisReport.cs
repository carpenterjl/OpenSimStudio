using OpenSim.Core.PostProcessing;

namespace OpenSim.Optics.Analysis;

/// <summary>One window's part in a composite report: its title, and its last result or why there is none.</summary>
public sealed record OpticsAnalysisReportEntry(string Title, OpticsAnalysisResult? Result, string Status);

/// <summary>
/// The composite report page (OpticStudio's Report Graphic): every open analysis window's last
/// result in one <see cref="StudyReport"/>, in window order, each result's sections under its
/// window's title and every assumption once. A window with no result is listed with its status
/// rather than left out, so the page never looks more complete than it is.
/// </summary>
public static class OpticsAnalysisReport
{
    public static StudyReport Compose(string projectName, OpticsMode mode, IReadOnlyList<OpticsAnalysisReportEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var inputs = new List<(string, string)>
        {
            ("Project", projectName),
            ("Mode", OpticsModes.Label(mode)),
            ("Analyses", entries.Count == 0 ? "none open" : string.Join(", ", entries.Select(e => e.Title))),
        };
        var sections = new List<ReportSection>();
        var assumptions = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Result is not { } result)
            {
                sections.Add(new ReportSection(entry.Title) { Lines = new[] { $"No result: {entry.Status}" } });
                continue;
            }
            if (result.Sections.Count == 0)
                sections.Add(new ReportSection(entry.Title) { Lines = result.Text.Replace("\r", "").TrimEnd('\n').Split('\n') });
            foreach (var section in result.Sections)
                sections.Add(section with { Heading = $"{entry.Title}: {section.Heading}" });
            if (result.Plots.Count > 0)
                sections.Add(new ReportSection($"{entry.Title}: plots")
                {
                    Lines = result.Plots.Select(p => $"Plot '{p.Title}' (shown in the analysis window).").ToArray()
                });
            foreach (var a in result.Assumptions)
                if (!assumptions.Contains(a)) assumptions.Add(a);
        }
        return new StudyReport
        {
            Title = $"{projectName} — Optics analyses",
            Inputs = inputs, Sections = sections, Assumptions = assumptions
        };
    }
}
