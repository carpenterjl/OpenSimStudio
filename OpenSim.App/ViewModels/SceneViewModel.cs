using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.App.Rendering;
using OpenSim.App.Services;
using OpenSim.Core.Results;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Owns everything the 3D viewport renders for the FE workflow: the face/result models,
/// mesh wireframe, contour overlay, and legend. Rebuilds are driven purely by session
/// events and <see cref="ResultsViewModel.DisplayOptionsChanged"/>; it reads (never
/// writes) the results and electrodes view models for display state.
/// </summary>
public partial class SceneViewModel : ObservableObject
{
    private const int ContourLevelCount = 10;

    private readonly ProjectSession _session;
    private readonly ResultsViewModel _results;
    private readonly ElectrodesViewModel _electrodes;

    private Dictionary<int, GeometryModel3D> _faceModels = new();

    /// <summary>Element→node averaging is O(elements) and identical per (field, frame) —
    /// fields are session-transient and immutable, so reference identity is a safe key.
    /// Cleared whenever the fields or the mesh can change.</summary>
    private readonly Dictionary<IResultField, SceneBuilder.NodalScalars> _nodalizeCache = new();

    /// <summary>Per-axis mesh extent for the section plane (an O(nodes) scan otherwise
    /// re-run on every slider tick).</summary>
    private readonly Dictionary<OpenSim.Core.PostProcessing.SectionAxis, (double Min, double Max)> _axisExtents = new();

    /// <summary>Debounces slider-burst rebuilds: dragging deform/clamp/section fires one
    /// DisplayOptionsChanged per tick, each a full skin+cut+contour rebuild. Selection
    /// changes bypass the timer and render immediately.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _burstRebuildTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(140)
    };

    public SceneViewModel(ProjectSession session, ResultsViewModel results, ElectrodesViewModel electrodes)
    {
        _session = session;
        _results = results;
        _electrodes = electrodes;
        _burstRebuildTimer.Tick += (_, _) =>
        {
            _burstRebuildTimer.Stop();
            ShowResultScene();
        };
        session.GeometryReplaced += (_, _) =>
        {
            InvalidateMeshCaches();
            ShowGeometryScene();
        };
        session.MeshChanged += (_, _) =>
        {
            InvalidateMeshCaches();
            RefreshMeshEdges();
        };
        session.ResultsProduced += (_, _) => _nodalizeCache.Clear();
        session.HighlightsInvalidated += (_, _) => UpdateFaceHighlights();
        results.DisplayOptionsChanged += (_, e) =>
        {
            _burstRebuildTimer.Stop();
            if (e.Burst) _burstRebuildTimer.Start();
            else ShowResultScene();
        };
    }

    private void InvalidateMeshCaches()
    {
        _nodalizeCache.Clear();
        _axisExtents.Clear();
    }

    [ObservableProperty] private Model3DGroup _sceneRoot = new();
    [ObservableProperty] private Point3DCollection _meshEdges = new();
    [ObservableProperty] private bool _showMeshEdges = true;

    /// <summary>Shows/hides the solid model (<see cref="SceneRoot"/>: face geometry or the
    /// result color skin). Independent of the wireframe, so wireframe-only views work.</summary>
    [ObservableProperty] private bool _showBody = true;
    [ObservableProperty] private Point3DCollection _contourPoints = new();
    [ObservableProperty] private Brush _legendBrush = Brushes.Transparent;
    [ObservableProperty] private string _legendMin = "";
    [ObservableProperty] private string _legendMax = "";

    /// <summary>Reverse lookup used by viewport hit testing.</summary>
    public int? GetFaceIdForModel(GeometryModel3D model)
    {
        foreach (var (face, m) in _faceModels)
            if (ReferenceEquals(m, model))
                return face;
        return null;
    }

    private void ShowGeometryScene()
    {
        var group = new Model3DGroup();
        _faceModels = _session.Body.Geometry is null
            ? new Dictionary<int, GeometryModel3D>()
            : SceneBuilder.BuildFaceModels(_session.Body.Geometry, GetBodyColor());
        foreach (var model in _faceModels.Values)
            group.Children.Add(model);
        SceneRoot = group;
        ContourPoints = new Point3DCollection();
        LegendBrush = Brushes.Transparent;
        LegendMin = LegendMax = "";
    }

    private void ShowResultScene()
    {
        var mesh = _session.Body.Mesh;
        var field = _results.SelectedField;
        if (mesh is null || field is null)
        {
            ShowGeometryScene();
            return;
        }
        if (!_nodalizeCache.TryGetValue(field, out var scalars))
            _nodalizeCache[field] = scalars = SceneBuilder.NodalizeField(mesh, field);
        var displacement = _results.ResultFields.OfType<NodalVectorField>()
            .FirstOrDefault(f => f.Name is "Displacement" or "Mode shape");
        var colormap = _results.UseViridis ? ColormapKind.Viridis : ColormapKind.Rainbow;

        // Skin, cut face, and contours all receive the SAME displacement, scale and
        // display range so every overlay lies exactly on the rendered (possibly
        // deformed) surface and one legend describes them all.
        double displayMax = scalars.Min + _results.ResultClampFraction * (scalars.Max - scalars.Min);
        var displayRange = new SceneBuilder.ScalarRange(scalars.Min, displayMax);
        var plane = CurrentSectionPlane();
        var model = SceneBuilder.BuildResultModel(
            mesh, scalars, displayRange, colormap, displacement, _results.DeformScale, plane);

        var group = new Model3DGroup();
        group.Children.Add(model);
        if (plane is { } cutPlane)
            group.Children.Add(SceneBuilder.BuildSectionModel(
                mesh, scalars, displayRange, colormap, displacement, _results.DeformScale, cutPlane));
        SceneRoot = group;
        ContourPoints = _results.ShowContours
            ? SceneBuilder.BuildContourSegments(mesh, scalars, displayRange, displacement,
                _results.DeformScale, ContourLevelCount, plane)
            : new Point3DCollection();
        _faceModels = new Dictionary<int, GeometryModel3D>();

        LegendBrush = Colormap.CreateBrush(colormap);
        LegendMin = FormatEng(scalars.Min, field.Unit);
        LegendMax = FormatEng(displayMax, field.Unit)
            + (_results.ResultClampFraction < 1 ? " ▲" : "");   // ▲ = values above are saturated
        _session.StatusText = $"{field.Name}: {LegendMin} … {LegendMax}";
    }

    /// <summary>The world-space section plane from the axis + bbox-fraction controls.</summary>
    private OpenSim.Core.PostProcessing.SectionPlane? CurrentSectionPlane()
    {
        if (!_results.SectionEnabled || _session.Body.Mesh is null) return null;
        if (!_axisExtents.TryGetValue(_results.SectionAxis, out var extent))
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var n in _session.Body.Mesh.Nodes)
            {
                double c = _results.SectionAxis switch
                {
                    OpenSim.Core.PostProcessing.SectionAxis.X => n.X,
                    OpenSim.Core.PostProcessing.SectionAxis.Y => n.Y,
                    _ => n.Z
                };
                min = Math.Min(min, c);
                max = Math.Max(max, c);
            }
            _axisExtents[_results.SectionAxis] = extent = (min, max);
        }
        return new OpenSim.Core.PostProcessing.SectionPlane(
            _results.SectionAxis, extent.Min + _results.SectionOffsetFraction * (extent.Max - extent.Min))
        { FlipKeptSide = _results.SectionFlip };
    }

    private void RefreshMeshEdges()
    {
        MeshEdges = _session.Body.Mesh is null
            ? new Point3DCollection()
            : SceneBuilder.BuildBoundaryEdges(_session.Body.Mesh);
    }

    private void UpdateFaceHighlights()
    {
        var baseColor = GetBodyColor();
        var padFaces = _electrodes.PadElectrodes.Select(p => p.FaceId).ToHashSet();
        foreach (var (face, model) in _faceModels)
        {
            Color color;
            if (_electrodes.SourceFaceId == face) color = Colors.LimeGreen;      // source electrode
            else if (_electrodes.SinkFaceId == face) color = Colors.OrangeRed;   // sink electrode
            else if (padFaces.Contains(face)) color = Colors.Gold;               // selectable pad
            else if (_session.SelectedFaces.Contains(face)) color = Colors.Orange;
            else color = baseColor;
            var material = new DiffuseMaterial(new SolidColorBrush(color));
            model.Material = material;
            model.BackMaterial = material;
        }
    }

    private Color GetBodyColor()
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(_session.SelectedMaterial?.Color ?? "#B0B0B0");
        }
        catch
        {
            return Colors.LightGray;
        }
    }

    private static string FormatEng(double value, string unit) => $"{value:g4} {unit}";
}
