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
    private readonly CfdSetupViewModel _cfd;

    public SolveViewModel(ProjectSession session, ILogService log, IEnumerable<ISolver> solvers,
        OpenSim.Solvers.JouleHeatingStudy jouleStudy, MaterialsViewModel materials,
        ElectrodesViewModel electrodes, EnvironmentViewModel environment, CfdSetupViewModel cfd)
    {
        _cfd = cfd;
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

    /// <summary>CFD cell size [m] for the conjugate analysis; 0 = automatic (smallest
    /// body extent / 24, coarsened to the cell budget — the choice is logged).</summary>
    [ObservableProperty] private double _cfdCellSize;

    [RelayCommand]
    private async Task SolveAsync()
    {
        if (_session.SelectedAnalysis.Kind == AnalysisType.EnvironmentThermal)
        {
            await SolveEnvironmentAsync();
            return;
        }
        if (_session.SelectedAnalysis.Kind == AnalysisType.ConjugateHeatFlow)
        {
            await SolveConjugateAsync();
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

        if (!await MeshesPassTheAuditAsync(new[] { body })) return;

        body.Material = _session.SelectedMaterial;
        var kind = _session.SelectedAnalysis.Kind;
        bool transientRequested = kind == AnalysisType.TransientThermal
            || (kind == AnalysisType.JouleCoupled && JouleTransient);
        // Resolving a geometry-scoped condition can fail with an actionable message (a stale
        // id, a curve the mesh no longer carries), so it is reported the way a validation
        // failure is rather than escaping the command.
        IReadOnlyList<BoundaryCondition> conditions;
        IReadOnlyDictionary<int, OpenSim.Core.Model.Material>? regionMaterials;
        try
        {
            conditions = BuildBoundaryConditions(kind, body);
            // A region whose material name no longer resolves is a failure, not a fallback.
            regionMaterials = _materials.ResolveRegionMaterials(body);
        }
        catch (Exception ex)
        {
            _log.Append($"Validation: {ex.Message}");
            _session.StatusText = "Validation failed";
            return;
        }

        var input = new SolveInput
        {
            Mesh = body.Mesh,
            Material = _session.SelectedMaterial,
            BoundaryConditions = conditions,
            RegionMaterials = regionMaterials,
            TransientThermal = transientRequested
                ? new TransientThermalSettings
                {
                    InitialTemperature = InitialTemperature,
                    Duration = TransientDuration,
                    TimeStep = TransientTimeStep,
                    PowerProfile = BuildPowerProfile()
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
    /// A mesh that has not been through the post-mesh audit - one loaded from a project
    /// file, which carries no verdict - is audited here, against its body's geometry, before
    /// any solver sees it. A mesh that fails the audit does not reach a solver.
    /// </summary>
    private async Task<bool> MeshesPassTheAuditAsync(IReadOnlyList<Body> bodies)
    {
        try
        {
            foreach (var body in bodies)
            {
                var report = await Task.Run(() => OpenSim.Meshing.MeshAuditGate.AuditIfNeeded(body));
                if (report is null) continue;
                body.Mesh = body.Mesh!.WithAudit(report);
                _log.Append($"Mesh '{body.Name}' had not been audited; audited before solving: passed.");
            }
            return true;
        }
        catch (MeshAuditException ex)
        {
            _log.Append($"Validation: {ex.Message}");
            _session.StatusText = "Validation failed";
            return false;
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
        var bodies = _session.Bodies.Where(b => b.Role == BodyRole.Solid).ToList();
        if (bodies.Count == 0)
        {
            _log.Append("The project has no solid bodies to solve.");
            return;
        }

        if (!await MeshesPassTheAuditAsync(bodies)) return;

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
                    // The merge and the contact detection depend only on the MESHES, so a
                    // second solve after a material or condition edit reuses them; the
                    // conditions themselves are always resolved fresh.
                    var cache = _session.Assemblies;
                    int hitsBefore = cache.MergeHits;
                    var merged = cache.Assemble(bodies);
                    lines.Add($"Merged {bodies.Count} bodies: {merged.Mesh.NodeCount:N0} nodes, " +
                              $"{merged.Mesh.ElementCount:N0} elements" +
                              (cache.MergeHits > hitsBefore ? " (reused from the last solve)." : "."));
                    var contacts = cache.Contacts(detection,
                        () => ContactDetector.Find(merged.Mesh, merged.NodeBases, detection, lines.Add));
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
                                TimeStep = TransientTimeStep,
                                PowerProfile = BuildPowerProfile()
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
    /// Conjugate heat flow (Stage 2): the same assembly composition as the environment
    /// solve, but the surroundings are RESOLVED — a first-party laminar CFD solve on a
    /// voxel grid computes the flow and fluid temperature around the bodies, and its wall
    /// heat flux drives the solid conduction through the same <see cref="SurfaceFilmModel"/>
    /// seam the Stage 1 correlations fill. Steady exchanges wall temperatures in an outer
    /// loop; transient holds the flow frozen while the solids warm (stated in the log).
    /// </summary>
    private async Task SolveConjugateAsync()
    {
        // A body marked as a fluid volume is NOT material: it never reaches the FE
        // assembly or the voxelizer solid set. Filling the passage with metal would
        // leave no flow path at all, so this filter is load-bearing, not cosmetic.
        var bodies = _session.Bodies.Where(b => b.Role == BodyRole.Solid).ToList();
        int fluidBodies = _session.Bodies.Count - bodies.Count;
        if (bodies.Count == 0)
        {
            _log.Append(fluidBodies > 0
                ? "Every body is marked as a fluid volume — there is no solid to conduct heat."
                : "The project has no bodies to solve.");
            return;
        }
        if (fluidBodies > 0)
            _log.Append($"{fluidBodies} body(ies) marked as fluid volumes are excluded from the " +
                        "FE assembly; they define the flow domain instead.");

        if (!await MeshesPassTheAuditAsync(bodies)) return;

        var environment = _environment.Build();
        _session.Project.Environment = environment;
        if (environment.Medium == MediumKind.Vacuum)
        {
            _log.Append("Validation: a vacuum has no fluid to resolve — use 'Heat flow in an " +
                        "environment', which solves radiation-only exchange.");
            _session.StatusText = "Validation failed";
            return;
        }
        // An INTERNAL circuit is driven by its own inlet, so the surroundings being still
        // says nothing about whether flow exists; the refusal below is about an EXTERNAL
        // domain, where a still fluid without gravity genuinely has no steady state.
        bool internalFlow = _cfd.DomainMode == 1;
        if (!internalFlow && environment.Medium == MediumKind.StillFluid
            && environment.Gravity.Length <= 0)
        {
            _log.Append("Validation: still fluid with zero gravity has no way to carry heat " +
                        "away (no stream, no buoyant plume) — no steady flow state exists. " +
                        "Restore gravity or give the fluid a velocity.");
            _session.StatusText = "Validation failed";
            return;
        }

        var detection = (_session.Project.Assembly ?? new AssemblySettings()).ToDetectionSettings();
        bool transient = !EnvironmentSteadyState;
        var cfd = _cfd.Build(CfdCellSize, environment.FlowVelocity);
        if (cfd is null)
        {
            _session.StatusText = "Validation failed";
            return;
        }
        // Persisted with the project: a CFD case that cannot be saved cannot be re-run,
        // and a number nobody can re-run is not reproducible.
        _session.Project.Cfd = cfd;

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
            FeMeshAssembler.AssembledMesh assembled;
            SolveInput input;
            List<string> setup;
            try
            {
                (assembled, input, setup) = await Task.Run(() =>
                {
                    var lines = new List<string>();
                    // The merge and the contact detection depend only on the MESHES, so a
                    // second solve after a material or condition edit reuses them; the
                    // conditions themselves are always resolved fresh.
                    var cache = _session.Assemblies;
                    int hitsBefore = cache.MergeHits;
                    var merged = cache.Assemble(bodies);
                    lines.Add($"Merged {bodies.Count} bodies: {merged.Mesh.NodeCount:N0} nodes, " +
                              $"{merged.Mesh.ElementCount:N0} elements" +
                              (cache.MergeHits > hitsBefore ? " (reused from the last solve)." : "."));
                    var contacts = cache.Contacts(detection,
                        () => ContactDetector.Find(merged.Mesh, merged.NodeBases, detection, lines.Add));
                    var source = FeMeshAssembler.BuildElementHeatSource(merged, bodies, lines.Add);
                    lines.Add($"Environment (resolved by CFD): {environment.Describe()}.");
                    var built = new SolveInput
                    {
                        Mesh = merged.Mesh,
                        Material = bodies[0].Material!,
                        RegionMaterials = merged.RegionMaterials,
                        BoundaryConditions = merged.BoundaryConditions,
                        ElementHeatSource = source,
                        ThermalContacts = contacts,
                        // Environment stays NULL on purpose: the conjugate study supplies
                        // the surface exchange itself through the prescribed film.
                        TransientThermal = transient
                            ? new TransientThermalSettings
                            {
                                InitialTemperature = InitialTemperature,
                                Duration = TransientDuration,
                                TimeStep = TransientTimeStep,
                                PowerProfile = BuildPowerProfile()
                            }
                            : null
                    };
                    return (merged, built, lines);
                });
            }
            catch (InvalidOperationException ex)
            {
                _log.Append($"Validation: {ex.Message}");
                _session.StatusText = "Validation failed";
                return;
            }
            foreach (var line in setup) _log.Append(line);

            var result = await Task.Run(() => OpenSim.Cfd.ConjugateHeatStudy.Run(
                input, assembled.NodeBases, environment, cfd, progress));
            foreach (var line in result.Thermal.Log)
                _log.Append(line);

            _session.SetAssembledMesh(assembled);
            _session.SetFlowResult(result.Flow, result.Domain);
            _session.RaiseResultsProduced(result.Thermal.Fields,
                analysis: AnalysisType.ConjugateHeatFlow,
                frames: result.Thermal.Frames, frameAxis: result.Thermal.FrameAxis);
            _session.StatusText = "Solve complete";
            _log.Append($"Conjugate solve complete: peak fluid speed " +
                        $"{result.Flow.MaxSpeed():G3} m/s. Use the flow panel to visualize " +
                        "the airflow, or select a result field for the solids.");
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
        // Geometry-scoped edges and vertices become mesh ids here, on transient copies: the
        // project stores the geometry ids because they survive remeshing, and the solvers
        // only ever speak mesh ids. An unresolvable scope throws, and the caller logs it.
        var conditions = GeometryScopeResolver.ResolveForBody(body).ToList();
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
            AnalysisType.Electrostatic => _solvers.First(s => s is OpenSim.Solvers.ElectrostaticSolver),
            _ => throw new InvalidOperationException($"Unknown analysis type '{kind}'.")
        };
        return (solver.Validate, (input, progress) => solver.Solve(input, progress));
    }
}
