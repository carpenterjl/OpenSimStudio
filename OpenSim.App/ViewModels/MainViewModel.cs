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
/// for the AC-sweep/Joule analyses), Pcb → {Inductance, Antenna} (board hand-off for
/// the PEEC self/mutual/loop analysis and the antenna simulator), Antenna → Electrodes
/// (the selected source pad places the feed on net-sourced antennas).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ProjectSerializer _serializer;
    private readonly RecentProjectsService _recentProjects;

    public MainViewModel(ProjectSession session, ILogService log, GeometryViewModel geometry,
        MaterialsViewModel materials, MeshingViewModel meshing, BoundaryConditionsViewModel conditions,
        PcbViewModel pcb, ElectrodesViewModel electrodes, InductanceViewModel inductance,
        AntennaViewModel antenna, SolveViewModel solve, ResultsViewModel results, SceneViewModel scene,
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
        Solve = solve;
        Results = results;
        Scene = scene;
        _serializer = serializer;
        _recentProjects = recentProjects;
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
    public SolveViewModel Solve { get; }
    public ResultsViewModel Results { get; }
    public SceneViewModel Scene { get; }

    /// <summary>Viewport left-click on a face: pad clicks assign electrodes, everything
    /// else toggles the boundary-condition face selection.</summary>
    public void OnFaceClicked(int faceId)
    {
        if (Electrodes.TryAssignElectrode(faceId)) return;
        Conditions.ToggleFaceSelection(faceId);
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
