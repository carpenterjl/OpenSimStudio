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
    [ObservableProperty] private bool _hasBoard;

    private PcbBoard? _board;
    private Func<NetMeshOptions>? _meshOptions;
    private BoardCoupledResult? _boardExtraction;

    /// <summary>Populates the board net list — mirrors the antenna/inductance panels so the
    /// same import feeds every downstream analysis one board.</summary>
    public void LoadBoard(PcbBoard board, Func<NetMeshOptions> meshOptions)
    {
        _board = board;
        _meshOptions = meshOptions;
        _boardExtraction = null;
        UseBoardNets = false;
        BoardNets.Clear();
        foreach (var net in board.Nets) BoardNets.Add(new SiNetSelection(net));
        HasBoard = board.Nets.Count > 0;
        BoardExtractionResult = "";
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
        if (UseBoardNets && _boardExtraction is { Rlgc: { } rlgcBoard, Network: { } networkBoard })
            return (rlgcBoard, networkBoard);
        var rlgc = RlgcExtractor.Extract(BuildCrossSection());
        return (rlgc, new MtlNetwork(new[] { new MtlSection(rlgc, LineLengthMm * 1e-3) }));
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
            + " Linear Thevenin driver + R∥C receiver (IBIS models are a named follow-up); "
            + "uniform coupled section (board net extraction is the next SI stage).";

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

    [RelayCommand]
    private async Task RunEyeDiagram()
    {
        EyeResult = "Solving…";
        try
        {
            var (rlgc, network) = BuildNetwork();
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
