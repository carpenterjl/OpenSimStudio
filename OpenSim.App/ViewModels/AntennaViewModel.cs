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
    public const string ProbeFedCoveredPatchMode = "Probe-fed covered patch (wizard, RWG + coax + cover)";
    public const string IslandMode = "Copper island (PCB, RWG)";
    public const string WireFedPlateMode = "Wire-fed plate (wizard, RWG + attached wire)";

    private readonly ILogService _log;
    private readonly ElectrodesViewModel _electrodes;
    private PcbBoard? _board;

    /// <summary>The resolved board stackup's stated assumptions (which layer is the ground,
    /// each gap's material, whether the net is buried) — set when a board net resolves through
    /// <see cref="OpenSim.Rf.Layered.BoardAntennaStackup"/>, null otherwise. Surfaced with the
    /// solver's own assumptions so a buried net never reports a bare-substrate model.</summary>
    private IReadOnlyList<string>? _boardStackupNotes;
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
            ProbeFedPatchMode, CoveredPatchMode, ProbeFedCoveredPatchMode, IslandMode,
            WireFedPlateMode };

    /// <summary>Surface (RWG) modes solve sheets; the others solve thin wires. The two were once
    /// disjoint by construction, so no combined request could even be expressed; <see
    /// cref="WireFedPlateMode"/> is the exception (Stage D1) and routes to its own solver rather
    /// than the port-fed surface path, which is why every dispatch below tests it FIRST.</summary>
    private bool IsSurfaceMode =>
        SourceMode is PlateMode or PatchMode or ProbeFedPatchMode or CoveredPatchMode
            or ProbeFedCoveredPatchMode or IslandMode or WireFedPlateMode;

    /// <summary>The wire-fed plate (Stage D1) is the one mode where a wire and a sheet appear in
    /// ONE structure. It is a surface mode for plumbing purposes but never reaches the port-fed
    /// surface path: its feed is a delta gap on the wire, its unknowns are RWG + wire + one
    /// junction, and its far field sums three currents the solve deliberately keeps apart.
    /// Everything that has no hybrid implementation yet says so by name rather than quietly
    /// solving the sheet alone.</summary>
    private bool IsWireFedPlateMode => SourceMode == WireFedPlateMode;

    /// <summary>The covered patch (Stage F): a patch buried under a dielectric cover, solved
    /// through the multi-layer transmission-line Green's function with the source at the buried
    /// interface. The cover may be any material (its own εr/tanδ, defaulting to the
    /// substrate's); it loads the patch, so the resonance drops below the bare patch's — and
    /// drops further with a denser or thicker cover.</summary>
    private bool IsCoveredPatchMode =>
        SourceMode is CoveredPatchMode or ProbeFedCoveredPatchMode;

    /// <summary>The probe-fed patch drives the substrate patch with a real coaxial
    /// port through the slab (Stage E); the other surface modes use an edge/gap port. Stage C2
    /// adds the covered variant — the same coax, but the patch is buried and the tube ends on
    /// an interior interface, so the solve routes through the multi-layer kernels.</summary>
    private bool IsProbeMode =>
        SourceMode is ProbeFedPatchMode or ProbeFedCoveredPatchMode;

    // Coaxial probe feed [mm]: lateral position from the patch centre, bore radius,
    // and the number of tube segments across the slab (≥ 2; the slab must be thick
    // enough that each segment ≥ 2·radius — a typed failure otherwise).
    [ObservableProperty] private double _probeXMm;
    [ObservableProperty] private double _probeYMm = -20;
    [ObservableProperty] private double _probeRadiusMm = 0.2;
    [ObservableProperty] private int _probeSegments = 3;

    // The attached wire (Stage D1, wire-fed plate): its length, the angle it meets the sheet at
    // (90° = normal, the classical finite-ground monopole), and where it lands, measured from
    // the plate centre. The landing point is forced in as a MESH VERTEX when the plate is built,
    // because the attachment fan needs an anchor and a guessed contact is a wrong placement.
    [ObservableProperty] private double _attachedWireLengthMm = 60;
    [ObservableProperty] private double _attachedWireDegrees = 90;
    [ObservableProperty] private double _attachXMm;
    [ObservableProperty] private double _attachYMm;

    // Dielectric cover (superstrate) over a covered patch [mm]: thickness, and its OWN
    // material. A cover εr differing from the substrate's is supported — the interior-source
    // read-out is single-valued across the ε jump (the TM contrast source at the sheet cancels
    // the 1/ε difference identically; see TransmissionLineGreens.EvaluateInterior). Zero or
    // negative εr means "track the substrate", which is what every project written before the
    // superstrate shipped carries — so those load and solve byte-identically.
    [ObservableProperty] private double _coverThicknessMm = 0.8;
    [ObservableProperty] private double _coverEpsR;
    [ObservableProperty] private double _coverTanD = -1;

    /// <summary>The superstrate permittivity actually used: the explicit cover value when set,
    /// else the substrate's (the matched-cover default).</summary>
    private double EffectiveCoverEpsR => CoverEpsR >= 1.0 ? CoverEpsR : SubstrateEpsR;

    /// <summary>The superstrate loss tangent actually used; negative means track the substrate.</summary>
    private double EffectiveCoverTanD => CoverTanD >= 0 ? CoverTanD : Math.Max(SubstrateTanD, 0);

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

    /// <summary>Surface mesh density: elements per (dielectric) wavelength at the highest
    /// frequency in play. 10 is the long-standing default; a resonant dimension also gets
    /// at least 10 elements whatever the wavelength.</summary>
    [ObservableProperty] private double _meshCellsPerWavelength = 10;

    /// <summary>After a surface solve, solve once more at the display frequency on a mesh
    /// 1.5× finer and report how far Zin moved.</summary>
    [ObservableProperty] private bool _checkMeshConvergence = true;

    /// <summary>Edge-fed patch: distance of the series gap from the patch edge [mm]. 0 ⇒
    /// one eighth of the patch length. A physical position, so it does not move with the mesh.</summary>
    [ObservableProperty] private double _patchGapOffsetMm;

    /// <summary>Edge-fed patch: physical width of the series gap [mm], centred on the offset. 0 ⇒
    /// one eighteenth of the patch length. A finite gap's impedance converges under refinement; a
    /// delta gap's does not (its capacitance grows as ln 1/h).</summary>
    [ObservableProperty] private double _patchGapWidthMm;

    /// <summary>Near-field sample grid resolution per axis (n³ arrows).</summary>
    [ObservableProperty] private int _gridResolution = 9;

    [ObservableProperty] private string _antennaResult = "";
    [ObservableProperty] private string _fieldResult = "";
    [ObservableProperty] private string _antennaAssumptions = "";
    public ObservableCollection<AntennaZinPoint> ZinSweep { get; } = new();

    // ------------------------------------------------------------------
    // Board field overlay (SIwave-style): a translucent |field| heatmap plane over
    // the PCB/structure. E and H = ∇×A/µ₀ alike, through every evaluator: free-space/PEC
    // (Stage S7), a single-slab substrate (S9a), and multi-layer / covered stackups (S9b,
    // the TLGF per-z kernels) — including the per-copper-layer board overlay over a
    // multi-layer stack. An evaluator that supplies no H refuses by name; it never paints
    // |E| under an |H| legend.
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
    public bool HasBoardOverlay =>
        _board is not null && SourceMode is NetMode or IslandMode;

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
                + $"thickness {HeightAboveGroundMm:g3} mm) — edit in the panel; εr = 1 restores the air/PEC path. "
                + "A board island is solved over the PCB stackup panel's gaps down to the nearest plane under it; "
                + "these fields then only switch the substrate on and set the mesh density.");
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
        ClearNetworkResults();
        AntennaResult = "";
        AntennaAssumptions = "";
        if (SweepFMinMHz <= 0 || SweepFMaxMHz < SweepFMinMHz || SweepPoints < 1 || FrequencyMHz <= 0)
        {
            AntennaResult = "Not solvable: the frequency range is invalid.";
            return;
        }

        if (IsWireFedPlateMode)
        {
            await SolveWireFedPlateAsync();
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
            if (AdaptiveSweep && SweepPoints > 2 && SweepFMaxMHz > SweepFMinMHz)
            {
                var rational = OpenSim.Rf.Network.RationalSweep.Run(
                    f => new[] { solver.Solve(wire, f, feedBasis).InputImpedance },
                    SweepFMinMHz * 1e6, SweepFMaxMHz * 1e6, maxSamples: Math.Max(SweepPoints, 30));
                for (int k = 0; k < rational.FrequenciesHz.Count; k++)
                    ZinSweep.Add(new AntennaZinPoint(rational.FrequenciesHz[k],
                        rational.Values[k][0].Real, rational.Values[k][0].Imaginary));
                ShowNetworkResults(DenseCurve(rational), AdaptiveNote(rational));
            }
            else
            {
                for (int k = 0; k < SweepPoints; k++)
                {
                    double f = SweepPoints == 1
                        ? SweepFMinMHz * 1e6
                        : SweepFMinMHz * 1e6 * Math.Pow(SweepFMaxMHz / SweepFMinMHz, (double)k / (SweepPoints - 1));
                    var point = solver.Solve(wire, f, feedBasis);
                    ZinSweep.Add(new AntennaZinPoint(f,
                        point.InputImpedance.Real, point.InputImpedance.Imaginary));
                }
                ShowNetworkResultsFromSweep();
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
        if (IsWireFedPlateMode)
        {
            FieldResult = "Not computable: the wire↔sheet hybrid near field is a named "
                + "follow-up — the junction's disc and half-RWG continuations need their own "
                + "near-field transforms, and summing only the sheet's would silently drop the "
                + "current that crosses the contact. The far field IS available, and its power "
                + "ledger is the gate that would catch exactly that omission.";
            return;
        }
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
            // The reported peak leaves out samples within three radii of a wire's axis: on or
            // inside the conductor the reduced kernel returns a model value, not a field.
            const double nearRadii = 3.0;
            var allPoints = points.Concat(slicePoints).ToList();
            var allMagnitudes = map.Magnitude.Concat(slice.Magnitude).ToList();
            var onWire = FieldProbe.NearWire(wire, allPoints, nearRadii);
            int excluded = onWire.Count(v => v);
            double peak = excluded == allPoints.Count
                ? double.NaN
                : allMagnitudes.Where((_, i) => !onWire[i]).Max();
            FieldResult = $"Near field at {FrequencyMHz:g4} MHz: peak |E| = {peak:g4} V/m " +
                          $"(1 V feed, {n}³ grid + mid-plane slice; arrows are the t = 0 snapshot, " +
                          "color is log₁₀|E| over 3 decades" +
                          (excluded > 0
                              ? $"; {excluded} sample(s) within {nearRadii:g2} radii of a wire are " +
                                "drawn but left out of the peak)"
                              : ")");
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ShowFarField()
    {
        FieldResult = "";
        if (IsWireFedPlateMode)
        {
            await ShowWireFedPlateFarFieldAsync();
            return;
        }
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
            if (FarFieldEvaluator.GridWarning(FarFieldEvaluator.Extent(wire), FrequencyMHz * 1e6,
                    pattern.ThetaRadians.Count, pattern.PhiRadians.Count,
                    hemisphere: wire.Ground is not null) is { } gridWarning)
                FieldResult += $". Warning: {gridWarning}";
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

        if (OverlayOverBoard && _board is not null && SourceMode is NetMode or IslandMode)
        {
            await ShowBoardOverlayAsync(n, mode, opacity, magnetic);
            return;
        }
        if (IsWireFedPlateMode)
        {
            FieldResult = "Not computable: the wire↔sheet hybrid field overlay is a named "
                + "follow-up, for the same reason as the near field — the junction current has no "
                + "near-field transform yet, and painting the sheet's alone would look right and "
                + "be wrong.";
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
                    var mlSolved = SolveMultiLayer(solver, surface, mlTable, port, probe);
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
            var solver = new OpenSim.Rf.Surface.SurfaceMomSolver();
            if (layered is { } spec)
            {
                // Multi-layer / covered stackups paint through the S9b per-z field kernels. The
                // overlay samples each copper plane's MID-height, so a buried source plane is
                // approached from one side rather than sat on exactly; either side is now
                // well-defined (observation below a buried source was the A2 item).
                var mlTable = BuildMultiLayerTable(surface, spec, frequency);
                var mlSol = SolveMultiLayer(solver, surface, mlTable, port, probe);
                evaluate = pts => OpenSim.Rf.Layered.LayeredFieldEvaluator.Evaluate(
                    surface, mlTable, mlSol, pts);
                kernelNote = $"{spec.Stackup.Layers.Count}-layer TLGF kernels"
                    + (spec.SourceInterface is { } si
                        && si < spec.Stackup.Layers.Count - 1 ? " (buried metal)" : "");
            }
            else if (substrate is null)
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
                    if (magnetic && map.HMagnitude is null)
                        throw new InvalidOperationException(
                            "This evaluator does not supply H (the magnetic near field is not "
                            + "available for this structure); switch the overlay back to E.");
                    var vals = (magnetic ? map.HMagnitude! : map.Magnitude).ToArray();
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
        // matching unit. Where an evaluator supplies no H the request is REFUSED by name — the
        // earlier silent fall-back painted |E| values under an |H| legend in A/m, which reads as
        // a field three orders of magnitude wrong rather than as a missing feature.
        if (magnetic && map.HMagnitude is null)
        {
            FieldResult = "Not computable: this evaluator does not supply H "
                + "(the magnetic near field is not available for this structure); "
                + "switch the overlay back to E.";
            return;
        }
        IReadOnlyList<double> values = magnetic ? map.HMagnitude! : map.Magnitude;
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
        _netAntennaNotes.Clear();

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
        feedBasis = wire.NearestBasis(feedHint);
        var all = new List<string>(_netAntennaNotes);
        all.AddRange(grid.Warnings);
        all.AddRange(WireModelChecks.ThinWire(wire, maxFrequency));
        if (WireModelChecks.FeedAtJunction(wire, feedBasis) is { } atJunction) all.Add(atJunction);
        warnings = all;
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
        // The WHOLE net, branches included. The path between the two farthest pads with its
        // side branches pruned is right for a DC current and wrong for an antenna: an open
        // stub carries a standing wave, and an inverted-F loses its radiating arm.
        var graph = TraceChainBuilder.BuildGraph(traces, net.StitchingVias, options, net.Islands);
        if (graph.Segments is null)
        {
            failure = graph.FailureReason;
            return false;
        }

        Vector3D? padPoint = null;
        if (_electrodes.SelectedSource is { } pad && graph.Junctions is { Count: > 0 } junctions)
        {
            double z = 0.5 * (junctions.Min(j => j.Position.Z) + junctions.Max(j => j.Position.Z));
            padPoint = new Vector3D(pad.Center.X, pad.Center.Y, z);
        }
        TraceGraphAntenna antenna;
        try { antenna = TraceChainAntenna.FromGraph(graph, padPoint); }
        catch (ArgumentException ex)
        {
            failure = ex.Message;
            return false;
        }
        wires = antenna.Wires;
        _netAntennaNotes.Add($"Net modelled whole: {wires.Count} wire segment(s), "
            + $"{antenna.BranchNodes} branch node(s), {antenna.OpenEnds} open end(s).");
        if (antenna.DroppedPieces > 0)
            _netAntennaNotes.Add($"Warning: {antenna.DroppedPieces} disconnected piece(s) of the net "
                + $"({antenna.DroppedLengthMeters * 1e3:g3} mm of trace) are NOT in the model — only "
                + "one connected piece can be solved"
                + (padPoint is null ? " (the longest was kept)." : " (the one at the source pad was kept)."));

        // Feed at the selected source pad when one is picked; otherwise the middle of the
        // longest wire (a delta gap at an open end sees I ≈ 0 and is meaningless).
        if (padPoint is { } point)
        {
            feedHint = point;
        }
        else
        {
            var longest = wires.OrderByDescending(w => w.Length).First();
            feedHint = (longest.A + longest.B) / 2;
        }
        return true;
    }

    /// <summary>What the net-mode build has to say about the model it made; cleared at the start
    /// of every discretization and appended to its warnings.</summary>
    private readonly List<string> _netAntennaNotes = new();

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
        out OpenSim.Rf.Surface.ProbeFeed? probe, out LayeredSpec? layered, double refine = 1)
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

        // Element size: the user's elements per wavelength, and never fewer than 10 along
        // the plate's longer (resonant) dimension. `refine` scales both for the
        // convergence pass.
        double cells = Math.Max(MeshCellsPerWavelength, 4) * refine;
        double wavelengthElement = lambdaMin / cells;
        double plateElement = PlateWidthMm > 0 && PlateLengthMm > 0
            ? Math.Min(wavelengthElement, Math.Max(PlateWidthMm, PlateLengthMm) * 1e-3 / (10 * refine))
            : wavelengthElement;
        // The edge-fed patch's series gap, at a physical distance from the edge.
        double gapOffset = PatchGapOffsetMm > 0 ? PatchGapOffsetMm * 1e-3 : PlateLengthMm * 1e-3 / 8;
        double gapWidth = PatchGapWidthMm > 0 ? PatchGapWidthMm * 1e-3 : PlateLengthMm * 1e-3 / 18;

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
                    PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, plateElement, z, 0.5, ground);
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
                        PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, plateElement,
                        z: groundZ + PatchHeightMm * 1e-3, portOffset: gapOffset, portGapWidth: gapWidth);
                    substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                        SubstrateEpsR, Math.Max(SubstrateTanD, 0), PatchHeightMm * 1e-3);
                }
                else
                {
                    grid = OpenSim.Rf.Surface.SurfaceMeshBuilder.BuildRectangularPlate(
                        PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, plateElement,
                        z: groundZ + PatchHeightMm * 1e-3,
                        ground: new GroundPlane(groundZ), portOffset: gapOffset, portGapWidth: gapWidth);
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
                    PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, plateElement,
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
                    PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, plateElement,
                    z: groundZ + hSub, portOffset: gapOffset, portGapWidth: gapWidth);
                substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                    SubstrateEpsR, Math.Max(SubstrateTanD, 0), hSub);
                layered = new LayeredSpec(
                    OpenSim.Rf.Layered.LayeredStackup.CoveredPatch(
                        SubstrateEpsR, Math.Max(SubstrateTanD, 0), hSub,
                        EffectiveCoverEpsR, EffectiveCoverTanD, CoverThicknessMm * 1e-3),
                    OpenSim.Rf.Layered.LayeredStackup.CoveredPatchMetalInterface);
                break;
            }

            case ProbeFedCoveredPatchMode:
            {
                // Stage C2: the coax of the probe-fed patch, into the BURIED metal of the
                // covered patch. Both sets of validations apply verbatim — there is no third
                // model here, only the two feeds and media meeting for the first time.
                if (PlateWidthMm <= 0 || PlateLengthMm <= 0 || PatchHeightMm <= 0)
                {
                    failure = "the probe-fed covered patch needs positive width, length, and "
                        + "substrate thickness";
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
                double pcx = ProbeXMm * 1e-3, pcy = ProbeYMm * 1e-3;
                if (Math.Abs(pcx) >= PlateWidthMm * 1e-3 / 2 || Math.Abs(pcy) >= PlateLengthMm * 1e-3 / 2)
                {
                    failure = "the probe (x, y) must lie inside the patch footprint";
                    return false;
                }
                double hSubProbe = PatchHeightMm * 1e-3;
                grid = OpenSim.Rf.Surface.SurfaceMeshBuilder.BuildRectangularPlate(
                    PlateWidthMm * 1e-3, PlateLengthMm * 1e-3, plateElement,
                    z: groundZ + hSubProbe, portFraction: 0, snapVertex: (pcx, pcy));
                substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                    SubstrateEpsR, Math.Max(SubstrateTanD, 0), hSubProbe);
                probe = new OpenSim.Rf.Surface.ProbeFeed(
                    pcx, pcy, ProbeRadiusMm * 1e-3, ProbeSegments);
                layered = new LayeredSpec(
                    OpenSim.Rf.Layered.LayeredStackup.CoveredPatch(
                        SubstrateEpsR, Math.Max(SubstrateTanD, 0), hSubProbe,
                        EffectiveCoverEpsR, EffectiveCoverTanD, CoverThicknessMm * 1e-3),
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
                double element = Math.Min(wavelengthElement, diagonal / (8 * refine));
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
                {
                    substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                        SubstrateEpsR, Math.Max(SubstrateTanD, 0), HeightAboveGroundMm * 1e-3);
                    // A net on an INNER copper layer has dielectric above it as well as below —
                    // physically a covered patch, which the multi-layer TLGF has solved since
                    // Stage F2b. The board's own per-gap εr/tanδ/h already rides on
                    // PcbBoard.Stackup; before this it was simply never carried across, and every
                    // net was solved over one homogeneous slab from the two panel scalars.
                    // A top-layer net still resolves to a single slab and keeps the pre-existing
                    // SubstrateStackup path, so those solves are unchanged.
                    _boardStackupNotes = null;
                    if (_board is { } boardForStackup)
                    {
                        // The stackup panel's stackup (the object the mesher, PEEC chain and
                        // SI extractors read), and the net's own footprint so the ground is
                        // the nearest plane under it rather than always the bottom layer.
                        var boardStackup = _options?.Invoke().Stackup
                            ?? OpenSim.Pcb.Import.BoardStackup.FromBoard(boardForStackup,
                                HeightAboveGroundMm * 1e-3, SubstrateEpsR, Math.Max(SubstrateTanD, 0));
                        if (OpenSim.Rf.Layered.BoardAntennaStackup.TryResolve(
                                boardForStackup, choice.Island.LayerOrder, boardStackup,
                                choice.Island.Shape, out var resolved, out var stackFailure))
                        {
                            if (resolved!.IsSingleSlabTop)
                            {
                                // Same single-slab solver path as before, but over the gap the
                                // board stackup resolved to — not the panel's seed scalars.
                                var slab = resolved.Stackup.Layers[0];
                                substrate = new OpenSim.Rf.Layered.SubstrateStackup(
                                    slab.RelativePermittivity, slab.LossTangent, slab.ThicknessMeters);
                            }
                            else
                                layered = new LayeredSpec(resolved.Stackup, resolved.SourceInterface);
                            _boardStackupNotes = resolved.Assumptions;
                        }
                        else
                        {
                            // Not a silent fall-back to one slab: say which board fact refused.
                            _log.Append($"Antenna: board stackup — {stackFailure} Solving over "
                                + "the single substrate slab from the panel values instead.");
                        }
                    }
                }
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
            // Read on the UI thread: the sheet-loss model and the sweep kind.
            var sheet = SheetModel(applies: probe is null, overGround: substrate is not null || surface.Ground is not null);
            bool adaptive = AdaptiveSweep && points > 2 && fMax > fMin;
            int maxSamples = Math.Max(points, 30);
            var (sweep, display, rational) = await Task.Run(() =>
            {
                var solver = new OpenSim.Rf.Surface.SurfaceMomSolver { SheetImpedance = sheet };
                if (adaptive)
                {
                    // One solver run after another: each new frequency is chosen from the
                    // interpolant through the ones before it.
                    var found = OpenSim.Rf.Network.RationalSweep.Run(
                        f => new[] { SolveSurfacePoint(solver, surface, port, f, substrate, probe, layered, timing).InputImpedance },
                        fMin, fMax, maxSamples: maxSamples);
                    var sampled = found.FrequenciesHz.Select((f, k) =>
                        new AntennaZinPoint(f, found.Values[k][0].Real, found.Values[k][0].Imaginary)).ToList();
                    return (sampled, SolveSurfacePoint(solver, surface, port, frequency, substrate, probe, layered, timing),
                        (OpenSim.Rf.Network.RationalSweepResult?)found);
                }
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
                return (list, SolveSurfacePoint(solver, surface, port, frequency, substrate, probe, layered, timing),
                    (OpenSim.Rf.Network.RationalSweepResult?)null);
            });

            foreach (var point in sweep) ZinSweep.Add(point);
            if (rational is not null) ShowNetworkResults(DenseCurve(rational), AdaptiveNote(rational));
            else ShowNetworkResultsFromSweep();
            // Where the input power goes in the metal, at the display frequency.
            string lossNote = "";
            if (sheet is not null)
            {
                var zs = sheet(frequency);
                double heat = OpenSim.Rf.Surface.SheetLoss.OhmicPower(surface, display.EdgeCurrents, zs);
                double input = 0.5 * (System.Numerics.Complex.One / display.InputImpedance).Real;
                lossNote = $" Conductor loss: {heat / input:p1} of the input power is heat in the sheet "
                    + $"(copper {MetalThicknessUm:g3} µm, {zs.Real * 1e3:g3} mΩ per square at this frequency). "
                    + "A zero-thickness sheet has a singular current at its edges, so this figure rises slowly "
                    + "with mesh density; the ground plane is still a perfect conductor.";
            }
            else if (ConductorLoss)
                lossNote = " Conductor loss is not applied to a probe-fed solve: the metal here is perfect.";
            SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(surface, display, ColormapKind.Viridis);
            GroundPlaneModel = BuildSurfaceGroundOverlay(surface, substrate);

            // Two-pass mesh check: the same model on a mesh 1.5× finer, at the display
            // frequency. The change in Zin is the evidence the density is (or is not) enough.
            string meshCheck = "";
            if (CheckMeshConvergence)
            {
                // The finer mesh is built here (it reads panel state); only the solve runs off-thread.
                if (!TryDiscretizeSurface(out var fineSurface, out var finePort, out _,
                        out string? fineFailure, out var fineSubstrate, out var fineProbe,
                        out var fineLayered, refine: 1.5))
                    meshCheck = $" Mesh check not run: the finer mesh could not be built ({fineFailure}).";
                else
                    meshCheck = await Task.Run(() =>
                    {
                        try
                        {
                            var fine = SolveSurfacePoint(new OpenSim.Rf.Surface.SurfaceMomSolver { SheetImpedance = sheet },
                                fineSurface, finePort, frequency, fineSubstrate, fineProbe, fineLayered,
                                new List<string>());
                            return " " + OpenSim.Rf.Surface.MeshConvergence.Describe(
                                display.InputImpedance, surface.BasisCount,
                                fine.InputImpedance, fineSurface.BasisCount);
                        }
                        catch (Exception ex) { return $" Mesh check not run: {ex.Message}"; }
                    });
            }
            string portLabel = probe is not null ? ""
                : SourceMode == PatchMode || SourceMode == CoveredPatchMode
                    ? $" Port: a series gap {(PatchGapWidthMm > 0 ? PatchGapWidthMm : PlateLengthMm / 18):g3} mm wide "
                      + $"across the full patch width, centred "
                      + $"{(PatchGapOffsetMm > 0 ? PatchGapOffsetMm : PlateLengthMm / 8):g3} mm in from the edge. "
                      + "Zin is the impedance in series at that cut — NOT the ground-referenced edge or "
                      + "inset-feed impedance; use the probe-fed patch for a ground-referenced figure."
                    : "";
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
                    : layered is null
                        ? OpenSim.Rf.Surface.SurfaceMomSolver.ProbeFedAssumptions
                        : OpenSim.Rf.Surface.SurfaceMomSolver.MultiLayerProbeFedAssumptions)
                + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "")
                + portLabel + meshCheck + lossNote;
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
            var mlSolution = SolveMultiLayer(solver, surface, mlTable, port, probe);
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

    /// <summary>One multi-layer solve, with or without a coaxial probe. It exists so the probe
    /// cannot be SILENTLY dropped: before Stage C2 every multi-layer call site solved the sheet
    /// through its port and ignored any probe, which was harmless only because no wizard mode
    /// could produce both — and stopped being harmless the moment one could.</summary>
    private static OpenSim.Rf.Surface.SurfaceMomSolution SolveMultiLayer(
        OpenSim.Rf.Surface.SurfaceMomSolver solver, OpenSim.Rf.Surface.SurfaceStructure surface,
        OpenSim.Rf.Layered.MultiLayerKernelTable table, OpenSim.Rf.Surface.SurfacePort port,
        OpenSim.Rf.Surface.ProbeFeed? probe)
        => probe is { } p
            ? solver.SolveProbeFed(surface, table, p).Surface
            : solver.Solve(surface, table, port);

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
                    var mlSolved = SolveMultiLayer(solver, surface, mlTable, port, probe);
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
                          "; arrows are the t = 0 snapshot; the sheet is colored by log₁₀|J|)" +
                          (probe is null ? "" : " The map is the field of the patch sheet currents only: " +
                              "the coaxial probe's own vertical current is NOT included, so the field " +
                              "within a few substrate thicknesses of the probe is incomplete.");
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    /// <summary>The Stage D1 hybrid, reachable from the wizard: a bare rectangular plate in FREE
    /// SPACE with a thin wire ENDING on one of its interior mesh vertices — the finite-ground
    /// monopole, and the first mode in which a wire and a sheet appear in one structure.
    ///
    /// <para>Free space is the whole scope, and the refusal below says so rather than degrading:
    /// the junction's 1/ρ disc reads a free-space radial kernel, so a PEC ground would need the
    /// disc's own image pass and a substrate its layered radial kernel — both named follow-ups.
    /// The wire's attaching end is forced to be a mesh vertex by building the plate with that
    /// point snapped in, because the attachment fan needs an anchor and a guessed contact is a
    /// wrong placement, not an approximation.</para></summary>
    private async Task SolveWireFedPlateAsync()
    {
        if (!TryBuildWireFedPlate(out var surface, out var wire, out string? failure))
        {
            AntennaResult = $"Not solvable: {failure}";
            return;
        }
        AntennaResult = $"Solving ({surface.BasisCount} RWG + {wire.BasisCount} wire unknowns)…";
        try
        {
            double frequency = FrequencyMHz * 1e6;
            double fMin = SweepFMinMHz * 1e6, fMax = SweepFMaxMHz * 1e6;
            int points = SweepPoints;
            var (sweep, display) = await Task.Run(() =>
            {
                var solver = new OpenSim.Rf.Surface.SurfaceMomSolver();
                // The feed is the delta gap at the CONTACT — the attachment basis is a legal feed
                // and is what a monopole standing on a finite ground plane means.
                int feed = OpenSim.Rf.Surface.SurfaceMomSolver.AttachmentFeedBasis(surface, wire);
                double FrequencyAt(int k) => points == 1
                    ? fMin
                    : fMin * Math.Pow(fMax / fMin, (double)k / (points - 1));
                var results = new OpenSim.Rf.Surface.WireAttachedSolution[points];
                try
                {
                    Parallel.For(0, points, k =>
                        results[k] = solver.SolveWireAttached(surface, wire, FrequencyAt(k), feed));
                }
                catch (AggregateException e) { throw e.InnerExceptions[0]; }
                var list = new List<AntennaZinPoint>(points);
                for (int k = 0; k < points; k++)
                    list.Add(new AntennaZinPoint(FrequencyAt(k),
                        results[k].InputImpedance.Real, results[k].InputImpedance.Imaginary));
                return (list, solver.SolveWireAttached(surface, wire, frequency, feed));
            });

            foreach (var point in sweep) ZinSweep.Add(point);
            ShowNetworkResultsFromSweep();
            // The FOLDED edge currents are the generic-consumer view (the junction's transported
            // current spread onto the fan's outer edges), which is exactly what a current display
            // wants; the far field uses the raw ones plus the junction's own exact transform.
            SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(surface,
                new OpenSim.Rf.Surface.SurfaceMomSolution(display.FrequencyHz,
                    display.InputImpedance, display.EdgeCurrents), ColormapKind.Viridis);
            GroundPlaneModel = null;
            AntennaResult = $"Zin = {display.InputImpedance.Real:g4} " +
                            $"{(display.InputImpedance.Imaginary >= 0 ? "+" : "−")} " +
                            $"j{Math.Abs(display.InputImpedance.Imaginary):g4} Ω at {FrequencyMHz:g4} MHz " +
                            $"({surface.BasisCount} RWG + {wire.BasisCount} wire unknowns, " +
                            $"{display.IncidenceDegrees:g3}° incidence)";
            if (display.FanFluxMismatch > OpenSim.Rf.Surface.WireAttachedSolution.SkewedFanThreshold)
                AntennaResult += $". Warning: the contact point sits close to a neighbouring mesh " +
                                 $"vertex (junction flux mismatch {display.FanFluxMismatch:P0}); the " +
                                 "reactance is less reliable — move the contact or change the mesh size";
            AntennaAssumptions = "Assumptions: " + string.Join(" ",
                OpenSim.Rf.Surface.SurfaceMomSolver.WireAttachedAssumptions);
            _log.Append($"Antenna: {AntennaResult}");
        }
        catch (Exception ex) { AntennaResult = $"Not solvable: {ex.Message}"; }
    }

    private async Task ShowWireFedPlateFarFieldAsync()
    {
        if (!TryBuildWireFedPlate(out var surface, out var wire, out string? failure))
        {
            FieldResult = $"Not computable: {failure}";
            return;
        }
        FieldResult = $"Computing ({surface.BasisCount} RWG + {wire.BasisCount} wire unknowns)…";
        try
        {
            double frequency = FrequencyMHz * 1e6;
            var (pattern, solution, inputPower) = await Task.Run(() =>
            {
                var solver = new OpenSim.Rf.Surface.SurfaceMomSolver();
                int feed = OpenSim.Rf.Surface.SurfaceMomSolver.AttachmentFeedBasis(surface, wire);
                var solved = solver.SolveWireAttached(surface, wire, frequency, feed);
                double pin = 0.5 * (System.Numerics.Complex.One / solved.InputImpedance).Real;
                return (OpenSim.Rf.Surface.WireAttachedFarField.Compute(surface, wire, solved),
                    solved, pin);
            });

            var (center, diagonal) = SurfaceBounds(surface);
            FarFieldLobeModel = SceneBuilder.BuildFarFieldLobe(
                pattern, center, scale: 1.25 * diagonal, ColormapKind.Viridis);
            SurfaceCurrentModel = SceneBuilder.BuildSurfaceCurrentModel(surface,
                new OpenSim.Rf.Surface.SurfaceMomSolution(solution.FrequencyHz,
                    solution.InputImpedance, solution.EdgeCurrents), ColormapKind.Viridis);
            GroundPlaneModel = null;

            double dbi = 10 * Math.Log10(pattern.MaxDirectivity);
            FieldResult = $"Far field at {FrequencyMHz:g4} MHz: P_rad = " +
                          $"{pattern.TotalRadiatedPowerWatts:g4} W (1 V feed), " +
                          $"D_max = {pattern.MaxDirectivity:g4} ({dbi:g3} dBi); " +
                          "lobe radius ∝ radiation intensity";
            // Free space has no surface wave, so the ledger is the whole story here and is worth
            // showing: it is the identity that caught the junction's disc sign at −5.9.
            if (inputPower > 0)
                FieldResult += $"; P_rad = {pattern.TotalRadiatedPowerWatts / inputPower:P1} of "
                    + "the power the feed delivers";
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    /// <summary>The solver's own grazing-incidence floor, restated here so the wizard refuses
    /// before it builds rather than throwing out of the fill. The solver remains the authority:
    /// it re-checks the angle it actually measures from the built geometry.</summary>
    private const double MinimumAttachmentDegrees = 10.0;

    /// <summary>The wire-fed plate's geometry: a bare centred plate with the attachment point
    /// snapped in as a mesh vertex, and a straight wire leaving that point at the requested
    /// incidence. Every refusal names what to change.</summary>
    private bool TryBuildWireFedPlate(out OpenSim.Rf.Surface.SurfaceStructure surface,
        out WireStructure wire, out string? failure)
    {
        surface = null!;
        wire = null!;
        failure = null;
        if (PlateWidthMm <= 0 || PlateLengthMm <= 0)
        { failure = "the plate needs a positive width and length"; return false; }
        if (AttachedWireLengthMm <= 0)
        { failure = "the attached wire needs a positive length"; return false; }
        if (WireRadiusMm <= 0)
        { failure = "the wire radius must be positive"; return false; }
        if (AttachedWireDegrees < MinimumAttachmentDegrees || AttachedWireDegrees > 90)
        {
            failure = $"the wire must meet the sheet between {MinimumAttachmentDegrees:g3}° and 90° "
                + "— at grazing incidence the reduced-kernel tube overlaps the metal it attaches to";
            return false;
        }
        if (UseSubstrate || UseGroundPlane)
        {
            failure = "a wire-fed plate is solved in FREE SPACE — clear the ground plane and the "
                + "substrate. The junction's disc reads a free-space radial kernel; the imaged and "
                + "layered versions are named follow-ups, not silent approximations";
            return false;
        }

        double maxFrequency = Math.Max(FrequencyMHz, Math.Max(SweepFMinMHz, SweepFMaxMHz)) * 1e6;
        if (maxFrequency <= 0) { failure = "the frequency must be positive"; return false; }
        double lambdaMin = 299_792_458.0 / maxFrequency;
        double w = PlateWidthMm * 1e-3, l = PlateLengthMm * 1e-3;
        double ax = AttachXMm * 1e-3, ay = AttachYMm * 1e-3;
        if (Math.Abs(ax) >= w / 2 || Math.Abs(ay) >= l / 2)
        {
            failure = "the attachment point must lie strictly inside the plate (it is measured "
                + "from the plate centre, and the fan needs a full ring of triangles around it)";
            return false;
        }

        var grid = OpenSim.Rf.Surface.SurfaceMeshBuilder.BuildRectangularPlate(
            w, l, lambdaMin / 10, z: 0, portFraction: 0, ground: null, snapVertex: (ax, ay));
        if (grid.Structure is null) { failure = grid.FailureReason; return false; }
        surface = grid.Structure;

        double radians = AttachedWireDegrees * Math.PI / 180;
        double length = AttachedWireLengthMm * 1e-3;
        var contact = new Vector3D(ax, ay, 0);
        // The wire leans in +x as it rises; at 90° it is the plain normal monopole.
        var tip = contact + new Vector3D(Math.Cos(radians) * length, 0, Math.Sin(radians) * length);
        var built = WireGridBuilder.Build(
            new[] { new WireSegment(contact, tip, WireRadiusMm * 1e-3) },
            lambdaMin / 10, ground: null, attachmentPoint: contact);
        if (built.Structure is null) { failure = built.FailureReason; return false; }
        wire = built.Structure;
        return true;
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
            var sheet = SheetModel(applies: probe is null, overGround: substrate is not null || surface.Ground is not null);
            var (pattern, solution, surfaceWavePower, inputPower) = await Task.Run(() =>
            {
                var solver = new OpenSim.Rf.Surface.SurfaceMomSolver { SheetImpedance = sheet };
                if (layered is { } spec)
                {
                    // The multi-layer (Stage F) far field: horizontal RWG currents radiating
                    // through the stack, region-0 amplitude from the TLGF; P_sw from the
                    // multi-layer poles. Covered-patch ledger P_rad + P_sw ≡ ½Re(V·I*). With a
                    // coaxial probe (Stage C2) the tube and junction legs join both sides of
                    // that ledger — the surface-wave launches add COHERENTLY, so they cannot be
                    // summed as two separate powers.
                    var mlTable = BuildMultiLayerTable(surface, spec, frequency);
                    if (probe is { } mlProbe)
                    {
                        var mlPf = solver.SolveProbeFed(surface, mlTable, mlProbe);
                        double mlProbePin =
                            0.5 * (System.Numerics.Complex.One / mlPf.Surface.InputImpedance).Real;
                        return (OpenSim.Rf.Layered.LayeredFarField.Compute(
                                surface, mlTable, mlPf, mlProbe),
                            mlPf.Surface,
                            OpenSim.Rf.Layered.LayeredFarField.SurfaceWavePowerWatts(
                                surface, mlTable, mlPf, mlProbe),
                            mlProbePin);
                    }
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
                    // junction current; the surface-wave ledger adds the tube's launch
                    // coherently, at the tube's own position (FU-1). On a lossless slab the
                    // ledger closes to 0.1 %.
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
                // takes is real power the pattern never sees, and on a lossy substrate the
                // rest is dielectric loss — named, with the efficiency and gain it implies.
                double heat = sheet is null ? 0
                    : OpenSim.Rf.Surface.SheetLoss.OhmicPower(surface, solution.EdgeCurrents, sheet(frequency));
                FieldResult += "; " + OpenSim.Rf.Layered.PowerLedger.Describe(inputPower,
                    pattern.TotalRadiatedPowerWatts, surfaceWavePower,
                    substrate.LossTangent, pattern.MaxDirectivity, heat);
            }
            else if (substrate is null && sheet is not null)
            {
                // Free space with lossy metal: what is not radiated is heat in the sheet.
                double input = 0.5 * (System.Numerics.Complex.One / solution.InputImpedance).Real;
                double heat = OpenSim.Rf.Surface.SheetLoss.OhmicPower(surface, solution.EdgeCurrents, sheet(frequency));
                double efficiency = pattern.TotalRadiatedPowerWatts / input;
                FieldResult += $"; radiation efficiency {efficiency:p1}, gain "
                    + $"{10 * Math.Log10(Math.Max(efficiency * pattern.MaxDirectivity, 1e-300)):g3} dBi; conductor loss "
                    + $"{heat / input:p1} of the input (radiated + heat = {(pattern.TotalRadiatedPowerWatts + heat) / input:p1})";
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
            // A board net's resolved stackup states which layer became the ground and what each
            // gap is made of — facts the generic layered assumptions cannot know.
            if (_boardStackupNotes is { Count: > 0 } boardNotes)
                lines.InsertRange(1, boardNotes);
            lines.Insert(1,
                $"Substrate: εr = {substrate.RelativePermittivity:g3}, tanδ = {substrate.LossTangent:g3}, " +
                $"thickness {substrate.ThicknessMeters * 1e3:g4} mm; the reported power ledger counts " +
                "only the extracted surface-wave modes (TM0 always; higher modes above cutoff).");
            // Covered patch: name the cover so the downward resonance shift is not a surprise.
            if (layered is { SourceInterface: not null } spec && spec.Stackup.Layers.Count > 1)
            {
                var cover = spec.Stackup.Layers[^1];
                bool matched = cover.RelativePermittivity == substrate.RelativePermittivity;
                lines.Insert(2,
                    $"Dielectric cover: εr = {cover.RelativePermittivity:g3}, tanδ = "
                    + $"{cover.LossTangent:g3}, thickness {cover.ThicknessMeters * 1e3:g4} mm above "
                    + "the buried metal (a covered patch, Stage F multi-layer TLGF) — the cover "
                    + "loads the patch, so its resonance sits below the bare patch's."
                    + (matched ? "" : " The cover permittivity differs from the substrate's; the "
                        + "buried-source read-out is single-valued across that jump (the TM "
                        + "contrast source at the sheet cancels the 1/ε difference exactly)."));
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
