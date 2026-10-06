using System.Numerics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls;
using OpenSim.Rf.Network;

namespace OpenSim.App.ViewModels;

/// <summary>
/// A two-part lumped match (L-section) for the swept antenna: designed at one frequency from the
/// solved Zin, then applied across the sweep with the parts' Q, so the matched band can be read
/// against the bare one.
/// </summary>
public partial class AntennaViewModel
{
    /// <summary>Design frequency [MHz]; 0 = the best-match point of the sweep.</summary>
    [ObservableProperty] private double _matchFrequencyMHz;
    [ObservableProperty] private double _inductorQ = 40;
    [ObservableProperty] private double _capacitorQ = 200;
    [ObservableProperty] private string _matchResult = "";
    [ObservableProperty] private ImageSource? _matchImage;

    [RelayCommand]
    private void DesignMatch()
    {
        MatchResult = "";
        MatchImage = null;
        if (_networkCurve.Count < 2) { MatchResult = "Solve a Zin sweep first."; return; }
        double z0 = ReferenceOhms;
        var curve = _networkCurve.OrderBy(p => p.Hz).ToList();
        double f0 = MatchFrequencyMHz > 0
            ? MatchFrequencyMHz * 1e6
            : curve.MinBy(p => PortTermination.Reflection(p.Zin, z0).Magnitude).Hz;
        if (f0 < curve[0].Hz || f0 > curve[^1].Hz)
        {
            MatchResult = $"{f0 / 1e6:g5} MHz is outside the sweep ({curve[0].Hz / 1e6:g5}–{curve[^1].Hz / 1e6:g5} MHz).";
            return;
        }
        int k = Math.Clamp(curve.FindIndex(p => p.Hz >= f0), 1, curve.Count - 1);
        double t = (f0 - curve[k - 1].Hz) / (curve[k].Hz - curve[k - 1].Hz);
        Complex load = curve[k - 1].Zin + t * (curve[k].Zin - curve[k - 1].Zin);
        if (!(load.Real > 0)) { MatchResult = $"Zin at {f0 / 1e6:g5} MHz has no resistance to match."; return; }

        var designs = MatchingNetwork.DesignLSection(load, z0, f0,
            InductorQ > 0 ? InductorQ : null, CapacitorQ > 0 ? CapacitorQ : null);
        if (designs.Count == 0) { MatchResult = "Already matched at that frequency; no network needed."; return; }

        var loads = curve.Select(p => (p.Hz, p.Zin)).ToList();
        var lines = new List<string>
        {
            $"Zin at {f0 / 1e6:g5} MHz: {load.Real:g4} {(load.Imaginary < 0 ? "−" : "+")} j{Math.Abs(load.Imaginary):g4} Ω"
            + (MatchFrequencyMHz > 0 ? "" : " (the sweep's best match)") + "; between sweep points Zin is a straight-line reading."
        };
        var series = new List<PlotSeries>
        {
            new("bare", curve.Select(p => p.Hz / 1e6).ToList(),
                curve.Select(p => -PortTermination.ReturnLossDb(p.Zin, z0)).ToList(), Color.FromRgb(0x90, 0x90, 0x90))
        };
        var colors = new[] { Color.FromRgb(0x3D, 0x8B, 0xFD), Color.FromRgb(0xE0, 0x6C, 0x2B), Color.FromRgb(0x2E, 0xA0, 0x5A), Color.FromRgb(0xA0, 0x4C, 0xC8) };
        for (int i = 0; i < designs.Count; i++)
        {
            var matched = MatchingNetwork.Apply(designs[i], loads);
            var band = MatchingNetwork.Band(matched, z0);
            lines.Add($"{i + 1}: {designs[i].Describe()} — "
                + (band is { } b ? $"−10 dB from {b.Low / 1e6:g5} to {b.High / 1e6:g5} MHz" : "no −10 dB point with these Q values"));
            series.Add(new PlotSeries($"match {i + 1}", matched.Select(p => p.FrequencyHz / 1e6).ToList(),
                matched.Select(p => -PortTermination.ReturnLossDb(p.InputImpedance, z0)).ToList(), colors[i % colors.Length]));
        }
        MatchResult = string.Join(Environment.NewLine, lines);
        foreach (var line in lines) _log.Append("Antenna match: " + line);
        MatchImage = CurvePlot.Render(series, logX: false, logY: false, "frequency [MHz]", "|S11| [dB]", height: 220);
    }
}
