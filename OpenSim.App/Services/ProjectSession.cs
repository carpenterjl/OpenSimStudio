using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Core.Results;

namespace OpenSim.App.Services;

/// <summary>Payload for <see cref="ProjectSession.GeometryReplaced"/>.</summary>
public sealed class GeometryReplacedEventArgs : EventArgs
{
    /// <summary>True when the replacement returns to the generic workflow (primitive,
    /// STL, project open) so the PCB view model tears its board state down. False for
    /// replacements inside the PCB workflow (board import, net meshing).</summary>
    public required bool LeavingPcbMode { get; init; }
}

/// <summary>Payload for <see cref="ProjectSession.ResultsProduced"/>. An empty
/// field list clears the current results.</summary>
public sealed class ResultsProducedEventArgs : EventArgs
{
    public required IReadOnlyList<IResultField> Fields { get; init; }

    /// <summary>Analysis that produced the fields — picks the default display field.</summary>
    public AnalysisType? Analysis { get; init; }

    /// <summary>Exact field name to select, tried before the analysis default.</summary>
    public string? PreferFieldName { get; init; }

    /// <summary>Multi-frame results (time steps / modes / frequency points); null for
    /// single-result solves. <see cref="Fields"/> is the default frame's field list.</summary>
    public IReadOnlyList<ResultFrame>? Frames { get; init; }

    /// <summary>Frame axis caption ("Time", "Mode", "Frequency"); null without frames.</summary>
    public string? FrameAxis { get; init; }
}

/// <summary>
/// The shared per-application state every workflow view model reads and mutates: the
/// open project/body, the chosen analysis and material, busy/status reporting, and the
/// face selection. Cross-cutting moments (geometry replaced, mesh changed, results
/// produced…) are typed events raised here so view models never subscribe to each other;
/// the few sanctioned read-only VM→VM references are documented on MainViewModel.
/// </summary>
public partial class ProjectSession : ObservableObject
{
    private readonly ILogService _log;

    public ProjectSession(ILogService log)
    {
        _log = log;
        Project = new SimProject();
        Body = new Body { Name = "Body 1" };
        Project.Bodies.Add(Body);
    }

    public SimProject Project { get; set; }
    public Body Body { get; set; }

    /// <summary>Faces picked in the viewport, targets for new boundary conditions.</summary>
    public ObservableCollection<int> SelectedFaces { get; } = new();

    /// <summary>The analyses offered by the active workspace's analysis picker.</summary>
    public IReadOnlyList<AnalysisOption> AnalysisOptions => AnalysisOption.ForWorkspace(ActiveWorkspace);

    /// <summary>True while the start (project) screen covers the workspace UI.</summary>
    [ObservableProperty] private bool _isHomeActive = true;

    [ObservableProperty] private WorkspaceKind _activeWorkspace = WorkspaceKind.Mechanical;

    partial void OnActiveWorkspaceChanged(WorkspaceKind value)
    {
        OnPropertyChanged(nameof(AnalysisOptions));
        if (!AnalysisOptions.Contains(SelectedAnalysis))
            SelectedAnalysis = AnalysisOptions[0];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStructuralAnalysis))]
    [NotifyPropertyChangedFor(nameof(IsElectricalAnalysis))]
    [NotifyPropertyChangedFor(nameof(IsThermalAnalysis))]
    [NotifyPropertyChangedFor(nameof(IsTransientThermalAnalysis))]
    [NotifyPropertyChangedFor(nameof(IsModalAnalysis))]
    [NotifyPropertyChangedFor(nameof(IsAcElectricalAnalysis))]
    [NotifyPropertyChangedFor(nameof(IsJouleAnalysis))]
    [NotifyPropertyChangedFor(nameof(ShowsTransientSettings))]
    private AnalysisOption _selectedAnalysis = AnalysisOption.All[0];

    partial void OnSelectedAnalysisChanged(AnalysisOption value)
    {
        // A workspace switch resets the picker's ItemsSource, and WPF momentarily pushes
        // a null SelectedItem through the two-way binding. Coerce straight back so the
        // selection is never observably null.
        if (value is null)
            SelectedAnalysis = AnalysisOptions[0];
    }

    // Null-tolerant pattern matches: see OnSelectedAnalysisChanged for the transient.
    public bool IsStructuralAnalysis => SelectedAnalysis
        is { Kind: AnalysisType.Static or AnalysisType.Modal };
    public bool IsElectricalAnalysis => SelectedAnalysis
        is { Kind: AnalysisType.Electrical or AnalysisType.JouleCoupled or AnalysisType.AcElectrical };
    public bool IsThermalAnalysis => SelectedAnalysis
        is { Kind: AnalysisType.Thermal or AnalysisType.JouleCoupled or AnalysisType.TransientThermal };
    public bool IsTransientThermalAnalysis => SelectedAnalysis
        is { Kind: AnalysisType.TransientThermal };
    public bool IsModalAnalysis => SelectedAnalysis is { Kind: AnalysisType.Modal };
    public bool IsAcElectricalAnalysis => SelectedAnalysis is { Kind: AnalysisType.AcElectrical };
    public bool IsJouleAnalysis => SelectedAnalysis is { Kind: AnalysisType.JouleCoupled };

    /// <summary>The transient settings apply to the transient-thermal analysis and to the
    /// Joule study's optional transient thermal leg.</summary>
    public bool ShowsTransientSettings => IsTransientThermalAnalysis || IsJouleAnalysis;

    [ObservableProperty] private Material? _selectedMaterial;

    /// <summary>True from PCB import until the user returns to generic geometry (primitive,
    /// STL, or project open). Hides the primitive/meshing/material panels, which don't
    /// apply to the board workflow (the net mesher picks its own materials).</summary>
    [ObservableProperty] private bool _isPcbMode;

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressFraction;

    /// <summary>Raised after the active body/geometry is replaced (primitive, STL,
    /// board import, net mesh, project open) so dependents re-sync and the view refits.</summary>
    public event EventHandler<GeometryReplacedEventArgs>? GeometryReplaced;

    /// <summary>Raised after <see cref="Body"/>.Mesh is generated, loaded, or cleared.</summary>
    public event EventHandler? MeshChanged;

    /// <summary>Raised when a solve produces (or clears) result fields.</summary>
    public event EventHandler<ResultsProducedEventArgs>? ResultsProduced;

    /// <summary>Raised when face paint state (selection, electrodes) changed.</summary>
    public event EventHandler? HighlightsInvalidated;

    public void RaiseGeometryReplaced(bool leavingPcbMode) =>
        GeometryReplaced?.Invoke(this, new GeometryReplacedEventArgs { LeavingPcbMode = leavingPcbMode });

    public void RaiseMeshChanged() => MeshChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseResultsProduced(IReadOnlyList<IResultField> fields,
        AnalysisType? analysis = null, string? preferFieldName = null,
        IReadOnlyList<ResultFrame>? frames = null, string? frameAxis = null) =>
        ResultsProduced?.Invoke(this, new ResultsProducedEventArgs
        {
            Fields = fields, Analysis = analysis, PreferFieldName = preferFieldName,
            Frames = frames, FrameAxis = frameAxis
        });

    public void RaiseHighlightsInvalidated() => HighlightsInvalidated?.Invoke(this, EventArgs.Empty);

    public void ReportError(Exception ex)
    {
        _log.Append($"Error: {ex.Message}");
        StatusText = "Error — see log";
    }
}
