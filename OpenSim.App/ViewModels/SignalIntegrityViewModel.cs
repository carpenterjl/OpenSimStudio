using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Signals;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;

namespace OpenSim.App.ViewModels;

/// <summary>A board net offered for coupled extraction, with its selection toggle.</summary>
public partial class SiNetSelection : ObservableObject
{
    public SiNetSelection(CopperNet net) => Net = net;
    public CopperNet Net { get; }
    public string Label => Net.Label;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The Signal Integrity panel (SI track, Stage S5): wizard-defined coupled microstrip
/// lines → per-unit-length RLGC (2D quasi-static BEM) → the exact frequency-domain MTL
/// network → S-parameters/Touchstone and the periodic-steady-state transient with eye
/// diagrams. Thevenin driver + R∥C receiver, or a full nonlinear IBIS buffer (Stage S11); board
/// net extraction is Stage S6 — v1 geometry is the N-coupled-microstrip wizard.
/// All engine assumptions and typed failures surface verbatim.
/// </summary>
public partial class SignalIntegrityViewModel : ObservableObject
{
    private const int SamplesPerUi = 32;

    private readonly ILogService _log;

    public SignalIntegrityViewModel(ILogService log) => _log = log;

    /// <summary>The stackup and impedance calculator shown above the wizard.</summary>
    public ImpedanceCalculatorViewModel Calculator { get; } = new();

    // ------------------------------------------------------------------
    // Wizard geometry: N identical coupled microstrips on the substrate.
    // ------------------------------------------------------------------
    [ObservableProperty] private int _lineCount = 2;
    [ObservableProperty] private double _traceWidthMm = 0.3;
    [ObservableProperty] private double _traceGapMm = 0.3;
    [ObservableProperty] private double _substrateHeightMm = 0.2;
    [ObservableProperty] private double _siEpsR = 4.4;
    [ObservableProperty] private double _siTanD = 0.02;
    [ObservableProperty] private double _copperThicknessUm = 35;
    [ObservableProperty] private double _lineLengthMm = 50;

    // ------------------------------------------------------------------
    // Driver / receiver (linear Thevenin + R∥C).
    // ------------------------------------------------------------------
    public const string Prbs7 = "PRBS-7";
    public const string Prbs9 = "PRBS-9";
    public const string Prbs11 = "PRBS-11";
    public const string ClockPattern = "Clock (1010…)";
    public ObservableCollection<string> SignalTypes { get; } =
        new() { Prbs7, Prbs9, Prbs11, ClockPattern };

    /// <summary>Nullable so a ComboBox transient null push lands harmlessly.</summary>
    [ObservableProperty] private string? _signalType = Prbs7;
    [ObservableProperty] private int _drivenLine = 1;             // 1-based in the UI
    [ObservableProperty] private double _bitRateGbps = 1.0;
    [ObservableProperty] private double _riseFractionPercent = 25;
    [ObservableProperty] private double _amplitudeVolts = 1.0;
    [ObservableProperty] private double _sourceOhms = 50;
    [ObservableProperty] private double _loadOhms = 50;
    [ObservableProperty] private double _loadPicofarads;
    /// <summary>Drive every OTHER line with a decorrelated PRBS — the crosstalk-closed
    /// victim eye (each aggressor gets its own LFSR seed).</summary>
    [ObservableProperty] private bool _aggressorsEnabled;

    /// <summary>Replace the v1 <c>max(R_dc, R_s√f)</c> per-conductor resistance with the
    /// full proximity-effect filament solve (Stage S8): frequency-dependent N×N R(f) with
    /// current crowding + internal L(f). Default OFF so existing results don't shift.</summary>
    [ObservableProperty] private bool _proximityEffect;

    // ------------------------------------------------------------------
    // IBIS driver (Stage S11): a nonlinear behavioral buffer replaces the Thevenin driver.
    // Coupled lines are solved together in the same nonlinear solve (NonlinearLink.SolveNPort).
    // ------------------------------------------------------------------
    [ObservableProperty] private bool _useIbisDriver;

    /// <summary>Model the receiver with the IBIS buffer's own GND/POWER protection clamps
    /// instead of a plain R∥C load. Off by default: clamps only act where the waveform leaves
    /// the rails, so on a well-terminated link they change nothing, and leaving them off keeps
    /// existing eyes exactly as they were.</summary>
    [ObservableProperty] private bool _useReceiverClamps;
    public ObservableCollection<string> IbisModelNames { get; } = new();
    [ObservableProperty] private string? _selectedIbisModel;
    public ObservableCollection<string> IbisCorners { get; } = new() { "Typ", "Min", "Max" };
    [ObservableProperty] private string? _ibisCorner = "Typ";
    [ObservableProperty] private string _ibisStatus = "";
    private OpenSim.Rf.Si.Ibis.IbisFile? _ibisFile;

    // ------------------------------------------------------------------
    // Board net extraction (Stage S6): take the coupled cross-section from real nets
    // instead of the wizard geometry. When _boardExtraction is set, the RLGC/network the
    // Extract/S-param/eye commands consume comes from the board, not the wizard fields.
    // ------------------------------------------------------------------

    /// <summary>Use the coupled cross-section extracted from the selected board nets rather
    /// than the wizard geometry. Set true by a successful extraction; the wizard fields stay
    /// editable but are ignored while it holds.</summary>
    [ObservableProperty] private bool _useBoardNets;

    /// <summary>Drive the first selected net from its other end: exchanges the network's
    /// near and far ends at the next extraction (the result line states where the near
    /// end is).</summary>
    [ObservableProperty] private bool _swapBoardEnds;

    /// <summary>The importable board nets, each with a selection toggle (pick 2+ parallel
    /// signal nets, then Extract).</summary>
    public ObservableCollection<SiNetSelection> BoardNets { get; } = new();

    [ObservableProperty] private string _boardExtractionResult = "";
    [ObservableProperty] private string _traceCapResult = "";
    [ObservableProperty] private string _dcNetsResult = "";
    [ObservableProperty] private string _layoutCheckResult = "";
    [ObservableProperty] private string _crosstalkScanResult = "";
    /// <summary>Aggressor edge for the board crosstalk scan [ns].</summary>
    [ObservableProperty] private double _scanRiseTimeNs = 0.5;
    [ObservableProperty] private bool _hasBoard;

    /// <summary>How many findings or pairs the panel lists; the log gets all of them.</summary>
    private const int ListedLines = 12;

    /// <summary>Geometric layout checks over the whole board: plane gaps under traces,
    /// reference changes without a return path, via stubs, traces at the board edge.</summary>
    [RelayCommand]
    private async Task CheckLayoutRules()
    {
        if (_board is null || _meshOptions is null)
        {
            LayoutCheckResult = "Import a board first (PCB panel).";
            return;
        }
        LayoutCheckResult = "Checking…";
        try
        {
            var board = _board;
            var stackup = _meshOptions().Stackup;
            var report = await Task.Run(() => OpenSim.Rf.Si.Layout.LayoutRuleChecker.Check(board,
                new OpenSim.Rf.Si.Layout.LayoutCheckOptions { Stackup = stackup }));
            var kinds = Enum.GetValues<OpenSim.Rf.Si.Layout.LayoutFindingKind>()
                .Select(k => $"{report.Count(k)} {k}");
            string head = report.Findings.Count == 0
                ? "No findings."
                : $"{report.Findings.Count} finding(s): {string.Join(", ", kinds)}.";
            var lines = report.Findings.Take(ListedLines).Select(f => $"[{f.Severity}] {f.Message}").ToList();
            if (report.Findings.Count > ListedLines)
                lines.Add($"… and {report.Findings.Count - ListedLines} more in the log.");
            LayoutCheckResult = string.Join(Environment.NewLine,
                new[] { head }.Concat(lines).Concat(report.Notes));
            _log.Append($"SI layout checks: {head}");
            foreach (var f in report.Findings) _log.Append($"  [{f.Severity}] {f.Message}");
        }
        catch (Exception ex)
        {
            LayoutCheckResult = "Layout check failed: " + ex.Message;
        }
    }

    /// <summary>Whole-board crosstalk scan: every two nets running side by side, ranked.</summary>
    [RelayCommand]
    private async Task ScanBoardCrosstalk()
    {
        if (_board is null || _meshOptions is null)
        {
            CrosstalkScanResult = "Import a board first (PCB panel).";
            return;
        }
        CrosstalkScanResult = "Scanning…";
        try
        {
            var board = _board;
            var options = new OpenSim.Rf.Si.Layout.CrosstalkScanOptions
            {
                Stackup = _meshOptions().Stackup,
                RiseTimeSeconds = ScanRiseTimeNs * 1e-9
            };
            var report = await Task.Run(() => OpenSim.Rf.Si.Layout.CrosstalkScan.Run(board, options));
            string head = report.Pairs.Count == 0
                ? "No two nets run side by side within the scan's limits."
                : $"{report.Pairs.Count} coupled pair(s), worst first:";
            var lines = report.Pairs.Take(ListedLines).Select(p => p.Describe()).ToList();
            if (report.Pairs.Count > ListedLines)
                lines.Add($"… and {report.Pairs.Count - ListedLines} more in the log.");
            CrosstalkScanResult = string.Join(Environment.NewLine,
                new[] { head }.Concat(lines).Concat(report.Notes));
            _log.Append($"SI crosstalk scan: {head}");
            foreach (var p in report.Pairs) _log.Append("  " + p.Describe());
        }
        catch (Exception ex)
        {
            CrosstalkScanResult = "Crosstalk scan failed: " + ex.Message;
        }
    }

    private PcbBoard? _board;
    private string? _boardFileName;
    private Func<NetMeshOptions>? _meshOptions;
    private BoardCoupledResult? _boardExtraction;

    /// <summary>Populates the board net list — mirrors the antenna/inductance panels so the
    /// same import feeds every downstream analysis one board. The source file name is only
    /// for the DC-nets CSV report's Board column (the board model itself has no path).</summary>
    public void LoadBoard(PcbBoard board, Func<NetMeshOptions> meshOptions,
        string? sourceFileName = null)
    {
        _board = board;
        _boardFileName = sourceFileName;
        _meshOptions = meshOptions;
        _boardExtraction = null;
        UseBoardNets = false;
        BoardNets.Clear();
        foreach (var net in board.Nets) BoardNets.Add(new SiNetSelection(net));
        HasBoard = board.Nets.Count > 0;
        BoardExtractionResult = "";
        DcNetsResult = "";
        LayoutCheckResult = "";
        CrosstalkScanResult = "";
    }

    [ObservableProperty] private string _rlgcResult = "";
    [ObservableProperty] private string _sParamResult = "";
    [ObservableProperty] private string _eyeResult = "";
    [ObservableProperty] private string _siAssumptions = "";
    [ObservableProperty] private ImageSource? _eyeImage;

    // ------------------------------------------------------------------
    // Shared build steps.
    // ------------------------------------------------------------------

    private CoupledLineCrossSection BuildCrossSection()
    {
        if (UseBoardNets && _boardExtraction?.CrossSection is { } extracted)
            return extracted;
        if (LineCount < 1 || LineCount > 8)
            throw new ArgumentException("Line count must be 1–8 (the wizard's coupled group).");
        double w = TraceWidthMm * 1e-3, s = TraceGapMm * 1e-3;
        var stack = new LayeredStackup(new[]
            { new LayeredStackup.Layer(SiEpsR, SiTanD, SubstrateHeightMm * 1e-3) });
        var traces = new TraceCrossSection[LineCount];
        double pitch = w + s;
        double origin = -(LineCount - 1) * pitch / 2;
        for (int i = 0; i < LineCount; i++)
            traces[i] = TraceCrossSection.Copper(origin + i * pitch, w,
                CopperThicknessUm * 1e-6);
        return new CoupledLineCrossSection(stack, 0, traces);
    }

    private (RlgcResult Rlgc, MtlNetwork Network) BuildNetwork()
    {
        if (UseBoardNets && _boardExtraction is
            { Rlgc: { } rlgcBoard, Network: { } networkBoard, CrossSection: { } sectionBoard })
        {
            if (!ProximityEffect) return (rlgcBoard, networkBoard);
            // The SAME cascade (every coupled stretch and every lead), each section with
            // the proximity providers — not one rebuilt section that drops the leads.
            var perSection = new Dictionary<CoupledLineCrossSection, RlgcResult>(
                ReferenceEqualityComparer.Instance);
            var network = _boardExtraction.BuildNetwork(s => perSection[s] = ExtractRlgc(s));
            return (perSection[sectionBoard], network);
        }
        return Build(BuildCrossSection(), LineLengthMm * 1e-3);
    }

    /// <summary>Extract the RLGC and build the one-section network, optionally attaching the
    /// Stage S8 proximity-effect R(f)/L(f) providers (a filament solve over the band the eye
    /// uses). Off ⇒ the v1 scalar-R model, bit-for-bit.</summary>
    private (RlgcResult, MtlNetwork) Build(CoupledLineCrossSection section, double lengthMeters)
    {
        var rlgc = ExtractRlgc(section);
        return (rlgc, new MtlNetwork(new[] { new MtlSection(rlgc, lengthMeters) }));
    }

    private RlgcResult ExtractRlgc(CoupledLineCrossSection section)
    {
        // The board model: thickness, return-path and crowding loss with its internal
        // inductance, causal dielectric. The proximity option replaces the STRIPS' share of
        // the conductor loss with the filament solve and keeps the plane's.
        var rlgc = RlgcExtractor.Extract(section, RlgcModel.Board);
        if (ProximityEffect)
        {
            double fMax = Math.Max(1e10, BitRateGbps * 1e9 * 20);
            rlgc = ProximityExtractor.Attach(rlgc, ProximityExtractor.Extract(section, 1e3, fMax));
        }
        return rlgc;
    }

    private LineTermination[] Terminations()
    {
        var termination = new LineTermination(SourceOhms, LoadOhms, LoadPicofarads * 1e-12);
        var all = new LineTermination[LineCount];
        Array.Fill(all, termination);
        return all;
    }

    private void ShowAssumptions(RlgcResult rlgc) =>
        SiAssumptions = "Assumptions: " + string.Join(" ", rlgc.Assumptions)
            + (ProximityEffect
                ? " Proximity effect ON: the strips' R(f) and internal L(f) come from the 2D "
                  + "filament solve over a perfect plane (current crowding + skin effect, full "
                  + "N×N); the plane's own loss is added from the incremental-inductance rule."
                : "")
            + " Linear Thevenin driver + R∥C receiver (this run did not use an IBIS buffer).";

    // ------------------------------------------------------------------
    // Commands.
    // ------------------------------------------------------------------

    /// <summary>Extract the coupled cross-section from the selected board nets. On success
    /// the RLGC/network is cached and <see cref="UseBoardNets"/> flips on, so the existing
    /// Extract RLGC / S-parameter / eye commands run on the real board geometry. A typed
    /// failure (non-parallel tangle, pour net, lateral overlap, no overlap, layer change)
    /// surfaces verbatim and leaves the wizard geometry active.</summary>
    [RelayCommand]
    private async Task ExtractFromBoardNets()
    {
        if (_board is null || _meshOptions is null)
        {
            BoardExtractionResult = "Import a board first (PCB panel).";
            return;
        }
        var selected = BoardNets.Where(n => n.IsSelected).Select(n => n.Net).ToList();
        if (selected.Count < 2)
        {
            BoardExtractionResult = "Select at least two parallel signal nets.";
            return;
        }

        BoardExtractionResult = "Extracting…";
        try
        {
            var options = _meshOptions();
            var extraction = await Task.Run(() => BoardCoupledExtractor.Extract(_board, selected,
                new BoardCoupledOptions
                {
                    // The stackup panel's stackup: this layer's copper and its gap's
                    // thickness, εr and tanδ — the object the mesher and PEEC chain read.
                    Stackup = options.Stackup,
                    CopperThicknessMeters = options.CopperThickness,
                    SwapEnds = SwapBoardEnds,
                    Model = RlgcModel.Board,
                }));
            if (extraction.FailureReason is not null)
            {
                _boardExtraction = null;
                UseBoardNets = false;
                BoardExtractionResult = $"Not a coupled line: {extraction.FailureReason}";
                _log.Append($"SI board extraction failed — {extraction.FailureReason}");
                return;
            }

            _boardExtraction = extraction;
            UseBoardNets = true;
            LineCount = extraction.CrossSection!.Traces.Count;      // terminations follow the real count
            DrivenLine = Math.Clamp(DrivenLine, 1, LineCount);
            var widths = string.Join(", ",
                extraction.CrossSection.Traces.Select(t => $"{t.WidthMeters * 1e3:g3}"));
            BoardExtractionResult =
                $"{LineCount} coupled conductors, coupled length = {extraction.CoupledLengthMeters * 1e3:g4} mm "
                + $"in {extraction.Sections.Count(s => s.Coupled is not null)} section(s), routed lengths ["
                + string.Join(", ", extraction.RoutedLengthsMeters.Select(l => $"{l * 1e3:g4}"))
                + $"] mm, widths [{widths}] mm, near end of {selected[0].Label} at ("
                + $"{extraction.NearEnds[0].X * 1e3:g4}, {extraction.NearEnds[0].Y * 1e3:g4}) mm"
                + " — RLGC/S-params/eye now use the board geometry.";
            ShowAssumptions(extraction.Rlgc!);
            SiAssumptions = "Assumptions: " + string.Join(" ", extraction.Assumptions);
            _log.Append($"SI board extraction — {BoardExtractionResult}");
        }
        catch (Exception ex)
        {
            _boardExtraction = null;
            UseBoardNets = false;
            BoardExtractionResult = $"Not solvable: {ex.Message}";
        }
    }

    /// <summary>Capacitance to the reference plane for EVERY selected board net — one net
    /// is enough, unlike the coupled extraction: this is a per-net electrostatic number.
    /// The net's FULL routed copper counts (bends and branches — all copper holds charge),
    /// priced as Σ C′(width, gap) × length by the same 2D BEM, plus parallel-plate pad
    /// terms. Typed failures (pour nets, no adjacent gap) surface verbatim per net.</summary>
    [RelayCommand]
    private async Task ComputeTraceCapacitance()
    {
        if (_board is null || _meshOptions is null)
        {
            TraceCapResult = "Import a board first (PCB panel).";
            return;
        }
        var selected = BoardNets.Where(n => n.IsSelected).Select(n => n.Net).ToList();
        if (selected.Count == 0)
        {
            TraceCapResult = "Select at least one net.";
            return;
        }

        TraceCapResult = "Computing…";
        try
        {
            var board = _board;
            var panel = _meshOptions();
            var options = new BoardCoupledOptions
            {
                Stackup = panel.Stackup,
                CopperThicknessMeters = panel.CopperThickness,
            };
            var results = await Task.Run(() => selected
                .Select(net => (Net: net, Result: TraceCapacitanceExtractor.Extract(board, net, options)))
                .ToList());

            var summaries = new List<string>();
            IReadOnlyList<string>? assumptions = null;
            foreach (var (net, r) in results)
            {
                if (r.FailureReason is not null)
                {
                    summaries.Add($"{net.Label}: not computable — {r.FailureReason}");
                    _log.Append($"SI trace C: net '{net.Label}' — {r.FailureReason}");
                    continue;
                }
                double lengthMm = r.Groups.Sum(g => g.LengthMeters) * 1e3;
                string line = $"{net.Label}: C ≈ {r.TotalFarads * 1e12:g4} pF to ground "
                    + $"(traces {r.TraceFarads * 1e12:g4} pF over {lengthMm:g4} mm"
                    + (r.PadCount > 0
                        ? $", pads +{r.PadFarads * 1e12:g4} pF plate ({r.PadCount})" : "")
                    + $"; {r.Groups.Count} cross-section group(s))";
                summaries.Add(line);
                _log.Append($"SI trace C: {line}");
                assumptions ??= r.Assumptions;
            }
            TraceCapResult = string.Join("\n", summaries);
            if (assumptions is not null)
                SiAssumptions = "Assumptions: " + string.Join(" ", assumptions);
        }
        catch (Exception ex) { TraceCapResult = $"Not computable: {ex.Message}"; }
    }

    /// <summary>The board-wide DC screen: every net with at least two COMPONENT PINS
    /// (pads that carry a component reference — an IPC-2581 import, or Gerber files with
    /// X2 %TO.P attributes; a Gerber set without them has none, so every net is skipped and
    /// counted) gets its pin-pair resistances from the nodal
    /// network on the trace graph (branches AND parallel paths — the case the inductance
    /// chain refuses), the net's total C to the reference plane, and the lumped
    /// τ = R·C screen, written to a CSV report via a save dialog. Non-conforming nets
    /// appear as typed note ROWS in the file; the panel and log carry one summary line —
    /// hundreds of per-net log lines would help nobody.</summary>
    [RelayCommand]
    private async Task EvaluateDcNets()
    {
        if (_board is null || _meshOptions is null)
        {
            DcNetsResult = "Import a board first (PCB panel).";
            return;
        }

        DcNetsResult = "Evaluating…";
        try
        {
            var board = _board;
            string boardName = _boardFileName ?? "board";
            var meshOptions = _meshOptions();
            var options = new BoardCoupledOptions
            {
                Stackup = meshOptions.Stackup,
                CopperThicknessMeters = meshOptions.CopperThickness,
            };
            var report = await Task.Run(() =>
                DcNetEvaluator.Evaluate(board, meshOptions, options, boardName));

            string text = $"{report.NetsEvaluated} net(s) evaluated — "
                + $"{report.Rows.Count} component-pin pair(s) reported (R and C), "
                + $"{report.PairsOmitted} omitted; "
                + $"{report.NetsSkipped} skipped (<2 component pins), {report.NetsFailed} not computable";

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"{Path.GetFileNameWithoutExtension(boardName)}_dc-nets.csv",
                Filter = "CSV report|*.csv|All files|*.*",
                Title = "Save DC net evaluation (CSV)"
            };
            if (dialog.ShowDialog() == true)
            {
                // UTF-8 with BOM so spreadsheet apps read the preamble's symbols right.
                File.WriteAllText(dialog.FileName, DcNetReportCsv.Write(report),
                    System.Text.Encoding.UTF8);
                text += $"; saved {Path.GetFileName(dialog.FileName)}";
            }
            else
            {
                text += "; not saved";
            }
            DcNetsResult = text;
            _log.Append($"SI DC nets ({boardName}): {text}");
        }
        catch (Exception ex) { DcNetsResult = $"Not computable: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ExtractRlgc()
    {
        RlgcResult = "Extracting…";
        try
        {
            var (rlgc, _) = await Task.Run(BuildNetwork);
            // Impedances from L and C together: Z0 for one line; even, odd, differential
            // and common for a symmetric pair; the characteristic impedance matrix
            // otherwise (the "Z0e/Z0o" of lines 1 and 2 mean nothing for three lines or
            // for two unequal ones).
            string text = LineReadout.Describe(rlgc);
            RlgcResult = text;
            ShowAssumptions(rlgc);
            _log.Append($"SI: RLGC extracted — {text}");
        }
        catch (Exception ex) { RlgcResult = $"Not solvable: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ComputeSParameters()
    {
        SParamResult = "Solving…";
        try
        {
            double nyquist = BitRateGbps * 1e9 / 2;
            var (rlgc, network) = BuildNetwork();
            var (text, freqs, matrices) = await Task.Run(() =>
            {
                // DC to 2× Nyquist — the band an eye at this bit rate uses — on a uniform
                // grid fine enough for the line's delay (at most π/8 of phase per point).
                // The DC point is taken at 1 Hz when the network cannot be evaluated at 0.
                double delay = network.TotalLengthMeters * Math.Sqrt(
                    rlgc.InductanceHenriesPerMeter[0, 0] * rlgc.CapacitanceFaradsPerMeter[0, 0]);
                var frequencies = LineReadout.ExportFrequencies(2 * nyquist, delay);
                int points = frequencies.Length;
                var scattering = new Complex[points][,];
                for (int k = 0; k < points; k++)
                    scattering[k] = network.Scattering(Math.Max(frequencies[k], 1.0));
                var s = network.Scattering(nyquist);
                int n = network.ConductorCount;
                int driven = Math.Clamp(DrivenLine - 1, 0, n - 1);
                string summary = $"At Nyquist {nyquist / 1e9:g3} GHz: |S11| = "
                    + $"{s[driven, driven].Magnitude:g3}, |S21| = "
                    + $"{s[n + driven, driven].Magnitude:g3}";
                if (n > 1)
                {
                    int victim = driven == 0 ? 1 : 0;
                    summary += $", NEXT |S{victim + 1}{driven + 1}| = "
                        + $"{s[victim, driven].Magnitude:g3}, FEXT = "
                        + $"{s[n + victim, driven].Magnitude:g3}";
                }
                return (summary, frequencies, scattering);
            });

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"coupled_lines.s{2 * network.ConductorCount}p",
                Filter = "Touchstone|*.s*p|All files|*.*",
                Title = "Export S-parameters (Touchstone)"
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, TouchstoneWriter.Write(freqs, matrices,
                    portNames: LineReadout.PortNames(network.ConductorCount)));
                text += $"; exported {Path.GetFileName(dialog.FileName)} ({freqs.Length} points)";
            }
            SParamResult = text;
            ShowAssumptions(rlgc);
            _log.Append($"SI: {text}");
        }
        catch (Exception ex) { SParamResult = $"Not solvable: {ex.Message}"; }
    }

    /// <summary>Load an IBIS (.ibs) file and populate the model picker; selecting a model +
    /// ticking "Use IBIS driver" routes the eye through the nonlinear engine (single line).</summary>
    [RelayCommand]
    private void LoadIbis()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "IBIS model (*.ibs)|*.ibs|All files|*.*",
            Title = "Select an IBIS (.ibs) model file",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _ibisFile = new OpenSim.Rf.Si.Ibis.IbisParser().ParseFile(dialog.FileName);
            IbisModelNames.Clear();
            foreach (var m in _ibisFile.Models) IbisModelNames.Add(m.Name);
            SelectedIbisModel = _ibisFile.Models.FirstOrDefault(m => m.IsOutput)?.Name
                ?? IbisModelNames.FirstOrDefault();
            UseIbisDriver = SelectedIbisModel is not null;
            IbisStatus = $"Loaded {Path.GetFileName(dialog.FileName)}: {_ibisFile.Models.Count} model(s)"
                + (_ibisFile.Warnings.Count > 0 ? $", {_ibisFile.Warnings.Count} skipped keyword(s)" : "");
            _log.Append($"SI: IBIS — {IbisStatus}");
            // The warning STRINGS, not just the count: each one names declared content this
            // reader did not consume, which is the difference between "your file has 6 skipped
            // keywords" and knowing that the one that matters is a package model.
            const int maxShown = 10;
            for (int i = 0; i < Math.Min(maxShown, _ibisFile.Warnings.Count); i++)
                _log.Append($"SI: {_ibisFile.Warnings[i]}");
            if (_ibisFile.Warnings.Count > maxShown)
                _log.Append($"SI: IBIS — +{_ibisFile.Warnings.Count - maxShown} more warning(s).");
        }
        catch (Exception ex) { IbisStatus = $"Not readable: {ex.Message}"; _ibisFile = null; }
    }

    [RelayCommand]
    private async Task RunEyeDiagram()
    {
        EyeResult = "Solving…";
        try
        {
            var (rlgc, network) = BuildNetwork();
            if (UseIbisDriver && _ibisFile is not null && SelectedIbisModel is not null)
            {
                await RunIbisEye(rlgc, network);
                return;
            }
            var terminations = Terminations();
            int driven = Math.Clamp(DrivenLine - 1, 0, LineCount - 1);
            double dt = 1.0 / (BitRateGbps * 1e9 * SamplesPerUi);
            double rise = Math.Clamp(RiseFractionPercent / 100.0, 0, 1);
            bool aggressors = AggressorsEnabled && LineCount > 1;
            string? type = SignalType;

            var (eye, peakXtalk) = await Task.Run(() =>
            {
                var sources = new double[LineCount][];
                var (victimWave, victimBits) = BuildPattern(type, rise, seed: 1);
                sources[driven] = victimWave;
                if (aggressors)
                    for (int i = 0; i < LineCount; i++)
                        if (i != driven)
                            sources[i] = BuildPattern(type, rise, seed: (uint)(1000 + i)).Wave;
                var transient = TransientLink.SolvePeriodic(
                    network, terminations, sources, dt);

                // The quiet-line peak (aggressors only, victim silent) names the raw
                // crosstalk even when the victim is driven.
                double xtalk = 0;
                if (aggressors)
                {
                    var quietSources = (double[]?[])sources.Clone();
                    quietSources[driven] = null;
                    var coupled = TransientLink.SolvePeriodic(
                        network, terminations, quietSources, dt);
                    xtalk = coupled.FarVoltages[driven].Max(Math.Abs);
                }
                return (EyeDiagram.Fold(transient.FarVoltages[driven], SamplesPerUi, dt,
                    victimBits), xtalk);
            });

            EyeImage = RenderEye(eye);
            EyeResult = $"Eye at {BitRateGbps:g3} Gb/s ({type}): {EyeMetrics(eye)}"
                + (aggressors ? $"; aggressor crosstalk peak = {peakXtalk * 1e3:g3} mV" : "");
            ShowAssumptions(rlgc);
            _log.Append($"SI: {EyeResult}");
        }
        catch (Exception ex) { EyeResult = $"Not solvable: {ex.Message}"; EyeImage = null; }
    }

    /// <summary>The IBIS eye: the nonlinear behavioral driver (Stage S11) into the single-line
    /// channel + R∥C receiver, folded like the linear path. Requires a single conductor (the
    /// nonlinear N-port engine: every port is an unknown, so coupled lines and clamped
    /// receivers are solved together rather than being out of scope).</summary>
    private async Task RunIbisEye(RlgcResult rlgc, MtlNetwork network)
    {
        var model = _ibisFile!.Model(SelectedIbisModel!);
        if (!model.IsOutput)
        {
            EyeResult = $"IBIS model '{model.Name}' is an input/terminator, not an output driver.";
            return;
        }
        var corner = IbisCorner == "Min" ? OpenSim.Rf.Si.Ibis.IbisCornerSelection.Min
            : IbisCorner == "Max" ? OpenSim.Rf.Si.Ibis.IbisCornerSelection.Max
            : OpenSim.Rf.Si.Ibis.IbisCornerSelection.Typ;
        double dt = 1.0 / (BitRateGbps * 1e9 * SamplesPerUi);
        var bits = IbisBits(SignalType, seed: 1);
        int lines = network.ConductorCount;
        // The line picked as "Driven line" carries the pattern and is the one whose eye is
        // shown; every other coupled line is an aggressor driven by the same buffer on its own
        // decorrelated data (or held quiet when the aggressor toggle is off).
        int driven = Math.Clamp(DrivenLine - 1, 0, lines - 1);
        bool aggressorsOn = AggressorsEnabled;
        string? signalType = SignalType;
        // One warm-up period is enough once the period is longer than the channel's memory
        // (the FIR is at most 8192 taps); short patterns keep the customary four.
        int warmup = bits.Length * SamplesPerUi > 8192 ? 1 : 4;
        var (eye, note, switchingSource, switchingWarnings) = await Task.Run(() =>
        {
            var near = new INonlinearDriver[lines];
            var far = new INonlinearDriver[lines];
            string source = "";
            IReadOnlyList<string> warns = Array.Empty<string>();
            for (int i = 0; i < lines; i++)
            {
                var pattern = i == driven ? bits
                    : aggressorsOn ? IbisBits(signalType, seed: (uint)(1000 + i))
                    : new bool[bits.Length];
                near[i] = IbisDriver.FromBits(model, corner, pattern, SamplesPerUi, dt,
                    out string src, out var w);
                if (i == driven) { source = src; warns = w; }
                // The receiver: the buffer's own protection clamps when asked for, else the
                // plain R∥C load. Clamps only matter where the waveform leaves the rails.
                far[i] = UseReceiverClamps
                    ? new IbisReceiverElement(model, corner, LoadOhms)
                    : new LinearLoadElement(LoadOhms, LoadPicofarads * 1e-12);
            }
            var result = NonlinearLink.SolveNPort(network, near, far,
                bits.Length * SamplesPerUi, dt, warmupPeriods: warmup);
            var folded = EyeDiagram.Fold(result.FarVolts[driven], SamplesPerUi, dt, bits);
            return (folded, $"channel FIR {result.ChannelMemorySamples} taps, "
                + $"tail {result.TailEnergyFraction:e1}"
                + (lines > 1 ? $", line {driven + 1} of {lines} coupled" : ""), source, warns);
        });
        foreach (var w in switchingWarnings) _log.Append($"SI: IBIS — {w}");
        EyeImage = RenderEye(eye);
        EyeResult = $"IBIS eye ({model.Name}, {IbisCorner}) at {BitRateGbps:g3} Gb/s: "
            + $"{EyeMetrics(eye)} ({note})";
        SiAssumptions = "Assumptions: " + string.Join(" ", rlgc.Assumptions)
            + " Nonlinear IBIS driver (V-I tables; C_comp and the load capacitance carried in "
            + "the channel reduction; edges on the continuous timeline) into "
            + (lines > 1
                ? $"the {lines}-line coupled channel (matrix FIR, all lines solved together). "
                : "the one-line channel (the same N-port engine, with one line). ")
            + $"Switching profile: {switchingSource}. "
            + (UseReceiverClamps
                ? "Receiver = the buffer's own GND/POWER protection clamps (nonlinear) plus the "
                  + "termination."
                : "Receiver = linear R∥C (tick 'Receiver clamps' to include the buffer's "
                  + "protection diodes).");
        _log.Append($"SI: {EyeResult}");
    }

    /// <summary>The eye's numbers as text. The height is measured against the transmitted
    /// bits, so it can be zero or negative; that is a closed eye and is said so.</summary>
    private static string EyeMetrics(EyeDiagram eye) =>
        eye.IsClosed
            ? $"CLOSED — the lowest received one is {-eye.EyeHeight * 1e3:g3} mV below the highest "
              + "received zero at the best sampling phase; "
              + $"jitter p-p = {eye.JitterPeakToPeakSeconds * 1e12:g3} ps"
            : $"height = {eye.EyeHeight:g3} V, width = {eye.EyeWidthSeconds * 1e12:g3} ps "
              + $"({eye.EyeWidthSeconds / eye.UnitIntervalSeconds:P0} of UI), "
              + $"jitter p-p = {eye.JitterPeakToPeakSeconds * 1e12:g3} ps";

    /// <summary>The bit pattern for the chosen signal type. The clock is 64 alternating bits; a
    /// different seed gives it the opposite phase rather than a different sequence.</summary>
    private static bool[] IbisBits(string? type, uint seed)
    {
        if (type == ClockPattern)
            return Enumerable.Range(0, 64).Select(i => (i + seed) % 2 == 1).ToArray();
        int order = type == Prbs9 ? 9 : type == Prbs11 ? 11 : 7;
        return PrbsGenerator.Generate(order, (1 << order) - 1, seed);
    }

    private (double[] Wave, bool[] Bits) BuildPattern(string? type, double rise, uint seed)
    {
        var bits = IbisBits(type, seed);
        return (SourceWaveform.Trapezoid(bits, SamplesPerUi, rise, AmplitudeVolts, 0), bits);
    }

    /// <summary>The eye persistence bitmap: density → a dark-to-hot ramp with log
    /// compression (single hits stay visible next to the piled-up plateaus).</summary>
    private static ImageSource RenderEye(EyeDiagram eye)
    {
        const int heightBins = 128;
        var map = eye.DensityMap(heightBins);
        int width = map.GetLength(0);
        int max = 1;
        foreach (var v in map) max = Math.Max(max, v);
        double logMax = Math.Log(1 + max);

        var bitmap = new WriteableBitmap(width, heightBins, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new uint[width * heightBins];
        for (int x = 0; x < width; x++)
            for (int y = 0; y < heightBins; y++)
            {
                double u = map[x, y] == 0 ? 0 : Math.Log(1 + map[x, y]) / logMax;
                // Black → green → yellow ramp (the classic scope persistence look).
                byte r = (byte)(255 * Math.Clamp(2 * u - 1, 0, 1));
                byte g = (byte)(255 * Math.Clamp(1.6 * u, 0, 1));
                pixels[(heightBins - 1 - y) * width + x] =
                    0xFF000000u | ((uint)r << 16) | ((uint)g << 8);
            }
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, heightBins),
            pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
