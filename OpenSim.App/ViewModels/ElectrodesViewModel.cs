using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Pad electrodes on a meshed net and the pad-to-pad electrical test (resistance).
/// Pads are loaded by the PCB view model after a net is meshed; clicking a gold pad in
/// the viewport assigns Source then Sink.
/// </summary>
public partial class ElectrodesViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private readonly IReadOnlyList<ISolver> _solvers;
    private readonly MaterialsViewModel _materials;

    public ElectrodesViewModel(ProjectSession session, ILogService log,
        IEnumerable<ISolver> solvers, MaterialsViewModel materials)
    {
        _session = session;
        _log = log;
        _solvers = solvers.ToList();
        _materials = materials;
        // Stale pad face ids must never intercept clicks on a NEW body's faces.
        session.GeometryReplaced += (_, _) => ClearPads();
    }

    public ObservableCollection<NetMesher.PadElectrode> PadElectrodes { get; } = new();
    [ObservableProperty] private int? _sourceFaceId;
    [ObservableProperty] private int? _sinkFaceId;
    [ObservableProperty] private NetMesher.PadElectrode? _selectedSource;
    [ObservableProperty] private NetMesher.PadElectrode? _selectedSink;
    [ObservableProperty] private double _testVoltage = 0.1;
    [ObservableProperty] private string _electrodeInfo = "Mesh a net, then pick Source and Sink pads.";
    [ObservableProperty] private string _electricalResult = "";

    /// <summary>Excitation choice for the pad-to-pad test: a prescribed source voltage
    /// (default) or an injected source current against the 0 V sink.</summary>
    [ObservableProperty] private bool _driveWithCurrent;
    [ObservableProperty] private double _testCurrent = 1.0;

    // Lumped AC impedance estimate Z(f) = R + jωL (R from the DC test, L from PEEC).
    public ObservableCollection<ImpedancePoint> AcSweep { get; } = new();
    [ObservableProperty] private double _acFMin = 1e3;      // Hz
    [ObservableProperty] private double _acFMax = 1e8;      // Hz
    [ObservableProperty] private int _acPoints = 7;
    [ObservableProperty] private string _acResult = "";
    [ObservableProperty] private string _acAssumptions = "";

    private Func<(ChainTerminal Source, ChainTerminal Sink)?, TraceChain3DResult>? _chainBuilder;
    private double? _lastDcResistance;

    /// <summary>Guards the pad reset on geometry replacement: the electrode-info/status
    /// text and highlight repaint must not fire while the scene is being rebuilt.</summary>
    private bool _suppressSelectionEffects;

    /// <summary>The Source/Sink dropdowns and 3D pad clicks share one selection: setting the
    /// electrode drives the face id the solver uses and repaints the highlight.</summary>
    partial void OnSelectedSourceChanged(NetMesher.PadElectrode? value)
    {
        SourceFaceId = value?.FaceId;
        if (_suppressSelectionEffects) return;
        UpdateElectrodeInfo();
        _session.RaiseHighlightsInvalidated();
    }

    partial void OnSelectedSinkChanged(NetMesher.PadElectrode? value)
    {
        SinkFaceId = value?.FaceId;
        if (_suppressSelectionEffects) return;
        UpdateElectrodeInfo();
        _session.RaiseHighlightsInvalidated();
    }

    /// <summary>Installs the pads of a freshly meshed net and resets the selection.</summary>
    public void LoadPads(IReadOnlyList<NetMesher.PadElectrode> pads)
    {
        ClearPads();
        foreach (var pad in pads) PadElectrodes.Add(pad);
        ElectrodeInfo = pads.Count == 0
            ? "No selectable pads on this net (no pad flashes on its layers, or the pads sit on buried " +
              "inner layers). Pick a net that ends in component pads, or use the Electrical conditions " +
              "panel to place a voltage on a face."
            : $"{pads.Count} pads found. Choose a Source and a Sink below, or click a gold pad in the view.";
    }

    /// <summary>Installs the selected net's 3D trace-chain builder (traces at stackup z
    /// plus via barrels) for the AC estimate. A delegate so the estimate can pass the
    /// pad pair selected at estimate time as chain terminals — branched nets then reduce
    /// to the source→sink path instead of refusing. Called by the PCB view model after a
    /// net mesh — the same sanctioned edge as <see cref="LoadPads"/>.</summary>
    public void LoadTraceChain(Func<(ChainTerminal Source, ChainTerminal Sink)?, TraceChain3DResult> builder)
    {
        _chainBuilder = builder;
        AcSweep.Clear();
        AcResult = "";
        AcAssumptions = "";
    }

    /// <summary>Silent reset: no status/highlight churn — callers repaint afterwards.</summary>
    private void ClearPads()
    {
        PadElectrodes.Clear();
        _suppressSelectionEffects = true;
        try
        {
            SelectedSource = null;
            SelectedSink = null;
        }
        finally { _suppressSelectionEffects = false; }
        ElectrodeInfo = "Mesh a net, then pick Source and Sink pads.";
        ElectricalResult = "";
        _chainBuilder = null;
        _lastDcResistance = null;
        AcSweep.Clear();
        AcResult = "";
        AcAssumptions = "";
    }

    /// <summary>Viewport click on a pad face assigns Source, then Sink, then starts over.
    /// Returns false when the face is not a pad (the click falls through to selection).</summary>
    public bool TryAssignElectrode(int faceId)
    {
        var pad = PadElectrodes.FirstOrDefault(p => p.FaceId == faceId);
        if (pad is null) return false;
        if (SelectedSource is null)
            SelectedSource = pad;
        else if (SelectedSink is null && pad.FaceId != SelectedSource.FaceId)
            SelectedSink = pad;
        else
        {
            SelectedSink = null;
            SelectedSource = pad;
        }
        return true;
    }

    private void UpdateElectrodeInfo()
    {
        ElectrodeInfo = $"Source: {SelectedSource?.Label ?? "—"}    Sink: {SelectedSink?.Label ?? "—"}";
        _session.StatusText = ElectrodeInfo;
    }

    /// <summary>
    /// The selected Source/Sink pad pair as solver boundary conditions, or null when both
    /// pads are not yet picked. Voltage drive: ΔV across the pads. Current drive: injected
    /// current on the source; the sink stays a 0 V Dirichlet ground, which also gives the
    /// solver the reference potential it requires for uniqueness. Shared by the DC pad test
    /// and the AC-sweep/Joule solve paths so every analysis drives the same excitation.
    /// </summary>
    public IReadOnlyList<BoundaryCondition>? TryBuildElectrodeConditions()
    {
        if (SourceFaceId is null || SinkFaceId is null) return null;
        BoundaryCondition source = DriveWithCurrent
            ? new CurrentFlow { Name = "Source", FaceIds = new[] { SourceFaceId.Value }, TotalCurrent = TestCurrent }
            : new VoltagePotential { Name = "Source", FaceIds = new[] { SourceFaceId.Value }, Volts = TestVoltage };
        return new[]
        {
            source,
            new VoltagePotential { Name = "Sink", FaceIds = new[] { SinkFaceId.Value }, Volts = 0 }
        };
    }

    /// <summary>Human-readable "source → sink" pad labels for solve-log lines.</summary>
    public string ElectrodeSummary =>
        $"{SelectedSource?.Label ?? "pad"} → {SelectedSink?.Label ?? "pad"} (0 V)";

    [RelayCommand]
    private async Task RunElectricalTestAsync()
    {
        var body = _session.Body;
        if (body.Mesh is null) { _log.Append("Mesh a net before the electrical test."); return; }
        var electrodeConditions = TryBuildElectrodeConditions();
        if (electrodeConditions is null)
        {
            _log.Append("Click a pad as Source and another as Sink first.");
            return;
        }

        var regionMaterials = _materials.ResolveRegionMaterials(body);
        var input = new SolveInput
        {
            Mesh = body.Mesh,
            Material = regionMaterials?.GetValueOrDefault(0)
                       ?? _session.SelectedMaterial ?? _materials.DefaultConductor(),
            RegionMaterials = regionMaterials,
            BoundaryConditions = electrodeConditions
        };

        var solver = _solvers.First(s => s is OpenSim.Solvers.ElectricalConductionSolver);
        _session.IsBusy = true;
        _session.StatusText = "Solving electrical test…";
        try
        {
            var output = await Task.Run(() => solver.Solve(input));
            foreach (var line in output.Log) _log.Append(line);
            _session.RaiseResultsProduced(output.Fields, preferFieldName: "Current density");

            if (output.Summary is not null
                && output.Summary.TryGetValue("Resistance (Ω)", out double r)
                && output.Summary.TryGetValue("Current (A)", out double i))
            {
                ElectricalResult = DriveWithCurrent
                    ? $"R = {r:g4} Ω    ΔV = {r * i:g4} V    (I = {TestCurrent:g4} A)"
                    : $"R = {r:g4} Ω    I = {i:g4} A    (ΔV = {TestVoltage:g4} V)";
                _lastDcResistance = r;   // feeds the lumped AC estimate
            }
            else
            {
                ElectricalResult = "Solved, but no two-electrode resistance was computed.";
            }
            _session.StatusText = ElectricalResult;
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally { _session.IsBusy = false; }
    }

    /// <summary>
    /// Lumped Z(f) = R + jωL for the meshed net's trace chain: R from the last DC test's
    /// field solve, L from the PEEC partial-inductance composition (multi-layer chains
    /// include their plated via barrels as vertical tube segments). With Source/Sink pads
    /// picked, the chain is the unique current path between them — dead side branches
    /// carry no current under pad-to-pad drive and are pruned (exactly, and reported).
    /// Without pads, only a non-branching open chain composes; anything else surfaces the
    /// chain builder's specific reason instead of a number.
    /// </summary>
    [RelayCommand]
    private void EstimateAcImpedance()
    {
        AcSweep.Clear();
        AcAssumptions = "";
        if (_chainBuilder is null)
        {
            AcResult = "Mesh a net first — the estimate needs its trace centerlines.";
            return;
        }
        (ChainTerminal Source, ChainTerminal Sink)? terminals = null;
        if (SelectedSource is { } source && SelectedSink is { } sink)
            terminals = (new ChainTerminal(source.Center, source.LayerOrder),
                         new ChainTerminal(sink.Center, sink.LayerOrder));

        var traceChain = _chainBuilder(terminals);
        string anchorNote = "";
        if (traceChain.Chain is null && terminals is not null)
        {
            // Real boards have pads that connect only through a pour, out of reach of
            // any drawn junction. If the whole net still composes as one open chain,
            // that estimate is exactly what this feature offered before pad anchoring —
            // use it, and say so.
            var wholeNet = _chainBuilder(null);
            if (wholeNet.Chain is not null)
            {
                anchorNote = $" The chain could not be anchored at the selected pads " +
                             $"({traceChain.FailureReason}); the whole net's open chain was used instead.";
                traceChain = wholeNet;
            }
        }
        if (traceChain.Chain is null)
        {
            AcResult = $"AC estimate unavailable for this net: {traceChain.FailureReason}." +
                       (terminals is null
                           ? " Picking Source and Sink pads lets the estimate follow just the" +
                             " current path between them (side branches are pruned)."
                           : "");
            return;
        }
        if (_lastDcResistance is not double resistance)
        {
            AcResult = "Run the electrical test first — R comes from the DC field solve.";
            return;
        }

        try
        {
            var report = NetImpedanceEstimator.Estimate(resistance, traceChain.Chain,
                AcFMin, AcFMax, AcPoints);
            foreach (var point in report.Points)
                AcSweep.Add(point);
            int barrels = traceChain.Chain.Count(s => s.Profile == SegmentProfile.RoundTube);
            AcResult = $"R = {report.ResistanceOhms:g4} Ω    L = {report.InductanceHenries * 1e9:g4} nH " +
                       $"({traceChain.Chain.Count} segments" +
                       (barrels > 0 ? $", {barrels} via barrel(s))" : ")");
            AcAssumptions = "Assumptions: " + string.Join(" ", report.Assumptions)
                + (traceChain.CopperBridges > 0
                    ? $" {traceChain.CopperBridges} pad/fill crossing(s) composed as straight trace-width bars."
                    : "")
                + (traceChain.PrunedSegments > 0
                    ? $" {traceChain.PrunedSegments} side-branch segment(s) " +
                      $"({traceChain.PrunedLengthMeters * 1e3:g3} mm) carry no current under " +
                      "pad-to-pad drive and were excluded."
                    : "")
                + anchorNote;
            _log.Append($"AC estimate: {AcResult}");
        }
        catch (Exception ex) { _session.ReportError(ex); }
    }
}
