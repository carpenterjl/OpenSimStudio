using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;

namespace OpenSim.App.ViewModels;

/// <summary>
/// PEEC inductance analysis from trace centerlines — self L of a net's chain, mutual
/// M + coupling k between two nets, and loop L closed through a return net or a copper
/// plane (image theory). Works straight from the imported board, no meshing required.
/// The PCB view model hands the board over after import (the same sanctioned edge as
/// pad hand-off) and clears it on teardown; every chain/composer failure reason is
/// surfaced verbatim — never a garbage number.
/// </summary>
public partial class InductanceViewModel : ObservableObject
{
    private readonly ILogService _log;
    private PcbBoard? _board;
    private Func<NetMeshOptions>? _options;

    public InductanceViewModel(ILogService log) => _log = log;

    /// <summary>Nets offered for analysis (largest first, as imported).</summary>
    public ObservableCollection<CopperNet> Nets { get; } = new();

    /// <summary>Copper layer orders selectable as the return plane.</summary>
    public ObservableCollection<int> PlaneLayers { get; } = new();

    [ObservableProperty] private CopperNet? _outboundNet;
    [ObservableProperty] private CopperNet? _secondNet;
    [ObservableProperty] private bool _returnIsPlane;

    /// <summary>Nullable so the ComboBox's transient null push on ItemsSource resets
    /// lands harmlessly (the same WPF behavior the analysis picker coerces).</summary>
    [ObservableProperty] private int? _planeLayer;
    [ObservableProperty] private string _selfResult = "";
    [ObservableProperty] private string _mutualResult = "";
    [ObservableProperty] private string _loopResult = "";
    [ObservableProperty] private string _inductanceAssumptions = "";

    /// <summary>Installs an imported board. <paramref name="meshOptions"/> is evaluated
    /// per computation so stackup edits in the PCB panel flow through — one z model for
    /// preview, mesh, and inductance.</summary>
    public void LoadBoard(PcbBoard board, Func<NetMeshOptions> meshOptions)
    {
        Clear();
        _board = board;
        _options = meshOptions;
        foreach (var net in board.Nets) Nets.Add(net);
        foreach (var layer in board.Layers.OrderBy(l => l.CopperOrder))
            PlaneLayers.Add(layer.CopperOrder);
        if (PlaneLayers.Count > 0) PlaneLayer = PlaneLayers[^1];
    }

    /// <summary>Follows the PCB panel's net selection as the default outbound net until
    /// the user picks one explicitly here.</summary>
    public void SetDefaultNet(CopperNet? net)
    {
        if (OutboundNet is null && net is not null && Nets.Contains(net))
            OutboundNet = net;
    }

    public void Clear()
    {
        _board = null;
        _options = null;
        OutboundNet = null;
        SecondNet = null;
        PlaneLayer = null;
        Nets.Clear();
        PlaneLayers.Clear();
        SelfResult = "";
        MutualResult = "";
        LoopResult = "";
        InductanceAssumptions = "";
    }

    [RelayCommand]
    private void ComputeSelf()
    {
        InductanceAssumptions = "";
        if (OutboundNet is not { } net) { SelfResult = "Pick a net first."; return; }
        var chain = BuildChain(net);
        if (chain.Chain is null) { SelfResult = $"Not composable: {chain.FailureReason}."; return; }

        try
        {
            var report = new LoopComposer().Compose(chain.Chain);
            SelfResult = $"L (partial) = {report.LoopInductance * 1e9:g4} nH    " +
                         $"Σ segment self = {report.TotalSelf * 1e9:g4} nH    " +
                         $"({chain.Chain.Count} segments{BridgeNote(chain)})";
            InductanceAssumptions = "Assumptions: " + string.Join(" ", report.Assumptions);
            _log.Append($"Inductance: {net.Label}: {SelfResult}");
        }
        catch (Exception ex) { SelfResult = $"Not composable: {ex.Message}"; }
    }

    [RelayCommand]
    private void ComputeMutual()
    {
        InductanceAssumptions = "";
        if (OutboundNet is not { } a || SecondNet is not { } b) { MutualResult = "Pick both nets first."; return; }
        if (ReferenceEquals(a, b)) { MutualResult = "Pick two different nets."; return; }

        // Both chains must share ONE stackup z frame: widen each build's span with the
        // other net's layers.
        var chainA = BuildChain(a, b.Layers);
        if (chainA.Chain is null) { MutualResult = $"{a.Label}: {chainA.FailureReason}."; return; }
        var chainB = BuildChain(b, a.Layers);
        if (chainB.Chain is null) { MutualResult = $"{b.Label}: {chainB.FailureReason}."; return; }

        try
        {
            var report = new MutualCouplingAnalyzer().Analyze(chainA.Chain, chainB.Chain);
            MutualResult = $"M = {report.MutualHenries * 1e9:g4} nH    k = {report.CouplingK:g4}";
            InductanceAssumptions = "Assumptions: " + string.Join(" ", report.Assumptions);
            _log.Append($"Inductance: M({a.Label}, {b.Label}): {MutualResult}");
        }
        catch (Exception ex) { MutualResult = $"Not composable: {ex.Message}"; }
    }

    [RelayCommand]
    private void ComputeLoop()
    {
        InductanceAssumptions = "";
        if (OutboundNet is not { } net) { LoopResult = "Pick the outbound net first."; return; }
        try
        {
            if (ReturnIsPlane) ComputePlaneLoop(net);
            else ComputeNetLoop(net);
        }
        catch (Exception ex) { LoopResult = $"Not composable: {ex.Message}"; }
    }

    private void ComputeNetLoop(CopperNet net)
    {
        if (SecondNet is not { } ret) { LoopResult = "Pick the return net."; return; }
        if (ReferenceEquals(net, ret)) { LoopResult = "Pick two different nets."; return; }
        var chainA = BuildChain(net, ret.Layers);
        if (chainA.Chain is null) { LoopResult = $"{net.Label}: {chainA.FailureReason}."; return; }
        var chainB = BuildChain(ret, net.Layers);
        if (chainB.Chain is null) { LoopResult = $"{ret.Label}: {chainB.FailureReason}."; return; }

        var report = new MutualCouplingAnalyzer().Analyze(chainA.Chain, chainB.Chain);
        LoopResult = $"L (loop) = {report.LoopInductanceHenries * 1e9:g4} nH " +
                     $"(return traversed {(report.ReturnTraversedForward ? "start → end" : "end → start")})";
        InductanceAssumptions = "Assumptions: " + string.Join(" ", report.Assumptions);
        _log.Append($"Inductance: loop({net.Label} → {ret.Label}): {LoopResult}");
    }

    private void ComputePlaneLoop(CopperNet net)
    {
        if (PlaneLayer is not int planeLayer) { LoopResult = "Pick a plane layer."; return; }
        var chain = BuildChain(net, new[] { planeLayer });
        if (chain.Chain is null) { LoopResult = $"{net.Label}: {chain.FailureReason}."; return; }
        if (chain.LayerZ is null || !chain.LayerZ.TryGetValue(planeLayer, out var plane))
        {
            LoopResult = $"Layer L{planeLayer} is not in the stackup.";
            return;
        }

        // Image theory mirrors across the plane's copper surface FACING the chain.
        double chainMid = chain.Chain.Average(s => 0.5 * (s.Start.Z + s.End.Z));
        double surface = chainMid >= 0.5 * (plane.zLo + plane.zHi) ? plane.zHi : plane.zLo;

        var report = new PlaneReturnComposer().Compose(chain.Chain, surface);
        if (report.LoopInductanceHenries is not { } loop)
        {
            LoopResult = $"Not composable: {report.FailureReason}.";
            return;
        }
        LoopResult = $"L (loop over L{planeLayer} plane) = {loop * 1e9:g4} nH";
        InductanceAssumptions = "Assumptions: " + string.Join(" ", report.Assumptions)
                                + BridgeNote(chain, leading: " ");
        _log.Append($"Inductance: loop({net.Label} over L{planeLayer}): {LoopResult}");
    }

    private TraceChain3DResult BuildChain(CopperNet net, IReadOnlyCollection<int>? includeLayers = null)
    {
        if (_board is null || _options is null)
            return TraceChain3DResult.Failure("import a board first");
        if (_board.TraceCenterlines.Count == 0)
            return TraceChain3DResult.Failure(
                "no trace centerlines were captured for this board (the file may contain only " +
                "pours, regions, and flashes — no drawn traces)");

        var traces = NetTraceExtractor.ForNet(_board, net);
        var options = _options();
        var terminals = NetTraceExtractor.FarthestPadTerminals(_board, net);
        if (terminals is null)
            return TraceChainBuilder.Build(traces, net.StitchingVias, options, net.Islands, includeLayers);

        // Pad-anchored first (branched nets reduce to the pad-to-pad path); if the
        // anchoring itself is what fails (e.g. a pad hanging on a far pour), the classic
        // whole-net build still gets its chance. When both fail, the anchored failure is
        // the more specific story.
        var anchored = TraceChainBuilder.Build(traces, net.StitchingVias, options,
            net.Islands, includeLayers, terminals);
        if (anchored.Chain is not null) return anchored;
        var plain = TraceChainBuilder.Build(traces, net.StitchingVias, options, net.Islands, includeLayers);
        return plain.Chain is not null ? plain : anchored;
    }

    private static string BridgeNote(TraceChain3DResult chain, string leading = ", ") =>
        (chain.CopperBridges > 0
            ? $"{leading}{chain.CopperBridges} pad/fill crossing(s) as straight bars"
            : "")
        + (chain.PrunedSegments > 0
            ? $"{leading}{chain.PrunedSegments} side-branch segment(s) " +
              $"({chain.PrunedLengthMeters * 1e3:g3} mm) pruned — no current between the " +
              "anchoring pads"
            : "");
}
