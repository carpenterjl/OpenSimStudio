using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Extraction;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Frequency-dependent R and L of the chosen path by the filament solve
/// (<see cref="LoopExtraction"/>), with SPICE and Touchstone export.
/// </summary>
public partial class InductanceViewModel
{
    public const string PathAlone = "The net end to end (partial)";
    public const string PathLoop = "Loop: out on the net, back on the return";
    public const string PathTwoPorts = "Net and second net as two coupled ports";

    public IReadOnlyList<string> AcPaths { get; } = new[] { PathAlone, PathLoop, PathTwoPorts };

    /// <summary>Nullable so a ComboBox transient null push lands harmlessly.</summary>
    [ObservableProperty] private string? _acPath = PathLoop;

    /// <summary>Frequencies to solve at [MHz], separated by commas or spaces.</summary>
    [ObservableProperty] private string _acFrequenciesMhz = "0.001, 0.1, 1, 10, 100";

    [ObservableProperty] private string _acResult = "";
    [ObservableProperty] private string _acAssumptions = "";
    [ObservableProperty] private bool _hasAcResult;

    private LoopExtractionResult? _acSolved;
    private string _acName = "path";

    private static List<double>? ParseFrequencies(string text)
    {
        var list = new List<double>();
        foreach (string token in text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz) || !(mhz > 0))
                return null;
            list.Add(mhz * 1e6);
        }
        return list.Count > 0 ? list.Distinct().OrderBy(f => f).ToList() : null;
    }

    [RelayCommand]
    private async Task ComputeFrequencyDependent()
    {
        AcAssumptions = "";
        HasAcResult = false;
        _acSolved = null;
        if (_board is null || _options is null) { AcResult = "Import a board first."; return; }
        if (OutboundNet is not { } net) { AcResult = "Pick a net first."; return; }
        var frequencies = ParseFrequencies(AcFrequenciesMhz);
        if (frequencies is null) { AcResult = "The frequencies are positive numbers in MHz, separated by commas."; return; }

        Func<LoopExtractionResult> solve;
        string name;
        if (AcPath == PathAlone)
        {
            var chain = BuildChain(net);
            if (chain.Chain is null) { AcResult = $"Not composable: {chain.FailureReason}."; return; }
            var segments = chain.Chain;
            solve = () => LoopExtraction.Chain(segments, frequencies);
            name = net.Label;
        }
        else if (AcPath == PathLoop && ReturnIsPlane)
        {
            if (PlaneLayer is not int planeLayer) { AcResult = "Pick a plane layer."; return; }
            var chain = BuildChain(net, new[] { planeLayer });
            if (chain.Chain is null) { AcResult = $"{net.Label}: {chain.FailureReason}."; return; }
            if (chain.LayerZ is null || !chain.LayerZ.TryGetValue(planeLayer, out var planeZ))
            {
                AcResult = $"Layer L{planeLayer} is not in the stackup.";
                return;
            }
            // The copper of that layer that is not the net's own: the island under the
            // path's start, or failing that the largest.
            var start = new Point2(chain.Chain[0].Start.X, chain.Chain[0].Start.Y);
            var candidates = _board.Islands.Where(i => i.LayerOrder == planeLayer && !net.Islands.Contains(i)).ToList();
            var island = candidates.FirstOrDefault(i =>
                             OpenSim.Pcb.Meshing2D.PlanarMesher.ContainsPoint(new[] { i.Shape }, start))
                         ?? candidates.MaxBy(i => i.Area);
            if (island is null) { AcResult = $"L{planeLayer} has no copper of another net to return through."; return; }
            var segments = chain.Chain;
            var shape = new[] { island.Shape };
            solve = () => LoopExtraction.ChainOverPlane(segments, shape, planeZ, frequencies);
            name = $"{net.Label} over L{planeLayer}";
        }
        else
        {
            if (SecondNet is not { } second) { AcResult = "Pick the second net."; return; }
            if (ReferenceEquals(net, second)) { AcResult = "Pick two different nets."; return; }
            var chainA = BuildChain(net, second.Layers);
            if (chainA.Chain is null) { AcResult = $"{net.Label}: {chainA.FailureReason}."; return; }
            var chainB = BuildChain(second, net.Layers);
            if (chainB.Chain is null) { AcResult = $"{second.Label}: {chainB.FailureReason}."; return; }
            var a = chainA.Chain;
            var b = chainB.Chain;
            string nameA = net.Name ?? $"Net {net.Id}", nameB = second.Name ?? $"Net {second.Id}";
            solve = AcPath == PathTwoPorts
                ? () => LoopExtraction.TwoChains(a, nameA, b, nameB, frequencies)
                : () => LoopExtraction.ChainAndReturn(a, b, frequencies);
            name = $"{nameA} and {nameB}";
        }

        AcResult = "Solving…";
        try
        {
            var result = await Task.Run(solve);
            _acSolved = result;
            _acName = name;
            HasAcResult = true;
            AcResult = string.Join(Environment.NewLine, result.Describe());
            AcAssumptions = $"{result.Filaments} filaments. Assumptions: " + string.Join(" ", result.Assumptions);
            foreach (string line in result.Describe()) _log.Append($"Extraction ({name}): {line}");
        }
        catch (Exception ex) { AcResult = $"Not solved: {ex.Message}"; }
    }

    /// <summary>The SPICE subcircuit at the highest solved frequency.</summary>
    [RelayCommand]
    private void ExportSpice()
    {
        if (_acSolved is not { } solved) return;
        var point = solved.Points[^1];
        Save("SPICE subcircuit|*.cir;*.lib;*.sp|All files|*.*", ".cir", () =>
            ParasiticExport.SpiceSubcircuit(_acName, solved.PortNames, point.Impedance, point.FrequencyHz));
    }

    [RelayCommand]
    private void ExportTouchstone()
    {
        if (_acSolved is not { } solved) return;
        Save($"Touchstone|*.s{solved.PortNames.Count}p|All files|*.*", $".s{solved.PortNames.Count}p", () =>
            ParasiticExport.Touchstone(solved.Points.Select(p => p.FrequencyHz).ToList(),
                solved.Points.Select(p => p.Impedance).ToList(), solved.PortNames));
    }

    private void Save(string filter, string extension, Func<string> write)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = new string(_acName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + extension,
            Filter = filter,
            Title = "Save extracted parasitics"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, write());
            _log.Append($"Extraction: saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) { AcResult = $"Not saved: {ex.Message}"; }
    }
}
