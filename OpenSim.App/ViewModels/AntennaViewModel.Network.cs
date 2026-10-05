using System.IO;
using System.Numerics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls;
using OpenSim.Rf.Network;
using OpenSim.Rf.Si;
using OpenSim.Rf.Surface;

namespace OpenSim.App.ViewModels;

/// <summary>
/// What a designer reads off an antenna solve: S11 against a reference impedance, the match
/// bandwidth, a Smith chart, Touchstone export, conductor loss, and a sweep that places its
/// own frequency points.
/// </summary>
public partial class AntennaViewModel
{
    [ObservableProperty] private double _referenceOhms = 50;

    /// <summary>Let the sweep choose its frequencies (rational interpolation between solver
    /// runs) instead of stepping a fixed number of points.</summary>
    [ObservableProperty] private bool _adaptiveSweep;

    /// <summary>Sheet metal with copper loss instead of a perfect conductor (edge-fed sheet
    /// solves only).</summary>
    [ObservableProperty] private bool _conductorLoss;
    [ObservableProperty] private double _metalConductivity = 5.8e7;
    [ObservableProperty] private double _metalThicknessUm = 35;

    [ObservableProperty] private string _sParameterResult = "";
    [ObservableProperty] private ImageSource? _smithImage;
    [ObservableProperty] private ImageSource? _returnLossImage;
    [ObservableProperty] private bool _hasSParameters;

    private List<(double Hz, Complex Zin)> _networkCurve = new();

    /// <summary>The sheet impedance the panel asks for, or null for perfect metal. A sheet
    /// close over a ground plane carries its current on the face toward the plane; one in
    /// free space on both.</summary>
    private Func<double, Complex>? SheetModel(bool applies, bool overGround)
    {
        if (!ConductorLoss || !applies) return null;
        double sigma = MetalConductivity, thickness = MetalThicknessUm * 1e-6;
        if (!(sigma > 0 && thickness > 0)) return null;
        return f => SheetLoss.CopperSheet(f, sigma, thickness, bothFaces: !overGround);
    }

    private void ClearNetworkResults()
    {
        _networkCurve = new();
        SParameterResult = "";
        SmithImage = null;
        ReturnLossImage = null;
        HasSParameters = false;
    }

    /// <summary>S11 of the swept input impedance: the summary lines, the Smith chart and the
    /// return-loss plot.</summary>
    /// <param name="curve">Zin against frequency: the solved points, or a dense curve
    /// interpolated between them.</param>
    private void ShowNetworkResults(IReadOnlyList<(double Hz, Complex Zin)> curve, string? sweepNote = null)
    {
        ClearNetworkResults();
        if (curve.Count == 0 || !(ReferenceOhms > 0)) return;
        _networkCurve = curve.ToList();
        double z0 = ReferenceOhms;
        var gamma = curve.Select(p => (p.Zin - z0) / (p.Zin + z0)).ToList();
        var checks = curve.Select((p, k) => NetworkChecks.Check(p.Hz, new[,] { { gamma[k] } })).ToList();
        int best = Enumerable.Range(0, gamma.Count).MinBy(k => gamma[k].Magnitude);
        static double Db(double magnitude) => 20 * Math.Log10(Math.Max(magnitude, 1e-12));

        var lines = new List<string>
        {
            $"Best match to {z0:g4} Ω: S11 = {Db(gamma[best].Magnitude):f1} dB at {curve[best].Hz / 1e6:g5} MHz "
            + $"(VSWR {(1 + gamma[best].Magnitude) / Math.Max(1 - gamma[best].Magnitude, 1e-9):g3}, "
            + $"{1 - gamma[best].Magnitude * gamma[best].Magnitude:p1} of the incident power accepted)."
        };
        // The band round the best match where S11 stays under −10 dB.
        const double limit = 0.31622776601683794;
        if (gamma[best].Magnitude < limit && curve.Count > 2)
        {
            double Edge(int from, int step)
            {
                for (int k = from; k + step >= 0 && k + step < gamma.Count; k += step)
                {
                    double a = gamma[k].Magnitude, b = gamma[k + step].Magnitude;
                    if (b >= limit)
                        return curve[k].Hz + (curve[k + step].Hz - curve[k].Hz) * (limit - a) / (b - a);
                }
                return double.NaN;
            }
            double low = Edge(best, -1), high = Edge(best, 1);
            lines.Add(double.IsNaN(low) || double.IsNaN(high)
                ? "S11 is under −10 dB to an end of the sweep: widen it to see the band."
                : $"−10 dB band: {low / 1e6:g5} to {high / 1e6:g5} MHz ({(high - low) / curve[best].Hz:p2} of the centre)"
                  + (sweepNote is null ? " — between solved points this is a straight-line reading." : "."));
        }
        lines.AddRange(NetworkChecks.Describe(checks));
        if (sweepNote is not null) lines.Add(sweepNote);
        SParameterResult = string.Join(Environment.NewLine, lines);
        foreach (string line in lines) _log.Append("Antenna: " + line);

        SmithImage = SmithPlot.Render(gamma, best);
        ReturnLossImage = CurvePlot.Render(new[]
        {
            new PlotSeries("S11", curve.Select(p => p.Hz / 1e6).ToList(), gamma.Select(g => Db(g.Magnitude)).ToList(),
                Color.FromRgb(0x3D, 0x8B, 0xFD))
        }, logX: false, logY: false, "frequency [MHz]", "|S11| [dB]", height: 220);
        HasSParameters = true;
    }

    /// <summary>The interpolated response on 301 points across the band.</summary>
    private static List<(double Hz, Complex Zin)> DenseCurve(RationalSweepResult sweep)
    {
        double from = sweep.FrequenciesHz[0], to = sweep.FrequenciesHz[^1];
        var curve = new List<(double, Complex)>();
        for (int k = 0; k <= 300; k++)
        {
            double f = from + (to - from) * k / 300;
            curve.Add((f, sweep.At(f)[0]));
        }
        return curve;
    }

    private static string AdaptiveNote(RationalSweepResult sweep) =>
        $"Adaptive sweep: {sweep.FrequenciesHz.Count} solver runs, the curve between them by rational interpolation; "
        + (sweep.Converged
            ? $"successive interpolants agree to {sweep.EstimatedError:e1} of the largest |Zin|."
            : $"NOT converged — successive interpolants still differ by {sweep.EstimatedError:e1} of the largest "
              + "|Zin| at the sample limit: raise 'Sweep points' (the limit) or narrow the band.");

    /// <summary>S11 from the points now in the Zin list (the fixed sweep's).</summary>
    private void ShowNetworkResultsFromSweep() =>
        ShowNetworkResults(ZinSweep.OrderBy(p => p.FrequencyHz)
            .Select(p => (p.FrequencyHz, new Complex(p.Resistance, p.Reactance))).ToList());

    [RelayCommand]
    private void ExportS1p()
    {
        if (_networkCurve.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "antenna.s1p", Filter = "Touchstone|*.s1p|All files|*.*", Title = "Save S11 (Touchstone)"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            double z0 = ReferenceOhms;
            File.WriteAllText(dialog.FileName, TouchstoneWriter.Write(
                _networkCurve.Select(p => p.Hz).ToList(),
                _networkCurve.Select(p => new[,] { { (p.Zin - z0) / (p.Zin + z0) } }).ToList(),
                z0, new[] { "antenna feed" }));
            _log.Append($"Antenna: saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) { SParameterResult = "Not saved: " + ex.Message; }
    }
}
