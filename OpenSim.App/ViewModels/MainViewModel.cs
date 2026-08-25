using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenSim.App.Services;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Composition shell: exposes the per-concern child view models the window binds to and
/// owns the cross-cutting operations — project save/load, home/workspace navigation, and
/// viewport click routing.
///
/// Communication rules: shared state and events live on <see cref="ProjectSession"/>;
/// view models never call each other except these read-only edges:
/// Scene → {Results, Electrodes} (display state), Pcb → {Meshing, Electrodes, Materials}
/// (edge length, pad hand-off, material lookup), Solve/Electrodes → Materials
/// (region-material resolution), Solve → Electrodes (pad-electrode boundary conditions
/// for the AC-sweep/Joule analyses), Solve → Environment (the surroundings the assembly
/// heat-flow study runs in), Pcb → {Inductance, Antenna} (board hand-off for
/// the PEEC self/mutual/loop analysis and the antenna simulator), Antenna → Electrodes
/// (the selected source pad places the feed on net-sourced antennas), Scene → Bodies
/// (per-body visibility for the assembly scene), Scene → Colormap (the colors and value
/// range the result view paints through).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ProjectSerializer _serializer;
    private readonly RecentProjectsService _recentProjects;

    public MainViewModel(ProjectSession session, ILogService log, GeometryViewModel geometry,
        MaterialsViewModel materials, MeshingViewModel meshing, BoundaryConditionsViewModel conditions,
        ScopeSelectionViewModel scopeSelection,
        PcbViewModel pcb, ElectrodesViewModel electrodes, InductanceViewModel inductance,
        AntennaViewModel antenna, SignalIntegrityViewModel signalIntegrity, SolveViewModel solve,
        ResultsViewModel results, SceneViewModel scene, BodiesViewModel bodies,
        EnvironmentViewModel environment, ColormapViewModel colormap,
        FlowVisualizationViewModel flow, CfdSetupViewModel cfdSetup,
        StudyRailViewModel studyRail,
        ProjectSerializer serializer, RecentProjectsService recentProjects,
        ThemeService theme)
    {
        Session = session;
        Log = log;
        Geometry = geometry;
        Materials = materials;
        Meshing = meshing;
        Conditions = conditions;
        ScopeSelection = scopeSelection;
        Pcb = pcb;
        Electrodes = electrodes;
        Inductance = inductance;
        Antenna = antenna;
        SignalIntegrity = signalIntegrity;
        Solve = solve;
        Results = results;
        Scene = scene;
        Bodies = bodies;
        Environment = environment;
        Colormap = colormap;
        Flow = flow;
        CfdSetup = cfdSetup;
        StudyRail = studyRail;
        Theme = theme;
        _serializer = serializer;
        _recentProjects = recentProjects;
        // Switching the active body re-syncs the panels that mirror body state, exactly as
        // opening a project does.
        session.ActiveBodyChanged += (_, _) => AdoptActiveBody();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProjectSession.ActiveWorkspace)
                or nameof(ProjectSession.IsHomeActive))
                RefreshWorkspaceNav();
        };
        RefreshWorkspaceNav();
        RefreshRecentProjects();
        log.Append("Ready. Create a primitive or import an STL file to begin.");
    }

    public ProjectSession Session { get; }
    public ILogService Log { get; }
    public GeometryViewModel Geometry { get; }
    public MaterialsViewModel Materials { get; }
    public MeshingViewModel Meshing { get; }
    public BoundaryConditionsViewModel Conditions { get; }

    /// <summary>Geometric edge and vertex selection for boundary-condition scoping.</summary>
    public ScopeSelectionViewModel ScopeSelection { get; }
    public PcbViewModel Pcb { get; }
    public ElectrodesViewModel Electrodes { get; }
    public InductanceViewModel Inductance { get; }
    public AntennaViewModel Antenna { get; }
    public SignalIntegrityViewModel SignalIntegrity { get; }
    public SolveViewModel Solve { get; }
    public ResultsViewModel Results { get; }
    public SceneViewModel Scene { get; }
    public BodiesViewModel Bodies { get; }
    public EnvironmentViewModel Environment { get; }
    public ColormapViewModel Colormap { get; }
    public FlowVisualizationViewModel Flow { get; }

    /// <summary>The CFD case: domain, fluid, grid and the detected openings.</summary>
    public CfdSetupViewModel CfdSetup { get; }

    /// <summary>The gated study-steps rail; its selected step drives the properties pane.</summary>
    public StudyRailViewModel StudyRail { get; }

    /// <summary>Dark/light theme switching (the ribbon's Theme button).</summary>
    public ThemeService Theme { get; }

    [RelayCommand]
    private void ToggleTheme() => Theme.Toggle();

    /// <summary>One nav-rail row per workspace (icon geometry resolved from the app's
    /// icon dictionary; active state follows the session).</summary>
    public IReadOnlyList<WorkspaceNavItem> WorkspaceNav { get; } = new[]
    {
        new WorkspaceNavItem(WorkspaceKind.Structural, "Struct", "IconBox", "WorkspaceStructuralButton"),
        new WorkspaceNavItem(WorkspaceKind.Thermal, "Thermal", "IconThermometer", "WorkspaceThermalButton"),
        new WorkspaceNavItem(WorkspaceKind.Electrical, "Elec", "IconZap", "WorkspaceElectricalButton"),
        new WorkspaceNavItem(WorkspaceKind.Rf, "RF", "IconRadioTower", "WorkspaceRfButton"),
        new WorkspaceNavItem(WorkspaceKind.SignalIntegrity, "SI", "IconActivity", "WorkspaceSignalIntegrityButton"),
        new WorkspaceNavItem(WorkspaceKind.Flow, "Flow", "IconWind", "WorkspaceFlowButton")
    };

    private void RefreshWorkspaceNav()
    {
        foreach (var item in WorkspaceNav)
            item.IsActive = !Session.IsHomeActive && Session.ActiveWorkspace == item.Kind;
    }

    /// <summary>
    /// Re-points the body-mirroring panels (material, meshing, conditions) at the active
    /// body. Deliberately NOT a geometry replacement: in an assembly the scene already
    /// shows every part, and treating a selection as a replacement would throw away the
    /// results the user is looking at. The previous body keeps its material because the
    /// session writes material choices through as they are made.
    /// </summary>
    private void AdoptActiveBody()
    {
        Materials.AdoptProjectMaterial(Session.Body.Material);
        Meshing.TargetEdgeLength = Session.Body.MeshSettings.TargetEdgeLength;
        // Face ids mean different things on different bodies, so the selection cannot
        // survive the switch.
        Session.SelectedFaces.Clear();
        Conditions.ResyncFromBody();
        Geometry.RefreshFromBody();
        Session.RaiseMeshChanged();
        Session.RaiseHighlightsInvalidated();
    }

    /// <summary>
    /// Viewport left-click on a face: pad clicks assign electrodes, everything else toggles
    /// the boundary-condition face selection. In an assembly the id is assembly-wide, so it
    /// first selects the part that owns the face — a condition always lands on the body the
    /// user just pointed at.
    /// </summary>
    public void OnFaceClicked(int faceId)
    {
        if (Session.ResolveBodyForFace(faceId) is { } hit)
        {
            Bodies.SelectByIndex(hit.BodyIndex);
            Conditions.ToggleFaceSelection(hit.LocalFaceId);
            return;
        }
        if (Electrodes.TryAssignElectrode(faceId)) return;
        Conditions.ToggleFaceSelection(faceId);
    }

    /// <summary>Viewport left-click on a solved assembly part (the result scene has one
    /// model per body, no face models): selects that part.</summary>
    public void OnBodyClicked(int bodyIndex) => Bodies.SelectByIndex(bodyIndex);

    /// <summary>
    /// Viewport left-click resolved against the ACTIVE body's feature edges and vertices,
    /// before the face pick gets a look. Returns whether it consumed the click.
    /// <para>
    /// Edges have to be tried first. A face covers vastly more of the screen than the line
    /// along its border, so resolving faces first would mean an edge could never be clicked at
    /// all — which is why they were list-only until now. The tolerance is a few pixels wide,
    /// so the face pick is unaffected a pixel away from a border.
    /// </para>
    /// <para>
    /// The active body only: the scope panel is per-body, so an id from a different one would
    /// tick a row that is not on screen.
    /// </para>
    /// </summary>
    public bool TryPickScope(Vector3D rayOrigin, Vector3D rayDirection, double tolerance,
        double maxRayT)
    {
        var body = Session.Body;
        // The id space the scope panel is showing — geometry where the body has some, the
        // mesh skin otherwise. One fact, read the same way everywhere.
        var (edges, positions) = Session.ScopeIsGeometric
            ? (body.Geometry!.FeatureEdges, body.Geometry.Vertices)
            : body.Mesh is { } mesh
                ? (mesh.Edges, mesh.Nodes)
                : (null, null);
        if (edges is null || positions is null || edges.Edges.Count == 0) return false;

        // Vertices take a wider radius than edges: a corner is a point where several edges
        // meet, so at equal tolerance an edge would always win and corners would be
        // unselectable.
        if (EdgePicker.PickVertex(edges, positions, rayOrigin, rayDirection,
                tolerance * 1.5, maxRayT) is { } vertex)
        {
            Conditions.ToggleVertexSelection(vertex.VertexId);
            return true;
        }
        if (EdgePicker.PickEdge(edges, positions, rayOrigin, rayDirection,
                tolerance, maxRayT) is { } edge)
        {
            Conditions.ToggleEdgeSelection(edge.EdgeId);
            return true;
        }
        return false;
    }

    // ---------------- Home / workspace navigation ----------------

    public ObservableCollection<RecentProject> RecentProjects { get; } = new();

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var entry in _recentProjects.Load())
            RecentProjects.Add(entry);
    }

    [RelayCommand]
    private void GoHome()
    {
        RefreshRecentProjects();
        Session.IsHomeActive = true;
    }

    /// <summary>Nav-rail navigation: one command, the workspace as its parameter.</summary>
    [RelayCommand]
    private void EnterWorkspace(WorkspaceKind workspace)
    {
        Session.ActiveWorkspace = workspace;
        Session.IsHomeActive = false;
    }

    /// <summary>
    /// The ribbon's Solve routes by analysis kind: the UI-dispatch kinds (antenna, signal
    /// integrity) run their own view-model pipelines, everything else goes through the
    /// ISolver path. Routing lives HERE because MainViewModel is the documented place
    /// where cross-view-model edges are allowed.
    /// </summary>
    [RelayCommand]
    private void SolveActive()
    {
        switch (Session.SelectedAnalysis.Kind)
        {
            case AnalysisType.Antenna:
                if (Antenna.SolveAntennaCommand.CanExecute(null))
                    Antenna.SolveAntennaCommand.Execute(null);
                else
                    Log.Append("Antenna solve is not ready — pick a geometry source in the RF panel first.");
                break;
            case AnalysisType.SignalIntegrity:
                if (SignalIntegrity.RunEyeDiagramCommand.CanExecute(null))
                    SignalIntegrity.RunEyeDiagramCommand.Execute(null);
                else
                    Log.Append("Eye run is not ready — extract a cross-section in the SI panel first.");
                break;
            default:
                Solve.SolveCommand.Execute(null);
                break;
        }
    }

    [RelayCommand]
    private void NewProject()
    {
        Session.Project = new Core.Model.SimProject();
        Session.Body = new Core.Model.Body { Name = "Body 1" };
        Session.Project.Bodies.Add(Session.Body);
        Session.ActiveWorkspace = WorkspaceKind.Structural;
        Session.IsHomeActive = false;
        Session.RaiseGeometryReplaced(leavingPcbMode: true);
        Session.RaiseMeshChanged();
        Log.Append("New project. Create a primitive or import an STL file to begin.");
    }

    /// <summary>Home tile: STL import belongs to the Structural workspace.</summary>
    [RelayCommand]
    private void HomeImportStl()
    {
        Session.ActiveWorkspace = WorkspaceKind.Structural;
        Session.IsHomeActive = false;
        Geometry.ImportStlCommand.Execute(null);
    }

    /// <summary>Home tile: STEP import belongs to the Structural workspace.</summary>
    [RelayCommand]
    private async Task HomeImportStepAsync()
    {
        Session.ActiveWorkspace = WorkspaceKind.Structural;
        Session.IsHomeActive = false;
        await Geometry.ImportStepCommand.ExecuteAsync(null);
    }

    /// <summary>Home tile: a STEP assembly lands in the Flow workspace, the only one
    /// that solves several bodies together.</summary>
    [RelayCommand]
    private async Task HomeImportAssemblyAsync()
    {
        Session.ActiveWorkspace = WorkspaceKind.Flow;
        Session.IsHomeActive = false;
        await Geometry.ImportStepAssemblyCommand.ExecuteAsync(null);
    }

    /// <summary>Home tile: PCB import lands in the Electrical workspace.</summary>
    [RelayCommand]
    private async Task HomeImportPcbAsync()
    {
        Session.ActiveWorkspace = WorkspaceKind.Electrical;
        Session.IsHomeActive = false;
        await Pcb.ImportPcbCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenRecent(RecentProject? entry)
    {
        if (entry is null) return;
        LoadProjectFromPath(entry.Path);
    }

    // ---------------- Project persistence ----------------

    [RelayCommand]
    private void SaveProject()
    {
        var dialog = new SaveFileDialog
        {
            Filter = ProjectSerializer.FileFilter,
            DefaultExt = ProjectSerializer.FileExtension,
            FileName = Session.Project.Name
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            Session.Body.Material = Session.SelectedMaterial;
            Session.Project.Name = Path.GetFileNameWithoutExtension(dialog.FileName);
            Session.Project.AnalysisType = Session.SelectedAnalysis.Kind.ToString();
            _serializer.Save(Session.Project, dialog.FileName);
            _recentProjects.Add(dialog.FileName);
            Log.Append($"Project saved to {dialog.FileName}");
        }
        catch (Exception ex) { Session.ReportError(ex); }
    }

    [RelayCommand]
    private void OpenProject()
    {
        var dialog = new OpenFileDialog { Filter = ProjectSerializer.FileFilter };
        if (dialog.ShowDialog() != true) return;
        LoadProjectFromPath(dialog.FileName);
    }

    private void LoadProjectFromPath(string path)
    {
        try
        {
            var project = _serializer.Load(path);
            Session.Project = project;
            Session.Body = project.Bodies.FirstOrDefault() ?? new Core.Model.Body { Name = "Body 1" };
            if (project.Bodies.Count == 0) project.Bodies.Add(Session.Body);
            Session.IsPcbMode = false;   // a loaded project uses the generic workflow panels

            // Route to the analysis's home workspace BEFORE selecting the analysis, so
            // the workspace's option-list coercion cannot override the loaded choice.
            if (Enum.TryParse<AnalysisType>(project.AnalysisType, out var analysisKind))
            {
                Session.ActiveWorkspace = AnalysisOption.WorkspaceOf(analysisKind);
                Session.SelectedAnalysis = AnalysisOption.All.First(o => o.Kind == analysisKind);
            }
            if (project.Stackup is not null)
            {
                Pcb.PcbCopperThickness = project.Stackup.CopperThickness;
                Pcb.PcbBoardThickness = project.Stackup.BoardThickness;
            }

            Materials.AdoptProjectMaterial(Session.Body.Material);
            Meshing.TargetEdgeLength = Session.Body.MeshSettings.TargetEdgeLength;

            // The events fan out: PCB teardown, condition/result resync, scene + mesh
            // info/edges rebuild, zoom-to-fit.
            Session.RaiseGeometryReplaced(leavingPcbMode: true);
            Session.RaiseMeshChanged();
            Session.IsHomeActive = false;
            _recentProjects.Add(path);
            RefreshRecentProjects();
            Log.Append($"Project loaded from {path}");
        }
        catch (Exception ex) { Session.ReportError(ex); }
    }
}
