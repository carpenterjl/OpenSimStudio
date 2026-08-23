using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.App.Rendering;
using OpenSim.App.Services;
using OpenSim.Core.PostProcessing;
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
    private readonly BodiesViewModel _bodies;
    private readonly ColormapViewModel _colormap;

    private Dictionary<int, GeometryModel3D> _faceModels = new();

    /// <summary>The materials of the current result models. Recoloring the map swaps the
    /// brush on these in place — the texture coordinates already hold the normalized field,
    /// so no geometry has to be rebuilt (which is what lets a stop drag stay live).</summary>
    private readonly List<DiffuseMaterial> _resultMaterials = new();

    /// <summary>Body index → everything that body contributes to the scene (its face models
    /// before a solve, its colored skin after one). Hiding a part drops its entry from the
    /// rendered group without rebuilding anything.</summary>
    private Dictionary<int, Model3D> _bodyContent = new();

    /// <summary>Assembly-scene models that belong to no single body (the section cut face),
    /// re-added on every recompose so toggling a part cannot lose them.</summary>
    private readonly List<Model3D> _sceneExtras = new();

    /// <summary>Element→node averaging is O(elements) and identical per (field, frame) —
    /// fields are session-transient and immutable, so reference identity is a safe key.
    /// Cleared whenever the fields or the mesh can change.</summary>
    private readonly Dictionary<IResultField, SceneBuilder.NodalScalars> _nodalizeCache = new();

    /// <summary>Per-axis mesh extent for the section plane (an O(nodes) scan otherwise
    /// re-run on every slider tick).</summary>
    private readonly Dictionary<OpenSim.Core.PostProcessing.SectionAxis, (double Min, double Max)> _axisExtents = new();

    /// <summary>The mesh <see cref="_axisExtents"/> was measured on.</summary>
    private OpenSim.Core.Model.FeMesh? _extentsMesh;

    /// <summary>Debounces slider-burst rebuilds: dragging deform/clamp/section fires one
    /// DisplayOptionsChanged per tick, each a full skin+cut+contour rebuild. Selection
    /// changes bypass the timer and render immediately.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _burstRebuildTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(140)
    };

    public SceneViewModel(ProjectSession session, ResultsViewModel results,
        ElectrodesViewModel electrodes, BodiesViewModel bodies, ColormapViewModel colormap)
    {
        _session = session;
        _results = results;
        _electrodes = electrodes;
        _bodies = bodies;
        _colormap = colormap;
        colormap.ColormapChanged += (_, e) =>
        {
            if (e.Kind == ColormapChangeKind.Colors && Recolor()) return;
            // A range change (or a recolor with nothing built yet) needs the texture
            // coordinates back: same debounce as the other sliders, since dragging a
            // range box or the decade count arrives in the same bursts.
            _burstRebuildTimer.Stop();
            _burstRebuildTimer.Start();
        };
        _burstRebuildTimer.Tick += (_, _) =>
        {
            _burstRebuildTimer.Stop();
            ShowResultScene();
        };
        // Hiding a part is a view-only change: recompose from the models already built.
        // Guarded so a toggle can never clobber a single-body scene, which has no per-body
        // content to compose from.
        bodies.VisibilityChanged += (_, _) =>
        {
            if (_bodyContent.Count > 0) ApplyBodyVisibility();
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
            RefreshSelectedEdges();
        };
        session.ResultsProduced += (_, _) => _nodalizeCache.Clear();
        session.HighlightsInvalidated += (_, _) =>
        {
            UpdateFaceHighlights();
            RefreshSelectedEdges();
        };
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
        _extentsMesh = null;
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

    /// <summary>Interior value ticks under the legend gradient — the values the colormap's
    /// own stops stand for. Empty outside the workspace that owns the colormap editor, so
    /// the Mechanical/Electrical legends are unchanged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LegendTickRowHeight))]
    private IReadOnlyList<LegendTick> _legendTicks = Array.Empty<LegendTick>();

    /// <summary>Height of the legend's tick row: zero when there are no ticks, so a legend
    /// without them keeps exactly the layout it had before ticks existed.</summary>
    public double LegendTickRowHeight => LegendTicks.Count > 0 ? 15 : 0;

    /// <summary>
    /// Renders the viewport (3D scene plus the legend overlay) to a bitmap. Set ONCE by
    /// <c>Viewport3DView</c> — the only thing that can rasterise a live visual tree — and
    /// left null in any host that has no viewport. This is a deliberately narrow seam: the
    /// view supplies the pixels, the viewmodel decides where they go.
    /// </summary>
    public Func<double, System.Windows.Media.Imaging.BitmapSource?>? CaptureViewport { get; set; }

    /// <summary>
    /// Saves the viewport as a PNG. The framing follows the current window, which is stated
    /// rather than corrected: re-laying out to a fixed size would change the camera framing
    /// the user set up. The supersampling factor is fixed, so the same window always yields
    /// the same pixel size.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ExportViewportImage()
    {
        if (CaptureViewport is null)
        {
            _session.StatusText = "The viewport is not available to capture.";
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export viewport image (PNG)",
            FileName = "result-view.png",
            Filter = "PNG images (*.png)|*.png",
            DefaultExt = ".png"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            const double supersample = 2.0;
            var bitmap = CaptureViewport(supersample);
            if (bitmap is null)
            {
                _session.StatusText = "The viewport had nothing to capture.";
                return;
            }
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(dialog.FileName);
            encoder.Save(stream);
            _session.StatusText =
                $"Exported {bitmap.PixelWidth}x{bitmap.PixelHeight} image to " +
                System.IO.Path.GetFileName(dialog.FileName) + ".";
        }
        catch (Exception ex)
        {
            _session.ReportError(ex);
        }
    }

    /// <summary>Reverse lookup used by viewport hit testing.</summary>
    public int? GetFaceIdForModel(GeometryModel3D model)
    {
        foreach (var (face, m) in _faceModels)
            if (ReferenceEquals(m, model))
                return face;
        return null;
    }

    /// <summary>Reverse lookup for a click on a solved assembly part, which is one model
    /// per body rather than one per face.</summary>
    public int? GetBodyForModel(GeometryModel3D model)
    {
        foreach (var (body, content) in _bodyContent)
            if (ReferenceEquals(content, model))
                return body;
        return null;
    }

    /// <summary>True when the project holds several parts, so the scene must show them all
    /// rather than only the active one.</summary>
    private bool IsAssembly => _session.Bodies.Count > 1;

    private void ShowGeometryScene()
    {
        if (IsAssembly)
        {
            ShowMultiBodyGeometryScene();
            return;
        }
        _bodyContent = new Dictionary<int, Model3D>();
        _sceneExtras.Clear();
        var group = new Model3DGroup();
        _faceModels = _session.Body.Geometry is null
            ? new Dictionary<int, GeometryModel3D>()
            : SceneBuilder.BuildFaceModels(_session.Body.Geometry, GetBodyColor());
        foreach (var model in _faceModels.Values)
            group.Children.Add(model);
        SceneRoot = group;
        ContourPoints = new Point3DCollection();
        _resultMaterials.Clear();
        LegendBrush = Brushes.Transparent;
        LegendMin = LegendMax = "";
        LegendTicks = Array.Empty<LegendTick>();
    }

    /// <summary>
    /// Every part of the assembly, each in its own material color and each carrying its
    /// faces under ASSEMBLY-WIDE face ids (the body's base + its local id — the same
    /// partition <see cref="OpenSim.Core.Model.FeMeshAssembler"/> uses), so one click
    /// resolves to both the part and the face the user meant.
    /// </summary>
    private void ShowMultiBodyGeometryScene()
    {
        var bodies = _session.Bodies;
        var bases = OpenSim.Core.Model.FeMeshAssembler.FaceIdBases(bodies);
        var faceModels = new Dictionary<int, GeometryModel3D>();
        var content = new Dictionary<int, Model3D>();

        for (int b = 0; b < bodies.Count; b++)
        {
            if (bodies[b].Geometry is not { } geometry) continue;
            var group = new Model3DGroup();
            foreach (var (face, model) in SceneBuilder.BuildFaceModels(geometry, BodyColor(bodies[b])))
            {
                faceModels[face + bases[b]] = model;
                group.Children.Add(model);
            }
            content[b] = group;
        }

        _faceModels = faceModels;
        _bodyContent = content;
        _sceneExtras.Clear();
        ApplyBodyVisibility();
        ContourPoints = new Point3DCollection();
        _resultMaterials.Clear();
        LegendBrush = Brushes.Transparent;
        LegendMin = LegendMax = "";
        LegendTicks = Array.Empty<LegendTick>();
    }

    /// <summary>
    /// Remembers the materials of the models just built, so a colormap recolor can swap
    /// their brush instead of rebuilding them. A frozen material would refuse the swap, so
    /// finding one abandons the fast path entirely rather than recoloring half the scene.
    /// </summary>
    private void TrackResultMaterials(IEnumerable<Model3D> models)
    {
        _resultMaterials.Clear();
        foreach (var model in models)
            if (!Collect(model))
            {
                _resultMaterials.Clear();
                return;
            }

        bool Collect(Model3D model)
        {
            switch (model)
            {
                case GeometryModel3D geometry:
                    if (geometry.Material is not DiffuseMaterial material || material.IsFrozen)
                        return false;
                    if (!_resultMaterials.Contains(material)) _resultMaterials.Add(material);
                    return true;
                case Model3DGroup group:
                    foreach (var child in group.Children)
                        if (!Collect(child)) return false;
                    return true;
                default:
                    return true;
            }
        }
    }

    /// <summary>Rebuilds <see cref="SceneRoot"/> from the per-body models already built,
    /// leaving out the hidden ones. No geometry is regenerated.</summary>
    private void ApplyBodyVisibility()
    {
        var hidden = _bodies.HiddenBodyIndices;
        var group = new Model3DGroup();
        foreach (int body in _bodyContent.Keys.OrderBy(k => k))
            if (!hidden.Contains(body))
                group.Children.Add(_bodyContent[body]);
        foreach (var extra in _sceneExtras)
            group.Children.Add(extra);
        SceneRoot = group;
    }

    private void ShowResultScene()
    {
        if (_session.AssembledMesh is { } assembled && _results.DisplayField is not null)
        {
            ShowMultiBodyResultScene(assembled);
            return;
        }
        var mesh = _session.Body.Mesh;
        var field = _results.DisplayField;
        if (mesh is null || field is null)
        {
            ShowGeometryScene();
            return;
        }
        if (!_nodalizeCache.TryGetValue(field, out var scalars))
            _nodalizeCache[field] = scalars = SceneBuilder.NodalizeField(mesh, field);
        var displacement = _results.ResultFields.OfType<NodalVectorField>()
            .FirstOrDefault(f => f.Name is "Displacement" or "Mode shape");
        var colormap = CurrentColormap;

        // Skin, cut face, and contours all receive the SAME displacement, scale and
        // display range so every overlay lies exactly on the rendered (possibly
        // deformed) surface and one legend describes them all.
        var displayRange = CurrentScale(scalars);
        var plane = CurrentSectionPlane();
        var model = SceneBuilder.BuildResultModel(
            mesh, scalars, displayRange, colormap, displacement, _results.DeformScale, plane);

        var group = new Model3DGroup();
        group.Children.Add(model);
        if (plane is { } cutPlane)
            group.Children.Add(SceneBuilder.BuildSectionModel(
                mesh, scalars, displayRange, colormap, displacement, _results.DeformScale, cutPlane));
        SceneRoot = group;
        TrackResultMaterials(group.Children);
        ContourPoints = _results.ShowContours
            ? SceneBuilder.BuildContourSegments(mesh, scalars, displayRange, displacement,
                _results.DeformScale, ContourLevelCount, plane)
            : new Point3DCollection();
        _faceModels = new Dictionary<int, GeometryModel3D>();

        UpdateLegend(colormap, displayRange, scalars, field);
        _session.StatusText = $"{field.Name}: {LegendMin} … {LegendMax}";
    }

    /// <summary>
    /// The solved assembly: one colored model per body, all normalized through ONE display
    /// range so the colors compare part to part, and all painted from one legend.
    /// </summary>
    private void ShowMultiBodyResultScene(OpenSim.Core.Model.FeMeshAssembler.AssembledMesh assembled)
    {
        var mesh = assembled.Mesh;
        var field = _results.DisplayField!;
        if (!_nodalizeCache.TryGetValue(field, out var scalars))
            _nodalizeCache[field] = scalars = SceneBuilder.NodalizeField(mesh, field);
        var colormap = CurrentColormap;

        var displayRange = CurrentScale(scalars);
        var plane = CurrentSectionPlane(mesh);

        // No displacement leg: an environment heat-flow result carries temperature, not a
        // deformation, so there is nothing to deform the parts by.
        var models = SceneBuilder.BuildBodyResultModels(assembled, scalars, displayRange,
            colormap, displacement: null, deformScale: 1.0, plane);
        _bodyContent = models.ToDictionary(kv => kv.Key, kv => (Model3D)kv.Value);
        _faceModels = new Dictionary<int, GeometryModel3D>();

        _sceneExtras.Clear();
        if (plane is { } cutPlane)
            _sceneExtras.Add(SceneBuilder.BuildSectionModel(
                mesh, scalars, displayRange, colormap, null, 1.0, cutPlane));
        ApplyBodyVisibility();
        TrackResultMaterials(models.Values.Cast<Model3D>().Concat(_sceneExtras));
        ContourPoints = _results.ShowContours
            ? SceneBuilder.BuildContourSegments(mesh, scalars, displayRange, null, 1.0,
                ContourLevelCount, plane)
            : new Point3DCollection();

        UpdateLegend(colormap, displayRange, scalars, field);
        _session.StatusText = $"{field.Name}: {LegendMin} … {LegendMax} " +
                              $"over {assembled.BodyCount} bodies";
    }

    /// <summary>
    /// True where the colormap editor is on screen. Elsewhere the result view keeps the
    /// fixed Rainbow/Viridis pair and the plain auto range it has always used, so the
    /// Mechanical and Electrical workspaces are unaffected by anything edited here.
    /// </summary>
    private bool UsesEditableColormap => _session.ActiveWorkspace == WorkspaceKind.Flow;

    /// <summary>The colormap the result scene paints with.</summary>
    private ColormapDefinition CurrentColormap => UsesEditableColormap
        ? _colormap.Current
        : Colormap.Definition(_results.UseViridis ? ColormapKind.Viridis : ColormapKind.Rainbow);

    /// <summary>
    /// The scalar → colormap mapping the whole result scene shares: skin, section cut,
    /// contours and legend all normalize through this one <see cref="FieldScale"/>, which
    /// is what makes a single legend describe every one of them.
    /// </summary>
    private FieldScale CurrentScale(SceneBuilder.NodalScalars scalars)
    {
        if (UsesEditableColormap)
        {
            _colormap.SeedRangeFromData(scalars.Min, scalars.Max);
            return _colormap.ResolveScale(scalars.Min, scalars.Max, _results.ResultClampFraction);
        }
        double displayMax = scalars.Min + _results.ResultClampFraction * (scalars.Max - scalars.Min);
        return new FieldScale(FieldScaleMode.Linear, scalars.Min, displayMax);
    }

    /// <summary>
    /// Swaps the gradient on the models already built. Returns false when there is nothing
    /// colored on screen, in which case the caller must do the full rebuild instead.
    /// </summary>
    private bool Recolor()
    {
        if (_resultMaterials.Count == 0) return false;
        var brush = ColormapBrushFactory.CreateBrush(CurrentColormap);
        foreach (var material in _resultMaterials)
            material.Brush = brush;
        LegendBrush = brush;
        LegendTicks = BuildLegendTicks(_lastScale);
        return true;
    }

    /// <summary>The scale the legend ticks were last built from, so moving a stop can
    /// redraw them without re-nodalizing the field.</summary>
    private FieldScale _lastScale = new(FieldScaleMode.Linear, 0, 1);

    /// <summary>
    /// The legend for the current scale. The ▲/▼ markers say that data lies OUTSIDE the
    /// displayed range and is saturated at the end color — tested against the actual data
    /// bounds rather than against the clamp setting, because a raised floor or a hand-typed
    /// range saturates just as a dragged clamp does (and a uniform field saturates nothing).
    /// </summary>
    private void UpdateLegend(ColormapDefinition colormap, FieldScale scale,
        SceneBuilder.NodalScalars scalars, IResultField field)
    {
        LegendBrush = ColormapBrushFactory.CreateBrush(colormap);
        LegendMin = (scale.EffectiveMin > scalars.Min ? "▼ " : "")
                    + FormatEng(scale.EffectiveMin, field.Unit);
        LegendMax = FormatEng(scale.Max, field.Unit) + (scale.Max < scalars.Max ? " ▲" : "");
        _lastScale = scale;
        LegendTicks = BuildLegendTicks(scale);
    }

    /// <summary>
    /// A tick per INTERIOR colormap stop, labeled with the value that stop colors
    /// (<see cref="FieldScale.ValueAt"/> — the inverse of the mapping the mesh was colored
    /// through, so the labels stay right under a log scale). The end stops are left out:
    /// the min/max row already carries them, with the unit.
    /// </summary>
    private IReadOnlyList<LegendTick> BuildLegendTicks(FieldScale scale)
    {
        if (!UsesEditableColormap) return Array.Empty<LegendTick>();
        var stops = _colormap.Current.Stops;
        var ticks = new List<LegendTick>(Math.Max(0, stops.Count - 2));
        for (int i = 1; i < stops.Count - 1; i++)
            ticks.Add(LegendTick.At(stops[i].Position,
                FormatEng(scale.ValueAt(stops[i].Position), unit: "").Trim()));
        return ticks;
    }

    /// <summary>The world-space section plane from the axis + bbox-fraction controls.</summary>
    private OpenSim.Core.PostProcessing.SectionPlane? CurrentSectionPlane(
        OpenSim.Core.Model.FeMesh? over = null)
    {
        var target = over ?? _session.Body.Mesh;
        if (!_results.SectionEnabled || target is null) return null;
        // The cache is per mesh as well as per axis: the assembly path measures the MERGED
        // mesh, which spans a different box than any single body's.
        if (!ReferenceEquals(target, _extentsMesh))
        {
            _axisExtents.Clear();
            _extentsMesh = target;
        }
        if (!_axisExtents.TryGetValue(_results.SectionAxis, out var extent))
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var n in target.Nodes)
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
        if (IsAssembly)
        {
            // Every meshed part's wireframe at once — the assembly scene shows them all,
            // so a wireframe of only the active body would look like missing geometry.
            var points = new Point3DCollection();
            foreach (var body in _session.Bodies)
            {
                if (body.Mesh is null) continue;
                foreach (var p in SceneBuilder.BuildBoundaryEdges(body.Mesh))
                    points.Add(p);
            }
            MeshEdges = points;
            return;
        }
        MeshEdges = _session.Body.Mesh is null
            ? new Point3DCollection()
            : SceneBuilder.BuildBoundaryEdges(_session.Body.Mesh);
    }

    /// <summary>
    /// The selected geometric edges, drawn over the wireframe. Face highlighting recolours
    /// a whole face model; an edge owns no model, so it is drawn as its own line set.
    /// </summary>
    [ObservableProperty] private Point3DCollection _selectedEdgeLines = new();

    private void RefreshSelectedEdges()
    {
        if (_session.SelectedEdges.Count == 0)
        {
            SelectedEdgeLines = new Point3DCollection();
            return;
        }
        // The ids are in whichever space the session says, so the highlight is drawn from
        // the same source the panel listed them from - reading the mesh here while the
        // panel listed geometry ids would highlight arbitrary other edges.
        SelectedEdgeLines = _session.ScopeIsGeometric
            ? SceneBuilder.BuildEdgeHighlight(_session.Body.Geometry!, _session.SelectedEdges)
            : _session.Body.Mesh is { } mesh
                ? SceneBuilder.BuildEdgeHighlight(mesh, _session.SelectedEdges)
                : new Point3DCollection();
    }

    /// <summary>
    /// Repaints the face models. In an assembly the keys are assembly-wide face ids while
    /// the selection is always local to the ACTIVE body (that is the body a new boundary
    /// condition would land on), so a face highlights only when it belongs to that body.
    /// </summary>
    private void UpdateFaceHighlights()
    {
        var padFaces = _electrodes.PadElectrodes.Select(p => p.FaceId).ToHashSet();
        int activeBody = IsAssembly ? _session.Bodies.ToList().FindIndex(
            b => ReferenceEquals(b, _session.Body)) : 0;
        foreach (var (face, model) in _faceModels)
        {
            var resolved = _session.ResolveBodyForFace(face);
            int bodyIndex = resolved?.BodyIndex ?? 0;
            int localFace = resolved?.LocalFaceId ?? face;
            var baseColor = IsAssembly ? BodyColor(_session.Bodies[bodyIndex]) : GetBodyColor();

            Color color;
            if (_electrodes.SourceFaceId == face) color = Colors.LimeGreen;      // source electrode
            else if (_electrodes.SinkFaceId == face) color = Colors.OrangeRed;   // sink electrode
            else if (padFaces.Contains(face)) color = Colors.Gold;               // selectable pad
            else if (bodyIndex == activeBody && _session.SelectedFaces.Contains(localFace))
                color = Colors.Orange;
            else color = baseColor;
            var material = new DiffuseMaterial(new SolidColorBrush(color));
            model.Material = material;
            model.BackMaterial = material;
        }
    }

    private Color GetBodyColor() => ParseColor(_session.SelectedMaterial?.Color);

    /// <summary>A part's own material color, so an unsolved assembly still reads as
    /// distinct parts rather than one grey blob.</summary>
    private static Color BodyColor(OpenSim.Core.Model.Body body) => ParseColor(body.Material?.Color);

    private static Color ParseColor(string? text)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(text ?? "#B0B0B0");
        }
        catch
        {
            return Colors.LightGray;
        }
    }

    private static string FormatEng(double value, string unit) => $"{value:g4} {unit}";
}
