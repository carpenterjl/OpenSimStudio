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
    private readonly EnvironmentViewModel _environment;

    public SolveViewModel(ProjectSession session, ILogService log, IEnumerable<ISolver> solvers,
        OpenSim.Solvers.JouleHeatingStudy jouleStudy, MaterialsViewModel materials,
        ElectrodesViewModel electrodes, EnvironmentViewModel environment)
    {
        _session = session;
        _log = log;
        _solvers = solvers.ToList();
        _jouleStudy = jouleStudy;
        _materials = materials;
        _electrodes = electrodes;
        _environment = environment;
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

    /// <summary>Environment heat flow: solve the final equilibrium directly instead of the
    /// time history. The same nonlinear film/radiation coupling is iterated to convergence,
    /// so it answers "how hot does it end up" without integrating the way there.</summary>
    [ObservableProperty] private bool _environmentSteadyState;

    [RelayCommand]
    private async Task SolveAsync()
    {
        if (_session.SelectedAnalysis.Kind == AnalysisType.EnvironmentThermal)
        {
            await SolveEnvironmentAsync();
            return;
        }

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
    /// Environment heat flow: the whole assembly solved at once. Every body is meshed
    /// independently and merged here (<see cref="FeMeshAssembler"/>), the joints between
    /// them are detected and given a finite conductance, each body's dissipation becomes a
    /// volumetric source, and the surroundings supply convection + radiation on every
    /// exterior face the user did not claim with a condition of their own.
    /// </summary>
    private async Task SolveEnvironmentAsync()
    {
        var bodies = _session.Bodies.ToList();
        if (bodies.Count == 0)
        {
            _log.Append("The project has no bodies to solve.");
            return;
        }

        var environment = _environment.Build();
        _session.Project.Environment = environment;
        var detection = (_session.Project.Assembly ?? new AssemblySettings()).ToDetectionSettings();
        bool transient = !EnvironmentSteadyState;
        var solver = transient
            ? (ISolver)_solvers.First(s => s is OpenSim.Solvers.TransientThermalSolver)
            : _solvers.First(s => s is OpenSim.Solvers.HeatConductionSolver);

        _session.IsBusy = true;
        _session.StatusText = "Assembling…";
        _session.ProgressFraction = 0;
        var progress = new Progress<SolverProgress>(p =>
        {
            _session.StatusText = p.Stage;
            _session.ProgressFraction = p.Fraction;
        });

        try
        {
            // Merge + contact detection are O(surface) on a real assembly, so they run off
            // the UI thread with the solve rather than freezing the window first.
            FeMeshAssembler.AssembledMesh assembled;
            SolveInput input;
            List<string> setup;
            try
            {
                (assembled, input, setup) = await Task.Run(() =>
                {
                    var lines = new List<string>();
                    var merged = FeMeshAssembler.Assemble(bodies);
                    lines.Add($"Merged {bodies.Count} bodies: {merged.Mesh.NodeCount:N0} nodes, " +
                              $"{merged.Mesh.ElementCount:N0} elements.");
                    var contacts = ContactDetector.Find(merged.Mesh, merged.NodeBases, detection, lines.Add);
                    var source = FeMeshAssembler.BuildElementHeatSource(merged, bodies, lines.Add);
                    lines.Add($"Environment: {environment.Describe()}.");
                    var built = new SolveInput
                    {
                        Mesh = merged.Mesh,
                        Material = bodies[0].Material!,
                        RegionMaterials = merged.RegionMaterials,
                        BoundaryConditions = merged.BoundaryConditions,
                        ElementHeatSource = source,
                        ThermalContacts = contacts,
                        Environment = environment,
                        TransientThermal = transient
                            ? new TransientThermalSettings
                            {
                                InitialTemperature = InitialTemperature,
                                Duration = TransientDuration,
                                TimeStep = TransientTimeStep
                            }
                            : null
                    };
                    solver.Validate(built);
                    return (merged, built, lines);
                });
            }
            catch (InvalidOperationException ex)
            {
                // A part with no mesh, no material, or a body nothing anchors: all of these
                // are things the user must fix, not failures of the run.
                _log.Append($"Validation: {ex.Message}");
                _session.StatusText = "Validation failed";
                return;
            }
            foreach (var line in setup) _log.Append(line);

            var output = await Task.Run(() => solver.Solve(input, progress));
            foreach (var line in output.Log)
                _log.Append(line);

            // Published before the results so the scene, which rebuilds on the result
            // event, already knows which merged node belongs to which body.
            _session.SetAssembledMesh(assembled);
            _session.RaiseResultsProduced(output.Fields, analysis: AnalysisType.EnvironmentThermal,
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
