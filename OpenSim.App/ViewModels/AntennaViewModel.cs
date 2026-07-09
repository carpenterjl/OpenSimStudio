using System.Collections.ObjectModel;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Rendering;
using OpenSim.App.Services;
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

    public ObservableCollection<string> SourceModes { get; } = new() { NetMode, DipoleMode, LoopMode };

    /// <summary>Nullable so the ComboBox's transient null push lands harmlessly.</summary>
    [ObservableProperty] private string? _sourceMode = DipoleMode;

    public ObservableCollection<CopperNet> Nets { get; } = new();
    [ObservableProperty] private CopperNet? _antennaNet;

    // Wizard dimensions [mm] — defaults sit near λ/2 resonance at the default frequency.
    [ObservableProperty] private double _dipoleLengthMm = 500;
    [ObservableProperty] private double _loopRadiusMm = 80;
    [ObservableProperty] private double _wireRadiusMm = 0.5;

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

    // Viewport overlays (bound by Viewport3DView, the same hosting pattern as the
    // PCB preview lines).
    [ObservableProperty] private Model3D? _vectorFieldModel;
    [ObservableProperty] private Model3D? _fieldSliceModel;
    [ObservableProperty] private Model3D? _farFieldLobeModel;
    [ObservableProperty] private Point3DCollection _wirePoints = new();

    /// <summary>Installs an imported board (same sanctioned edge as the inductance panel).</summary>
    public void LoadBoard(PcbBoard board, Func<NetMeshOptions> meshOptions)
    {
        Clear();
        _board = board;
        _options = meshOptions;
        foreach (var net in board.Nets) Nets.Add(net);
        SourceMode = NetMode;
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
        FarFieldLobeModel = null;
        WirePoints = new Point3DCollection();
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    [RelayCommand]
    private void SolveAntenna()
    {
        ZinSweep.Clear();
        AntennaResult = "";
        AntennaAssumptions = "";
        if (SweepFMinMHz <= 0 || SweepFMaxMHz < SweepFMinMHz || SweepPoints < 1 || FrequencyMHz <= 0)
        {
            AntennaResult = "Not solvable: the frequency range is invalid.";
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
            AntennaAssumptions = "Assumptions: " + string.Join(" ", ThinWireMomSolver.Assumptions)
                + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "");
            _log.Append($"Antenna: {AntennaResult}");
        }
        catch (Exception ex) { AntennaResult = $"Not solvable: {ex.Message}"; }
    }

    [RelayCommand]
    private void ComputeNearField()
    {
        FieldResult = "";
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
            double peak = map.Magnitude.Concat(slice.Magnitude).Max();
            FieldResult = $"Near field at {FrequencyMHz:g4} MHz: peak |E| = {peak:g4} V/m " +
                          $"(1 V feed, {n}³ grid + mid-plane slice; arrows are the t = 0 snapshot, " +
                          "color is log₁₀|E| over 3 decades)";
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
    }

    [RelayCommand]
    private void ShowFarField()
    {
        FieldResult = "";
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

            double dbi = 10 * Math.Log10(pattern.MaxDirectivity);
            FieldResult = $"Far field at {FrequencyMHz:g4} MHz: P_rad = " +
                          $"{pattern.TotalRadiatedPowerWatts:g4} W (1 V feed), " +
                          $"D_max = {pattern.MaxDirectivity:g4} ({dbi:g3} dBi); " +
                          "lobe radius ∝ radiation intensity";
            _log.Append($"Antenna: {FieldResult}");
        }
        catch (Exception ex) { FieldResult = $"Not computable: {ex.Message}"; }
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
                break;

            case LoopMode:
                if (LoopRadiusMm <= 0 || WireRadiusMm <= 0)
                {
                    failure = "the loop needs a positive loop radius and wire radius";
                    return false;
                }
                wires = CanonicalAntennas.Loop(LoopRadiusMm * 1e-3, WireRadiusMm * 1e-3);
                feedHint = new Vector3D(LoopRadiusMm * 1e-3, 0, 0);
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
        var grid = WireGridBuilder.Build(wires, maxElementLength: lambdaMin / 10);
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
}
