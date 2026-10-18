using System.IO;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Solvers;

namespace OpenSim.App.ViewModels;

/// <summary>
/// Pulsed power for the transient thermal solves, and the thermal impedance curve of the
/// model with its Foster and Cauer networks (<see cref="ThermalImpedanceSolver"/>,
/// <see cref="ThermalNetworkFit"/>).
/// </summary>
public partial class SolveViewModel
{
    /// <summary>Switch the heat sources on and off as a pulse train during a transient.</summary>
    [ObservableProperty] private bool _pulsedPower;
    [ObservableProperty] private double _pulsePeriod = 1.0;          // s
    [ObservableProperty] private double _pulseDutyPercent = 50;

    /// <summary>Heat capacity matrix of a transient: 0 automatic, 1 consistent, 2 lumped
    /// (<see cref="ThermalCapacity"/>).</summary>
    [ObservableProperty] private int _transientCapacityIndex;

    /// <summary>Time integrator of a transient: 0 backward Euler, 1 TR-BDF2, 2 adaptive
    /// (<see cref="TimeIntegrator"/>).</summary>
    [ObservableProperty] private int _transientIntegratorIndex;

    /// <summary>The adaptive integrator's error per step [K].</summary>
    [ObservableProperty] private double _transientTolerance = 0.01;

    /// <summary>Z_th from the full nonlinear transient instead of the linear model about
    /// the steady state.</summary>
    [ObservableProperty] private bool _zthNonlinear;

    [ObservableProperty] private double _zthStartTime = 1e-4;        // s
    [ObservableProperty] private double _zthEndTime = 100;           // s

    /// <summary>Stages of the fitted network; 0 = as few as follow the curve within 0.5 %.</summary>
    [ObservableProperty] private int _zthStages;

    [ObservableProperty] private string _zthResult = "";
    [ObservableProperty] private ImageSource? _zthPlot;
    [ObservableProperty] private bool _hasZth;

    /// <summary>The mutual-impedance matrix over every heat-flow condition (FU-44): one linear
    /// run per source with the others off, each read at every source.</summary>
    [ObservableProperty] private bool _zthMutual;

    private ThermalImpedanceCurve? _zthCurve;
    private FosterFit? _zthFit;
    private CauerNetwork? _zthLadder;
    private ThermalImpedanceMatrix? _zthMatrix;
    private FosterMatrixFit? _zthMatrixFit;

    /// <summary>The transient settings the panel asks for.</summary>
    private TransientThermalSettings BuildTransientSettings() => new()
    {
        InitialTemperature = InitialTemperature,
        Duration = TransientDuration,
        TimeStep = TransientTimeStep,
        PowerProfile = BuildPowerProfile(),
        Capacity = (ThermalCapacity)Math.Clamp(TransientCapacityIndex, 0, 2),
        Integrator = (TimeIntegrator)Math.Clamp(TransientIntegratorIndex, 0, 2),
        Tolerance = TransientTolerance
    };

    /// <summary>The profile the transient settings ask for; null = constant power.</summary>
    private PowerProfile? BuildPowerProfile() => PulsedPower
        ? PowerProfile.Pulse(PulsePeriod, Math.Clamp(PulseDutyPercent / 100, 0, 1))
        : null;

    [RelayCommand]
    private async Task SolveThermalImpedanceAsync()
    {
        var body = _session.Body;
        if (body.Mesh is null) { ZthResult = "Generate a mesh first."; return; }
        if (_session.SelectedMaterial is null) { ZthResult = "Select a material first."; return; }
        SolveInput input;
        try
        {
            var conditions = BuildBoundaryConditions(AnalysisType.Thermal, body)
                .Where(c => c is FixedTemperature or HeatFlux or Convection).ToList();
            input = new SolveInput
            {
                Mesh = body.Mesh,
                Material = _session.SelectedMaterial,
                BoundaryConditions = conditions,
                RegionMaterials = _materials.ResolveRegionMaterials(body),
                Environment = _session.SelectedAnalysis.Kind == AnalysisType.EnvironmentThermal ? _environment.Build() : null,
                SurfaceRadiation = BuildRadiationSets(_session.SelectedAnalysis.Kind)
            };
        }
        catch (Exception ex) { ZthResult = ex.Message; return; }

        var settings = new ThermalImpedanceSettings { StartTime = ZthStartTime, EndTime = ZthEndTime, Nonlinear = ZthNonlinear };
        if (ZthMutual)
        {
            await SolveThermalImpedanceMatrixAsync(input, settings);
            return;
        }
        _zthMatrix = null;
        _zthMatrixFit = null;
        int stages = Math.Max(0, ZthStages);
        _session.IsBusy = true;
        _session.StatusText = "Thermal impedance…";
        var progress = new Progress<SolverProgress>(p =>
        {
            _session.StatusText = p.Stage;
            _session.ProgressFraction = p.Fraction;
        });
        try
        {
            var (result, fit, ladder) = await Task.Run(() =>
            {
                var r = ThermalImpedanceSolver.Solve(input, null, settings, progress);
                var curve = r.Curves[0];
                var f = ThermalNetworkFit.FitFoster(curve.TimesSeconds, curve.KelvinPerWatt, stages);
                CauerNetwork? l = null;
                try { l = ThermalNetworkFit.ToCauer(f.Network); } catch (InvalidOperationException) { }
                return (r, f, l);
            });
            _zthCurve = result.Curves[0];
            _zthFit = fit;
            _zthLadder = ladder;
            HasZth = true;
            foreach (string line in result.Log) _log.Append(line);
            _log.Append(fit.Describe());

            var times = _zthCurve.TimesSeconds;
            ZthPlot = Controls.CurvePlot.Render(new List<Controls.PlotSeries>
            {
                new(_zthCurve.Name, times, _zthCurve.KelvinPerWatt, Color.FromRgb(0x3D, 0x8B, 0xFD)),
                new($"{fit.Network.Stages}-stage network", times, times.Select(fit.Network.Impedance).ToList(),
                    Color.FromRgb(0xE5, 0x53, 0x4B), Dashed: true)
            }, logX: true, logY: true, "time [s]", "Z_th [K/W]");
            var text = new StringBuilder();
            text.AppendLine($"Z_th at '{_zthCurve.Name}': {_zthCurve.KelvinPerWatt[^1]:g4} K/W at {times[^1]:g3} s" +
                            (double.IsNaN(_zthCurve.SteadyKelvinPerWatt) ? " (no steady value: nothing carries the heat away)."
                                : $", {_zthCurve.SteadyKelvinPerWatt:g4} K/W steady."));
            text.AppendLine(fit.Describe());
            text.AppendLine("Foster R [K/W] / τ [s]: " + string.Join("; ",
                fit.Network.Resistances.Select((r, i) => $"{r:g4} / {fit.Network.TimeConstants[i]:g3}")));
            text.Append(ladder is null
                ? "No Cauer ladder: the conversion failed for these stages."
                : "Cauer R [K/W] / C [J/K]: " + string.Join("; ",
                    ladder.Resistances.Select((r, i) => $"{r:g4} / {ladder.Capacitances[i]:g3}")));
            if (PulsedPower)
                text.Append($"{Environment.NewLine}Pulsed peak at {PulseDutyPercent:g3} % of {PulsePeriod:g3} s: " +
                            $"{fit.Network.PulsedPeak(PulsePeriod, Math.Clamp(PulseDutyPercent / 100, 0, 1)):g4} K/W of peak power.");
            ZthResult = text.ToString();
            _session.StatusText = "Thermal impedance done";
        }
        catch (Exception ex) { ZthResult = ex.Message; _session.ReportError(ex); }
        finally
        {
            _session.IsBusy = false;
            _session.ProgressFraction = 0;
        }
    }

    /// <summary>The mutual matrix: every heat-flow condition a source, the curves [at ← from], the
    /// steady matrix, and the Foster matrix fitted to them with shared time constants.</summary>
    private async Task SolveThermalImpedanceMatrixAsync(SolveInput input, ThermalImpedanceSettings settings)
    {
        var sources = input.BoundaryConditions.OfType<HeatFlux>()
            .Select(h => new ThermalSource { Name = h.Name, HeatFlow = h.Name }).ToList();
        if (sources.Count < 2)
        {
            ZthResult = "The mutual matrix needs two or more heat-flow conditions on the model (each is one source).";
            return;
        }
        if (settings.Nonlinear)
        {
            ZthResult = "The mutual matrix belongs to the linear model (superposition); untick the nonlinear surface exchange.";
            return;
        }
        _session.IsBusy = true;
        _session.StatusText = "Thermal impedance matrix…";
        var progress = new Progress<SolverProgress>(p =>
        {
            _session.StatusText = p.Stage;
            _session.ProgressFraction = p.Fraction;
        });
        try
        {
            var (matrix, fit) = await Task.Run(() =>
            {
                var m = ThermalImpedanceSolver.SolveMatrix(input, sources, settings, progress);
                return (m, ThermalNetworkFit.FitFosterMatrix(m));
            });
            _zthMatrix = matrix;
            _zthMatrixFit = fit;
            _zthCurve = null;
            _zthFit = null;
            _zthLadder = null;
            HasZth = true;
            foreach (string line in matrix.Log) _log.Append(line);
            _log.Append(fit.Describe());
            var colors = new[]
            {
                Color.FromRgb(0x3D, 0x8B, 0xFD), Color.FromRgb(0xE0, 0x6C, 0x2B), Color.FromRgb(0x2E, 0xA0, 0x5A),
                Color.FromRgb(0xA0, 0x4C, 0xC8), Color.FromRgb(0xE5, 0x53, 0x4B), Color.FromRgb(0x9A, 0xA0, 0xA6)
            };
            int n = matrix.Sources.Count;
            var series = new List<Controls.PlotSeries>();
            for (int at = 0; at < n && series.Count < 12; at++)
                for (int from = 0; from < n && series.Count < 12; from++)
                {
                    var curve = matrix[at, from];
                    series.Add(new(curve.Name, curve.TimesSeconds, curve.KelvinPerWatt, colors[series.Count % colors.Length], Dashed: at != from));
                }
            ZthPlot = Controls.CurvePlot.Render(series, logX: true, logY: true, "time [s]", "Z_th [K/W]");
            var text = new StringBuilder();
            text.AppendLine($"Mutual Z_th over {n} sources (rise at the row's faces per watt of the column's source alone):");
            text.AppendLine("steady [K/W]: " + string.Join("  |  ", Enumerable.Range(0, n).Select(at =>
                $"{matrix.Sources[at]}: " + string.Join(", ", Enumerable.Range(0, n).Select(from =>
                    double.IsNaN(matrix[at, from].SteadyKelvinPerWatt) ? "—" : $"{matrix[at, from].SteadyKelvinPerWatt:g4}")))));
            text.Append(fit.Describe());
            ZthResult = text.ToString();
            _session.StatusText = "Thermal impedance matrix done";
        }
        catch (Exception ex) { ZthResult = ex.Message; _session.ReportError(ex); }
        finally
        {
            _session.IsBusy = false;
            _session.ProgressFraction = 0;
        }
    }

    [RelayCommand]
    private void SaveZthCurve()
    {
        if (_zthMatrix is { } matrix)
        {
            var columns = Enumerable.Range(0, matrix.Sources.Count)
                .SelectMany(at => Enumerable.Range(0, matrix.Sources.Count).Select(from => matrix[at, from])).ToList();
            var csv = new StringBuilder("time [s]," + string.Join(",", columns.Select(c => $"\"{c.Name} [K/W]\"")) + "\n");
            var times = columns[0].TimesSeconds;
            for (int i = 0; i < times.Count; i++)
                csv.Append(System.Globalization.CultureInfo.InvariantCulture, $"{times[i]:G9}")
                   .Append(string.Concat(columns.Select(c => "," + c.KelvinPerWatt[i].ToString("G9", System.Globalization.CultureInfo.InvariantCulture))))
                   .Append('\n');
            Save("zth-matrix.csv", "CSV|*.csv", csv.ToString());
            return;
        }
        if (_zthCurve is null) return;
        var text = new StringBuilder("time [s],Z_th [K/W]\n");
        for (int i = 0; i < _zthCurve.TimesSeconds.Count; i++)
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"{_zthCurve.TimesSeconds[i]:G9},{_zthCurve.KelvinPerWatt[i]:G9}\n");
        Save("zth-curve.csv", "CSV|*.csv", text.ToString());
    }

    [RelayCommand]
    private void SaveZthNetworks()
    {
        if (_zthMatrixFit is { } matrixFit)
        {
            Save("zth-matrix.cir", "SPICE netlist|*.cir;*.lib|All files|*.*",
                "* " + matrixFit.Describe() + Environment.NewLine + matrixFit.Network.ToSpice());
            return;
        }
        if (_zthFit is null) return;
        var text = new StringBuilder();
        text.AppendLine("* " + _zthFit.Describe());
        text.Append(_zthFit.Network.ToSpice());
        if (_zthLadder is not null) text.AppendLine().Append(_zthLadder.ToSpice());
        Save("zth-networks.cir", "SPICE netlist|*.cir;*.lib|All files|*.*", text.ToString());
    }

    [RelayCommand]
    private void SaveZthTable()
    {
        if (_zthFit is null) return;
        var text = new StringBuilder("Foster\n").Append(_zthFit.Network.ToCsv());
        if (_zthLadder is not null) text.Append("\nCauer\n").Append(_zthLadder.ToCsv());
        Save("zth-networks.csv", "CSV|*.csv", text.ToString());
    }

    private void Save(string fileName, string filter, string text)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = fileName, Filter = filter, Title = "Save thermal impedance" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, text);
            _log.Append($"Thermal impedance saved to {dialog.FileName}.");
        }
        catch (Exception ex) { _session.ReportError(ex); }
    }
}
