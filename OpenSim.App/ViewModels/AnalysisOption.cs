namespace OpenSim.App.ViewModels;

/// <summary>The analysis workflows the app can run.</summary>
public enum AnalysisType
{
    Static,
    Electrical,
    Thermal,
    JouleCoupled,
    TransientThermal,
    Modal,
    AcElectrical
}

/// <summary>The task-focused workspaces the main window can show.</summary>
public enum WorkspaceKind
{
    /// <summary>Generic geometry (primitives, STL): structural + thermal analyses.</summary>
    Mechanical,

    /// <summary>PCB workflow + DC electrical + Joule heating (also on generic geometry).</summary>
    Electrical
}

/// <summary>A display entry for the analysis-type selector.</summary>
public sealed record AnalysisOption(string Label, AnalysisType Kind)
{
    public static readonly IReadOnlyList<AnalysisOption> All = new[]
    {
        new AnalysisOption("Static (structural)", AnalysisType.Static),
        new AnalysisOption("Electrical (DC conduction)", AnalysisType.Electrical),
        new AnalysisOption("Thermal (steady-state)", AnalysisType.Thermal),
        new AnalysisOption("Joule heating (electrical → thermal)", AnalysisType.JouleCoupled),
        new AnalysisOption("Thermal (transient)", AnalysisType.TransientThermal),
        new AnalysisOption("Modal (natural frequencies)", AnalysisType.Modal),
        new AnalysisOption("Electrical (AC sweep — quasistatic)", AnalysisType.AcElectrical)
    };

    private static AnalysisOption Of(AnalysisType kind) => All.First(o => o.Kind == kind);

    /// <summary>The analyses a workspace offers in its analysis picker.</summary>
    public static IReadOnlyList<AnalysisOption> ForWorkspace(WorkspaceKind workspace) =>
        workspace switch
        {
            WorkspaceKind.Mechanical => new[]
            {
                Of(AnalysisType.Static), Of(AnalysisType.Modal),
                Of(AnalysisType.Thermal), Of(AnalysisType.TransientThermal)
            },
            _ => new[]
            {
                Of(AnalysisType.Electrical), Of(AnalysisType.AcElectrical), Of(AnalysisType.JouleCoupled)
            }
        };

    /// <summary>The workspace an analysis type naturally belongs to.</summary>
    public static WorkspaceKind WorkspaceOf(AnalysisType kind) =>
        kind is AnalysisType.Electrical or AnalysisType.JouleCoupled or AnalysisType.AcElectrical
            ? WorkspaceKind.Electrical
            : WorkspaceKind.Mechanical;
}
