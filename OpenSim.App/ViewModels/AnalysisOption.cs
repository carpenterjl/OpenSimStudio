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
    AcElectrical,

    /// <summary>Multi-body transient heat flow in an environment (vacuum, still air, or a
    /// moving fluid): radiation and convection come from the environment settings rather
    /// than hand-entered film coefficients.</summary>
    EnvironmentThermal
}

/// <summary>The task-focused workspaces the main window can show.</summary>
public enum WorkspaceKind
{
    /// <summary>Generic geometry (primitives, STL): structural + thermal analyses.</summary>
    Mechanical,

    /// <summary>PCB workflow + DC electrical + Joule heating (also on generic geometry).</summary>
    Electrical,

    /// <summary>Multi-body assemblies in an environment: transient heat flow, and (later)
    /// the airflow that drives it.</summary>
    ThermalFlow
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
        new AnalysisOption("Electrical (AC sweep — quasistatic)", AnalysisType.AcElectrical),
        new AnalysisOption("Heat flow in an environment", AnalysisType.EnvironmentThermal)
    };

    private static AnalysisOption Of(AnalysisType kind) => All.First(o => o.Kind == kind);

    /// <summary>The analyses a workspace offers in its analysis picker. Every workspace is
    /// listed EXPLICITLY: a default arm would silently hand a new workspace the previous
    /// one's analyses instead of failing where the omission was made.</summary>
    public static IReadOnlyList<AnalysisOption> ForWorkspace(WorkspaceKind workspace) =>
        workspace switch
        {
            WorkspaceKind.Mechanical => new[]
            {
                Of(AnalysisType.Static), Of(AnalysisType.Modal),
                Of(AnalysisType.Thermal), Of(AnalysisType.TransientThermal)
            },
            WorkspaceKind.Electrical => new[]
            {
                Of(AnalysisType.Electrical), Of(AnalysisType.AcElectrical), Of(AnalysisType.JouleCoupled)
            },
            WorkspaceKind.ThermalFlow => new[]
            {
                Of(AnalysisType.EnvironmentThermal)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(workspace), workspace,
                "No analysis list registered for this workspace.")
        };

    /// <summary>The workspace an analysis type naturally belongs to.</summary>
    public static WorkspaceKind WorkspaceOf(AnalysisType kind) => kind switch
    {
        AnalysisType.Electrical or AnalysisType.JouleCoupled or AnalysisType.AcElectrical
            => WorkspaceKind.Electrical,
        AnalysisType.EnvironmentThermal => WorkspaceKind.ThermalFlow,
        _ => WorkspaceKind.Mechanical
    };
}
