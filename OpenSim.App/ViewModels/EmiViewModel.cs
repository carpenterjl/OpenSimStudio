using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls;
using OpenSim.App.Services;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Emi;
using OpenSim.Rf.Extraction;

namespace OpenSim.App.ViewModels;

/// <summary>A limit line offered in a picker.</summary>
public sealed record EmiLimitChoice(string Label, EmiLimitLine Line);

/// <summary>
/// EMI pre-compliance of a hard-switched converter (Feature 14, FU-41) — the study the command
/// line's <c>emi</c> job runs (<see cref="EmiStudy"/>): a buck's trapezoidal switch current and node
/// voltage, the input capacitor, an optional LC filter and Y capacitors, the hot loop's ringing and
/// diode recovery, an isolated converter's interwinding path; the conducted estimate through two
/// LISNs against a limit line, and the comparative radiated estimate of the hot loop and the input
/// cable. With an imported board, the hot loop's inductance, resistance and area and the switch
/// node's capacitance are read off the copper (<see cref="ConverterBoard"/>) and add to the values
/// typed here. The FCC Part 15 limit lines are read from the regulation's text; the CISPR 32
/// lines are typed as commonly reproduced and labelled so (<see cref="EmiLimitLine.Source"/>).
/// </summary>
public partial class EmiViewModel : ObservableObject
{
    private readonly ILogService _log;
    private PcbBoard? _board;
    private string? _boardFileName;
    private Func<NetMeshOptions>? _meshOptions;
    private ConverterBoardReading? _reading;
    private EmiStudyResult? _solved;

    public EmiViewModel(ILogService log)
    {
        _log = log;
        foreach (var line in EmiLimits.All)
        {
            if (line.Unit == "dBµV") ConductedLimits.Add(new EmiLimitChoice(line.Name, line));
            else RadiatedLimits.Add(new EmiLimitChoice(line.Name, line));
        }
        _conductedLimit = ConductedLimits.FirstOrDefault(c => ReferenceEquals(c.Line, EmiLimits.ConductedClassBQuasiPeak));
        _radiatedLimit = RadiatedLimits.FirstOrDefault(c => ReferenceEquals(c.Line, EmiLimits.RadiatedClassB3m));
    }

    // ---- The converter (panel units) ----
    [ObservableProperty] private double _inputVolts = 12;
    [ObservableProperty] private double _outputAmps = 3;
    [ObservableProperty] private double _switchingKhz = 500;
    [ObservableProperty] private double _dutyPercent = 40;
    [ObservableProperty] private double _riseNs = 10;
    [ObservableProperty] private double _fallNs = 10;
    /// <summary>Switch node to earth — the heatsink tab, the case: the common-mode path [pF].</summary>
    [ObservableProperty] private double _switchNodeToEarthPf = 20;
    [ObservableProperty] private double _inputMicrofarads = 10;
    [ObservableProperty] private double _inputEsrMilliohms = 5;
    [ObservableProperty] private double _inputEslNanohenries = 1;

    // ---- Filter (0 µH = none) and Y capacitors ----
    [ObservableProperty] private double _filterMicrohenries;
    [ObservableProperty] private double _filterMilliohms;
    [ObservableProperty] private double _filterMicrofarads;
    [ObservableProperty] private double _filterEsrMilliohms;
    [ObservableProperty] private double _filterEslNanohenries;
    [ObservableProperty] private double _yNanofarads;

    // ---- Hot loop, ringing, recovery ----
    [ObservableProperty] private double _hotLoopNanohenries;
    [ObservableProperty] private double _hotLoopMilliohms;
    [ObservableProperty] private double _switchNodeToReturnPf;
    [ObservableProperty] private double _recoveryNanocoulombs;
    [ObservableProperty] private double _recoverySoftness = 1;

    // ---- Isolated converter's interwinding path ----
    [ObservableProperty] private double _interwindingPf;
    [ObservableProperty] private double _secondaryToEarthPf;
    [ObservableProperty] private double _secondaryYPf;

    // ---- Radiated (comparative) ----
    /// <summary>The hot loop's area [mm²]; 0 takes the board's when the loop was read off it.</summary>
    [ObservableProperty] private double _loopAreaMm2 = 100;
    [ObservableProperty] private double _cableLengthM = 1;
    [ObservableProperty] private double _distanceM = 3;

    // ---- Limits ----
    public ObservableCollection<EmiLimitChoice> ConductedLimits { get; } = new();
    public ObservableCollection<EmiLimitChoice> RadiatedLimits { get; } = new();
    [ObservableProperty] private EmiLimitChoice? _conductedLimit;
    [ObservableProperty] private EmiLimitChoice? _radiatedLimit;

    // ---- The board ----
    public ObservableCollection<CopperNet> Nets { get; } = new();
    [ObservableProperty] private CopperNet? _inputNet;
    [ObservableProperty] private CopperNet? _returnNet;
    [ObservableProperty] private CopperNet? _switchNodeNet;
    [ObservableProperty] private string _inputCapacitorRef = "";
    [ObservableProperty] private string _highSideRef = "";
    /// <summary>The low-side switch or diode; blank = the high-side part (an integrated converter).</summary>
    [ObservableProperty] private string _lowSideRef = "";
    /// <summary>Plane mesh pitch for the loop's PEEC solve [mm]; 0 = graded (fine, slower).</summary>
    [ObservableProperty] private double _planePitchMm;
    [ObservableProperty] private bool _includeBoard;
    [ObservableProperty] private string _boardResult = "";
    [ObservableProperty] private bool _hasBoard;

    // ---- Results ----
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _assumptions = "";
    [ObservableProperty] private ImageSource? _conductedPlot;
    [ObservableProperty] private ImageSource? _radiatedPlot;
    [ObservableProperty] private bool _hasResult;
    public ObservableCollection<string> StrongestLines { get; } = new();

    /// <summary>Installs an imported board (the PCB panel's sanctioned hand-off).</summary>
    public void LoadBoard(PcbBoard board, Func<NetMeshOptions> meshOptions, string? sourceFileName)
    {
        _board = board;
        _boardFileName = sourceFileName;
        _meshOptions = meshOptions;
        _reading = null;
        IncludeBoard = false;
        BoardResult = "";
        Nets.Clear();
        foreach (var net in board.Nets.OrderByDescending(n => n.Area)) Nets.Add(net);
        ReturnNet = Nets.FirstOrDefault(n => n.Name is { } name
            && (name.StartsWith("GND", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("PGND", StringComparison.OrdinalIgnoreCase)
                || name.Equals("0V", StringComparison.OrdinalIgnoreCase)));
        InputNet = null;
        SwitchNodeNet = null;
        HasBoard = true;
    }

    public void Clear()
    {
        _board = null;
        _boardFileName = null;
        _meshOptions = null;
        _reading = null;
        Nets.Clear();
        InputNet = ReturnNet = SwitchNodeNet = null;
        IncludeBoard = false;
        BoardResult = "";
        HasBoard = false;
    }

    /// <summary>The study's numbers from the panel, in SI.</summary>
    private EmiStudyInput Spec() => new()
    {
        Vin = InputVolts, Iout = OutputAmps, FrequencyHz = SwitchingKhz * 1e3, Duty = DutyPercent / 100,
        RiseSeconds = RiseNs * 1e-9, FallSeconds = FallNs * 1e-9,
        SwitchNodeCapacitance = SwitchNodeToEarthPf * 1e-12,
        InputCapacitance = InputMicrofarads * 1e-6, InputEsr = InputEsrMilliohms * 1e-3, InputEsl = InputEslNanohenries * 1e-9,
        FilterInductance = FilterMicrohenries * 1e-6, FilterResistance = FilterMilliohms * 1e-3,
        FilterCapacitance = FilterMicrofarads * 1e-6, FilterCapacitorEsr = FilterEsrMilliohms * 1e-3,
        FilterCapacitorEsl = FilterEslNanohenries * 1e-9, YCapacitance = YNanofarads * 1e-9,
        HotLoopInductance = HotLoopNanohenries * 1e-9, HotLoopResistance = HotLoopMilliohms * 1e-3,
        SwitchNodeToReturnCapacitance = SwitchNodeToReturnPf * 1e-12,
        ReverseRecoveryCharge = RecoveryNanocoulombs * 1e-9, RecoverySoftness = RecoverySoftness,
        InterwindingCapacitance = InterwindingPf * 1e-12, SecondaryToEarthCapacitance = SecondaryToEarthPf * 1e-12,
        SecondaryYCapacitance = SecondaryYPf * 1e-12,
        LoopArea = LoopAreaMm2 * 1e-6, CableLength = CableLengthM, Distance = DistanceM
    };

    /// <summary>Reads the hot loop and the switch node's capacitance off the board.</summary>
    [RelayCommand]
    private async Task ReadBoard()
    {
        _reading = null;
        IncludeBoard = false;
        if (_board is not { } board) { BoardResult = "Import a board first (PCB panel)."; return; }
        if (InputNet is not { } input || ReturnNet is not { } ret || SwitchNodeNet is not { } sw)
        {
            BoardResult = "Choose the input, return and switch-node nets.";
            return;
        }
        if (string.IsNullOrWhiteSpace(InputCapacitorRef) || string.IsNullOrWhiteSpace(HighSideRef))
        {
            BoardResult = "Enter the input capacitor's and the high-side switch's reference designators "
                + "(and the low-side switch's unless one part is both).";
            return;
        }
        var parts = new ConverterParts(InputCapacitorRef.Trim(), HighSideRef.Trim(),
            string.IsNullOrWhiteSpace(LowSideRef) ? HighSideRef.Trim() : LowSideRef.Trim());
        var options = new ConverterBoardOptions
        {
            Stackup = _meshOptions?.Invoke().Stackup,
            Loop = PlanePitchMm > 0 ? new LoopExtractionOptions { PlanePitchMeters = PlanePitchMm * 1e-3 } : null
        };
        BoardResult = "Reading the hot loop off the board (a PEEC solve of the loop over its return; a graded plane mesh can take minutes)…";
        try
        {
            var reading = await Task.Run(() => ConverterBoard.Read(board, input, ret, sw, parts, options));
            _reading = reading;
            IncludeBoard = true;
            BoardResult = string.Join(Environment.NewLine, reading.Describe().Concat(reading.Notes));
            foreach (string line in reading.Describe()) _log.Append("EMI board: " + line);
        }
        catch (Exception ex)
        {
            BoardResult = "Not read: " + ex.Message;
        }
    }

    [RelayCommand]
    private void LoadLimitFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Limit line (CSV: f0 MHz, f1 MHz, limit0, limit1)|*.csv;*.txt|All files|*.*",
            Title = "Conducted limit line"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            string name = Path.GetFileNameWithoutExtension(dialog.FileName);
            var line = EmiLimitLine.ParseCsv(File.ReadAllText(dialog.FileName), name, "dBµV", $"loaded from {Path.GetFileName(dialog.FileName)}");
            var choice = new EmiLimitChoice($"{name} (file)", line);
            ConductedLimits.Add(choice);
            ConductedLimit = choice;
            _log.Append($"EMI: limit line {name} loaded ({line.Segments.Count} segment(s), {line.MinFrequency / 1e6:g3}–{line.MaxFrequency / 1e6:g4} MHz).");
        }
        catch (Exception ex)
        {
            Result = "Limit not loaded: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task Estimate()
    {
        _solved = null;
        HasResult = false;
        ConductedPlot = RadiatedPlot = null;
        StrongestLines.Clear();
        Assumptions = "";
        var spec = Spec();
        var limit = ConductedLimit?.Line;
        var radiatedLimit = RadiatedLimit?.Line;
        var reading = IncludeBoard ? _reading : null;
        Result = "Estimating…";
        try
        {
            var result = await Task.Run(() => EmiStudy.Run(spec, limit, radiatedLimit, reading));
            _solved = result;
            HasResult = true;
            var lines = new List<string>(result.Conducted.Describe().Split(Environment.NewLine).Take(3));
            if (result.StrongestRadiatedText is { } radiated) lines.Add("Radiated (comparative only): strongest " + radiated + ".");
            Result = string.Join(Environment.NewLine, lines);
            foreach (var line in result.Conducted.Lines.OrderByDescending(l => l.WorstDbuV).Take(10).OrderBy(l => l.FrequencyHz))
                StrongestLines.Add($"{line.FrequencyHz / 1e6,8:F3} MHz  {line.WorstDbuV,6:F1} dBµV  DM {line.DifferentialDbuV,6:F1}  "
                    + $"CM {line.CommonDbuV,6:F1}" + (line.MarginDb is { } m ? $"  margin {m,6:F1} dB" : ""));
            Assumptions = "Assumptions: " + string.Join(" ", result.Conducted.Assumptions)
                + (result.Radiated is not null ? " " + EmiStudy.RadiatedAssumption : "")
                + (reading is not null ? " The board's copper is included (read above)." : "");
            ConductedPlot = PlotConducted(result);
            if (result.Radiated is { Count: > 0 }) RadiatedPlot = PlotRadiated(result);
            foreach (var line in lines) _log.Append("EMI: " + line);
        }
        catch (Exception ex)
        {
            Result = "Not estimated: " + ex.Message;
        }
    }

    /// <summary>Lines below 0 dBµV (the duty cycle's nulls go to the −180 dBµV floor) are left out
    /// of the plot so the scale stays on the spectrum.</summary>
    private static double Shown(double dbuv) => dbuv < 0 ? double.NaN : dbuv;

    private static ImageSource PlotConducted(EmiStudyResult result)
    {
        var lines = result.Conducted.Lines;
        var f = lines.Select(l => l.FrequencyHz).ToList();
        var series = new List<PlotSeries>
        {
            new("worst of L, N", f, lines.Select(l => Shown(l.WorstDbuV)).ToList(), Color.FromRgb(0x3D, 0x8B, 0xFD)),
            new("differential", f, lines.Select(l => Shown(l.DifferentialDbuV)).ToList(), Color.FromRgb(0x2E, 0xA0, 0x5A), Dashed: true),
            new("common", f, lines.Select(l => Shown(l.CommonDbuV)).ToList(), Color.FromRgb(0xA0, 0x4C, 0xC8), Dashed: true)
        };
        if (result.Conducted.Limit is { } limit) series.Add(LimitSeries(limit, Color.FromRgb(0xE5, 0x53, 0x4B)));
        return CurvePlot.Render(series, logX: true, logY: false, "frequency [Hz]", "dBµV");
    }

    private static ImageSource PlotRadiated(EmiStudyResult result)
    {
        var lines = result.Radiated!;
        var f = lines.Select(l => l.FrequencyHz).ToList();
        var series = new List<PlotSeries>
        {
            new("hot loop", f, lines.Select(l => Shown(l.LoopDbuVm)).ToList(), Color.FromRgb(0x3D, 0x8B, 0xFD)),
            new("input cable", f, lines.Select(l => Shown(l.CableDbuVm)).ToList(), Color.FromRgb(0xE0, 0x6C, 0x2B))
        };
        if (result.RadiatedLimit is { } limit) series.Add(LimitSeries(limit, Color.FromRgb(0xE5, 0x53, 0x4B)));
        return CurvePlot.Render(series, logX: true, logY: false, "frequency [Hz]", "dBµV/m (comparative)");
    }

    /// <summary>A limit line drawn segment by segment, so its steps show as steps.</summary>
    private static PlotSeries LimitSeries(EmiLimitLine limit, Color color)
    {
        var x = new List<double>();
        var y = new List<double>();
        foreach (var s in limit.Segments)
        {
            x.Add(s.F0); y.Add(s.L0);
            x.Add(s.F1); y.Add(s.L1);
        }
        return new PlotSeries("limit", x, y, color);
    }

    [RelayCommand]
    private void SaveCsv()
    {
        if (_solved is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_boardFileName ?? "converter") + "_conducted.csv",
            Filter = "CSV|*.csv|All files|*.*",
            Title = "Save the conducted lines (the radiated ones go beside them)"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _solved.Conducted.ToCsv());
            string? radiated = null;
            if (_solved.Radiated is not null)
            {
                radiated = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? ".",
                    Path.GetFileNameWithoutExtension(dialog.FileName).Replace("_conducted", "") + "_radiated.csv");
                File.WriteAllText(radiated, _solved.RadiatedCsv());
            }
            _log.Append($"EMI: saved {Path.GetFileName(dialog.FileName)}" + (radiated is null ? "." : $" and {Path.GetFileName(radiated)}."));
        }
        catch (Exception ex)
        {
            Result = "Not saved: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SaveReport()
    {
        if (_solved is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_boardFileName ?? "converter") + "_emi.html",
            Filter = "HTML|*.html|Markdown|*.md|All files|*.*",
            Title = "Save the EMI report"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var report = _solved.Report($"{Path.GetFileNameWithoutExtension(_boardFileName ?? "Converter")} — EMI pre-compliance");
            bool markdown = Path.GetExtension(dialog.FileName).Equals(".md", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dialog.FileName, markdown ? report.ToMarkdown() : report.ToHtml());
            _log.Append($"EMI: saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            Result = "Not saved: " + ex.Message;
        }
    }
}
