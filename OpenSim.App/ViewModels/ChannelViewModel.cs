using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Rf.Channel;
using OpenSim.Rf.Si;

namespace OpenSim.App.ViewModels;

/// <summary>
/// A channel read from a Touchstone file — a measurement or another tool's result: what
/// the file holds, its mixed-mode S-parameters, a TDR impedance profile and an eye between
/// resistive terminations (<see cref="ImportedChannel"/>, <see cref="MixedMode"/>,
/// <see cref="Tdr"/>, <see cref="ChannelEye"/>).
/// </summary>
public partial class ChannelViewModel : ObservableObject
{
    private static readonly Color Blue = Color.FromRgb(0x3D, 0x8B, 0xFD);
    private static readonly Color Red = Color.FromRgb(0xE5, 0x53, 0x4B);
    private static readonly Color Grey = Color.FromRgb(0x9A, 0xA0, 0xA6);

    private readonly ILogService _log;
    private ImportedChannel? _channel;
    private string? _fileName;

    public ChannelViewModel(ILogService log) => _log = log;

    public ObservableCollection<string> SummaryLines { get; } = new();
    public IReadOnlyList<string> PortOrders { get; } = new[] { "1→2, 3→4 (near odd, far even)", "1→3, 2→4 (near first half)" };

    [ObservableProperty] private string _selectedPortOrder = "1→2, 3→4 (near odd, far even)";
    [ObservableProperty] private bool _hasChannel;
    [ObservableProperty] private bool _isFourPort;

    /// <summary>Use the two pairs of a four-port as one differential channel.</summary>
    [ObservableProperty] private bool _differential;

    /// <summary>1-based file ports for single-ended runs.</summary>
    [ObservableProperty] private int _drivenPort = 1;
    [ObservableProperty] private int _receiverPort = 2;

    [ObservableProperty] private double _bitRateGbps = 1;
    [ObservableProperty] private double _risePercent = 20;
    [ObservableProperty] private int _prbsOrder = 7;
    [ObservableProperty] private double _swingVolts = 1;
    [ObservableProperty] private double _sourceOhms = 50;
    [ObservableProperty] private double _loadOhms = 50;

    /// <summary>TDR edge [ps]; 0 = the fastest the file supports.</summary>
    [ObservableProperty] private double _tdrRisePs;

    [ObservableProperty] private string _eyeResult = "";
    [ObservableProperty] private ImageSource? _eyeImage;
    [ObservableProperty] private string _tdrResult = "";
    [ObservableProperty] private ImageSource? _tdrPlot;
    [ObservableProperty] private string _mixedModeResult = "";
    [ObservableProperty] private ImageSource? _mixedModePlot;

    private PortOrder Order => SelectedPortOrder == PortOrders[0] ? PortOrder.NearOddFarEven : PortOrder.NearFirstHalf;

    [RelayCommand]
    private void Load()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Touchstone|*.s1p;*.s2p;*.s3p;*.s4p;*.s6p;*.s8p|All files|*.*",
            Title = "Channel S-parameters"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var channel = new ImportedChannel(TouchstoneReader.ReadFile(dialog.FileName));
            _channel = channel;
            _fileName = dialog.FileName;
            HasChannel = true;
            IsFourPort = channel.Ports == 4;
            Differential = IsFourPort;
            SelectedPortOrder = PortOrders[channel.DetectOrder() == PortOrder.NearOddFarEven ? 0 : 1];
            var map = channel.Ports % 2 == 0 ? channel.NearFarPorts(Order) : new[] { 0 };
            DrivenPort = 1;
            ReceiverPort = channel.Ports >= 2 ? map[channel.Ports / 2] + 1 : 1;
            SummaryLines.Clear();
            SummaryLines.Add(Path.GetFileName(dialog.FileName));
            foreach (string line in channel.Describe()) SummaryLines.Add(line);
            if (channel.Ports >= 4)
                SummaryLines.Add($"Port order taken as {SelectedPortOrder} from where port 1's signal comes out; change it if the file says otherwise.");
            foreach (string line in SummaryLines) _log.Append("Channel: " + line);
            EyeResult = TdrResult = MixedModeResult = "";
            EyeImage = TdrPlot = MixedModePlot = null;
        }
        catch (Exception ex)
        {
            HasChannel = false;
            SummaryLines.Clear();
            SummaryLines.Add($"Not read: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RunEye()
    {
        if (_channel is not { } channel) return;
        try
        {
            var setup = new ChannelEyeSetup
            {
                BitRate = BitRateGbps * 1e9, RiseFractionOfUi = Math.Clamp(RisePercent / 100, 0, 1),
                PrbsOrder = PrbsOrder, Swing = SwingVolts, SourceOhms = SourceOhms, LoadOhms = LoadOhms
            };
            var result = Differential && channel.Ports == 4
                ? ChannelEye.Differential(channel, DifferentialPairing.For(Order), setup)
                : ChannelEye.SingleEnded(channel, Port(DrivenPort), Port(ReceiverPort), setup);
            EyeResult = string.Join(Environment.NewLine, result.Describe()) +
                        $"{Environment.NewLine}{result.SamplesPerUi} samples per bit; resistive terminations, no driver or receiver model.";
            EyeImage = SignalIntegrityViewModel.RenderEye(result.Eye);
            foreach (string line in result.Describe()) _log.Append("Channel eye: " + line);
        }
        catch (Exception ex) { EyeResult = $"Not solvable: {ex.Message}"; EyeImage = null; }
    }

    [RelayCommand]
    private void RunTdr()
    {
        if (_channel is not { } channel) return;
        try
        {
            double? rise = TdrRisePs > 0 ? TdrRisePs * 1e-12 : null;
            var profile = Differential && channel.Ports == 4
                ? Tdr.DifferentialProfile(channel, DifferentialPairing.For(Order), rise)
                : Tdr.Profile(channel, Port(DrivenPort), rise);
            var ns = profile.TimeSeconds.Select(t => t * 1e9).ToList();
            var curves = new List<Controls.PlotSeries> { new("as an instrument shows it", ns, profile.ImpedanceOhms, Grey, Dashed: true) };
            if (profile.PeeledOhms is { } peeled) curves.Add(new("earlier reflections removed", ns, peeled, Blue));
            TdrPlot = Controls.CurvePlot.Render(curves, logX: false, logY: false, "round-trip time [ns]", "impedance [Ω]");
            TdrResult = $"Edge {profile.RiseTimeSeconds * 1e12:f0} ps (10–90 %), reference {profile.ReferenceOhms:g4} Ω" +
                        (Differential && channel.Ports == 4 ? " differential" : $", port {DrivenPort}") + ". " +
                        string.Join(" ", profile.Notes);
            _log.Append("Channel TDR: " + TdrResult);
        }
        catch (Exception ex) { TdrResult = $"Not solvable: {ex.Message}"; TdrPlot = null; }
    }

    [RelayCommand]
    private void ShowMixedMode()
    {
        if (_channel is not { } channel) return;
        try
        {
            var mixed = MixedMode.Of(channel, DifferentialPairing.For(Order));
            List<double> Decibels(int row, int column) =>
                mixed.Select(m => 20 * Math.Log10(Math.Max(m[row, column].Magnitude, 1e-8))).ToList();
            var f = channel.FrequenciesHz;
            var sdd21 = Decibels(1, 0);
            var scd21 = Decibels(3, 0);
            MixedModePlot = Controls.CurvePlot.Render(new List<Controls.PlotSeries>
            {
                new("Sdd21", f, sdd21, Blue), new("Sdd11", f, Decibels(0, 0), Grey),
                new("Scd21 (mode conversion)", f, scd21, Red), new("Scc21", f, Decibels(3, 2), Grey, Dashed: true)
            }, logX: false, logY: false, "frequency [Hz]", "[dB]");
            MixedModeResult = $"Worst mode conversion Scd21 {scd21.Max():f1} dB; differential loss at the last point {sdd21[^1]:f1} dB. " +
                              $"Differential ports are referred to {2 * channel.ReferenceOhms:g4} Ω, common-mode to {channel.ReferenceOhms / 2:g4} Ω.";
            _log.Append("Channel mixed mode: " + MixedModeResult);
        }
        catch (Exception ex) { MixedModeResult = $"Not available: {ex.Message}"; MixedModePlot = null; }
    }

    [RelayCommand]
    private void SaveMixedMode()
    {
        if (_channel is not { } channel) return;
        try
        {
            string text = MixedMode.ToTouchstone(channel, DifferentialPairing.For(Order));
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileNameWithoutExtension(_fileName ?? "channel") + "-mixed.s4p",
                Filter = "Touchstone|*.s4p", Title = "Save mixed-mode S-parameters"
            };
            if (dialog.ShowDialog() != true) return;
            File.WriteAllText(dialog.FileName, text);
            _log.Append($"Channel: mixed-mode S-parameters saved to {dialog.FileName}.");
        }
        catch (Exception ex) { MixedModeResult = $"Not saved: {ex.Message}"; }
    }

    private int Port(int oneBased)
    {
        if (_channel is null || oneBased < 1 || oneBased > _channel.Ports)
            throw new InvalidOperationException($"Port {oneBased} is not one of the file's {_channel?.Ports} ports.");
        return oneBased - 1;
    }
}
