using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenSim.App.Services;
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
        PcbViewModel pcb, ElectrodesViewModel electrodes, InductanceViewModel inductance,
        AntennaViewModel antenna, SignalIntegrityViewModel signalIntegrity, SolveViewModel solve,
        ResultsViewModel results, SceneViewModel scene, BodiesViewModel bodies,
        EnvironmentViewModel environment, ColormapViewModel colormap,
        ProjectSerializer serializer, RecentProjectsService recentProjects)
    {
        Session = session;
        Log = log;
        Geometry = geometry;
        Materials = materials;
        Meshing = meshing;
        Conditions = conditions;
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
        _serializer = serializer;
        _recentProjects = recentProjects;
        // Switching the active body re-syncs the panels that mirror body state, exactly as
        // opening a project does.
        session.ActiveBodyChanged += (_, _) => AdoptActiveBody();
        RefreshRecentProjects();
        log.Append("Ready. Create a primitive or import an STL file to begin.");
    }

    public ProjectSession Session { get; }
    public ILogService Log { get; }
    public GeometryViewModel Geometry { get; }
    public MaterialsViewModel Materials { get; }
    public MeshingViewModel Meshing { get; }
    public BoundaryConditionsViewModel Conditions { get; }
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

    [RelayCommand]
    private void EnterMechanical()
    {
        Session.ActiveWorkspace = WorkspaceKind.Mechanical;
        Session.IsHomeActive = false;
    }

    [RelayCommand]
    private void EnterElectrical()
    {
        Session.ActiveWorkspace = WorkspaceKind.Electrical;
        Session.IsHomeActive = false;
    }

    [RelayCommand]
    private void EnterThermalFlow()
    {
        Session.ActiveWorkspace = WorkspaceKind.ThermalFlow;
        Session.IsHomeActive = false;
    }

    [RelayCommand]
    private void NewProject()
    {
        Session.Project = new Core.Model.SimProject();
        Session.Body = new Core.Model.Body { Name = "Body 1" };
        Session.Project.Bodies.Add(Session.Body);
        Session.ActiveWorkspace = WorkspaceKind.Mechanical;
        Session.IsHomeActive = false;
        Session.RaiseGeometryReplaced(leavingPcbMode: true);
        Session.RaiseMeshChanged();
        Log.Append("New project. Create a primitive or import an STL file to begin.");
    }

    /// <summary>Home tile: STL import belongs to the Mechanical workspace.</summary>
    [RelayCommand]
    private void HomeImportStl()
    {
        Session.ActiveWorkspace = WorkspaceKind.Mechanical;
        Session.IsHomeActive = false;
        Geometry.ImportStlCommand.Execute(null);
    }

    /// <summary>Home tile: STEP import belongs to the Mechanical workspace.</summary>
    [RelayCommand]
    private async Task HomeImportStepAsync()
    {
        Session.ActiveWorkspace = WorkspaceKind.Mechanical;
        Session.IsHomeActive = false;
        await Geometry.ImportStepCommand.ExecuteAsync(null);
    }

    /// <summary>Home tile: a STEP assembly lands in the Thermal &amp; Flow workspace, the
    /// only one that solves several bodies together.</summary>
    [RelayCommand]
    private async Task HomeImportAssemblyAsync()
    {
        Session.ActiveWorkspace = WorkspaceKind.ThermalFlow;
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
