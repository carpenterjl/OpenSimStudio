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
/// diagrams. Thevenin driver + R∥C receiver (IBIS import is a named follow-up); board
/// net extraction is Stage S6 — v1 geometry is the N-coupled-microstrip wizard.
/// All engine assumptions and typed failures surface verbatim.
/// </summary>
public partial class SignalIntegrityViewModel : ObservableObject
{
    private const int SamplesPerUi = 32;

    private readonly ILogService _log;

    public SignalIntegrityViewModel(ILogService log) => _log = log;

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
    // Single driven line only (the nonlinear engine); coupled lines keep the linear path.
    // ------------------------------------------------------------------
    [ObservableProperty] private bool _useIbisDriver;
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

    /// <summary>The importable board nets, each with a selection toggle (pick 2+ parallel
    /// signal nets, then Extract).</summary>
    public ObservableCollection<SiNetSelection> BoardNets { get; } = new();

    [ObservableProperty] private string _boardExtractionResult = "";
    [ObservableProperty] private string _traceCapResult = "";
    [ObservableProperty] private string _dcNetsResult = "";
    [ObservableProperty] private bool _hasBoard;

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
            return ProximityEffect
                ? Build(sectionBoard, _boardExtraction.CoupledLengthMeters)
                : (rlgcBoard, networkBoard);
        return Build(BuildCrossSection(), LineLengthMm * 1e-3);
    }

    /// <summary>Extract the RLGC and build the one-section network, optionally attaching the
    /// Stage S8 proximity-effect R(f)/L(f) providers (a filament solve over the band the eye
    /// uses). Off ⇒ the v1 scalar-R model, bit-for-bit.</summary>
    private (RlgcResult, MtlNetwork) Build(CoupledLineCrossSection section, double lengthMeters)
    {
        var rlgc = RlgcExtractor.Extract(section);
        if (ProximityEffect)
        {
            double fMax = Math.Max(1e10, BitRateGbps * 1e9 * 20);
            var prox = ProximityExtractor.Extract(section, 1e3, fMax);
            rlgc = rlgc with
            {
                ResistanceMatrixOhmsPerMeter = prox.ResistanceMatrix,
                InternalInductanceHenriesPerMeter = prox.InternalInductance,
            };
        }
        return (rlgc, new MtlNetwork(new[] { new MtlSection(rlgc, lengthMeters) }));
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
                ? " Proximity effect ON: R(f) and internal L(f) from the 2D filament solve "
                  + "(current crowding + skin effect, full N×N)."
                : " R = max(R_dc, R_s√f) per conductor (enable Proximity effect for the "
                  + "filament R(f)/L(f)).")
            + " Linear Thevenin driver + R∥C receiver (IBIS models are a named follow-up).";

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
                    CopperThicknessMeters = options.CopperThickness,
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
                $"{LineCount} coupled conductors, section = {extraction.CoupledLengthMeters * 1e3:g4} mm, "
                + $"widths [{widths}] mm — RLGC/S-params/eye now use the board geometry.";
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
            var options = new BoardCoupledOptions
            {
                CopperThicknessMeters = _meshOptions().CopperThickness,
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

    /// <summary>The board-wide DC screen: EVERY net with ≥2 pads (fewer is an import
    /// artifact — skipped and counted) gets its pad-pair resistances from the nodal
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
                CopperThicknessMeters = meshOptions.CopperThickness,
            };
            var report = await Task.Run(() =>
                DcNetEvaluator.Evaluate(board, meshOptions, options, boardName));

            string text = $"{report.NetsEvaluated} net(s) evaluated — "
                + $"{report.Rows.Count} pad pair(s) reported (R and C), "
                + $"{report.PairsOmitted} omitted; "
                + $"{report.NetsSkipped} skipped (<2 pads), {report.NetsFailed} not computable";

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
            double c = rlgc.CapacitanceFaradsPerMeter[0, 0];
            double cAir = rlgc.AirCapacitanceFaradsPerMeter[0, 0];
            double z0 = 1 / (299792458.0 * Math.Sqrt(c * cAir));
            string text = $"C11 = {c * 1e12:g4} pF/m, L11 = "
                + $"{rlgc.InductanceHenriesPerMeter[0, 0] * 1e9:g4} nH/m, "
                + $"Z0 = {z0:g4} Ω, ε_eff = {c / cAir:g4}, "
                + $"R_dc = {rlgc.ResistanceDcOhmsPerMeter[0]:g4} Ω/m";
            if (LineCount > 1)
            {
                double cm = rlgc.CapacitanceFaradsPerMeter[0, 1];
                double ce = c + cm, co = c - cm;
                double ceAir = cAir + rlgc.AirCapacitanceFaradsPerMeter[0, 1];
                double coAir = cAir - rlgc.AirCapacitanceFaradsPerMeter[0, 1];
                text += $"; coupling C12/C11 = {cm / c:P1}, "
                    + $"Z0e = {1 / (299792458.0 * Math.Sqrt(ce * ceAir)):g4} Ω, "
                    + $"Z0o = {1 / (299792458.0 * Math.Sqrt(co * coAir)):g4} Ω";
            }
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
                // A linear sweep to 2× Nyquist — the band an eye at this bit rate uses.
                const int points = 40;
                var frequencies = new double[points];
                var scattering = new Complex[points][,];
                for (int k = 0; k < points; k++)
                {
                    frequencies[k] = (k + 1) * 2 * nyquist / points;
                    scattering[k] = network.Scattering(frequencies[k]);
                }
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
                File.WriteAllText(dialog.FileName, TouchstoneWriter.Write(freqs, matrices));
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
                sources[driven] = BuildPattern(type, rise, seed: 1);
                if (aggressors)
                    for (int i = 0; i < LineCount; i++)
                        if (i != driven)
                            sources[i] = BuildPattern(type, rise, seed: (uint)(1000 + i));
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
                return (EyeDiagram.Fold(transient.FarVoltages[driven], SamplesPerUi, dt), xtalk);
            });

            EyeImage = RenderEye(eye);
            EyeResult = $"Eye at {BitRateGbps:g3} Gb/s ({type}): height = {eye.EyeHeight:g3} V, "
                + $"width = {eye.EyeWidthSeconds * 1e12:g3} ps "
                + $"({eye.EyeWidthSeconds / eye.UnitIntervalSeconds:P0} of UI), "
                + $"jitter p-p = {eye.JitterPeakToPeakSeconds * 1e12:g3} ps"
                + (aggressors ? $"; aggressor crosstalk peak = {peakXtalk * 1e3:g3} mV" : "");
            ShowAssumptions(rlgc);
            _log.Append($"SI: {EyeResult}");
        }
        catch (Exception ex) { EyeResult = $"Not solvable: {ex.Message}"; EyeImage = null; }
    }

    /// <summary>The IBIS eye: the nonlinear behavioral driver (Stage S11) into the single-line
    /// channel + R∥C receiver, folded like the linear path. Requires a single conductor (the
    /// nonlinear engine is single-line; coupled lines keep the linear Thevenin driver).</summary>
    private async Task RunIbisEye(RlgcResult rlgc, MtlNetwork network)
    {
        if (network.ConductorCount != 1)
        {
            EyeResult = "IBIS driver is single-line only (nonlinear crosstalk is a named "
                + "follow-up). Set the line count to 1, or untick 'Use IBIS driver' for the "
                + "linear Thevenin driver on coupled lines.";
            return;
        }
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
        var receiver = new NonlinearReceiver(LoadOhms, LoadPicofarads * 1e-12);
        var bits = IbisBits(SignalType);
        var (eye, note) = await Task.Run(() =>
        {
            var driver = IbisDriver.FromBits(model, corner, bits, SamplesPerUi, dt);
            var result = NonlinearLink.Solve(network, driver, receiver, bits, SamplesPerUi, dt);
            var folded = EyeDiagram.Fold(result.ReceiverVolts, SamplesPerUi, dt);
            return (folded, $"channel FIR {result.ChannelMemorySamples} taps, "
                + $"tail {result.TailEnergyFraction:e1}");
        });
        EyeImage = RenderEye(eye);
        EyeResult = $"IBIS eye ({model.Name}, {IbisCorner}) at {BitRateGbps:g3} Gb/s: "
            + $"height = {eye.EyeHeight:g3} V, width = {eye.EyeWidthSeconds * 1e12:g3} ps "
            + $"({eye.EyeWidthSeconds / eye.UnitIntervalSeconds:P0} of UI), "
            + $"jitter p-p = {eye.JitterPeakToPeakSeconds * 1e12:g3} ps ({note})";
        SiAssumptions = "Assumptions: " + string.Join(" ", rlgc.Assumptions)
            + " Nonlinear IBIS driver (V-I tables + ramp switching, C_comp backward-Euler) into "
            + "the single-line channel FIR + R∥C receiver; nonlinear receiver clamps and coupled "
            + "IBIS crosstalk are named follow-ups.";
        _log.Append($"SI: {EyeResult}");
    }

    private static bool[] IbisBits(string? type)
    {
        if (type == ClockPattern) return Enumerable.Range(0, 64).Select(i => i % 2 == 0).ToArray();
        int order = type == Prbs9 ? 9 : type == Prbs11 ? 11 : 7;
        return PrbsGenerator.Generate(order, (1 << order) - 1, seed: 1);
    }

    private double[] BuildPattern(string? type, double rise, uint seed)
    {
        double amplitude = AmplitudeVolts;
        if (type == ClockPattern)
            return SourceWaveform.Clock(64, SamplesPerUi, rise, amplitude, 0);
        int order = type == Prbs9 ? 9 : type == Prbs11 ? 11 : 7;
        var bits = PrbsGenerator.Generate(order, (1 << order) - 1, seed);
        return SourceWaveform.Trapezoid(bits, SamplesPerUi, rise, amplitude, 0);
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
