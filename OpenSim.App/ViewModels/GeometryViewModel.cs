using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenSim.App.Services;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Geometry.Step;

namespace OpenSim.App.ViewModels;

/// <summary>Geometry sources for the generic workflow: primitive creation and STL/STEP import.</summary>
public partial class GeometryViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private readonly IGeometryImporter _stlImporter;
    private readonly StepImporter _stepImporter;

    public GeometryViewModel(ProjectSession session, ILogService log, IEnumerable<IGeometryImporter> importers)
    {
        _session = session;
        _log = log;
        // Dispatch by declared extension — the registration seam scales to new formats.
        var all = importers.ToList();
        _stlImporter = all.First(i => i.FileExtensions.Contains(".stl"));
        // STEP needs the concrete type: ImportWithNotes carries the advisory notes.
        _stepImporter = all.OfType<StepImporter>().Single();
        session.GeometryReplaced += (_, _) => RefreshFromBody();
    }

    // Primitive parameters (meters)
    [ObservableProperty] private double _boxSizeX = 0.1;
    [ObservableProperty] private double _boxSizeY = 0.02;
    [ObservableProperty] private double _boxSizeZ = 0.01;
    [ObservableProperty] private double _cylinderRadius = 0.01;
    [ObservableProperty] private double _cylinderHeight = 0.05;

    [ObservableProperty] private string _geometryInfo = "No geometry";

    [RelayCommand]
    private void CreateBox()
    {
        try
        {
            SetGeometry(Geometry.PrimitiveFactory.CreateBox(BoxSizeX, BoxSizeY, BoxSizeZ),
                $"Box {BoxSizeX}×{BoxSizeY}×{BoxSizeZ} m");
        }
        catch (Exception ex) { _session.ReportError(ex); }
    }

    [RelayCommand]
    private void CreateCylinder()
    {
        try
        {
            SetGeometry(Geometry.PrimitiveFactory.CreateCylinder(CylinderRadius, CylinderHeight),
                $"Cylinder r={CylinderRadius} h={CylinderHeight} m");
        }
        catch (Exception ex) { _session.ReportError(ex); }
    }

    [RelayCommand]
    private void ImportStl()
    {
        var dialog = new OpenFileDialog { Filter = "STL files (*.stl)|*.stl" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var mesh = _stlImporter.Import(dialog.FileName);
            SetGeometry(mesh, System.IO.Path.GetFileName(dialog.FileName));
            WarnIfNotWatertight(mesh);
        }
        catch (Exception ex) { _session.ReportError(ex); }
    }

    [RelayCommand]
    private async Task ImportStepAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "STEP files (*.step;*.stp)|*.step;*.stp|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        // Parsing + tessellation of a large CAD part can take seconds — run it off the
        // UI thread and flush the importer's notes to the log afterwards, on it.
        _session.IsBusy = true;
        _session.StatusText = "Importing STEP…";
        try
        {
            var report = await Task.Run(() => _stepImporter.ImportWithNotes(dialog.FileName));
            SetGeometry(report.Mesh, System.IO.Path.GetFileName(dialog.FileName));
            foreach (var note in report.Notes) _log.Append($"STEP: {note}");
            WarnIfNotWatertight(report.Mesh);
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally
        {
            _session.IsBusy = false;
            _session.StatusText = "";
        }
    }

    private void WarnIfNotWatertight(TriangleMesh mesh)
    {
        if (!mesh.IsWatertight())
            _log.Append("Warning: imported surface is not watertight; meshing will fail until repaired.");
    }

    /// <summary>Installs new generic geometry on the active body. The session events do
    /// the fan-out: PCB teardown, boundary-condition/result reset, scene rebuild.</summary>
    private void SetGeometry(TriangleMesh geometry, string source)
    {
        var body = _session.Body;
        body.Geometry = geometry;
        body.GeometrySource = source;
        body.Mesh = null;
        body.BoundaryConditions.Clear();

        _session.RaiseGeometryReplaced(leavingPcbMode: true);
        _session.RaiseMeshChanged();
        _log.Append($"Geometry set: {GeometryInfo}");
    }

    /// <summary>Recomputes the info readout from the session body.</summary>
    private void RefreshFromBody()
    {
        var geometry = _session.Body.Geometry;
        GeometryInfo = geometry is null
            ? _session.IsPcbMode
                ? _session.Body.Mesh is null ? "PCB board preview" : "PCB (mesh only)"
                : "No geometry"
            : $"{_session.Body.GeometrySource} — {geometry.Vertices.Count} vertices, " +
              $"{geometry.Triangles.Count} triangles, {geometry.FaceCount} faces";
    }
}
