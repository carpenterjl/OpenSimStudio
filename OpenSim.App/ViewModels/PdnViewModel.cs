using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Pdn;
using OpenSim.Rf.Si;

namespace OpenSim.App.ViewModels;

/// <summary>One capacitor found between the power and the ground net, with the model the
/// solve gives it.</summary>
public partial class PdnCapacitorRow : ObservableObject
{
    public PdnCapacitorRow(BoardCapacitor capacitor) => Capacitor = capacitor;

    public BoardCapacitor Capacitor { get; }
    public string Label => Capacitor.RefDes + (Capacitor.PartName is { Length: > 0 } p ? $" ({p})" : "");

    /// <summary>Mounting inductance [pH], or why the part is not on the planes.</summary>
    public string Mounting => Capacitor.Site is null
        ? Capacitor.Problem ?? "not connected"
        : $"{Capacitor.MountingInductanceHenries * 1e12:f0} pH";

    public bool CanInclude => Capacitor.Site is not null;

    [ObservableProperty] private bool _include;
    [ObservableProperty] private double _nanofarads;
    [ObservableProperty] private double _esrMilliohms;
    [ObservableProperty] private double _eslNanohenries;
}

/// <summary>
/// PDN impedance against frequency at a load (<see cref="PdnAnalysis"/>): the plane pair of
/// a power and a ground net read off the imported board, its capacitors with their mounting
/// inductance, a regulator, and a target impedance.
/// </summary>
public partial class PdnViewModel : ObservableObject
{
    private readonly ILogService _log;
    private PcbBoard? _board;
    private string? _boardFileName;
    private Func<NetMeshOptions>? _meshOptions;
    private PdnBoardModel? _model;
    private (CopperNet Power, CopperNet Ground)? _nets;
    private PdnResult? _solved;
    private TargetImpedance? _target;
    private CapacitorModel? _vendorModel;

    public PdnViewModel(ILogService log) => _log = log;

    public ObservableCollection<SiNetSelection> Nets { get; } = new();
    [ObservableProperty] private SiNetSelection? _powerNet;
    [ObservableProperty] private SiNetSelection? _groundNet;

    public ObservableCollection<PdnCapacitorRow> Capacitors { get; } = new();

    [ObservableProperty] private string _loadRefDes = "";
    [ObservableProperty] private string _regulatorRefDes = "";
    [ObservableProperty] private double _regulatorMilliohms = 5;
    [ObservableProperty] private double _regulatorNanohenries = 20;

    /// <summary>What a found capacitor starts with; edit a row to change one part.</summary>
    [ObservableProperty] private double _defaultNanofarads = 100;
    [ObservableProperty] private double _defaultEsrMilliohms = 30;
    [ObservableProperty] private double _defaultEslNanohenries = 0.5;

    /// <summary>Target Z = V·ripple/ΔI up to a frequency; 0 V = no target.</summary>
    [ObservableProperty] private double _railVolts;
    [ObservableProperty] private double _ripplePercent = 5;
    [ObservableProperty] private double _stepAmps = 1;
    [ObservableProperty] private double _targetUpToMhz = 100;

    [ObservableProperty] private double _fromKhz = 10;
    [ObservableProperty] private double _toMhz = 1000;

    [ObservableProperty] private string _planesResult = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _assumptions = "";
    [ObservableProperty] private string _vendorModelStatus = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private System.Windows.Media.ImageSource? _plot;

    public void LoadBoard(PcbBoard board, Func<NetMeshOptions> meshOptions, string? sourceFileName)
    {
        _board = board;
        _boardFileName = sourceFileName;
        _meshOptions = meshOptions;
        _model = null;
        _nets = null;
        _solved = null;
        HasResult = false;
        Plot = null;
        Nets.Clear();
        Capacitors.Clear();
        // Largest first: the planes are at the top of the list.
        foreach (var net in board.Nets.OrderByDescending(n => n.Area)) Nets.Add(new SiNetSelection(net));
        GroundNet = Nets.FirstOrDefault(n => n.Net.Name is { } name
            && (name.StartsWith("GND", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("VSS", StringComparison.OrdinalIgnoreCase)
                || name.Equals("0V", StringComparison.OrdinalIgnoreCase)));
        PowerNet = null;
        PlanesResult = "";
        Result = "";
        Assumptions = "";
    }

    private PdnBoardOptions Options() => new() { Stackup = _meshOptions?.Invoke().Stackup };

    /// <summary>Reads the plane pair and the capacitors off the board.</summary>
    [RelayCommand]
    private async Task FindPlanes()
    {
        if (_board is null)
        {
            PlanesResult = "Import a board first (PCB panel).";
            return;
        }
        if (PowerNet is null || GroundNet is null)
        {
            PlanesResult = "Choose the power net and the ground net.";
            return;
        }
        PlanesResult = "Reading the planes…";
        try
        {
            var board = _board;
            var power = PowerNet.Net;
            var ground = GroundNet.Net;
            var options = Options();
            var model = await Task.Run(() => PdnBoard.Extract(board, power, ground, options));
            _model = model;
            _nets = (power, ground);
            Capacitors.Clear();
            foreach (var capacitor in model.Capacitors)
                Capacitors.Add(new PdnCapacitorRow(capacitor)
                {
                    Include = capacitor.Site is not null,
                    Nanofarads = DefaultNanofarads,
                    EsrMilliohms = DefaultEsrMilliohms,
                    EslNanohenries = DefaultEslNanohenries
                });
            int connected = model.Capacitors.Count(c => c.Site is not null);
            PlanesResult = $"Planes L{model.PowerLayer} ({model.PowerNet}) and L{model.GroundLayer} ({model.GroundNet}): "
                + $"{model.AreaSquareMeters * 1e4:g4} cm² overlap, {model.SeparationMeters * 1e6:g4} µm apart, "
                + $"εr {model.RelativePermittivity:g3}. {connected} of {model.Capacitors.Count} capacitor(s) between the "
                + "two nets reach the planes." + Environment.NewLine + string.Join(Environment.NewLine, model.Notes);
            _log.Append("PDN: " + PlanesResult.Replace(Environment.NewLine, " "));
            foreach (var c in model.Capacitors.Where(c => c.Site is null))
                _log.Append($"PDN: {c.RefDes} left out — {c.Problem}.");
        }
        catch (Exception ex)
        {
            PlanesResult = "Not read: " + ex.Message;
        }
    }

    /// <summary>A vendor S-parameter file to use for every included capacitor instead of
    /// C, ESR and ESL. One port, or two ports measured in shunt.</summary>
    [RelayCommand]
    private void LoadVendorModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Touchstone|*.s1p;*.s2p|All files|*.*",
            Title = "Capacitor S-parameters (1-port, or 2-port shunt)"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var data = TouchstoneReader.ReadFile(dialog.FileName);
            _vendorModel = CapacitorModel.FromTouchstone(Path.GetFileNameWithoutExtension(dialog.FileName), data,
                data.Ports == 1 ? CapacitorFixture.OnePort : CapacitorFixture.TwoPortShunt);
            VendorModelStatus = $"Every included capacitor uses {Path.GetFileName(dialog.FileName)} "
                + $"({data.Ports}-port{(data.Ports == 2 ? ", read as a shunt measurement" : "")}, "
                + $"{data.FrequenciesHz[0] / 1e6:g3}–{data.FrequenciesHz[^1] / 1e6:g4} MHz).";
        }
        catch (Exception ex)
        {
            _vendorModel = null;
            VendorModelStatus = "Not loaded: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearVendorModel()
    {
        _vendorModel = null;
        VendorModelStatus = "";
    }

    [RelayCommand]
    private async Task Solve()
    {
        if (_board is null || _model is null || _nets is null)
        {
            Result = "Read the planes first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(LoadRefDes))
        {
            Result = "Enter the load's reference designator (the part whose supply pins see the impedance).";
            return;
        }
        Result = "Solving…";
        Assumptions = "";
        try
        {
            var board = _board;
            var model = _model;
            var (power, ground) = _nets.Value;
            var options = Options();
            var rows = Capacitors.Where(r => r.Include && r.CanInclude).ToDictionary(r => r.Capacitor.RefDes);
            var vendor = _vendorModel;
            CapacitorModel? ModelFor(BoardCapacitor c)
            {
                if (!rows.TryGetValue(c.RefDes, out var row)) return null;
                if (vendor is not null) return vendor;
                return new CapacitorModel
                {
                    PartName = c.PartName ?? "",
                    CapacitanceFarads = row.Nanofarads * 1e-9,
                    EsrOhms = row.EsrMilliohms * 1e-3,
                    EslHenries = row.EslNanohenries * 1e-9
                };
            }
            (string, double, double)? regulator = string.IsNullOrWhiteSpace(RegulatorRefDes)
                ? null
                : (RegulatorRefDes.Trim(), RegulatorMilliohms * 1e-3, RegulatorNanohenries * 1e-9);
            _target = RailVolts > 0
                ? TargetImpedance.Flat(RailVolts, RipplePercent, StepAmps, TargetUpToMhz * 1e6)
                : null;
            var target = _target;
            string load = LoadRefDes.Trim();
            double from = FromKhz * 1e3, to = ToMhz * 1e6;

            var result = await Task.Run(() =>
            {
                var setup = PdnBoard.Setup(board, power, ground, model, load, ModelFor, regulator, options) with
                {
                    Target = target,
                    MinFrequencyHz = from,
                    MaxFrequencyHz = to
                };
                return PdnAnalysis.Solve(setup);
            });
            _solved = result;
            HasResult = true;
            var curves = new List<Controls.PlotSeries>
            {
                new("at the load", result.FrequenciesHz, result.Impedance.Select(z => z.Magnitude).ToList(),
                    System.Windows.Media.Color.FromRgb(0x3D, 0x8B, 0xFD)),
                new("planes alone", result.FrequenciesHz, result.BareImpedance.Select(z => z.Magnitude).ToList(),
                    System.Windows.Media.Color.FromRgb(0x9A, 0xA0, 0xA6), Dashed: true)
            };
            if (target is not null)
                curves.Add(new("target", result.FrequenciesHz, result.FrequenciesHz.Select(target.At).ToList(),
                    System.Windows.Media.Color.FromRgb(0xE5, 0x53, 0x4B)));
            Plot = Controls.CurvePlot.Render(curves, logX: true, logY: true, "frequency [Hz]", "|Z| [Ω]");
            Result = string.Join(Environment.NewLine, result.Describe());
            Assumptions = "Assumptions: " + string.Join(" ", result.Assumptions.Concat(model.Notes))
                + " The mounting inductance takes the nearest via of each pad and an escape trace as wide as the pad "
                + "or twice the via, whichever is less; more vias per pad would lower it.";
            foreach (string line in result.Describe()) _log.Append("PDN: " + line.Trim());
        }
        catch (Exception ex)
        {
            Result = "Not solved: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SaveCsv() => Save("CSV|*.csv|All files|*.*", "_pdn.csv", r => r.ToCsv(_target));

    [RelayCommand]
    private void SaveTouchstone() => Save("Touchstone|*.s1p|All files|*.*", "_pdn.s1p", r => r.ToTouchstone());

    private void Save(string filter, string suffix, Func<PdnResult, string> write)
    {
        if (_solved is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_boardFileName ?? "board") + suffix,
            Filter = filter,
            Title = "Save PDN impedance"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, write(_solved));
            _log.Append($"PDN: saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            Result = "Not saved: " + ex.Message;
        }
    }
}
