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

    /// <summary>Electrostatics: conductors as equipotentials in a dielectric; capacitance
    /// matrix, charge per electrode, E-field and dielectric stress.</summary>
    Electrostatic,

    /// <summary>Multi-body transient heat flow in an environment (vacuum, still air, or a
    /// moving fluid): radiation and convection come from the environment settings rather
    /// than hand-entered film coefficients.</summary>
    EnvironmentThermal,

    /// <summary>Conjugate heat transfer: the surrounding airflow is actually COMPUTED
    /// (first-party laminar CFD on a voxel grid) and its wall heat flux drives the solid
    /// conduction solve — Stage 2 of the environment track, replacing the Stage 1
    /// correlations with a resolved flow field.</summary>
    ConjugateHeatFlow,

    /// <summary>Antenna method-of-moments study (Zin sweep, near/far field). A UI-dispatch
    /// kind: the ribbon routes Solve to the antenna view model rather than an ISolver.</summary>
    Antenna,

    /// <summary>Coupled-line signal integrity (RLGC, S-parameters, PRBS transient + eye).
    /// A UI-dispatch kind like <see cref="Antenna"/>.</summary>
    SignalIntegrity
}

/// <summary>The task-focused workspaces the main window can show — one per physics,
/// matching the design's six-workspace nav rail.</summary>
public enum WorkspaceKind
{
    /// <summary>Generic geometry (primitives, STL, STEP): static + modal analysis.</summary>
    Structural,

    /// <summary>Steady-state and transient heat conduction on a single part.</summary>
    Thermal,

    /// <summary>PCB workflow + DC/AC electrical + Joule heating (also on generic geometry).</summary>
    Electrical,

    /// <summary>Antenna simulation: thin-wire and RWG surface method of moments.</summary>
    Rf,

    /// <summary>Coupled-line signal integrity: RLGC, S-parameters, eyes, board nets.</summary>
    SignalIntegrity,

    /// <summary>Multi-body assemblies in an environment: transient heat flow and the
    /// computed airflow that drives it.</summary>
    Flow
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
        new AnalysisOption("Electrostatic (capacitance, E-field)", AnalysisType.Electrostatic),
        new AnalysisOption("Heat flow in an environment", AnalysisType.EnvironmentThermal),
        new AnalysisOption("Heat flow with computed airflow (CFD)", AnalysisType.ConjugateHeatFlow),
        new AnalysisOption("Antenna (Zin sweep)", AnalysisType.Antenna),
        new AnalysisOption("Transient + eye (PRBS)", AnalysisType.SignalIntegrity)
    };

    private static AnalysisOption Of(AnalysisType kind) => All.First(o => o.Kind == kind);

    /// <summary>The analyses a workspace offers in its analysis picker. Every workspace is
    /// listed EXPLICITLY: a default arm would silently hand a new workspace the previous
    /// one's analyses instead of failing where the omission was made.</summary>
    public static IReadOnlyList<AnalysisOption> ForWorkspace(WorkspaceKind workspace) =>
        workspace switch
        {
            WorkspaceKind.Structural => new[]
            {
                Of(AnalysisType.Static), Of(AnalysisType.Modal)
            },
            WorkspaceKind.Thermal => new[]
            {
                Of(AnalysisType.Thermal), Of(AnalysisType.TransientThermal)
            },
            WorkspaceKind.Electrical => new[]
            {
                Of(AnalysisType.Electrical), Of(AnalysisType.AcElectrical), Of(AnalysisType.Electrostatic),
                Of(AnalysisType.JouleCoupled)
            },
            WorkspaceKind.Rf => new[] { Of(AnalysisType.Antenna) },
            WorkspaceKind.SignalIntegrity => new[] { Of(AnalysisType.SignalIntegrity) },
            WorkspaceKind.Flow => new[]
            {
                Of(AnalysisType.EnvironmentThermal), Of(AnalysisType.ConjugateHeatFlow)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(workspace), workspace,
                "No analysis list registered for this workspace.")
        };

    /// <summary>The workspace an analysis type naturally belongs to.</summary>
    public static WorkspaceKind WorkspaceOf(AnalysisType kind) => kind switch
    {
        AnalysisType.Electrical or AnalysisType.JouleCoupled or AnalysisType.AcElectrical
            or AnalysisType.Electrostatic => WorkspaceKind.Electrical,
        AnalysisType.Thermal or AnalysisType.TransientThermal => WorkspaceKind.Thermal,
        AnalysisType.Antenna => WorkspaceKind.Rf,
        AnalysisType.SignalIntegrity => WorkspaceKind.SignalIntegrity,
        AnalysisType.EnvironmentThermal or AnalysisType.ConjugateHeatFlow => WorkspaceKind.Flow,
        _ => WorkspaceKind.Structural
    };
}
