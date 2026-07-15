using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenSim.App.Rendering;
using OpenSim.App.Services;
using OpenSim.Core.Model;
using OpenSim.Pcb;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Ipc2581;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The PCB workflow: whole-board import, per-net/per-layer visibility and preview,
/// stackup editing, net picking in the viewport, and meshing the selected net into the
/// session body (with pad electrodes handed to <see cref="ElectrodesViewModel"/>).
/// </summary>
public partial class PcbViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private readonly MeshingViewModel _meshing;
    private readonly ElectrodesViewModel _electrodes;
    private readonly MaterialsViewModel _materials;
    private readonly InductanceViewModel _inductance;
    private readonly AntennaViewModel _antenna;
    private readonly SignalIntegrityViewModel _signalIntegrity;

    public PcbViewModel(ProjectSession session, ILogService log, MeshingViewModel meshing,
        ElectrodesViewModel electrodes, MaterialsViewModel materials, InductanceViewModel inductance,
        AntennaViewModel antenna, SignalIntegrityViewModel signalIntegrity)
    {
        _session = session;
        _log = log;
        _meshing = meshing;
        _electrodes = electrodes;
        _materials = materials;
        _inductance = inductance;
        _antenna = antenna;
        _signalIntegrity = signalIntegrity;
        session.GeometryReplaced += (_, e) =>
        {
            if (e.LeavingPcbMode) TearDownBoardState();
        };
    }

    public ObservableCollection<NetRow> NetRows { get; } = new();
    public ObservableCollection<LayerFilter> LayerFilters { get; } = new();

    /// <summary>Editable dielectric thickness between each adjacent copper-layer pair — the
    /// true z-separation (and via-barrel height) used when meshing a multi-layer net.</summary>
    public ObservableCollection<DielectricGap> DielectricGaps { get; } = new();
    [ObservableProperty] private NetRow? _selectedNetRow;
    [ObservableProperty] private CopperNet? _selectedNet;
    [ObservableProperty] private string _boardInfo = "No board imported";

    // PCB stackup (meters)
    [ObservableProperty] private double _pcbCopperThickness = 35e-6;
    [ObservableProperty] private double _pcbBoardThickness = 1.6e-3;

    /// <summary>Via barrel wall (plating) thickness [µm]; IPC Class 2 nominal ≈ 25 µm.</summary>
    [ObservableProperty] private double _viaPlatingMicrons = 25.0;

    /// <summary>
    /// The whole copper preview as one frozen model tree: per visible layer a group
    /// carrying the layer's stackup-z transform, whose children are the cached frozen
    /// per-(net, layer) ribbon meshes of the visible nets. A visibility toggle recomposes
    /// this tree from cached pieces (reference adds — no geometry work); only a stackup
    /// edit moves the transforms.
    /// </summary>
    [ObservableProperty] private Model3DGroup _copperPreviewModel = new();

    /// <summary>Frozen ribbon models keyed by (net id, layer order), built once per
    /// import on the import thread (frozen Freezables are thread-safe to share).</summary>
    private Dictionary<(int NetId, int LayerOrder), GeometryModel3D>? _previewCache;

    /// <summary>Stackup z per layer, cached because every toggle needs it but only a
    /// thickness/gap edit changes it.</summary>
    private IReadOnlyDictionary<int, double>? _layerZMapCache;

    /// <summary>True while a preview recompose is queued on the dispatcher — bursts of
    /// toggles (Show all, solo) collapse to a single recompose.</summary>
    private bool _refreshQueued;

    [ObservableProperty] private Point3DCollection _selectedNetOutlines = new();
    [ObservableProperty] private bool _showCopperPreview = true;
    [ObservableProperty] private Point3DCollection _boardOutlinePoints = new();
    [ObservableProperty] private bool _showBoardOutline = true;

    private PcbBoard? _board;

    /// <summary>
    /// True while the full-board copper preview is showing — clicks then pick nets. Once a
    /// net is meshed the preview hides and clicks pick faces instead (for electrodes);
    /// toggling "Show full-board copper" back on returns to net picking.
    /// </summary>
    public bool NetPickingActive => _board is not null && ShowCopperPreview;

    /// <summary>Whether the net's own list checkbox is on (layer filtering is separate, per island).</summary>
    private bool NetToggleOn(CopperNet net) =>
        NetRows.FirstOrDefault(r => r.Net == net) is not { IsVisible: false };

    /// <summary>Whether a copper layer's panel toggle is enabled (unknown layers default to visible).</summary>
    private bool LayerEnabled(int layerOrder) =>
        LayerFilters.FirstOrDefault(f => f.LayerOrder == layerOrder)?.Enabled ?? true;

    /// <summary>
    /// Mid-height z of every copper layer, computed with the mesher's own stackup function
    /// from the editable per-layer copper and dielectric thicknesses — the preview shows
    /// each layer exactly where a meshed net will sit.
    /// </summary>
    private IReadOnlyDictionary<int, double> LayerZMap()
    {
        if (LayerFilters.Count == 0) return new Dictionary<int, double>();
        int minL = LayerFilters.Min(f => f.LayerOrder);
        int maxL = LayerFilters.Max(f => f.LayerOrder);
        var (layerZ, _) = NetMesher.BuildStackupZ(minL, maxL, new NetMeshOptions
        {
            CopperThickness = PcbCopperThickness,
            LayerThickness = LayerFilters.ToDictionary(f => f.LayerOrder, f => f.ThicknessMeters),
            DielectricGapThickness = DielectricGaps.ToDictionary(g => g.UpperLayerOrder, g => g.ThicknessMeters),
            DefaultDielectricThickness = PcbBoardThickness
        });
        return layerZ.ToDictionary(kv => kv.Key, kv => (kv.Value.zLo + kv.Value.zHi) / 2);
    }

    private IReadOnlyDictionary<int, double> CachedLayerZMap() => _layerZMapCache ??= LayerZMap();

    /// <summary>A stackup edit moved every layer's z: recompute the z-map on next use.</summary>
    private void InvalidateLayerZ()
    {
        _layerZMapCache = null;
        RefreshCopperPreview();
    }

    /// <summary>
    /// Queues a preview recompose. Coalesced through the dispatcher so a burst of row
    /// toggles (Show all / solo fires one PropertyChanged per row) costs one recompose.
    /// </summary>
    private void RefreshCopperPreview()
    {
        if (_refreshQueued) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            RecomposeCopperPreview();
            return;
        }
        _refreshQueued = true;
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            _refreshQueued = false;
            RecomposeCopperPreview();
        });
    }

    /// <summary>Rebuilds the preview tree from the cached frozen ribbon models —
    /// O(nets × layers) dictionary lookups, no geometry construction.</summary>
    private void RecomposeCopperPreview()
    {
        var root = new Model3DGroup();
        if (_board is not null && _previewCache is not null && ShowCopperPreview)
        {
            var zMap = CachedLayerZMap();
            foreach (var filter in LayerFilters)
            {
                if (!filter.Enabled) continue;
                var layerGroup = new Model3DGroup
                {
                    Transform = new TranslateTransform3D(0, 0,
                        zMap.TryGetValue(filter.LayerOrder, out double z) ? z : 0)
                };
                foreach (var row in NetRows)
                    if (row.IsVisible &&
                        _previewCache.TryGetValue((row.Net.Id, filter.LayerOrder), out var model))
                        layerGroup.Children.Add(model);
                if (layerGroup.Children.Count > 0)
                    root.Children.Add(layerGroup);
            }
        }
        root.Freeze();
        CopperPreviewModel = root;
    }

    partial void OnShowCopperPreviewChanged(bool value) => RefreshCopperPreview();
    partial void OnPcbCopperThicknessChanged(double value) => InvalidateLayerZ();
    partial void OnPcbBoardThicknessChanged(double value) => InvalidateLayerZ();

    partial void OnSelectedNetRowChanged(NetRow? value) => SelectedNet = value?.Net;

    [RelayCommand]
    private void ShowAllNets() => SetAllNetsVisible(true);

    [RelayCommand]
    private void HideAllNets() => SetAllNetsVisible(false);

    private void SetAllNetsVisible(bool visible)
    {
        DropSolo();
        // Bulk write under the programmatic-write guard: each row change would otherwise
        // run the per-row handler (DropSolo is O(rows)); one refresh at the end suffices.
        _applyingSolo = true;
        try
        {
            foreach (var row in NetRows) row.IsVisible = visible;
        }
        finally { _applyingSolo = false; }
        RefreshCopperPreview();
    }

    [RelayCommand]
    private void ShowAllLayers() => SetAllLayersEnabled(true);

    [RelayCommand]
    private void HideAllLayers() => SetAllLayersEnabled(false);

    /// <summary>Guard for bulk layer writes: the per-filter Enabled handler re-filters the
    /// whole net list, which would otherwise run once per layer.</summary>
    private bool _applyingBulkLayers;

    private void SetAllLayersEnabled(bool enabled)
    {
        _applyingBulkLayers = true;
        try
        {
            foreach (var filter in LayerFilters) filter.Enabled = enabled;
        }
        finally { _applyingBulkLayers = false; }
        RefreshCopperPreview();
        RefreshNetListFilter();
    }

    /// <summary>
    /// Selects the visible net under a viewport click ray. Each island is tested at its
    /// own layer's z-plane — a single fixed pick plane selects the wrong trace under an
    /// angled camera, because the parallax between layer heights shifts each layer's hit
    /// point laterally. The smallest containing island wins, so a trace on top of a plane
    /// is picked over the plane beneath it; hidden nets/layers are skipped.
    /// </summary>
    public bool PickNetAt(Point3D rayOrigin, System.Windows.Media.Media3D.Vector3D rayDirection)
    {
        if (_board is null) return false;
        var zMap = CachedLayerZMap();
        if (zMap.Count == 0)
            zMap = _board.Islands.Select(i => i.LayerOrder).Distinct()
                .ToDictionary(order => order, _ => 0.0);
        var best = NetPicker.Pick(_board.Nets, NetToggleOn, LayerEnabled, zMap,
            (rayOrigin.X, rayOrigin.Y, rayOrigin.Z),
            (rayDirection.X, rayDirection.Y, rayDirection.Z));
        if (best is null) return false;
        SelectedNetRow = NetRows.FirstOrDefault(r => r.Net == best);
        SelectedNet = best;
        return true;
    }

    [RelayCommand]
    private async Task ImportPcbAsync()
    {
        // Import the WHOLE board first (classify layers, polygonize all copper, extract
        // connected nets), then let the user pick a net to mesh and simulate. Point at a
        // fabrication ZIP / Gerber folder, or an IPC-2581 (.cvg/.xml) export.
        var dialog = new OpenFileDialog
        {
            Filter = "PCB design (*.zip;*.gbr;*.drl;*.xln;*.cvg;*.xml)|*.zip;*.gbr;*.drl;*.xln;*.cvg;*.xml" +
                     "|IPC-2581 (*.cvg;*.xml)|*.cvg;*.xml" +
                     "|Gerber fabrication set (*.zip;*.gbr;*.drl;*.xln)|*.zip;*.gbr;*.drl;*.xln" +
                     "|All files (*.*)|*.*",
            Title = "Select an IPC-2581 file, a Gerber ZIP, or any file in the Gerber folder"
        };
        if (dialog.ShowDialog() != true) return;

        string path = dialog.FileName;
        bool ipc2581 = Ipc2581Reader.Matches(path);
        string archive = ipc2581 || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.GetDirectoryName(path) ?? path;

        _session.IsBusy = true;
        _session.StatusText = "Reading board…";
        try
        {
            var importTimer = System.Diagnostics.Stopwatch.StartNew();
            // The ribbon cache is built here too: frozen Freezables may be constructed
            // off the dispatcher thread, and a 300k-segment board takes long enough
            // that it must not run on the UI thread.
            var (board, previewCache) = await Task.Run(() =>
            {
                var b = ipc2581
                    ? new Ipc2581Reader().Read(archive)
                    : new PcbBoardReader().Read(archive);
                return (b, BuildPreviewCache(b));
            });
            importTimer.Stop();
            _board = board;
            _previewCache = previewCache;
            _layerZMapCache = null;
            _inductance.LoadBoard(board, BuildNetMeshOptions);
            _antenna.LoadBoard(board, BuildNetMeshOptions);
            _signalIntegrity.LoadBoard(board, BuildNetMeshOptions);

            _log.Append($"PCB archive: {board.Layers.Count} layers classified " +
                        $"(import {importTimer.ElapsedMilliseconds} ms).");
            foreach (var layer in board.Layers)
                _log.Append($"  • {layer.Type} — {layer.FileName}");
            foreach (var w in board.Warnings) _log.Append($"PCB: {w}");

            // The board workflow is electrical: land in the Electrical workspace and
            // switch the analysis right away (Joule stays — it includes the electrical
            // setup); PCB mode hides the generic primitive/material panels.
            _session.ActiveWorkspace = WorkspaceKind.Electrical;
            _session.IsPcbMode = true;
            if (_session.SelectedAnalysis.Kind is not (AnalysisType.Electrical or AnalysisType.JouleCoupled))
                _session.SelectedAnalysis = AnalysisOption.All.First(o => o.Kind == AnalysisType.Electrical);

            // Reset to a board-preview state: no active FE body, so clicks pick nets.
            _session.Body = new Body { Name = "Board preview" };
            PopulateNets(board);
            SelectedNet = null;
            SelectedNetRow = null;
            ShowCopperPreview = true;
            RefreshCopperPreview();
            // Board profile at z = 0, the bottom of the stackup the layers sit above.
            BoardOutlinePoints = SceneBuilder.BuildOutline(board.Outline);
            ShowBoardOutline = true;
            SelectedNetOutlines = new Point3DCollection();
            BoardInfo = $"{board.Nets.Count} nets, {board.Islands.Count} copper islands, " +
                        $"{board.Vias.Count(v => v.Plated)} plated vias";

            // The FE scene stays empty until a net is meshed; these events clear the
            // previous body's conditions/results/scene and refit the view.
            _session.RaiseGeometryReplaced(leavingPcbMode: false);
            _session.RaiseMeshChanged();

            _log.Append($"Board imported. {board.Nets.Count} nets found — select a net and click " +
                        "\"Mesh selected net\" to simulate it.");
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally { _session.IsBusy = false; _session.StatusText = "Ready"; }
    }

    /// <summary>One frozen ribbon model per (net, layer): the pieces every later
    /// visibility recompose assembles by reference.</summary>
    private static Dictionary<(int NetId, int LayerOrder), GeometryModel3D> BuildPreviewCache(PcbBoard board)
    {
        double halfWidth = PreviewRibbonHalfWidth(board);
        var cache = new Dictionary<(int, int), GeometryModel3D>();
        foreach (var net in board.Nets)
            foreach (var byLayer in net.Islands.GroupBy(i => i.LayerOrder))
                cache[(net.Id, byLayer.Key)] = SceneBuilder.BuildIslandOutlineRibbons(
                    byLayer, SceneBuilder.LayerColor(byLayer.Key), halfWidth);
        return cache;
    }

    /// <summary>
    /// Ribbon half-width scaled to the board: ~0.05% of the outline diagonal, clamped to
    /// [10, 60] µm. Near fit-to-view that is roughly a hairline; unlike screen-space
    /// lines it thins further when zooming out and stays honest world geometry.
    /// </summary>
    private static double PreviewRibbonHalfWidth(PcbBoard board)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var rings = board.Outline.Count > 0
            ? board.Outline.Select(p => p.Outer)
            : board.Islands.Select(i => i.Shape.Outer);
        foreach (var ring in rings)
            foreach (var p in ring)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
        if (maxX < minX) return 25e-6;   // no geometry at all: any sane default
        double diag = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        return Math.Clamp(2.5e-4 * diag, 10e-6, 60e-6);
    }

    /// <summary>Rebuilds the net rows and per-layer filters for a freshly imported board.</summary>
    private void PopulateNets(PcbBoard board)
    {
        foreach (var row in NetRows) row.PropertyChanged -= OnNetRowChanged;
        foreach (var f in LayerFilters) f.PropertyChanged -= OnLayerFilterChanged;
        foreach (var g in DielectricGaps) g.PropertyChanged -= OnDielectricGapChanged;
        NetRows.Clear();
        LayerFilters.Clear();
        DielectricGaps.Clear();
        _soloSnapshot = null;

        var layerNames = board.Layers
            .Where(l => l.CopperOrder > 0)
            .GroupBy(l => l.CopperOrder)
            .ToDictionary(g => g.Key, g => g.First().FileName);
        var orders = board.Islands.Select(i => i.LayerOrder).Distinct().OrderBy(o => o).ToList();
        foreach (var order in orders)
        {
            // IPC-2581 names its layers and declares per-layer copper thickness; Gerber
            // has neither, so fall back to "L{n}" and the 35 µm default.
            var filter = new LayerFilter(order, board.Stackup is null
                ? null : layerNames.GetValueOrDefault(order));
            if (board.Stackup is not null && order - 1 < board.Stackup.CopperLayerThicknesses.Count)
                filter.ThicknessMicrons = board.Stackup.CopperLayerThicknesses[order - 1] * 1e6;
            filter.PropertyChanged += OnLayerFilterChanged;
            LayerFilters.Add(filter);
        }

        // One editable dielectric gap between each adjacent copper-layer pair, seeded from
        // the file's stackup when it has one (IPC-2581), else by splitting the board
        // thickness evenly across the gaps.
        if (board.Stackup is not null && board.Stackup.DielectricGapThicknesses.Count > 0)
            PcbBoardThickness = board.Stackup.BoardThickness;
        int gapCount = Math.Max(1, orders.Count - 1);
        double defaultGapMicrons = PcbBoardThickness / gapCount * 1e6;
        for (int i = 0; i + 1 < orders.Count; i++)
        {
            var gaps = board.Stackup?.DielectricGapThicknesses;
            double microns = gaps is not null && orders[i] - 1 < gaps.Count
                ? gaps[orders[i] - 1] * 1e6
                : defaultGapMicrons;
            // RF materials: from the file's stackup when it names them, else FR4 (the gap
            // ctor's default). Index matches the thickness list — one entry per gap.
            var perms = board.Stackup?.DielectricGapPermittivities;
            var losses = board.Stackup?.DielectricGapLossTangents;
            double eps = perms is not null && orders[i] - 1 < perms.Count ? perms[orders[i] - 1] : 4.4;
            double tand = losses is not null && orders[i] - 1 < losses.Count ? losses[orders[i] - 1] : 0.02;
            var gap = new DielectricGap(orders[i], microns, eps, tand);
            gap.PropertyChanged += OnDielectricGapChanged;
            DielectricGaps.Add(gap);
        }
        foreach (var net in board.Nets)
        {
            var row = new NetRow(net);
            row.PropertyChanged += OnNetRowChanged;
            NetRows.Add(row);
        }
    }

    private void OnNetRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_applyingSolo) return;
        if (e.PropertyName == nameof(NetRow.IsSolo) && sender is NetRow row)
        {
            ApplySolo(row);
        }
        else if (e.PropertyName == nameof(NetRow.IsVisible))
        {
            DropSolo();   // a manual toggle takes over; keep the visibilities as they are now
            RefreshCopperPreview();
        }
    }

    private void OnLayerFilterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Enabled changes what is drawn and which nets are listed;
        // a thickness edit moves every layer's z.
        if (_applyingBulkLayers) return;
        if (e.PropertyName == nameof(LayerFilter.ThicknessMicrons))
            InvalidateLayerZ();
        else if (e.PropertyName == nameof(LayerFilter.Enabled))
        {
            RefreshCopperPreview();
            RefreshNetListFilter();
        }
    }

    /// <summary>Delists nets whose copper is entirely on disabled layers (no vias).</summary>
    private void RefreshNetListFilter()
    {
        foreach (var row in NetRows)
            row.IsListed = NetVisibility.IsListed(row.Net, LayerEnabled);
        if (SelectedNetRow is { IsListed: false })
            SelectedNetRow = null;
    }

    // ---------------- "Hide all except this net" (solo) ----------------

    /// <summary>Re-entrancy guard: applying a solo writes IsVisible/IsSolo on other rows,
    /// which must not be mistaken for the user taking over.</summary>
    private bool _applyingSolo;

    /// <summary>Visibilities as they were before the first solo, restored on un-solo.
    /// One snapshot per solo session: soloing net B directly after net A keeps A's
    /// pre-solo snapshot, so un-soloing returns to the original state.</summary>
    private Dictionary<NetRow, bool>? _soloSnapshot;

    private void ApplySolo(NetRow row)
    {
        _applyingSolo = true;
        try
        {
            if (row.IsSolo)
            {
                _soloSnapshot ??= NetRows.ToDictionary(r => r, r => r.IsVisible);
                foreach (var r in NetRows)
                {
                    if (!ReferenceEquals(r, row)) r.IsSolo = false;
                    r.IsVisible = ReferenceEquals(r, row);
                }
            }
            else
            {
                if (_soloSnapshot is not null)
                    foreach (var r in NetRows)
                        if (_soloSnapshot.TryGetValue(r, out bool wasVisible))
                            r.IsVisible = wasVisible;
                _soloSnapshot = null;
            }
        }
        finally { _applyingSolo = false; }
        RefreshCopperPreview();
    }

    /// <summary>The user edited visibility directly — the solo no longer describes the
    /// state, so drop the flag and the snapshot without restoring anything.</summary>
    private void DropSolo()
    {
        if (_soloSnapshot is null && !NetRows.Any(r => r.IsSolo)) return;
        _applyingSolo = true;
        try
        {
            foreach (var r in NetRows) r.IsSolo = false;
        }
        finally { _applyingSolo = false; }
        _soloSnapshot = null;
    }

    private void OnDielectricGapChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DielectricGap.ThicknessMicrons)) InvalidateLayerZ();
    }

    partial void OnSelectedNetChanged(CopperNet? value)
    {
        // The highlight obeys the same per-layer filter as the preview, so selecting a
        // net never re-reveals copper on a disabled layer.
        SelectedNetOutlines = value is null
            ? new Point3DCollection()
            : SceneBuilder.BuildIslandOutlines(
                value.Islands.Where(i => LayerEnabled(i.LayerOrder)), CachedLayerZMap());
        _session.StatusText = value is null ? "Ready" : value.Label;
        _inductance.SetDefaultNet(value);
        _antenna.SetDefaultNet(value);
    }

    [RelayCommand]
    private async Task MeshSelectedNetAsync()
    {
        if (SelectedNet is null)
        {
            _log.Append("Select a net from the list first.");
            return;
        }
        var net = SelectedNet;
        var boardPads = _board?.Pads;
        var options = BuildNetMeshOptions();
        _session.IsBusy = true;
        _session.StatusText = $"Meshing {net.Label}…";
        try
        {
            var result = await Task.Run(() => new NetMesher().MeshNet(net, boardPads, options));
            foreach (var w in result.Warnings) _log.Append($"Net: {w}");

            LoadImportedBody(result.Body, BuildStackupSettings());
            // The meshed net now stands in for the preview: hide the rest of the copper
            // (toggle it back on with "Show full-board copper").
            SelectedNetOutlines = new Point3DCollection();
            ShowCopperPreview = false;

            _electrodes.LoadPads(result.Pads);
            LoadTraceChainFor(net);
            if (_session.SelectedAnalysis.Kind == AnalysisType.Static)
            {
                _session.ActiveWorkspace = WorkspaceKind.Electrical;
                _session.SelectedAnalysis = AnalysisOption.All.First(o => o.Kind == AnalysisType.Electrical);
            }

            _session.RaiseHighlightsInvalidated();   // paint the pad faces gold so they read as clickable
            _log.Append($"Meshed {net.Label}: {result.Body.Mesh!.ElementCount} elements, {result.Pads.Count} pads. " +
                        "Choose Source and Sink pads, then Run electrical test for resistance.");
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally { _session.IsBusy = false; _session.StatusText = "Ready"; }
    }

    /// <summary>The stackup persisted with the project: copper/board thickness plus the
    /// per-gap thicknesses AND RF materials (εr/tanδ), indexed by (UpperLayerOrder − 1) so
    /// the import seeder reads them back at the same index. Empty when there are no gaps.</summary>
    private PcbStackupSettings BuildStackupSettings()
    {
        int maxOrder = DielectricGaps.Count == 0 ? 0 : DielectricGaps.Max(g => g.UpperLayerOrder);
        if (maxOrder == 0)
            return new PcbStackupSettings
            {
                CopperThickness = PcbCopperThickness,
                BoardThickness = PcbBoardThickness
            };
        var thick = new double[maxOrder];
        var perm = new double[maxOrder];
        var loss = new double[maxOrder];
        for (int i = 0; i < maxOrder; i++)
        {
            thick[i] = PcbBoardThickness / maxOrder;   // benign default for any unlisted gap
            perm[i] = 4.4;
            loss[i] = 0.02;
        }
        foreach (var g in DielectricGaps)
        {
            int idx = g.UpperLayerOrder - 1;
            thick[idx] = g.ThicknessMeters;
            perm[idx] = g.RelativePermittivity;
            loss[idx] = g.LossTangent;
        }
        return new PcbStackupSettings
        {
            CopperThickness = PcbCopperThickness,
            BoardThickness = PcbBoardThickness,
            DielectricGapThicknesses = thick,
            DielectricGapPermittivities = perm,
            DielectricGapLossTangents = loss
        };
    }

    /// <summary>The mesh options the net mesher, the copper preview, and the inductance
    /// chain builder all share — one stackup z model, so where copper is drawn, meshed,
    /// and composed for PEEC is the same place.</summary>
    private NetMeshOptions BuildNetMeshOptions() => new()
    {
        TargetEdgeLength = _meshing.TargetEdgeLength,
        CopperThickness = PcbCopperThickness,
        LayerThickness = LayerFilters.ToDictionary(f => f.LayerOrder, f => f.ThicknessMeters),
        DielectricGapThickness = DielectricGaps.ToDictionary(g => g.UpperLayerOrder, g => g.ThicknessMeters),
        DefaultDielectricThickness = PcbBoardThickness,
        ViaPlatingThickness = ViaPlatingMicrons * 1e-6
    };

    /// <summary>Hands the meshed net's 3D trace-chain builder to the electrodes view
    /// model for the lumped AC impedance estimate. A builder delegate, not a one-shot
    /// chain: the estimate rebuilds with the pad pair selected AT ESTIMATE TIME as the
    /// terminals, so branched nets reduce to the source→sink current path and the L is
    /// consistent with the DC R measured between the same pads.</summary>
    private void LoadTraceChainFor(CopperNet net)
    {
        var board = _board;
        if (board is null || board.TraceCenterlines.Count == 0)
        {
            var failure = OpenSim.Pcb.Inductance.TraceChain3DResult.Failure(
                "no trace centerlines were captured for this board (the file may contain only " +
                "pours, regions, and flashes — no drawn traces)");
            _electrodes.LoadTraceChain(_ => failure);
            return;
        }
        _electrodes.LoadTraceChain(terminals => OpenSim.Pcb.Inductance.TraceChainBuilder.Build(
            OpenSim.Pcb.Inductance.NetTraceExtractor.ForNet(board, net),
            net.StitchingVias, BuildNetMeshOptions(), net.Islands,
            includeLayers: null, terminals));
    }

    /// <summary>Installs an already-meshed imported body as the active body; the session
    /// events reset conditions/results and rebuild the scene, mesh info and edges.</summary>
    private void LoadImportedBody(Body body, PcbStackupSettings stackup)
    {
        _session.Project.Bodies.Clear();
        _session.Project.Bodies.Add(body);
        _session.Project.Stackup = stackup;
        _session.Body = body;

        if (body.RegionMaterialNames is not null
            && body.RegionMaterialNames.TryGetValue(0, out var copperName))
        {
            var match = _materials.FindByName(copperName);
            if (match is not null) _session.SelectedMaterial = match;
        }

        _session.RaiseGeometryReplaced(leavingPcbMode: false);
        _session.RaiseMeshChanged();
    }

    /// <summary>Returning to the generic workflow (primitive, STL, project open):
    /// clear every board-related visual and collection.</summary>
    private void TearDownBoardState()
    {
        if (_board is null && NetRows.Count == 0 && LayerFilters.Count == 0) return;
        _board = null;
        _inductance.Clear();
        _antenna.Clear();
        _session.IsPcbMode = false;
        foreach (var row in NetRows) row.PropertyChanged -= OnNetRowChanged;
        foreach (var f in LayerFilters) f.PropertyChanged -= OnLayerFilterChanged;
        foreach (var g in DielectricGaps) g.PropertyChanged -= OnDielectricGapChanged;
        NetRows.Clear();
        LayerFilters.Clear();
        DielectricGaps.Clear();
        _soloSnapshot = null;
        SelectedNet = null;
        SelectedNetRow = null;
        _previewCache = null;
        _layerZMapCache = null;
        CopperPreviewModel = new Model3DGroup();
        BoardOutlinePoints = new Point3DCollection();
        SelectedNetOutlines = new Point3DCollection();
        BoardInfo = "No board imported";
    }
}
