using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Services;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;

namespace OpenSim.App.ViewModels;

/// <summary>Runs the selected analysis on the session body and publishes the result
/// fields through <see cref="ProjectSession.ResultsProduced"/>.</summary>
public partial class SolveViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ILogService _log;
    private readonly IReadOnlyList<ISolver> _solvers;
    private readonly OpenSim.Solvers.JouleHeatingStudy _jouleStudy;
    private readonly MaterialsViewModel _materials;
    private readonly ElectrodesViewModel _electrodes;

    public SolveViewModel(ProjectSession session, ILogService log, IEnumerable<ISolver> solvers,
        OpenSim.Solvers.JouleHeatingStudy jouleStudy, MaterialsViewModel materials,
        ElectrodesViewModel electrodes)
    {
        _session = session;
        _log = log;
        _solvers = solvers.ToList();
        _jouleStudy = jouleStudy;
        _materials = materials;
        _electrodes = electrodes;
    }

    // Transient thermal settings (surfaced in the Analysis settings panel).
    [ObservableProperty] private double _initialTemperature = 293.15;   // K (20 °C)
    [ObservableProperty] private double _transientDuration = 10;        // s
    [ObservableProperty] private double _transientTimeStep = 0.1;       // s

    // Modal settings.
    [ObservableProperty] private int _modeCount = 6;

    // AC sweep settings.
    [ObservableProperty] private double _acMinFrequency = 1e3;    // Hz
    [ObservableProperty] private double _acMaxFrequency = 1e8;    // Hz
    [ObservableProperty] private int _acPointCount = 15;

    /// <summary>Joule coupling: run the thermal leg as a transient (backward Euler,
    /// constant DC power) instead of steady-state, using the transient settings above.</summary>
    [ObservableProperty] private bool _jouleTransient;

    [RelayCommand]
    private async Task SolveAsync()
    {
        var body = _session.Body;
        if (body.Mesh is null)
        {
            _log.Append("Generate a mesh before solving.");
            return;
        }
        if (_session.SelectedMaterial is null)
        {
            _log.Append("Select a material before solving.");
            return;
        }

        body.Material = _session.SelectedMaterial;
        var kind = _session.SelectedAnalysis.Kind;
        bool transientRequested = kind == AnalysisType.TransientThermal
            || (kind == AnalysisType.JouleCoupled && JouleTransient);
        var input = new SolveInput
        {
            Mesh = body.Mesh,
            Material = _session.SelectedMaterial,
            BoundaryConditions = BuildBoundaryConditions(kind, body),
            RegionMaterials = _materials.ResolveRegionMaterials(body),
            TransientThermal = transientRequested
                ? new TransientThermalSettings
                {
                    InitialTemperature = InitialTemperature,
                    Duration = TransientDuration,
                    TimeStep = TransientTimeStep
                }
                : null,
            Modal = kind == AnalysisType.Modal ? new ModalSettings { ModeCount = ModeCount } : null,
            HarmonicElectric = kind == AnalysisType.AcElectrical
                ? new HarmonicElectricSettings
                {
                    MinFrequency = AcMinFrequency,
                    MaxFrequency = AcMaxFrequency,
                    PointCount = AcPointCount
                }
                : null
        };

        var (validate, solve) = ResolveAnalysis(kind);
        try
        {
            validate(input);
        }
        catch (Exception ex)
        {
            _log.Append($"Validation: {ex.Message}");
            _session.StatusText = "Validation failed";
            return;
        }

        _session.IsBusy = true;
        _session.StatusText = "Solving…";
        _session.ProgressFraction = 0;
        var progress = new Progress<SolverProgress>(p =>
        {
            _session.StatusText = p.Stage;
            _session.ProgressFraction = p.Fraction;
        });

        try
        {
            var output = await Task.Run(() => solve(input, progress));
            foreach (var line in output.Log)
                _log.Append(line);

            _session.RaiseResultsProduced(output.Fields, analysis: kind,
                frames: output.Frames, frameAxis: output.FrameAxis);
            _session.StatusText = "Solve complete";
            _log.Append("Solve complete. Select a result field to display.");
        }
        catch (Exception ex) { _session.ReportError(ex); }
        finally
        {
            _session.IsBusy = false;
            _session.ProgressFraction = 0;
        }
    }

    /// <summary>
    /// The solve's boundary conditions: the body's own list, plus — for the AC sweep and
    /// Joule analyses — the selected pad electrodes as the electrical excitation. Pads are
    /// merged only when the body carries NO electrical condition of its own: a face-level
    /// merge could silently put two different Dirichlet potentials on shared nodes, so
    /// Conditions-panel entries win outright and the choice is logged either way.
    /// </summary>
    private IReadOnlyList<BoundaryCondition> BuildBoundaryConditions(AnalysisType kind, Body body)
    {
        var conditions = body.BoundaryConditions.ToList();
        if (kind is not (AnalysisType.AcElectrical or AnalysisType.JouleCoupled))
            return conditions;

        var padConditions = _electrodes.TryBuildElectrodeConditions();
        if (padConditions is null)
            return conditions;

        int electrical = conditions.Count(c => c is VoltagePotential or CurrentFlow);
        if (electrical > 0)
        {
            _log.Append($"Using {electrical} electrical condition(s) from the Conditions panel; " +
                        "pad Source/Sink selection ignored.");
            return conditions;
        }
        conditions.AddRange(padConditions);
        _log.Append($"Electrical excitation from pad electrodes: {_electrodes.ElectrodeSummary}.");
        return conditions;
    }

    /// <summary>
    /// Maps the selected analysis to its validate/solve pair. Single-physics analyses
    /// dispatch to a registered <see cref="ISolver"/>; the coupled study orchestrates two.
    /// </summary>
    private (Action<SolveInput> Validate, Func<SolveInput, IProgress<SolverProgress>?, SolveOutput> Solve)
        ResolveAnalysis(AnalysisType kind)
    {
        if (kind == AnalysisType.JouleCoupled)
            return (_jouleStudy.Validate, (input, progress) => _jouleStudy.Solve(input, progress));

        var solver = kind switch
        {
            AnalysisType.Static => _solvers.First(s => s is OpenSim.Solvers.LinearStaticSolver),
            AnalysisType.Electrical => _solvers.First(s => s is OpenSim.Solvers.ElectricalConductionSolver),
            AnalysisType.Thermal => _solvers.First(s => s is OpenSim.Solvers.HeatConductionSolver),
            AnalysisType.TransientThermal => _solvers.First(s => s is OpenSim.Solvers.TransientThermalSolver),
            AnalysisType.Modal => _solvers.First(s => s is OpenSim.Solvers.ModalAnalysisSolver),
            AnalysisType.AcElectrical => _solvers.First(s => s is OpenSim.Solvers.HarmonicElectricSolver),
            _ => throw new InvalidOperationException($"Unknown analysis type '{kind}'.")
        };
        return (solver.Validate, (input, progress) => solver.Solve(input, progress));
    }
}
