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

    /// <summary>Mounting inductance [pH] (every via of both pads), or why the part is not on the planes.</summary>
    public string Mounting => Capacitor.Site is null
        ? Capacitor.Problem ?? "not connected"
        : $"{Capacitor.MountingInductanceHenries * 1e12:f0} pH";

    public bool CanInclude => Capacitor.Site is not null;

    [ObservableProperty] private bool _include;
    [ObservableProperty] private double _nanofarads;
    [ObservableProperty] private double _esrMilliohms;
    [ObservableProperty] private double _eslNanohenries;

    /// <summary>The manufacturer's part number, from a bill of materials.</summary>
    [ObservableProperty] private string? _partNumber;

    /// <summary>The vendor's measured model found for the part number; replaces C, ESR and ESL.</summary>
    [ObservableProperty] private CapacitorModel? _measured;

    public string Source => Measured is not null ? "measured" : PartNumber ?? "";

    partial void OnMeasuredChanged(CapacitorModel? value) => OnPropertyChanged(nameof(Source));
    partial void OnPartNumberChanged(string? value) => OnPropertyChanged(nameof(Source));
}

/// <summary>
/// PDN impedance against frequency at a load (<see cref="PdnAnalysis"/>): the planes between a
/// power and a ground net read off the imported board (every plane-sized island on the layers
/// between, the stitching vias, the fringe of the edges), its capacitors on their pads and vias
/// (values from a bill of materials, measured models by part number), the load behind its
/// package, a regulator with or without its control loop, and a target impedance.
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

    /// <summary>The regulator's control loop: A(s) = 2π·f_gain/s · (1 + s/ω_zero)/(1 + s/ω_pole).</summary>
    [ObservableProperty] private bool _regulatorLoop;
    [ObservableProperty] private double _loopGainKhz = 20;
    [ObservableProperty] private double _loopZeroKhz = 5;
    [ObservableProperty] private double _loopPoleKhz = 1000;
    [ObservableProperty] private bool _senseAtLoad;

    /// <summary>The load's package between its die and its pads.</summary>
    [ObservableProperty] private double _packageMilliohms;
    [ObservableProperty] private double _packageNanohenries;

    /// <summary>Give the planes' open edges the fringe of the board's cross-section.</summary>
    [ObservableProperty] private bool _edgeFringe = true;

    [ObservableProperty] private string _bomStatus = "";
    private string? _vendorFolder;

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

    private PdnBoardOptions Options() => new()
    {
        Stackup = _meshOptions?.Invoke().Stackup,
        EdgeFringe = EdgeFringe,
        LoadPackageOhms = PackageMilliohms * 1e-3,
        LoadPackageHenries = PackageNanohenries * 1e-9
    };

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
            PlanesResult = $"Closest planes L{model.PowerLayer} ({model.PowerNet}) and L{model.GroundLayer} ({model.GroundNet}): "
                + $"{model.AreaSquareMeters * 1e4:g4} cm² overlap, {model.SeparationMeters * 1e6:g4} µm apart, "
                + $"εr {model.RelativePermittivity:g3}. {connected} of {model.Capacitors.Count} capacitor(s) between the "
                + "two nets reach the planes." + Environment.NewLine + string.Join(Environment.NewLine, model.Notes);
            ApplyVendorFolder();
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

    /// <summary>Capacitor values from a bill of materials (CSV/TSV: designator, value, and ESR,
    /// ESL and part number when it has them) or a placement file (its value column).</summary>
    [RelayCommand]
    private void ReadBom()
    {
        if (Capacitors.Count == 0)
        {
            BomStatus = "Read the planes and capacitors first.";
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Bill of materials or placement file|*.csv;*.tsv;*.txt;*.pos|All files|*.*",
            Title = "Bill of materials or placement file"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            string text = File.ReadAllText(dialog.FileName);
            IReadOnlyList<BomPart> parts;
            IReadOnlyList<string> notes;
            bool placement = Path.GetExtension(dialog.FileName).Equals(".pos", StringComparison.OrdinalIgnoreCase)
                             || text.Contains("PosX", StringComparison.OrdinalIgnoreCase);
            if (placement)
                parts = CapacitorBom.FromPlacement(OpenSim.Pcb.Thermal.ComponentPlacement.ReadPlacementFile(text, out notes));
            else
                parts = CapacitorBom.Read(text, out notes);
            var byRef = parts.GroupBy(p => p.RefDes, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            int values = 0, esr = 0, esl = 0, numbers = 0, missing = 0;
            foreach (var row in Capacitors)
            {
                if (!byRef.TryGetValue(row.Capacitor.RefDes, out var part)) { missing++; continue; }
                if (part.Farads is { } c) { row.Nanofarads = c * 1e9; values++; }
                if (part.EsrOhms is { } r) { row.EsrMilliohms = r * 1e3; esr++; }
                if (part.EslHenries is { } l) { row.EslNanohenries = l * 1e9; esl++; }
                if (part.PartNumber is { } pn) { row.PartNumber = pn; numbers++; }
            }
            ApplyVendorFolder();
            BomStatus = $"{Path.GetFileName(dialog.FileName)}: C for {values} of {Capacitors.Count} capacitor(s)"
                + (esr > 0 ? $", ESR for {esr}" : "") + (esl > 0 ? $", ESL for {esl}" : "")
                + (numbers > 0 ? $", a part number for {numbers}" : "")
                + (missing > 0 ? $"; {missing} not in the file keep their values" : "")
                + ". The rest keep the defaults. " + string.Join(" ", notes);
            _log.Append("PDN: " + BomStatus);
        }
        catch (Exception ex)
        {
            BomStatus = "Not read: " + ex.Message;
        }
    }

    /// <summary>A folder of vendor S-parameter files: a capacitor whose part number names one of
    /// them (.s1p across the part, .s2p measured in shunt) uses it instead of C, ESR and ESL.</summary>
    [RelayCommand]
    private void ChooseVendorFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder of vendor S-parameter files" };
        if (dialog.ShowDialog() != true) return;
        _vendorFolder = dialog.FolderName;
        ApplyVendorFolder();
    }

    private void ApplyVendorFolder()
    {
        if (_vendorFolder is null) return;
        int found = 0, numbered = 0;
        var problems = new List<string>();
        foreach (var row in Capacitors)
        {
            row.Measured = null;
            if (string.IsNullOrWhiteSpace(row.PartNumber)) continue;
            numbered++;
            try
            {
                row.Measured = CapacitorBom.VendorModel(_vendorFolder, row.PartNumber, out _);
                if (row.Measured is not null) found++;
            }
            catch (Exception ex)
            {
                problems.Add($"{row.Capacitor.RefDes}: {ex.Message}");
            }
        }
        BomStatus = $"Vendor files in {_vendorFolder}: a measured model for {found} of the {numbered} capacitor(s) with a part number."
            + (problems.Count > 0 ? " Not read: " + string.Join("; ", problems) : "");
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
                if (row.Measured is not null) return row.Measured;
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
            RegulatorLoop? loop = RegulatorLoop && regulator is not null
                ? new RegulatorLoop
                {
                    GainHz = LoopGainKhz * 1e3,
                    ZerosHz = LoopZeroKhz > 0 ? new[] { LoopZeroKhz * 1e3 } : Array.Empty<double>(),
                    PolesHz = LoopPoleKhz > 0 ? new[] { LoopPoleKhz * 1e3 } : Array.Empty<double>()
                }
                : null;
            bool senseAtLoad = SenseAtLoad;
            _target = RailVolts > 0
                ? TargetImpedance.Flat(RailVolts, RipplePercent, StepAmps, TargetUpToMhz * 1e6)
                : null;
            var target = _target;
            string load = LoadRefDes.Trim();
            double from = FromKhz * 1e3, to = ToMhz * 1e6;

            var result = await Task.Run(() =>
            {
                var setup = PdnBoard.Setup(board, power, ground, model, load, ModelFor, regulator, options, loop, senseAtLoad) with
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
                new("planes and the load's mounting", result.FrequenciesHz, result.BareImpedance.Select(z => z.Magnitude).ToList(),
                    System.Windows.Media.Color.FromRgb(0x9A, 0xA0, 0xA6), Dashed: true)
            };
            if (target is not null)
                curves.Add(new("target", result.FrequenciesHz, result.FrequenciesHz.Select(target.At).ToList(),
                    System.Windows.Media.Color.FromRgb(0xE5, 0x53, 0x4B)));
            Plot = Controls.CurvePlot.Render(curves, logX: true, logY: true, "frequency [Hz]", "|Z| [Ω]");
            Result = string.Join(Environment.NewLine, result.Describe());
            Assumptions = "Assumptions: " + string.Join(" ", result.Assumptions.Concat(model.Notes))
                + " An escape trace runs straight from each pad's centre to each of its vias, as wide as the pad "
                + "or twice the via, whichever is less (the trace itself is not read from the layout).";
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
