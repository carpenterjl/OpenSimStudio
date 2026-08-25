using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;

namespace OpenSim.App.ViewModels;

/// <summary>The step slots a workspace's study rail can show.</summary>
public enum StepKind { Geometry, Mesh, Environment, Setup, Solve, Results }

/// <summary>The display state of one rail step.</summary>
public enum StepState { Locked, Available, Done }

/// <summary>One row of the study-steps rail.</summary>
public sealed partial class StudyStep : ObservableObject
{
    public StudyStep(StepKind kind, int number, string title)
    {
        Kind = kind;
        Number = number;
        Title = title;
    }

    public StepKind Kind { get; }
    public int Number { get; }
    public string Title { get; }

    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private StepState _state = StepState.Locked;
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// The gated study-steps rail (design screens 02–16): each workspace shows the ordered
/// steps its physics needs, a step unlocks when its prerequisites exist in the session,
/// and the SELECTED step decides which panels the properties pane shows. Gating is
/// deliberately soft where the solver already validates (Setup never blocks Solve — the
/// Solve button's own validation carries the actionable message); it is hard only where
/// acting is impossible (meshing without geometry).
/// </summary>
public partial class StudyRailViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private bool _hasResults;
    private int _resultFieldCount;
    private string _solveSummary = "";

    public StudyRailViewModel(ProjectSession session)
    {
        _session = session;
        session.GeometryReplaced += (_, _) => { _hasResults = false; _solveSummary = ""; RefreshAndAdvance(); };
        session.MeshChanged += (_, _) => RefreshAndAdvance();
        session.BodiesChanged += (_, _) => Refresh();
        session.ResultsProduced += (_, e) =>
        {
            _hasResults = true;
            _resultFieldCount = e.Fields.Count;
            Refresh();
            SelectStep(StepKind.Results);
        };
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProjectSession.ActiveWorkspace))
                RebuildSteps();
            else if (e.PropertyName is nameof(ProjectSession.SelectedAnalysis)
                     or nameof(ProjectSession.IsPcbMode))
                Refresh();
        };
        RebuildSteps();
    }

    public ObservableCollection<StudyStep> Steps { get; } = new();

    [ObservableProperty] private StudyStep? _selectedStep;

    /// <summary>The active step's kind as a string ("Setup", …) for XAML visibility
    /// triggers — panels compare against it with a plain DataTrigger.</summary>
    public string ActiveStepName => SelectedStep?.Kind.ToString() ?? "";

    partial void OnSelectedStepChanged(StudyStep? oldValue, StudyStep? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        OnPropertyChanged(nameof(ActiveStepName));
    }

    /// <summary>Rail click: locked steps refuse (the rail row shows why).</summary>
    [RelayCommand]
    private void SelectStep(StudyStep? step)
    {
        if (step is null || step.State == StepState.Locked) return;
        SelectedStep = step;
    }

    private void SelectStep(StepKind kind)
    {
        var step = Steps.FirstOrDefault(s => s.Kind == kind);
        if (step is not null && step.State != StepState.Locked) SelectedStep = step;
    }

    /// <summary>
    /// Recompute, and when the ACTIVE step just completed, advance the selection to the
    /// first step that still needs the user — the natural forward flow after an import
    /// or a mesh (the design's coach-card language). A user-selected later step is never
    /// yanked backwards: advancing only happens off a completed active step.
    /// </summary>
    private void RefreshAndAdvance()
    {
        Refresh();
        if (SelectedStep is { State: StepState.Done })
        {
            var next = Steps.FirstOrDefault(s => s.State == StepState.Available);
            if (next is not null) SelectedStep = next;
        }
    }

    /// <summary>Solve completion summary for the rail row (set by the shell when a solve
    /// finishes; the rail itself never runs solves).</summary>
    public void ReportSolveSummary(string summary)
    {
        _solveSummary = summary;
        Refresh();
    }

    private static readonly StepKind[] SolverSteps =
        { StepKind.Geometry, StepKind.Mesh, StepKind.Setup, StepKind.Solve, StepKind.Results };
    private static readonly StepKind[] FlowSteps =
        { StepKind.Geometry, StepKind.Mesh, StepKind.Environment, StepKind.Setup, StepKind.Solve, StepKind.Results };
    private static readonly StepKind[] RailFreeSteps =
        { StepKind.Setup, StepKind.Solve, StepKind.Results };

    private void RebuildSteps()
    {
        // RF and SI need no mesh (MoM works from centerlines; SI from cross-sections),
        // so their rails skip straight to Setup — the design's "the rail drops it" rule.
        var kinds = _session.ActiveWorkspace switch
        {
            WorkspaceKind.Flow => FlowSteps,
            WorkspaceKind.Rf or WorkspaceKind.SignalIntegrity => RailFreeSteps,
            _ => SolverSteps
        };

        Steps.Clear();
        var number = 1;
        foreach (var kind in kinds)
            Steps.Add(new StudyStep(kind, number++, kind.ToString()));

        SelectedStep = null;
        Refresh();
        // Land on the first step that still needs the user.
        SelectedStep = Steps.FirstOrDefault(s => s.State == StepState.Available)
                       ?? Steps.FirstOrDefault(s => s.State != StepState.Locked)
                       ?? Steps.FirstOrDefault();
        if (SelectedStep is not null) SelectedStep.IsActive = true;
        OnPropertyChanged(nameof(ActiveStepName));
    }

    private void Refresh()
    {
        bool hasGeometry = _session.IsPcbMode
                           || _session.Bodies.Any(b => b.Geometry is not null);
        bool hasMesh = _session.Bodies.Any(b => b.Mesh is not null);
        bool hasConditions = _session.Bodies.Any(b => b.BoundaryConditions.Count > 0);
        bool railFree = _session.ActiveWorkspace is WorkspaceKind.Rf or WorkspaceKind.SignalIntegrity;

        foreach (var step in Steps)
        {
            switch (step.Kind)
            {
                case StepKind.Geometry:
                    step.State = hasGeometry ? StepState.Done : StepState.Available;
                    step.Detail = GeometryDetail(hasGeometry);
                    break;
                case StepKind.Mesh:
                    step.State = !hasGeometry ? StepState.Locked
                        : hasMesh ? StepState.Done : StepState.Available;
                    step.Detail = !hasGeometry ? "Needs geometry" : MeshDetail(hasMesh);
                    break;
                case StepKind.Environment:
                    step.State = !hasGeometry ? StepState.Locked
                        : _session.Project.Environment is not null ? StepState.Done : StepState.Available;
                    step.Detail = !hasGeometry ? "Needs geometry"
                        : _session.Project.Environment is not null ? "Medium and ambient set"
                        : "Medium and ambient";
                    break;
                case StepKind.Setup:
                    step.State = railFree ? StepState.Available
                        : !hasMesh ? StepState.Locked
                        : hasConditions ? StepState.Done : StepState.Available;
                    step.Detail = railFree ? "Study parameters"
                        : !hasMesh ? "Needs a mesh"
                        : hasConditions ? ConditionsDetail() : "Material and conditions";
                    break;
                case StepKind.Solve:
                    step.State = railFree ? StepState.Available
                        : !hasMesh ? StepState.Locked
                        : _hasResults ? StepState.Done : StepState.Available;
                    step.Detail = !railFree && !hasMesh ? "Needs a mesh"
                        : _hasResults && _solveSummary.Length > 0 ? _solveSummary
                        : _hasResults ? "Solved"
                        : "Run the study";
                    break;
                case StepKind.Results:
                    step.State = _hasResults ? StepState.Done : StepState.Locked;
                    step.Detail = _hasResults
                        ? $"{_resultFieldCount} field(s) · solved"
                        : "No solution yet";
                    break;
            }
        }

        OnPropertyChanged(nameof(StudyLines));
    }

    private string GeometryDetail(bool hasGeometry)
    {
        if (_session.IsPcbMode) return "Board loaded";
        if (!hasGeometry) return "Import or create a part";
        var bodies = _session.Bodies.Count(b => b.Geometry is not null);
        return bodies == 1
            ? _session.Body.GeometrySource ?? "1 body"
            : $"{bodies} bodies";
    }

    private string MeshDetail(bool hasMesh)
    {
        if (!hasMesh) return _session.IsPcbMode ? "Mesh the selected net" : "Mesh the part";
        var meshed = _session.Bodies.Where(b => b.Mesh is not null).Select(b => b.Mesh!).ToList();
        var elements = meshed.Sum(m => m.ElementCount);
        if (meshed.Any(m => m.IsHex))
            return $"{elements:N0} hexes · HEX20";
        var quadratic = meshed.Any(m => m.MidEdgeNodes is not null);
        return $"{elements:N0} tets{(quadratic ? " · TET10" : "")}";
    }

    private string ConditionsDetail()
    {
        var count = _session.Bodies.Sum(b => b.BoundaryConditions.Count);
        return $"{count} condition(s)";
    }

    /// <summary>The "Study" summary card at the rail's foot: workspace-appropriate
    /// key/value lines.</summary>
    public IReadOnlyList<StudyLine> StudyLines
    {
        get
        {
            var lines = new List<StudyLine>
            {
                new(_session.IsPcbMode ? "Board" : _session.Bodies.Count > 1 ? "Assembly" : "Part",
                    _session.Project.Name)
            };
            if (_session.Bodies.Count > 1)
                lines.Add(new("Bodies", _session.Bodies.Count.ToString()));
            if (!_session.IsPcbMode && _session.Body.Material is { } material)
                lines.Add(new("Material", material.Name));
            lines.Add(new("Units", _session.ActiveWorkspace switch
            {
                WorkspaceKind.Structural => "SI (m, N, Pa)",
                WorkspaceKind.Thermal or WorkspaceKind.Flow => "SI (m, W, K)",
                _ => "SI (m, K, V)"
            }));
            return lines;
        }
    }

    public sealed record StudyLine(string Key, string Value);
}
