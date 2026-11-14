using System.ComponentModel;
using OpenSim.Core.PostProcessing;

namespace OpenSim.Optics.Analysis;

/// <summary>Settings of the <see cref="SystemSummaryAnalysis"/>.</summary>
public sealed record SystemSummarySettings
{
    /// <summary>The unit lengths are listed in.</summary>
    [DisplayName("Lens units")]
    public LensUnit Units { get; init; } = LensUnit.Millimeter;

    /// <summary>Whether to list the conventions the program traces with.</summary>
    [DisplayName("List conventions")]
    public bool ShowConventions { get; init; } = true;
}

/// <summary>
/// A text summary of the optical system: the project, the mode and the conventions the trace
/// uses. The analysis framework's first analysis (OP-9), there to carry a window end to end;
/// the system data report of OP-44 grows out of it as the system editors add aperture, fields
/// and wavelengths.
/// </summary>
public sealed class SystemSummaryAnalysis : OpticsAnalysis<SystemSummarySettings>
{
    /// <summary>The saved id; see <see cref="IOpticsAnalysis.Id"/>.</summary>
    public const string SavedId = "SystemSummary";

    public override string Id => SavedId;
    public override string Name => "System summary";
    public override string Category => "Reports";

    protected override OpticsAnalysisResult Compute(OpticsAnalysisContext context, SystemSummarySettings settings,
        CancellationToken cancellation)
    {
        string unit = UnitName(settings.Units);
        var sections = new List<ReportSection>
        {
            new("System")
            {
                Pairs = new[]
                {
                    ("Project", context.ProjectName),
                    ("Mode", OpticsModes.Label(context.Mode)),
                    ("Lens units", unit),
                }
            }
        };
        if (settings.ShowConventions)
            sections.Add(new ReportSection("Conventions")
            {
                Lines = new[]
                {
                    $"Lengths are held in meters and listed in {unit} (1 {unit} = {StudyReport.Number(OpticsConventions.MetersPer(settings.Units))} m).",
                    "Wavelengths are listed in micrometers; rays carry the vacuum wavelength.",
                    "A radius is positive when its centre of curvature lies toward +z of the vertex.",
                    "The conventions in full: docs/Optics-Conventions.md.",
                }
            });
        cancellation.ThrowIfCancellationRequested();
        var assumptions = new[] { "Nothing is traced: the system editors are later plan items." };
        return OpticsAnalysisResult.FromSections(Name, sections, assumptions);
    }

    private static string UnitName(LensUnit unit) => unit switch
    {
        LensUnit.Millimeter => "mm",
        LensUnit.Centimeter => "cm",
        LensUnit.Inch => "in",
        LensUnit.Meter => "m",
        _ => unit.ToString()
    };
}
