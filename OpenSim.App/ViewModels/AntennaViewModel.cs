using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Rendering;
using OpenSim.App.Services;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.PostProcessing;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf;
using Vector3D = OpenSim.Core.Numerics.Vector3D;

namespace OpenSim.App.ViewModels;

/// <summary>One frequency point of the antenna input-impedance sweep.</summary>
public sealed record AntennaZinPoint(double FrequencyHz, double Resistance, double Reactance);

/// <summary>
/// The Antenna Simulator (thin-wire method of moments): input impedance vs frequency,
/// a free-space near-field vector map + slice heatmap, and the far-field radiation
/// lobe, rendered as viewport overlays. Geometry comes from the selected net's trace
/// chain (strips as equivalent-radius wires) or from the canonical dipole/loop wizard —
/// no meshing needed. All engine assumptions and typed failures surface verbatim;
/// free space means the board dielectric is NOT modeled, and the panel says so.
/// The PCB view model hands the board over after import (the same sanctioned edge as
/// the inductance panel); the electrodes view model is READ for the selected source
/// pad, which places the feed on net-sourced antennas.
/// </summary>
public partial class AntennaViewModel : ObservableObject
{
    public const string NetMode = "Selected net (PCB)";
    public const string DipoleMode = "Dipole (wizard)";
    public const string LoopMode = "Loop (wizard)";
    public const string MonopoleMode = "Monopole (wizard)";
    public const string PlateMode = "Plate (wizard, RWG)";
    public const string PatchMode = "Patch over ground (wizard, RWG)";
    public const string ProbeFedPatchMode = "Probe-fed patch (wizard, RWG + coax)";
    public const string CoveredPatchMode = "Covered patch (wizard, RWG + cover)";
    public const string IslandMode = "Copper island (PCB, RWG)";

    private readonly ILogService _log;
    private readonly ElectrodesViewModel _electrodes;
    private PcbBoard? _board;
    private Func<NetMeshOptions>? _options;

    public AntennaViewModel(ProjectSession session, ILogService log, ElectrodesViewModel electrodes)
    {
        _log = log;
        _electrodes = electrodes;
        // Stale field overlays floating over a replaced body/scene would misread as
        // results for the new geometry.
        session.GeometryReplaced += (_, _) => ClearOverlays();
    }

    public ObservableCollection<string> SourceModes { get; } =
        new() { NetMode, DipoleMode, LoopMode, MonopoleMode, PlateMode, PatchMode,
            ProbeFedPatchMode, CoveredPatchMode, IslandMode };

    /// <summary>Surface (RWG) modes solve sheets; the others solve thin wires. Mixing
    /// the two in one structure is not supported in v1 — the modes are disjoint by
    /// construction, so no combined request can even be expressed.</summary>
    private bool IsSurfaceMode =>
        SourceMode is PlateMode or PatchMode or ProbeFedPatchMode or CoveredPatchMode or IslandMode;

    /// <summary>The covered patch (Stage F): a patch buried under a dielectric cover of the
    /// SAME εr as its substrate (a homogeneous slab split at the metal), solved through the
    /// multi-layer transmission-line Green's function with the source at the buried interface.
    /// The cover loads the patch — its resonance drops below the bare patch's.</summary>
    private bool IsCoveredPatchMode => SourceMode == CoveredPatchMode;

    /// <summary>The probe-fed patch drives the substrate patch with a real coaxial
    /// port through the slab (Stage E); the other surface modes use an edge/gap port.</summary>
    private bool IsProbeMode => SourceMode == ProbeFedPatchMode;

    // Coaxial probe feed [mm]: lateral position from the patch centre, bore radius,
    // and the number of tube segments across the slab (≥ 2; the slab must be thick
    // enough that each segment ≥ 2·radius — a typed failure otherwise).
    [ObservableProperty] private double _probeXMm;
    [ObservableProperty] private double _probeYMm = -20;
    [ObservableProperty] private double _probeRadiusMm = 0.2;
    [ObservableProperty] private int _probeSegments = 3;

    // Dielectric cover over a covered patch [mm]: its thickness. The cover shares the
    // substrate's εr/tanδ (SubstrateEpsR / SubstrateTanD) — the homogeneous-slab-split
    // model that keeps the buried-source read-out unambiguous (Stage F2b).
    [ObservableProperty] private double _coverThicknessMm = 0.8;

    /// <summary>Nullable so the ComboBox's transient null push lands harmlessly.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBoardOverlay))]
    private string? _sourceMode = DipoleMode;

    public ObservableCollection<CopperNet> Nets { get; } = new();
    [ObservableProperty] private CopperNet? _antennaNet;

    // Wizard dimensions [mm] — defaults sit near λ/2 resonance at the default frequency.
    [ObservableProperty] private double _dipoleLengthMm = 500;
    [ObservableProperty] private double _loopRadiusMm = 80;
    [ObservableProperty] private double _monopoleHeightMm = 250;
    [ObservableProperty] private double _wireRadiusMm = 0.5;

    // Infinite PEC ground plane (image theory). The monopole and patch modes always
    // use it (they are meaningless without); other modes opt in. Wizard shapes are
    // lifted HeightAboveGroundMm above the plane; a net keeps its board coordinates.
    [ObservableProperty] private bool _useGroundPlane;
    [ObservableProperty] private double _groundZMm;
    [ObservableProperty] private double _heightAboveGroundMm = 250;

    // Surface (RWG) wizard dimensions [mm]. The plate doubles as the patch metal;
    // the patch height is the gap to the ground plane.
    [ObservableProperty] private double _plateWidthMm = 300;
    [ObservableProperty] private double _plateLengthMm = 500;
    [ObservableProperty] private double _patchHeightMm = 50;

    // Dielectric substrate filling the gap under surface metal (the layered-media
    // Green's-function path). εr = 1 keeps the Stage B PEC-image path — the default
    // changes nothing. The slab thickness IS the patch height / height above ground:
    // v1 metal sits on the slab's top surface. A board import seeds these (editable).
    [ObservableProperty] private double _substrateEpsR = 1.0;
    [ObservableProperty] private double _substrateTanD;

    /// <summary>Copper islands of the selected net, for the island (RWG) mode.</summary>
    public ObservableCollection<IslandChoice> AntennaIslands { get; } = new();
    [ObservableProperty] private IslandChoice? _antennaIsland;

    // Frequency [MHz]: the field/lobe frequency plus the impedance sweep range.
    [ObservableProperty] private double _frequencyMHz = 300;
    [ObservableProperty] private double _sweepFMinMHz = 100;
    [ObservableProperty] private double _sweepFMaxMHz = 1000;
    [ObservableProperty] private int _sweepPoints = 7;

    /// <summary>Near-field sample grid resolution per axis (n³ arrows).</summary>
    [ObservableProperty] private int _gridResolution = 9;

    [ObservableProperty] private string _antennaResult = "";
    [ObservableProperty] private string _fieldResult = "";
    [ObservableProperty] private string _antennaAssumptions = "";
    public ObservableCollection<AntennaZinPoint> ZinSweep { get; } = new();

    // ------------------------------------------------------------------
    // Board field overlay (SIwave-style): a translucent |field| heatmap plane over
    // the PCB/structure. E everywhere; H = ∇×A/µ₀ for free-space/PEC (Stage S7) AND over a
    // single-slab substrate (Stage S9a, the layered ∂zG̃_A / boundary-trick curl). Only
    // MULTI-LAYER / covered near-field maps (E and H) stay the S9b typed follow-up.
    // ------------------------------------------------------------------
    public const string EFieldOverlay = "E (electric)";
    public const string HFieldOverlay = "H (magnetic)";
    public const string LogScale = "Logarithmic";
    public const string LinearScale = "Linear";

    public ObservableCollection<string> OverlayFieldTypes { get; } =
        new() { EFieldOverlay, HFieldOverlay };
    public ObservableCollection<string> OverlayScaleModes { get; } =
        new() { LogScale, LinearScale };

    /// <summary>Nullable so a ComboBox transient null push lands harmlessly.</summary>
    [ObservableProperty] private string? _overlayFieldType = EFieldOverlay;
    [ObservableProperty] private string? _overlayScaleMode = LogScale;

    /// <summary>Overlay plane height above the structure's top metal [mm].</summary>
    [ObservableProperty] private double _overlayHeightMm = 1.0;
    /// <summary>Overlay sample grid resolution per axis (n² points).</summary>
    [ObservableProperty] private int _overlayGridN = 61;
    [ObservableProperty] private bool _overlayAutoRange = true;
    // Explicit color range [V/m], used when auto-range is off. In log mode a
    // non-positive min falls back to max/10^decades (FieldScale.EffectiveMin).
    [ObservableProperty] private double _overlayMinVPerM;
    [ObservableProperty] private double _overlayMaxVPerM = 100;
    /// <summary>Decades below the peak spanned by an auto-ranged log overlay.</summary>
    [ObservableProperty] private int _overlayDecades = 3;
    [ObservableProperty] private double _overlayOpacityPercent = 60;

    /// <summary>Paint the overlay over the actual board OUTLINE at each copper-layer z
    /// (Stage S10, the SIwave board view), instead of one rectangular plane hovering above
    /// the solved net. Only active when a board net is loaded (<see cref="HasBoardOverlay"/>).</summary>
    [ObservableProperty] private bool _overlayOverBoard;

    /// <summary>True when a board is loaded and the selected source is a board net — the
    /// board-outline overlay is available (the wizard shapes have no board to paint over).</summary>
    public bool HasBoardOverlay => _board is not null && SourceMode == NetMode;

    // The overlay's own legend (same visual style as the FE legend, separate pipeline —
    // the FE legend's title/visibility are driven by the Results VM's selected field).
    [ObservableProperty] private string _overlayLegendTitle = "";
    [ObservableProperty] private Brush _overlayLegendBrush = Brushes.Transparent;
    [ObservableProperty] private string _overlayLegendMin = "";
    [ObservableProperty] private string _overlayLegendMax = "";

    // Viewport overlays (bound by Viewport3DView, the same hosting pattern as the
    // PCB preview lines).
    [ObservableProperty] private Model3D? _vectorFieldModel;
    [ObservableProperty] private Model3D? _fieldSliceModel;
    [ObservableProperty] private Model3D? _fieldOverlayModel;
    [ObservableProperty] private Model3D? _farFieldLobeModel;
    [ObservableProperty] private Model3D? _groundPlaneModel;
    [ObservableProperty] private Model3D? _surfaceCurrentModel;
    [ObservableProperty] private Point3DCollection _wirePoints = new();

    /// <summary>Installs an imported board (same sanctioned edge as the inductance panel).</summary>
    public void LoadBoard(PcbBoard board, Func<NetMeshOptions> meshOptions)
    {
        Clear();
        _board = board;
        _options = meshOptions;
        foreach (var net in board.Nets) Nets.Add(net);
        SourceMode = NetMode;

        // Seed the island-antenna substrate from the board stackup (editable; only
        // when the user hasn't already set one — a re-import must not clobber edits).
        var options = meshOptions();
        if (options.DefaultDielectricThickness > 0)
            HeightAboveGroundMm = options.DefaultDielectricThickness * 1e3;
        if (SubstrateEpsR == 1.0)
        {
            SubstrateEpsR = 4.4; // FR4 — the board material the rest of the app assumes
            SubstrateTanD = 0.02;
            _log.Append("Antenna: substrate seeded from the board (FR4 εr 4.4, tanδ 0.02, "
                + $"thickness {HeightAboveGroundMm:g3} mm) — edit in the panel; εr = 1 restores the air/PEC path.");
        }
    }

    /// <summary>Follows the PCB panel's net selection until the user picks one here.</summary>
    public void SetDefaultNet(CopperNet? net)
    {
        if (AntennaNet is null && net is not null && Nets.Contains(net))
            AntennaNet = net;
    }

    public void Clear()
    {
        _board = null;
        _options = null;
        AntennaNet = null;
        Nets.Clear();
        ZinSweep.Clear();
        AntennaResult = "";
        FieldResult = "";
        AntennaAssumptions = "";
        ClearOverlays();
    }

    [RelayCommand]
    private void ClearOverlays()
    {
        VectorFieldModel = null;
        FieldSliceModel = null;
        FieldOverlayModel = null;
        FarFieldLobeModel = null;
        GroundPlaneModel = null;
        SurfaceCurrentModel = null;
        WirePoints = new Point3DCollection();
        OverlayLegendTitle = "";
        OverlayLegendBrush = Brushes.Transparent;
        OverlayLegendMin = OverlayLegendMax = "";
    }

    partial void OnAntennaNetChanged(CopperNet? value)
    {
        AntennaIslands.Clear();
        AntennaIsland = null;
        if (value is null) return;
        foreach (var island in value.Islands.OrderByDescending(
                     i => Math.Abs(OpenSim.Core.Geometry2D.Polygon2.RingArea(i.Shape.Outer))))
            AntennaIslands.Add(new IslandChoice(island));
        AntennaIsland = AntennaIslands.FirstOrDefault();
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task SolveAntenna()
    {
        ZinSweep.Clear();
        AntennaResult = "";
        AntennaAssumptions = "";
        if (SweepFMinMHz <= 0 || SweepFMaxMHz < SweepFMinMHz || SweepPoints < 1 || FrequencyMHz <= 0)
        {
            AntennaResult = "Not solvable: the frequency range is invalid.";
            return;
        }

        if (IsSurfaceMode)
        {
            // Surface fills are O(N²·quadrature) and can take tens of seconds — run
            // off-thread so the UI stays live (wire solves are sub-second and inline).
            await SolveSurfaceAntennaAsync();
            return;
        }

        if (!TryDiscretize(out var wire, out var feedBasis, out var warnings, out string? failure))
        {
            AntennaResult = $"Not solvable: {failure}";
            return;
        }

        try
        {
            var solver = new ThinWireMomSolver();
            for (int k = 0; k < SweepPoints; k++)
            {
                double f = SweepPoints == 1
                    ? SweepFMinMHz * 1e6
                    : SweepFMinMHz * 1e6 * Math.Pow(SweepFMaxMHz / SweepFMinMHz, (double)k / (SweepPoints - 1));
                var point = solver.Solve(wire, f, feedBasis);
                ZinSweep.Add(new AntennaZinPoint(f,
                    point.InputImpedance.Real, point.InputImpedance.Imaginary));
            }

            var display = solver.Solve(wire, FrequencyMHz * 1e6, feedBasis);
            AntennaResult = $"Zin = {display.InputImpedance.Real:g4} " +
                            $"{(display.InputImpedance.Imaginary >= 0 ? "+" : "−")} " +
                            $"j{Math.Abs(display.InputImpedance.Imaginary):g4} Ω at {FrequencyMHz:g4} MHz " +
                            $"({wire.BasisCount} unknowns)";
            AntennaAssumptions = "Assumptions: " + string.Join(" ", BuildAssumptions(wire))
                + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "");
            _log.Append($"Antenna: {AntennaResult}");
        }
        catch (Exception ex) { AntennaResult = $"Not solvable: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ComputeNearField()
    {
        FieldResult = "";
        if (IsSurfaceMode)
        {
            await ComputeSurfaceNearFieldAsync();
            return;
        }
        if (!TryDiscretize(out var wire, out var feedBasis, out _, out string? failure))
        {
            FieldResult = $"Not computable: {failure}";
            return;
        }

        try
        {
            var solution = new ThinWireMomSolver().Solve(wire, FrequencyMHz * 1e6, feedBasis);
            var (center, diagonal) = BoundingSphere(wire);
            double span = 1.6 * diagonal;
            int n = Math.Clamp(GridResolution, 3, 17);
            double spacing = span / (n - 1);

            var points = new List<Vector3D>(n * n * n);
            for (int z = 0; z < n; z++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        points.Add(center + new Vector3D(
                            x * spacing - span / 2, y * spacing - span / 2, z * spacing - span / 2));
            var map = FieldProbe.Evaluate(wire, solution, points);
            VectorFieldModel = SceneBuilder.BuildVectorFieldModel(
                map, ColormapKind.Viridis, arrowLength: 0.8 * spacing);

            // A finer slice heatmap through the structure's mid-plane.
            const int sliceN = 33;
            double sliceSpacing = span / (sliceN - 1);
            var slicePoints = new List<Vector3D>(sliceN * sliceN);
            for (int y = 0; y < sliceN; y++)
                for (int x = 0; x < sliceN; x++)
                    slicePoints.Add(center + new Vector3D(
                        x * sliceSpacing - span / 2, y * sliceSpacing - span / 2, 0));
            var slice = FieldProbe.Evaluate(wire, solution, slicePoints);
            FieldSliceModel = SceneBuilder.BuildFieldSliceModel(slice, sliceN, sliceN, ColormapKind.Viridis);

            WirePoints = BuildWireOverlay(wire);
            GroundPlaneModel = BuildGroundOverlay(wire);
            double peak = map.Magnitude.Concat(slice.Magnitude).Max();
            FieldResult = $"Near field at {FrequencyMHz:g4} MHz: peak |E| = {peak:g4} V/m " +
                          $"(1 V feed, {n}³ grid + mid-plane slice; arrows are the t = 0 snapshot, " +
                          "color is log₁₀|E| over 3 decades)";
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ShowFarField()
    {
        FieldResult = "";
        if (IsSurfaceMode)
        {
            await ShowSurfaceFarFieldAsync();
            return;
        }
        if (!TryDiscretize(out var wire, out var feedBasis, out _, out string? failure))
        {
            FieldResult = $"Not computable: {failure}";
            return;
        }

        try
        {
            var solution = new ThinWireMomSolver().Solve(wire, FrequencyMHz * 1e6, feedBasis);
            var pattern = FarFieldEvaluator.Compute(wire, solution);
            var (center, diagonal) = BoundingSphere(wire);
            FarFieldLobeModel = SceneBuilder.BuildFarFieldLobe(
                pattern, center, scale: 1.25 * diagonal, ColormapKind.Viridis);
            WirePoints = BuildWireOverlay(wire);
            GroundPlaneModel = BuildGroundOverlay(wire);

            double dbi = 10 * Math.Log10(pattern.MaxDirectivity);
            FieldResult = $"Far field at {FrequencyMHz:g4} MHz: P_rad = " +
                          $"{pattern.TotalRadiatedPowerWatts:g4} W (1 V feed), " +
                          $"D_max = {pattern.MaxDirectivity:g4} ({dbi:g3} dBi); " +
                          "lobe radius ∝ radiation intensity";
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    /// <summary>
    /// The board field overlay: samples |E| on a horizontal grid hovering
    /// <see cref="OverlayHeightMm"/> above the structure's top metal and renders it as
    /// a translucent heatmap plane over the copper preview — the SIwave-style radiated-
    /// field view. Uses the SAME field evaluators as the near-field commands, so the
    /// values carry the same gates and the same near-metal surface-scale caveat.
    /// </summary>
    [RelayCommand]
    private async Task ShowFieldOverlay()
    {
        FieldResult = "";
        int n = Math.Clamp(OverlayGridN, 9, 201);
        double opacity = Math.Clamp(OverlayOpacityPercent / 100.0, 0.05, 1.0);
        var mode = OverlayScaleMode == LinearScale ? FieldScaleMode.Linear : FieldScaleMode.Logarithmic;
        bool magnetic = OverlayFieldType == HFieldOverlay;

        if (OverlayOverBoard && _board is not null && SourceMode == NetMode)
        {
            await ShowBoardOverlayAsync(n, mode, opacity, magnetic);
            return;
        }
        if (IsSurfaceMode)
        {
            await ShowSurfaceFieldOverlayAsync(n, mode, opacity, magnetic);
            return;
        }
        if (!TryDiscretize(out var wire, out var feedBasis, out _, out string? failure))
        {
            FieldResult = $"Not computable: {failure}";
            return;
        }
        try
        {
            double frequency = FrequencyMHz * 1e6;
            var (center, diagonal) = BoundingSphere(wire);
            double z = wire.Nodes.Max(p => p.Z) + OverlayHeightMm * 1e-3;
            var map = await Task.Run(() =>
            {
                var solution = new ThinWireMomSolver().Solve(wire, frequency, feedBasis);
                return FieldProbe.Evaluate(wire, solution, OverlayGridPoints(center, diagonal, z, n));
            });
            ApplyFieldOverlay(map, magnetic, n, mode, opacity, z, kernelNote: "free-space kernels");
            WirePoints = BuildWireOverlay(wire);
            GroundPlaneModel = BuildGroundOverlay(wire);
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    private async Task ShowSurfaceFieldOverlayAsync(int n, FieldScaleMode mode, double opacity,
        bool magnetic)
    {
        if (!TryDiscretizeSurface(out var surface, out var port, out _, out string? failure,
                out var substrate, out var probe, out var layered))
        {
            FieldResult = $"Not computable: {failure}";
            return;
        }
        FieldResult = $"Computing ({surface.BasisCount} RWG unknowns"
                      + (layered is not null ? ", multi-layer field kernels"
                         : substrate is null ? "" : ", layered field kernels") + ")…";
        try
        {
            double frequency = FrequencyMHz * 1e6;
            var (center, diagonal) = SurfaceBounds(surface);
            double z = surface.Vertices.Max(v => v.Z) + OverlayHeightMm * 1e-3;
            var points = OverlayGridPoints(center, diagonal, z, n);
            var (map, solution) = await Task.Run(() =>
            {
                var solver = new OpenSim.Rf.Surface.SurfaceMomSolver();
                if (layered is { } spec)
                {
                    // Stage S9b — the multi-layer / covered-patch field kernels (TLGF per-z),
                    // sampled on the overlay plane above the (buried) metal.
                    var mlTable = BuildMultiLayerTable(surface, spec, frequency);
                    var mlSolved = solver.Solve(surface, mlTable, port);
                    return (OpenSim.Rf.Layered.LayeredFieldEvaluator.Evaluate(
                        surface, mlTable, mlSolved, points), mlSolved);
                }
                if (substrate is null)
                {
                    var solvedFree = solver.Solve(surface, frequency, port);
                    return (OpenSim.Rf.Surface.SurfaceFieldProbe.Evaluate(
                        surface, solvedFree, points), solvedFree);
                }
                // The Stage D layered field kernels (above-slab points); a probe-fed
                // patch maps its sheet currents, like the near-field command.
                var table = BuildKernelTable(surface, substrate, frequency);
                var solved = probe is { } p
                    ? solver.SolveProbeFed(surface, table, p).Surface
                    : solver.Solve(surface, table, port);
                return (OpenSim.Rf.Layered.LayeredFieldEvaluator.Evaluate(
                    surface, table, solved, points), solved);
            });
            ApplyFieldOverlay(map, magnetic, n, mode, opacity, z,
                kernelNote: layered is not null
                    ? $"εr = {SubstrateEpsR:g3} multi-layer / covered-patch kernels"
                    : substrate is null
                        ? "free-space kernels"
                        : $"εr = {substrate.RelativePermittivity:g3} layered kernels");
            SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(surface, solution, ColormapKind.Viridis);
            GroundPlaneModel = BuildSurfaceGroundOverlay(surface, substrate);
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    /// <summary>
    /// The SIwave board view (Stage S10): paints the radiated |E|/|H| over the board's
    /// actual OUTLINE at each copper-layer z, one masked heatmap per layer composed into a
    /// Model3DGroup. The net is solved ONCE; the field is sampled per layer plane (the
    /// layered evaluator builds one kernel table per distinct z — its cheap case). Colors
    /// share one FieldScale pooled over every layer's in-outline samples, so they compare
    /// layer-to-layer; grid cells outside the outline are not painted.
    /// </summary>
    private async Task ShowBoardOverlayAsync(int n, FieldScaleMode mode, double opacity, bool magnetic)
    {
        if (_board is null || _options is null) { FieldResult = "Not computable: no board loaded."; return; }
        var outlinePoints = _board.Outline.SelectMany(p => p.Outer).ToList();
        if (outlinePoints.Count == 0)
        {
            FieldResult = "Not computable: the board has no outline to paint over.";
            return;
        }
        double minX = outlinePoints.Min(p => p.X), maxX = outlinePoints.Max(p => p.X);
        double minY = outlinePoints.Min(p => p.Y), maxY = outlinePoints.Max(p => p.Y);
        var outlineIndex = new PolygonSetIndex(_board.Outline);

        // Copper-layer mid-heights the selected net spans — the planes to paint.
        var options = _options();
        var islandLayers = _board.Islands.Select(i => i.LayerOrder).ToList();
        if (islandLayers.Count == 0) { FieldResult = "Not computable: the board has no copper."; return; }
        var (layerZ, _) = NetMesher.BuildStackupZ(islandLayers.Min(), islandLayers.Max(), options);
        var netLayers = (AntennaNet?.Layers ?? layerZ.Keys.ToList())
            .Where(layerZ.ContainsKey).Distinct().OrderBy(l => l).ToList();
        var zPlanes = netLayers.Select(l => (layerZ[l].zLo + layerZ[l].zHi) / 2).ToList();
        if (zPlanes.Count == 0) { FieldResult = "Not computable: the net spans no known copper layer."; return; }

        // Solve once; capture an evaluator points → field map (evaluator picked by geometry).
        double frequency = FrequencyMHz * 1e6;
        string kernelNote;
        Func<IReadOnlyList<Vector3D>, FieldMap> evaluate;
        if (IsSurfaceMode)
        {
            if (!TryDiscretizeSurface(out var surface, out var port, out _, out string? failure,
                    out var substrate, out var probe, out var layered))
            {
                FieldResult = $"Not computable: {failure}";
                return;
            }
            if (layered is not null)
            {
                // Multi-layer / covered near-field MAPS ship (Stage S9b), but the per-copper-layer
                // BOARD overlay paints AT each copper plane (the source plane), which the field
                // maps do not resolve; board per-gap multi-layer overlays stay a named follow-up.
                FieldResult = "Not computable: the per-layer board overlay over a multi-layer / "
                    + "covered stackup is a named follow-up (it paints at the metal plane itself). "
                    + "The wizard covered-patch near-field map and free-space board overlays work.";
                return;
            }
            var solver = new OpenSim.Rf.Surface.SurfaceMomSolver();
            if (substrate is null)
            {
                var sol = solver.Solve(surface, frequency, port);
                evaluate = pts => OpenSim.Rf.Surface.SurfaceFieldProbe.Evaluate(surface, sol, pts);
                kernelNote = "free-space kernels";
            }
            else
            {
                var table = BuildKernelTable(surface, substrate, frequency);
                var sol = probe is { } p
                    ? solver.SolveProbeFed(surface, table, p).Surface
                    : solver.Solve(surface, table, port);
                evaluate = pts => OpenSim.Rf.Layered.LayeredFieldEvaluator.Evaluate(surface, table, sol, pts);
                kernelNote = $"εr = {substrate.RelativePermittivity:g3} layered kernels";
            }
        }
        else
        {
            if (!TryDiscretize(out var wire, out var feedBasis, out _, out string? failure))
            {
                FieldResult = $"Not computable: {failure}";
                return;
            }
            var sol = new ThinWireMomSolver().Solve(wire, frequency, feedBasis);
            evaluate = pts => FieldProbe.Evaluate(wire, sol, pts);
            kernelNote = "free-space kernels";
        }

        FieldResult = $"Computing board overlay ({zPlanes.Count} layer"
                      + (zPlanes.Count > 1 ? "s" : "") + $", {n}×{n})…";
        try
        {
            var (perLayer, pooled) = await Task.Run(() =>
            {
                var result = new List<(FieldMap Map, double[] Values, bool[] Inside)>();
                var pool = new List<double>();
                foreach (double z in zPlanes)
                {
                    var points = OverlayGrid.RectPoints(minX, minY, maxX, maxY, z, n, n);
                    var map = evaluate(points);
                    var vals = (magnetic ? map.HMagnitude ?? map.Magnitude : map.Magnitude).ToArray();
                    var inside = OverlayGrid.InteriorMask(points, outlineIndex);
                    for (int i = 0; i < vals.Length; i++) if (inside[i]) pool.Add(vals[i]);
                    result.Add((map, vals, inside));
                }
                return (result, pool);
            });
            if (pooled.Count == 0)
            {
                FieldResult = "Not computable: the sample grid fell entirely outside the board outline.";
                return;
            }

            string symbol = magnetic ? "|H|" : "|E|";
            string unit = magnetic ? "A/m" : "V/m";
            int decades = Math.Max(1, OverlayDecades);
            var scale = OverlayAutoRange
                ? FieldScale.Auto(mode, pooled, decades)
                : new FieldScale(mode, OverlayMinVPerM, OverlayMaxVPerM, decades);

            var group = new Model3DGroup();
            foreach (var (map, vals, inside) in perLayer)
                group.Children.Add(SceneBuilder.BuildMaskedFieldOverlayModel(
                    map, vals, n, n, inside, ColormapKind.Viridis, scale, opacity));
            group.Freeze();
            FieldOverlayModel = group;

            double peak = pooled.Max();
            OverlayLegendTitle = mode == FieldScaleMode.Logarithmic ? $"{symbol} (log)" : symbol;
            OverlayLegendBrush = Colormap.CreateBrush(ColormapKind.Viridis);
            OverlayLegendMin = $"{scale.EffectiveMin:g3} {unit}";
            OverlayLegendMax = $"{scale.Max:g3} {unit}" + (peak > scale.Max ? " ▲" : "");
            FieldResult = $"Board overlay at {FrequencyMHz:g4} MHz: peak {symbol} = {peak:g4} {unit} "
                + $"over {perLayer.Count} copper layer" + (perLayer.Count > 1 ? "s" : "")
                + $" (masked to the outline, {n}×{n} grid, {kernelNote}, "
                + $"{OverlayOpacityPercent:g0}% opacity)";
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    /// <summary>Builds the overlay model + its legend from a sampled map. The legend
    /// brush stays opaque (readability); only the viewport plane takes the opacity.</summary>
    private void ApplyFieldOverlay(FieldMap map, bool magnetic, int n, FieldScaleMode mode,
        double opacity, double zMeters, string kernelNote)
    {
        // Stage S7: the map carries |E| and |H|; the toggle picks which to color, with the
        // matching unit. H is present only on free-space/PEC maps (HMagnitude non-null).
        IReadOnlyList<double> values = magnetic ? map.HMagnitude ?? map.Magnitude : map.Magnitude;
        string symbol = magnetic ? "|H|" : "|E|";
        string unit = magnetic ? "A/m" : "V/m";

        int decades = Math.Max(1, OverlayDecades);
        var scale = OverlayAutoRange
            ? FieldScale.Auto(mode, values, decades)
            : new FieldScale(mode, OverlayMinVPerM, OverlayMaxVPerM, decades);
        FieldOverlayModel = SceneBuilder.BuildFieldOverlayModel(
            map, values, n, n, ColormapKind.Viridis, scale, opacity);

        double peak = values.Count > 0 ? values.Max() : 0;
        OverlayLegendTitle = mode == FieldScaleMode.Logarithmic ? $"{symbol} (log)" : symbol;
        OverlayLegendBrush = Colormap.CreateBrush(ColormapKind.Viridis);
        OverlayLegendMin = $"{scale.EffectiveMin:g3} {unit}";
        // ▲ = samples above the range top are saturated (the FE legend's convention).
        OverlayLegendMax = $"{scale.Max:g3} {unit}" + (peak > scale.Max ? " ▲" : "");

        string rangeNote = OverlayAutoRange
            ? mode == FieldScaleMode.Logarithmic
                ? $"auto range, top {decades} decades"
                : "auto range"
            : $"range {scale.EffectiveMin:g3}–{scale.Max:g3} {unit}";
        FieldResult = $"Field overlay at {FrequencyMHz:g4} MHz: peak {symbol} = {peak:g4} {unit} "
                      + $"on the z = {zMeters * 1e3:g4} mm plane (1 V feed, {n}×{n} grid, "
                      + $"{kernelNote}; {rangeNote}, {OverlayOpacityPercent:g0}% opacity)";
        _log.Append($"Antenna: {FieldResult}");
    }

    private static List<Vector3D> OverlayGridPoints(Vector3D center, double diagonal,
        double z, int n)
    {
        double span = 1.4 * diagonal;
        double spacing = span / (n - 1);
        var points = new List<Vector3D>(n * n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                points.Add(new Vector3D(
                    center.X + x * spacing - span / 2,
                    center.Y + y * spacing - span / 2, z));
        return points;
    }

    // ------------------------------------------------------------------
    // Geometry
    // ------------------------------------------------------------------

    /// <summary>Builds and discretizes the selected geometry: wizard shapes directly,
    /// or the selected net's pad-anchored trace chain mapped to equivalent-radius
    /// wires. Elements are capped at λ/10 of the HIGHEST frequency touched, so one
    /// grid serves the sweep and the field commands alike.</summary>
    private bool TryDiscretize(out WireStructure wire, out int feedBasis,
        out IReadOnlyList<string> warnings, out string? failure)
    {
        wire = null!;
        feedBasis = 0;
        warnings = Array.Empty<string>();
        failure = null;

        var ground = ActiveGround();
        double groundZ = ground?.SurfaceZ ?? 0;
        // Wizard shapes are generated around the origin; with a ground plane active they
        // are lifted so the LOWEST point sits HeightAboveGroundMm above it (a dipole
        // straddling the plane would rightly be a typed failure). Net geometry keeps its
        // board coordinates — the user places the plane relative to the board.
        double lift = groundZ + HeightAboveGroundMm * 1e-3;

        IReadOnlyList<WireSegment> wires;
        Vector3D feedHint;
        switch (SourceMode)
        {
            case DipoleMode:
                if (DipoleLengthMm <= 0 || WireRadiusMm <= 0)
                {
                    failure = "the dipole needs a positive length and wire radius";
                    return false;
                }
                wires = CanonicalAntennas.Dipole(DipoleLengthMm * 1e-3, WireRadiusMm * 1e-3);
                feedHint = Vector3D.Zero;
                if (ground is not null)
                {
                    var offset = new Vector3D(0, 0, lift + DipoleLengthMm * 1e-3 / 2);
                    wires = Translate(wires, offset);
                    feedHint += offset;
                }
                break;

            case LoopMode:
                if (LoopRadiusMm <= 0 || WireRadiusMm <= 0)
                {
                    failure = "the loop needs a positive loop radius and wire radius";
                    return false;
                }
                wires = CanonicalAntennas.Loop(LoopRadiusMm * 1e-3, WireRadiusMm * 1e-3);
                feedHint = new Vector3D(LoopRadiusMm * 1e-3, 0, 0);
                if (ground is not null)
                {
                    var offset = new Vector3D(0, 0, lift);
                    wires = Translate(wires, offset);
                    feedHint += offset;
                }
                break;

            case MonopoleMode:
                if (MonopoleHeightMm <= 0 || WireRadiusMm <= 0)
                {
                    failure = "the monopole needs a positive height and wire radius";
                    return false;
                }
                // The base sits ON the plane (that grounds it and puts the feed there).
                wires = Translate(
                    CanonicalAntennas.Monopole(MonopoleHeightMm * 1e-3, WireRadiusMm * 1e-3),
                    new Vector3D(0, 0, groundZ));
                feedHint = new Vector3D(0, 0, groundZ);
                break;

            case NetMode:
                if (!TryBuildNetWires(out wires, out feedHint, out failure)) return false;
                break;

            default:
                failure = "pick a geometry source";
                return false;
        }

        double maxFrequency = Math.Max(FrequencyMHz, Math.Max(SweepFMinMHz, SweepFMaxMHz)) * 1e6;
        double lambdaMin = 299_792_458.0 / maxFrequency;
        var grid = WireGridBuilder.Build(wires, maxElementLength: lambdaMin / 10, ground: ground);
        if (grid.Structure is null)
        {
            failure = grid.FailureReason;
            return false;
        }
        wire = grid.Structure;
        warnings = grid.Warnings;
        feedBasis = wire.NearestBasis(feedHint);
        return true;
    }

    /// <summary>The ground plane in effect: the monopole always images against one (it
    /// is meaningless without), other modes opt in via the checkbox.</summary>
    private GroundPlane? ActiveGround() =>
        SourceMode == MonopoleMode || UseGroundPlane ? new GroundPlane(GroundZMm * 1e-3) : null;

    private static IReadOnlyList<WireSegment> Translate(IReadOnlyList<WireSegment> segments,
        Vector3D offset) =>
        segments.Select(s => s with { A = s.A + offset, B = s.B + offset }).ToArray();

    private bool TryBuildNetWires(out IReadOnlyList<WireSegment> wires, out Vector3D feedHint,
        out string? failure)
    {
        wires = Array.Empty<WireSegment>();
        feedHint = Vector3D.Zero;
        failure = null;
        if (_board is null || _options is null || AntennaNet is not { } net)
        {
            failure = "import a board and pick a net first (or use a wizard shape)";
            return false;
        }
        if (_board.TraceCenterlines.Count == 0)
        {
            failure = "no trace centerlines were captured for this board";
            return false;
        }

        var traces = NetTraceExtractor.ForNet(_board, net);
        var options = _options();
        var terminals = NetTraceExtractor.FarthestPadTerminals(_board, net);
        var chain = TraceChainBuilder.Build(traces, net.StitchingVias, options, net.Islands,
            includeLayers: null, terminals);
        if (chain.Chain is null && terminals is not null)
            chain = TraceChainBuilder.Build(traces, net.StitchingVias, options, net.Islands);
        if (chain.Chain is null)
        {
            failure = chain.FailureReason;
            return false;
        }

        wires = TraceChainAntenna.FromChain(chain.Chain);
        // Feed at the selected source pad when one is picked; otherwise mid-chain (a
        // delta gap at an open end sees I ≈ 0 and is meaningless).
        if (_electrodes.SelectedSource is { } pad)
        {
            double z = 0.5 * (wires.Min(w => Math.Min(w.A.Z, w.B.Z)) + wires.Max(w => Math.Max(w.A.Z, w.B.Z)));
            feedHint = new Vector3D(pad.Center.X, pad.Center.Y, z);
        }
        else
        {
            feedHint = wires[wires.Count / 2].A;
        }
        return true;
    }

    /// <summary>The solver's assumption list, adjusted for an active ground plane: the
    /// free-space line is replaced by the image-theory statement (both at once would
    /// contradict each other).</summary>
    private IReadOnlyList<string> BuildAssumptions(WireStructure wire)
    {
        if (wire.Ground is not { } ground)
            return ThinWireMomSolver.Assumptions;
        var list = ThinWireMomSolver.Assumptions
            .Where(a => !a.StartsWith("Free space")).ToList();
        list.Insert(1,
            $"Infinite PEC ground plane at z = {ground.SurfaceZ * 1e3:g4} mm (image theory): " +
            "fields below the plane are zero; a real finite ground smaller than ~λ will differ. " +
            "Board dielectric is still not modeled.");
        return list;
    }

    private static (Vector3D Center, double Diagonal) BoundingSphere(WireStructure wire)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var node in wire.Nodes)
        {
            minX = Math.Min(minX, node.X); maxX = Math.Max(maxX, node.X);
            minY = Math.Min(minY, node.Y); maxY = Math.Max(maxY, node.Y);
            minZ = Math.Min(minZ, node.Z); maxZ = Math.Max(maxZ, node.Z);
        }
        var center = new Vector3D(0.5 * (minX + maxX), 0.5 * (minY + maxY), 0.5 * (minZ + maxZ));
        double diagonal = new Vector3D(maxX - minX, maxY - minY, maxZ - minZ).Length;
        return (center, diagonal > 0 ? diagonal : 1e-3);
    }

    /// <summary>A translucent disk at the ground plane's z so the modeling assumption is
    /// VISIBLE in the viewport (null when no ground is active).</summary>
    private static Model3D? BuildGroundOverlay(WireStructure wire)
    {
        if (wire.Ground is not { } ground) return null;
        var (center, diagonal) = BoundingSphere(wire);
        return SceneBuilder.BuildGroundPlaneModel(center.X, center.Y, ground.SurfaceZ,
            radius: 1.5 * diagonal);
    }

    private static Point3DCollection BuildWireOverlay(WireStructure wire)
    {
        var points = new Point3DCollection(wire.ElementCount * 2);
        for (int e = 0; e < wire.ElementCount; e++)
        {
            var a = wire.ElementStart(e);
            var b = wire.ElementEnd(e);
            points.Add(new System.Windows.Media.Media3D.Point3D(a.X, a.Y, a.Z));
            points.Add(new System.Windows.Media.Media3D.Point3D(b.X, b.Y, b.Z));
        }
        points.Freeze();
        return points;
    }

    // ------------------------------------------------------------------
    // Surface (RWG) pipeline — plates, air-spaced patches, copper islands
    // ------------------------------------------------------------------

    /// <summary>Whether the current surface configuration engages the layered-media
    /// (substrate) path: metal over the ground with a dielectric filling the gap.
    /// εr = 1 means air — the Stage B PEC-image path, byte-for-byte.</summary>
    private bool UseSubstrate => (SubstrateEpsR > 1.0
        && (SourceMode == PatchMode || UseGroundPlane))
        || IsProbeMode          // the coaxial probe lives inside the slab — always layered
        || IsCoveredPatchMode;  // the covered patch is buried in the slab — always layered

    /// <summary>A multi-layer (Stage F) solve spec: the stackup plus the metal source
    /// interface (null ⇒ coplanar at the slab top; m ⇒ buried at interface m, a covered
    /// patch). When present it supersedes <see cref="OpenSim.Rf.Layered.SubstrateStackup"/> —
    /// the solve, far field, and near field route through the multi-layer kernel table.</summary>
    private readonly record struct LayeredSpec(
        OpenSim.Rf.Layered.LayeredStackup Stackup, int? SourceInterface);

    private bool TryDiscretizeSurface(out OpenSim.Rf.Surface.SurfaceStructure surface,
        out OpenSim.Rf.Surface.SurfacePort port, out IReadOnlyList<string> warnings,
        out string? failure, out OpenSim.Rf.Layered.SubstrateStackup? substrate,
        out OpenSim.Rf.Surface.ProbeFeed? probe, out LayeredSpec? layered)
    {
        surface = null!;
        port = null!;
        warnings = Array.Empty<string>();
        failure = null;
        substrate = null;
        probe = null;
        layered = null;

        if (SubstrateEpsR < 1)
        {
            failure = "the substrate εr must be ≥ 1 (εr = 1 is an air gap)";
            return false;
        }
        double maxFrequency = Math.Max(FrequencyMHz, Math.Max(SweepFMinMHz, SweepFMaxMHz)) * 1e6;
        // The kernel varies on the DIELECTRIC wavelength — the λ/10 element ceiling
        // must resolve λ_d, not λ₀, when a substrate is engaged.
        double lambdaMin = 299_792_458.0 / maxFrequency
                           / (UseSubstrate ? Math.Sqrt(SubstrateEpsR) : 1.0);
        double groundZ = GroundZMm * 1e-3;

        OpenSim.Rf.Surface.SurfaceGridResult grid;
        switch (SourceMode)
        {
            case PlateMode:
            {
                if (PlateWidthMm <= 0 || PlateLengthMm <= 0)
                {
                    failure = "the plate needs a positive width and length";
                    return false;
                }
                if (UseSubstrate && HeightAboveGroundMm <= 0)
                {
                    failure = "the substrate thickness (height above ground) must be positive";
                    return false;
                }
                // Substrate: the ground lives inside the layered kernel, so the
                // structure is built bare at the slab's top surface.
                var ground = UseGroundPlane && !UseSubstrate ? new GroundPlane(groundZ) : null;
                double z = UseGroundPlane ? groundZ + HeightAboveGroundMm * 1e-3 : 0;
                grid = OpenSim.Rf.Surface.SurfaceMeshBuilder.BuildRectangularPlate(
                    PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, lambdaMin / 10, z, 0.5, ground);
                if (UseSubstrate)
                    substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                        SubstrateEpsR, Math.Max(SubstrateTanD, 0), HeightAboveGroundMm * 1e-3);
                break;
            }
            case PatchMode:
                if (PlateWidthMm <= 0 || PlateLengthMm <= 0 || PatchHeightMm <= 0)
                {
                    failure = "the patch needs positive width, length, and height above ground";
                    return false;
                }
                if (UseSubstrate)
                {
                    grid = OpenSim.Rf.Surface.SurfaceMeshBuilder.BuildRectangularPlate(
                        PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, lambdaMin / 10,
                        z: groundZ + PatchHeightMm * 1e-3, portFraction: 0);
                    substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                        SubstrateEpsR, Math.Max(SubstrateTanD, 0), PatchHeightMm * 1e-3);
                }
                else
                {
                    grid = OpenSim.Rf.Surface.SurfaceMeshBuilder.BuildPatchOverGround(
                        PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, PatchHeightMm * 1e-3,
                        groundZ, lambdaMin / 10);
                }
                break;

            case ProbeFedPatchMode:
            {
                if (PlateWidthMm <= 0 || PlateLengthMm <= 0 || PatchHeightMm <= 0)
                {
                    failure = "the probe-fed patch needs positive width, length, and slab thickness";
                    return false;
                }
                if (ProbeSegments < 2)
                {
                    failure = "the coaxial probe needs at least 2 tube segments";
                    return false;
                }
                if (ProbeRadiusMm <= 0)
                {
                    failure = "the probe bore radius must be positive";
                    return false;
                }
                double px = ProbeXMm * 1e-3, py = ProbeYMm * 1e-3;
                if (Math.Abs(px) >= PlateWidthMm * 1e-3 / 2 || Math.Abs(py) >= PlateLengthMm * 1e-3 / 2)
                {
                    failure = "the probe (x, y) must lie inside the patch footprint";
                    return false;
                }
                grid = OpenSim.Rf.Surface.SurfaceMeshBuilder.BuildRectangularPlate(
                    PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, lambdaMin / 10,
                    z: groundZ + PatchHeightMm * 1e-3, portFraction: 0, snapVertex: (px, py));
                substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                    Math.Max(SubstrateEpsR, 1.0), Math.Max(SubstrateTanD, 0), PatchHeightMm * 1e-3);
                probe = new OpenSim.Rf.Surface.ProbeFeed(px, py, ProbeRadiusMm * 1e-3, ProbeSegments);
                break;
            }

            case CoveredPatchMode:
            {
                if (PlateWidthMm <= 0 || PlateLengthMm <= 0 || PatchHeightMm <= 0)
                {
                    failure = "the covered patch needs positive width, length, and substrate thickness";
                    return false;
                }
                if (CoverThicknessMm <= 0)
                {
                    failure = "the dielectric cover thickness must be positive";
                    return false;
                }
                if (SubstrateEpsR < 1)
                {
                    failure = "the covered patch needs a substrate εr ≥ 1";
                    return false;
                }
                // Metal buried at the top of the substrate (interface 0), cover above. The plate
                // is built at the substrate-top z (cosmetic for overlays — the radial kernel
                // ignores it; the buried source height lives in the interior kernel table).
                double hSub = PatchHeightMm * 1e-3;
                grid = OpenSim.Rf.Surface.SurfaceMeshBuilder.BuildRectangularPlate(
                    PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, lambdaMin / 10,
                    z: groundZ + hSub, portFraction: 0);
                substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                    SubstrateEpsR, Math.Max(SubstrateTanD, 0), hSub);
                layered = new LayeredSpec(
                    OpenSim.Rf.Layered.LayeredStackup.CoveredPatch(
                        SubstrateEpsR, Math.Max(SubstrateTanD, 0), hSub, CoverThicknessMm * 1e-3),
                    OpenSim.Rf.Layered.LayeredStackup.CoveredPatchMetalInterface);
                break;
            }

            case IslandMode:
            {
                if (AntennaIsland is not { } choice)
                {
                    failure = "import a board and pick a net + island first";
                    return false;
                }
                var shape = choice.Island.Shape;
                double minX = shape.Outer.Min(p => p.X), maxX = shape.Outer.Max(p => p.X);
                double minY = shape.Outer.Min(p => p.Y), maxY = shape.Outer.Max(p => p.Y);
                double diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
                // Boards are usually far smaller than λ: the element size must resolve
                // the ISLAND, not just the wavelength.
                double element = Math.Min(lambdaMin / 10, diagonal / 8);
                var ground = UseGroundPlane && !UseSubstrate ? new GroundPlane(groundZ) : null;
                double z = UseGroundPlane ? groundZ + HeightAboveGroundMm * 1e-3 : 0;
                if (UseSubstrate && HeightAboveGroundMm <= 0)
                {
                    failure = "the substrate thickness (height above ground) must be positive";
                    return false;
                }
                OpenSim.Core.Geometry2D.Point2? hint = _electrodes.SelectedSource is { } pad
                    ? new OpenSim.Core.Geometry2D.Point2(pad.Center.X, pad.Center.Y)
                    : null;
                grid = OpenSim.Rf.Surface.SurfaceMeshBuilder.BuildFromPolygon(
                    shape, element, z, hint, ground);
                if (UseSubstrate)
                    substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                        SubstrateEpsR, Math.Max(SubstrateTanD, 0), HeightAboveGroundMm * 1e-3);
                break;
            }
            default:
                failure = "pick a surface geometry source";
                return false;
        }

        if (grid.Structure is null)
        {
            failure = grid.FailureReason;
            return false;
        }
        surface = grid.Structure;
        port = grid.Port!;
        warnings = grid.Warnings;
        return true;
    }

    private async Task SolveSurfaceAntennaAsync()
    {
        if (!TryDiscretizeSurface(out var surface, out var port, out var warnings,
                out string? failure, out var substrate, out var probe, out var layered))
        {
            AntennaResult = $"Not solvable: {failure}";
            return;
        }
        AntennaResult = $"Solving ({surface.BasisCount} RWG unknowns"
                        + (substrate is null ? "" : layered is null ? ", layered substrate" : ", multi-layer stackup") + ")…";
        try
        {
            double frequency = FrequencyMHz * 1e6;
            double fMin = SweepFMinMHz * 1e6, fMax = SweepFMaxMHz * 1e6;
            int points = SweepPoints;
            var timing = new List<string>();
            var (sweep, display) = await Task.Run(() =>
            {
                var solver = new OpenSim.Rf.Surface.SurfaceMomSolver();
                double FrequencyAt(int k) => points == 1
                    ? fMin
                    : fMin * Math.Pow(fMax / fMin, (double)k / (points - 1));

                // Sweep points are independent (each is its own table + fill + LU),
                // so they compute into pre-sized slots in parallel and assemble in
                // frequency order — every point is deterministic on its own, so the
                // sweep results and log lines match the serial loop exactly.
                var results = new OpenSim.Rf.Surface.SurfaceMomSolution[points];
                var pointTiming = new List<string>[points];
                try
                {
                    Parallel.For(0, points, k =>
                    {
                        pointTiming[k] = new List<string>();
                        results[k] = SolveSurfacePoint(solver, surface, port, FrequencyAt(k),
                            substrate, probe, layered, pointTiming[k]);
                    });
                }
                catch (AggregateException e) { throw e.InnerExceptions[0]; }

                var list = new List<AntennaZinPoint>(points);
                for (int k = 0; k < points; k++)
                {
                    list.Add(new AntennaZinPoint(FrequencyAt(k),
                        results[k].InputImpedance.Real, results[k].InputImpedance.Imaginary));
                    timing.AddRange(pointTiming[k]);
                }
                return (list, SolveSurfacePoint(solver, surface, port, frequency, substrate, probe, layered, timing));
            });

            foreach (var point in sweep) ZinSweep.Add(point);
            SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(surface, display, ColormapKind.Viridis);
            GroundPlaneModel = BuildSurfaceGroundOverlay(surface, substrate);
            AntennaResult = $"Zin = {display.InputImpedance.Real:g4} " +
                            $"{(display.InputImpedance.Imaginary >= 0 ? "+" : "−")} " +
                            $"j{Math.Abs(display.InputImpedance.Imaginary):g4} Ω at {FrequencyMHz:g4} MHz " +
                            $"({surface.BasisCount} RWG unknowns, {surface.Triangles.Count} triangles" +
                            (substrate is null ? ")"
                                : layered is { SourceInterface: not null }
                                    ? $", εr = {substrate.RelativePermittivity:g3} covered patch, Stage F multi-layer)"
                                    : layered is not null
                                        ? $", εr = {substrate.RelativePermittivity:g3} multi-layer stackup)"
                                        : $", εr = {substrate.RelativePermittivity:g3} substrate)");
            AntennaAssumptions = "Assumptions: "
                + string.Join(" ", probe is null
                    ? BuildSurfaceAssumptions(surface, substrate, layered)
                    : OpenSim.Rf.Surface.SurfaceMomSolver.ProbeFedAssumptions)
                + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "");
            // A slow sweep names its own bottleneck: the layered path rebuilds the
            // kernel table per frequency point (a table IS one (f, stackup) pair).
            foreach (string line in timing) _log.Append(line);
            _log.Append($"Antenna: {AntennaResult}");
        }
        catch (Exception ex) { AntennaResult = $"Not solvable: {ex.Message}"; }
    }

    /// <summary>One frequency point through the right kernel path. The layered table
    /// is built fresh per point (deterministic, ~0.2 s) and its cost logged.</summary>
    private OpenSim.Rf.Surface.SurfaceMomSolution SolveSurfacePoint(
        OpenSim.Rf.Surface.SurfaceMomSolver solver, OpenSim.Rf.Surface.SurfaceStructure surface,
        OpenSim.Rf.Surface.SurfacePort port, double frequencyHz,
        OpenSim.Rf.Layered.SubstrateStackup? substrate, OpenSim.Rf.Surface.ProbeFeed? probe,
        LayeredSpec? layered, List<string> timing)
    {
        if (layered is { } spec)
        {
            // The multi-layer (Stage F) path: a covered patch (buried source) or a genuine
            // multi-gap stackup, through the transmission-line Green's function kernel table.
            var mlTable = BuildMultiLayerTable(surface, spec, frequencyHz);
            var mlStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var mlSolution = solver.Solve(surface, mlTable, port);
            timing.Add($"Antenna: multi-layer point {frequencyHz / 1e6:g4} MHz — table "
                       + $"{mlTable.BuildMilliseconds:F0} ms ({mlTable.PoleCount} surface-wave pole(s)), "
                       + $"solve {mlStopwatch.Elapsed.TotalMilliseconds:F0} ms.");
            return mlSolution;
        }
        if (substrate is null) return solver.Solve(surface, frequencyHz, port);
        var table = BuildKernelTable(surface, substrate, frequencyHz);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // The probe-fed path returns the coax port impedance; its surface solution
        // carries the junction's transported current for the current/far-field consumers.
        var solution = probe is { } p
            ? solver.SolveProbeFed(surface, table, p).Surface
            : solver.Solve(surface, table, port);
        timing.Add($"Antenna: layered point {frequencyHz / 1e6:g4} MHz — table "
                   + $"{table.BuildMilliseconds:F0} ms ({table.PoleCount} surface-wave pole(s)), "
                   + (probe is null ? "" : "probe-fed ")
                   + $"solve {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
        return solution;
    }

    private static OpenSim.Rf.Layered.LayeredKernelTable BuildKernelTable(
        OpenSim.Rf.Surface.SurfaceStructure surface,
        OpenSim.Rf.Layered.SubstrateStackup substrate, double frequencyHz)
    {
        var (_, diagonal) = SurfaceBounds(surface);
        return new OpenSim.Rf.Layered.LayeredKernelTable(substrate, frequencyHz,
            rhoMax: 1.2 * diagonal);
    }

    private static OpenSim.Rf.Layered.MultiLayerKernelTable BuildMultiLayerTable(
        OpenSim.Rf.Surface.SurfaceStructure surface, LayeredSpec spec, double frequencyHz)
    {
        var (_, diagonal) = SurfaceBounds(surface);
        return new OpenSim.Rf.Layered.MultiLayerKernelTable(spec.Stackup, frequencyHz,
            rhoMax: 1.2 * diagonal, sourceInterface: spec.SourceInterface);
    }

    private async Task ComputeSurfaceNearFieldAsync()
    {
        if (!TryDiscretizeSurface(out var surface, out var port, out _, out string? failure,
                out var substrate, out var probe, out var layered))
        {
            FieldResult = $"Not computable: {failure}";
            return;
        }
        FieldResult = $"Computing ({surface.BasisCount} RWG unknowns"
                      + (layered is not null ? ", multi-layer field kernels"
                         : substrate is null ? "" : ", layered field kernels") + ")…";
        try
        {
            var (center, diagonal) = SurfaceBounds(surface);
            double span = 1.6 * diagonal;
            int n = Math.Clamp(GridResolution, 3, 17);
            double spacing = span / (n - 1);
            double frequency = FrequencyMHz * 1e6;

            var (map, solution) = await Task.Run(() =>
            {
                var solver = new OpenSim.Rf.Surface.SurfaceMomSolver();
                var points = new List<Vector3D>(n * n * n);
                for (int z = 0; z < n; z++)
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                            points.Add(center + new Vector3D(
                                x * spacing - span / 2, y * spacing - span / 2, z * spacing - span / 2));
                if (layered is { } spec)
                {
                    // Stage S9b — the multi-layer / covered-patch per-z field kernels (TLGF).
                    // Points above the (buried) metal are mapped; the below-source half stays
                    // zero (the below-metal image ladder is a named follow-up).
                    var mlTable = BuildMultiLayerTable(surface, spec, frequency);
                    var mlSolved = solver.Solve(surface, mlTable, port);
                    return (OpenSim.Rf.Layered.LayeredFieldEvaluator.Evaluate(
                        surface, mlTable, mlSolved, points), mlSolved);
                }
                if (substrate is null)
                {
                    var solvedFree = solver.Solve(surface, frequency, port);
                    return (OpenSim.Rf.Surface.SurfaceFieldProbe.Evaluate(surface, solvedFree, points),
                        solvedFree);
                }
                // The Stage D layered field kernels: per-z tables (in-slab AND
                // above-slab points; at/below the ground E = 0 exactly). The probe-fed
                // near field uses the patch sheet currents (the probe's own vertical
                // field is a named follow-up, like its far-field surface-wave leg).
                var table = BuildKernelTable(surface, substrate, frequency);
                var solved = probe is { } p
                    ? solver.SolveProbeFed(surface, table, p).Surface
                    : solver.Solve(surface, table, port);
                return (OpenSim.Rf.Layered.LayeredFieldEvaluator.Evaluate(
                    surface, table, solved, points), solved);
            });

            VectorFieldModel = SceneBuilder.BuildVectorFieldModel(
                map, ColormapKind.Viridis, arrowLength: 0.8 * spacing);
            SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(surface, solution, ColormapKind.Viridis);
            GroundPlaneModel = BuildSurfaceGroundOverlay(surface, substrate);

            double peak = map.Magnitude.Max();
            FieldResult = $"Near field at {FrequencyMHz:g4} MHz: peak |E| = {peak:g4} V/m " +
                          $"(1 V feed, {n}³ grid" +
                          (substrate is null ? "" : $", εr = {substrate.RelativePermittivity:g3} layered kernels") +
                          "; arrows are the t = 0 snapshot; the sheet is colored by log₁₀|J|)";
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    private async Task ShowSurfaceFarFieldAsync()
    {
        if (!TryDiscretizeSurface(out var surface, out var port, out _, out string? failure,
                out var substrate, out var probe, out var layered))
        {
            FieldResult = $"Not computable: {failure}";
            return;
        }
        FieldResult = $"Computing ({surface.BasisCount} RWG unknowns"
                      + (substrate is null ? "" : layered is null ? ", layered substrate" : ", multi-layer stackup") + ")…";
        try
        {
            double frequency = FrequencyMHz * 1e6;
            var (pattern, solution, surfaceWavePower, inputPower) = await Task.Run(() =>
            {
                var solver = new OpenSim.Rf.Surface.SurfaceMomSolver();
                if (layered is { } spec)
                {
                    // The multi-layer (Stage F) far field: horizontal RWG currents radiating
                    // through the stack, region-0 amplitude from the TLGF; P_sw from the
                    // multi-layer poles. Covered-patch ledger P_rad + P_sw ≡ ½Re(V·I*).
                    var mlTable = BuildMultiLayerTable(surface, spec, frequency);
                    var mlSolved = solver.Solve(surface, mlTable, port);
                    double mlPin = 0.5 * (System.Numerics.Complex.One / mlSolved.InputImpedance).Real;
                    return (OpenSim.Rf.Layered.LayeredFarField.Compute(surface, mlTable, mlSolved),
                        mlSolved,
                        OpenSim.Rf.Layered.LayeredFarField.SurfaceWavePowerWatts(surface, mlTable, mlSolved),
                        mlPin);
                }
                if (substrate is null)
                {
                    var solvedFree = solver.Solve(surface, frequency, port);
                    return (OpenSim.Rf.Surface.SurfaceFarFieldEvaluator.Compute(surface, solvedFree),
                        solvedFree, 0.0, 0.0);
                }
                var table = BuildKernelTable(surface, substrate, frequency);
                if (probe is { } p)
                {
                    // The probe-fed far field carries the vertical E_θ leg + the exact
                    // junction current; the ledger's remaining ~5% is the vertical
                    // surface-wave leg (a named follow-up, so P_rad+P_sw can read a few
                    // % over near resonance).
                    var pf = solver.SolveProbeFed(surface, table, p);
                    double pinP = 0.5 * (System.Numerics.Complex.One / pf.Surface.InputImpedance).Real;
                    return (OpenSim.Rf.Layered.LayeredFarField.Compute(surface, table, pf, p),
                        pf.Surface,
                        OpenSim.Rf.Layered.LayeredFarField.SurfaceWavePowerWatts(surface, table, pf, p),
                        pinP);
                }
                var solved = solver.Solve(surface, table, port);
                double pin = 0.5 * (System.Numerics.Complex.One / solved.InputImpedance).Real;
                return (OpenSim.Rf.Layered.LayeredFarField.Compute(surface, table, solved),
                    solved,
                    OpenSim.Rf.Layered.LayeredFarField.SurfaceWavePowerWatts(surface, table, solved),
                    pin);
            });

            var (center, diagonal) = SurfaceBounds(surface);
            FarFieldLobeModel = SceneBuilder.BuildFarFieldLobe(
                pattern, center, scale: 1.25 * diagonal, ColormapKind.Viridis);
            SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(surface, solution, ColormapKind.Viridis);
            GroundPlaneModel = BuildSurfaceGroundOverlay(surface, substrate);

            double dbi = 10 * Math.Log10(pattern.MaxDirectivity);
            FieldResult = $"Far field at {FrequencyMHz:g4} MHz: P_rad = " +
                          $"{pattern.TotalRadiatedPowerWatts:g4} W (1 V feed), " +
                          $"D_max = {pattern.MaxDirectivity:g4} ({dbi:g3} dBi); " +
                          "lobe radius ∝ radiation intensity";
            if (substrate is not null && inputPower > 0)
            {
                // The Stage C power ledger, shown to the user: what the surface wave
                // takes is real power the pattern never sees.
                FieldResult += $"; surface wave P_sw = {surfaceWavePower:g3} W " +
                               $"({surfaceWavePower / inputPower:P0} of input; " +
                               $"P_rad + P_sw = {(pattern.TotalRadiatedPowerWatts + surfaceWavePower) / inputPower:P1} of input)";
            }
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    private IReadOnlyList<string> BuildSurfaceAssumptions(
        OpenSim.Rf.Surface.SurfaceStructure surface,
        OpenSim.Rf.Layered.SubstrateStackup? substrate, LayeredSpec? layered = null)
    {
        if (substrate is not null)
        {
            var lines = OpenSim.Rf.Surface.SurfaceMomSolver.LayeredAssumptions.ToList();
            lines.Insert(1,
                $"Substrate: εr = {substrate.RelativePermittivity:g3}, tanδ = {substrate.LossTangent:g3}, " +
                $"thickness {substrate.ThicknessMeters * 1e3:g4} mm; the reported power ledger counts " +
                "only the extracted surface-wave modes (TM0 always; higher modes above cutoff).");
            // Covered patch: name the cover so the downward resonance shift is not a surprise.
            if (layered is { SourceInterface: not null } spec && spec.Stackup.Layers.Count > 1)
            {
                var cover = spec.Stackup.Layers[^1];
                lines.Insert(2,
                    $"Dielectric cover: εr = {cover.RelativePermittivity:g3}, thickness "
                    + $"{cover.ThicknessMeters * 1e3:g4} mm above the buried metal (a covered patch, "
                    + "Stage F multi-layer TLGF) — the cover loads the patch, so its resonance sits "
                    + "below the bare patch's.");
            }
            return lines;
        }
        if (surface.Ground is not { } ground)
            return OpenSim.Rf.Surface.SurfaceMomSolver.Assumptions;
        var list = OpenSim.Rf.Surface.SurfaceMomSolver.Assumptions
            .Where(a => !a.StartsWith("Free space")).ToList();
        list.Insert(1,
            $"Infinite PEC ground plane at z = {ground.SurfaceZ * 1e3:g4} mm (image theory): " +
            "fields below the plane are zero; an air gap only — set a substrate εr for a dielectric.");
        return list;
    }

    private Model3D? BuildSurfaceGroundOverlay(OpenSim.Rf.Surface.SurfaceStructure surface,
        OpenSim.Rf.Layered.SubstrateStackup? substrate)
    {
        // A layered structure is built bare — its ground lives inside the kernel at
        // (metal z − thickness); the overlay must still show it.
        double? groundZ = surface.Ground?.SurfaceZ;
        if (groundZ is null && substrate is not null)
            groundZ = surface.Vertices[0].Z - substrate.ThicknessMeters;
        if (groundZ is null) return null;
        var (center, diagonal) = SurfaceBounds(surface);
        return SceneBuilder.BuildGroundPlaneModel(center.X, center.Y, groundZ.Value,
            radius: 1.5 * diagonal);
    }

    private static (Vector3D Center, double Diagonal) SurfaceBounds(
        OpenSim.Rf.Surface.SurfaceStructure surface)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var v in surface.Vertices)
        {
            minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
            minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
        }
        var center = new Vector3D(0.5 * (minX + maxX), 0.5 * (minY + maxY), 0.5 * (minZ + maxZ));
        double diagonal = new Vector3D(maxX - minX, maxY - minY, maxZ - minZ).Length;
        return (center, diagonal > 0 ? diagonal : 1e-3);
    }
}

/// <summary>A copper island offered as RWG patch metal, labeled for the picker.</summary>
public sealed record IslandChoice(OpenSim.Pcb.Import.CopperIsland Island)
{
    public string Label
    {
        get
        {
            double area = Math.Abs(OpenSim.Core.Geometry2D.Polygon2.RingArea(Island.Shape.Outer));
            return $"L{Island.LayerOrder} · {area * 1e6:g3} mm²";
        }
    }
}
