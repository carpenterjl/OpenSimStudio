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

    /// <summary>The ACTIVE body — the one the meshing, material and boundary-condition
    /// panels edit and the single-body analyses solve. A project can hold several
    /// (a STEP assembly); see <see cref="Bodies"/>.</summary>
    public Body Body { get; set; }

    /// <summary>Every body in the project, in import order. That order is load-bearing:
    /// it is the region-id order the merged assembly mesh uses, so it must not be
    /// resorted for display.</summary>
    public IReadOnlyList<Body> Bodies => Project.Bodies;

    /// <summary>
    /// The merged assembly mesh the last multi-body solve ran on, kept so the result scene
    /// and viewport picking can map merged nodes/faces back to bodies. Session-transient
    /// like the results themselves — never serialized, dropped whenever the geometry or a
    /// mesh changes underneath it.
    /// </summary>
    public FeMeshAssembler.AssembledMesh? AssembledMesh { get; private set; }

    /// <summary>Publishes the merged mesh a solve just ran on.</summary>
    public void SetAssembledMesh(FeMeshAssembler.AssembledMesh? assembled) => AssembledMesh = assembled;

    /// <summary>
    /// Splits an assembly-wide face id into the body that owns it and that body's own local
    /// face id — the mapping a viewport click needs to say "you picked part 3, face 5".
    /// Returns null for a single-body project, where face ids are already body-local and
    /// every consumer must keep behaving exactly as it always has.
    /// </summary>
    public (int BodyIndex, int LocalFaceId)? ResolveBodyForFace(int faceId)
    {
        if (Bodies.Count < 2 || faceId < 0) return null;
        var bases = FeMeshAssembler.FaceIdBases(Bodies);
        for (int b = bases.Length - 1; b >= 0; b--)
            if (faceId >= bases[b])
                return (b, faceId - bases[b]);
        return null;
    }

    /// <summary>Raised when bodies are added, removed, or renamed (assembly import,
    /// project load) so the body list rebuilds.</summary>
    public event EventHandler? BodiesChanged;

    /// <summary>Raised after <see cref="Body"/> switches to another body of the project.</summary>
    public event EventHandler? ActiveBodyChanged;

    public void RaiseBodiesChanged() => BodiesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Makes <paramref name="body"/> the active one. The body must belong to the
    /// project — silently accepting a stranger would leave the panels editing an object
    /// that is never solved or saved.</summary>
    public void SelectActiveBody(Body body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Project.Bodies.Contains(body))
            throw new ArgumentException($"Body '{body.Name}' does not belong to this project.", nameof(body));
        if (ReferenceEquals(Body, body)) return;
        Body = body;
        ActiveBodyChanged?.Invoke(this, EventArgs.Empty);
    }

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
    [NotifyPropertyChangedFor(nameof(IsEnvironmentThermalAnalysis))]
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
        is { Kind: AnalysisType.Thermal or AnalysisType.JouleCoupled or AnalysisType.TransientThermal
                or AnalysisType.EnvironmentThermal };
    public bool IsTransientThermalAnalysis => SelectedAnalysis
        is { Kind: AnalysisType.TransientThermal };
    public bool IsModalAnalysis => SelectedAnalysis is { Kind: AnalysisType.Modal };
    public bool IsAcElectricalAnalysis => SelectedAnalysis is { Kind: AnalysisType.AcElectrical };
    public bool IsJouleAnalysis => SelectedAnalysis is { Kind: AnalysisType.JouleCoupled };
    public bool IsEnvironmentThermalAnalysis => SelectedAnalysis
        is { Kind: AnalysisType.EnvironmentThermal };

    /// <summary>The transient settings apply to the transient-thermal analysis, to the
    /// Joule study's optional transient thermal leg, and to environment heat flow (which
    /// is a transient study by nature — the user watches the parts warm up).</summary>
    public bool ShowsTransientSettings =>
        IsTransientThermalAnalysis || IsJouleAnalysis || IsEnvironmentThermalAnalysis;

    [ObservableProperty] private Material? _selectedMaterial;

    /// <summary>The material panel edits the ACTIVE body, so the choice is written through
    /// immediately. Deferring it to save/solve time was harmless with one body; with an
    /// assembly it would attribute the choice to whichever body happened to be active when
    /// the deferred write ran.</summary>
    partial void OnSelectedMaterialChanged(Material? value)
    {
        if (value is not null) Body.Material = value;
    }

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

    public void RaiseGeometryReplaced(bool leavingPcbMode)
    {
        AssembledMesh = null;   // the merge described geometry that no longer exists
        GeometryReplaced?.Invoke(this, new GeometryReplacedEventArgs { LeavingPcbMode = leavingPcbMode });
    }

    public void RaiseMeshChanged()
    {
        AssembledMesh = null;   // re-meshing one body invalidates the merge it took part in
        MeshChanged?.Invoke(this, EventArgs.Empty);
    }

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
