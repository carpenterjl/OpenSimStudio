using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Meshing;

namespace OpenSim.App.ViewModels;

/// <summary>Mesh settings + generation for the generic (non-PCB) workflow. The PCB net
/// mesher reads <see cref="TargetEdgeLength"/> too, so both workflows share one knob.</summary>
public partial class MeshingViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private readonly IMeshGenerator _meshGenerator;

    public MeshingViewModel(ProjectSession session, ILogService log, IMeshGenerator meshGenerator)
    {
        _session = session;
        _log = log;
        _meshGenerator = meshGenerator;
        session.MeshChanged += (_, _) => RefreshMeshInfo();
    }

    [ObservableProperty] private double _targetEdgeLength; // 0 = auto
    [ObservableProperty] private bool _autoEdgeLength = true;
    [ObservableProperty] private string _meshInfo = "No mesh";

    /// <summary>Generate TET10 (quadratic) elements — fixes TET4's bending stiffness.
    /// Structural solves only; the electrical/thermal solvers require linear meshes.</summary>
    [ObservableProperty] private bool _quadraticElements;

    /// <summary>Slider value [m]; setting it turns Auto off. Defaults to 0.3 mm when auto.</summary>
    public double EdgeLengthSlider
    {
        get => TargetEdgeLength > 0 ? TargetEdgeLength : 3e-4;
        set { AutoEdgeLength = false; TargetEdgeLength = value; }
    }

    public string EdgeLengthDisplay => AutoEdgeLength || TargetEdgeLength <= 0
        ? "auto"
        : $"{TargetEdgeLength * 1e3:g3} mm";

    partial void OnAutoEdgeLengthChanged(bool value)
    {
        if (value) TargetEdgeLength = 0;
        else if (TargetEdgeLength <= 0) TargetEdgeLength = 3e-4;
        OnPropertyChanged(nameof(EdgeLengthSlider));
        OnPropertyChanged(nameof(EdgeLengthDisplay));
    }

    partial void OnTargetEdgeLengthChanged(double value)
    {
        OnPropertyChanged(nameof(EdgeLengthSlider));
        OnPropertyChanged(nameof(EdgeLengthDisplay));
    }

    [RelayCommand]
    private async Task GenerateMeshAsync()
    {
        var body = _session.Body;
        if (body.Geometry is null)
        {
            _log.Append("Create or import geometry before meshing.");
            return;
        }
        _session.IsBusy = true;
        _session.StatusText = "Meshing…";
        try
        {
            var settings = new MeshSettings
            {
                TargetEdgeLength = TargetEdgeLength,
                ElementOrder = QuadraticElements ? ElementOrder.Quadratic : ElementOrder.Linear
            };
            body.MeshSettings = settings;
            var geometry = body.Geometry;
            var mesh = await Task.Run(() => _meshGenerator.Generate(geometry, settings));
            body.Mesh = mesh;

            _session.RaiseMeshChanged();
            _log.Append($"Mesh generated: {mesh.NodeCount} nodes, {mesh.ElementCount} tetrahedra.");
            _session.StatusText = "Mesh ready";
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally { _session.IsBusy = false; }
    }

    /// <summary>Recomputes the info readout from the session body's mesh.</summary>
    private void RefreshMeshInfo()
    {
        var mesh = _session.Body.Mesh;
        if (mesh is null)
        {
            MeshInfo = "No mesh";
            return;
        }
        var stats = MeshQuality.Compute(mesh);
        MeshInfo = $"{stats.NodeCount} nodes, {stats.ElementCount} elements\n" +
                   $"volume {stats.TotalVolume:g4} m³\n" +
                   $"quality avg {stats.AverageQuality:f3}, min {stats.MinQuality:f4}\n" +
                   $"edges {stats.MinEdgeLength:g3}–{stats.MaxEdgeLength:g3} m";
    }
}
