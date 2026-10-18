using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Core.PostProcessing;
using OpenSim.Meshing;
using OpenSim.Solvers;

namespace OpenSim.Cli;

/// <summary>The solver settings of one run: the project's saved <see cref="SolverSettings"/>
/// as the defaults (<see cref="FromProject"/>), overridden by the command line or a job file.</summary>
public sealed record StudySettings
{
    /// <summary>The settings a project was saved with; a project without them (every older
    /// file) gives the defaults below.</summary>
    public static StudySettings FromProject(SolverSettings? saved) => saved is null
        ? new StudySettings()
        : new StudySettings
        {
            InitialTemperature = saved.InitialTemperature, Duration = saved.Duration, TimeStep = saved.TimeStep,
            Integrator = saved.Integrator, Tolerance = saved.Tolerance, Capacity = saved.Capacity,
            MinFrequency = saved.MinFrequency, MaxFrequency = saved.MaxFrequency, PointCount = saved.PointCount,
            ModeCount = saved.ModeCount, OpenBoundary = saved.OpenBoundary, EnvironmentSteady = saved.EnvironmentSteady
        };

    public double InitialTemperature { get; init; } = 293.15;
    public double Duration { get; init; } = 10;
    public double TimeStep { get; init; } = 0.1;

    /// <summary>Transient: the time integrator, its tolerance (adaptive) and the capacity matrix.</summary>
    public TimeIntegrator Integrator { get; init; } = TimeIntegrator.BackwardEuler;
    public double Tolerance { get; init; } = 0.01;
    public ThermalCapacity Capacity { get; init; } = ThermalCapacity.Automatic;
    public double MinFrequency { get; init; } = 1e3;
    public double MaxFrequency { get; init; } = 1e8;
    public int PointCount { get; init; } = 15;
    public int ModeCount { get; init; } = 6;

    /// <summary>Electrostatic: the mesh boundary no electrode claims is open space (the
    /// asymptotic 1/r condition) instead of a zero-flux wall.</summary>
    public bool OpenBoundary { get; init; }

    /// <summary>Electrical and Thermal: the recovered flux, its error estimate and flux
    /// probes; null leaves the solve's fields as they are.</summary>
    public FluxRecoverySettings? FluxRecovery { get; init; }

    /// <summary>Environment heat flow: the final equilibrium instead of the time history.</summary>
    public bool EnvironmentSteady { get; init; }

    /// <summary>Conjugate heat flow: a cap on the steady flow march in place of the
    /// project's, and whether a transient marches the flow with the heating.</summary>
    public int? MaxFlowSteps { get; init; }
    public bool TransientMomentum { get; init; }

    /// <summary>The body to solve when the project has several and the analysis is a
    /// single-body one; null is the first solid body.</summary>
    public string? BodyName { get; init; }

    /// <summary>Re-mesh every body even when the file carries a mesh.</summary>
    public bool Remesh { get; init; }
}

/// <summary>What one solve produced, with the report built from it.</summary>
public sealed record StudyRun(string Analysis, string BodyName, SolveInput Input, SolveOutput Output, StudyReport Report);

/// <summary>
/// Runs a project file's analysis the way the application does, without the application:
/// mesh what has no mesh, resolve materials, build the solve input and dispatch to the
/// solver the analysis names. The one place the command line, the sweep and a script share.
/// </summary>
public static class StudyRunner
{
    public static readonly IReadOnlyList<string> Analyses = new[]
    {
        "Static", "Electrical", "Thermal", "JouleCoupled", "TransientThermal", "Modal", "AcElectrical",
        "Electrostatic", "EnvironmentThermal", "ConjugateThermal"
    };

    public static IMeshGenerator DefaultMesher() =>
        new MeshGeneratorSelector(new DelaunayMeshGenerator(), new StructuredLatticeMeshGenerator());

    /// <summary>Meshes every solid body that has geometry and no mesh (or all of them when
    /// <paramref name="force"/>), with each body's own persisted settings.</summary>
    public static void MeshBodies(SimProject project, IMeshGenerator mesher, bool force, Action<string> log)
    {
        foreach (var body in project.Bodies.Where(b => b.Role == BodyRole.Solid))
        {
            if (body.Mesh is not null && !force) continue;
            if (body.Geometry is null)
                throw new InvalidOperationException($"Body '{body.Name}' has no geometry and no mesh; nothing to solve on.");
            body.Mesh = mesher.Generate(body.Geometry, body.MeshSettings);
            log($"Meshed '{body.Name}': {body.Mesh.NodeCount} nodes, {body.Mesh.ElementCount} elements" +
                (body.MeshSettings.Method == MeshMethod.StructuredLattice ? " (structured lattice)." : " (Delaunay)."));
        }
    }

    /// <param name="materialOverrides">Materials by name that take precedence over the
    /// library and the bodies' own copies — how a sweep varies a material property.</param>
    public static StudyRun Solve(SimProject project, string? analysis, MaterialLibrary materials,
        StudySettings settings, Action<string> log, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, Material>? materialOverrides = null)
    {
        analysis ??= project.AnalysisType ?? throw new InvalidOperationException(
            "The project names no analysis; pass --analysis with one of: " + string.Join(", ", Analyses) + ".");
        string kind = Analyses.FirstOrDefault(a => string.Equals(a, analysis, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"Unknown analysis '{analysis}'. Known: {string.Join(", ", Analyses)}.");

        var solids = project.Bodies.Where(b => b.Role == BodyRole.Solid).ToList();
        if (solids.Count == 0) throw new InvalidOperationException("The project has no solid bodies.");
        // The project's own materials, not the library's of the same name (D24).
        ProjectMaterialLookup Lookup(Body first) => new(
            new[] { first }.Concat(solids).Select(b => b.Material), materials.Materials, materialOverrides, log);

        if (kind == "EnvironmentThermal")
            return SolveEnvironment(project, solids, settings, Lookup(solids[0]), log, progress, cancellationToken);
        if (kind == "ConjugateThermal")
            return SolveConjugate(project, solids, settings, Lookup(solids[0]), log, progress, cancellationToken);
        if (kind is "Electrical" or "AcElectrical" && solids.Count > 1 && settings.BodyName is null)
        {
            if (project.Assembly?.ElectricalContactConductance is { } joint)
                return SolveElectricalAssembly(project, kind, solids, settings, joint, Lookup(solids[0]), log, progress,
                    cancellationToken);
            log($"The project has {solids.Count} bodies; solving '{solids[0].Name}' alone. Set the assembly's " +
                "electrical contact conductance (S/m²) to solve them as one assembly joined at their contacts, " +
                "or name one body with --body.");
        }

        var body = settings.BodyName is null
            ? solids[0]
            : solids.FirstOrDefault(b => string.Equals(b.Name, settings.BodyName, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"No body named '{settings.BodyName}'. Bodies: {string.Join(", ", solids.Select(b => b.Name))}.");
        if (body.Mesh is null) throw new InvalidOperationException($"Body '{body.Name}' has no mesh.");
        var lookup = Lookup(body);
        var material = lookup.Of(body.Material ?? throw new InvalidOperationException($"Body '{body.Name}' has no material."));

        bool transient = kind == "TransientThermal";
        var input = new SolveInput
        {
            Mesh = body.Mesh,
            Material = material,
            BoundaryConditions = GeometryScopeResolver.ResolveForBody(body),
            RegionMaterials = RegionMaterialResolver.Resolve(body.Name, body.RegionMaterialNames, lookup.ByName),
            TransientThermal = transient
                ? new TransientThermalSettings
                {
                    InitialTemperature = settings.InitialTemperature, Duration = settings.Duration, TimeStep = settings.TimeStep,
                    Integrator = settings.Integrator, Tolerance = settings.Tolerance, Capacity = settings.Capacity
                }
                : null,
            Modal = kind == "Modal" ? new ModalSettings { ModeCount = settings.ModeCount } : null,
            HarmonicElectric = kind == "AcElectrical"
                ? new HarmonicElectricSettings
                {
                    MinFrequency = settings.MinFrequency, MaxFrequency = settings.MaxFrequency, PointCount = settings.PointCount
                }
                : null,
            Electrostatic = kind == "Electrostatic" && settings.OpenBoundary
                ? new ElectrostaticSettings { OpenBoundary = true }
                : null,
            FluxRecovery = Recovery(kind, settings, log)
        };

        SolveOutput output;
        if (kind == "JouleCoupled")
        {
            var study = new JouleHeatingStudy();
            study.Validate(input);
            output = study.Solve(input, progress, cancellationToken);
        }
        else
        {
            ISolver solver = kind switch
            {
                "Static" => new LinearStaticSolver(),
                "Electrical" => new ElectricalConductionSolver(),
                "Thermal" => new HeatConductionSolver(),
                "TransientThermal" => new TransientThermalSolver(),
                "Modal" => new ModalAnalysisSolver(),
                "AcElectrical" => new HarmonicElectricSolver(),
                "Electrostatic" => new ElectrostaticSolver(),
                _ => throw new InvalidOperationException($"No solver for '{kind}'.")
            };
            solver.Validate(input);
            output = solver.Solve(input, progress, cancellationToken);
        }
        foreach (var line in output.Log) log(line);
        var report = StudyReport.FromSolve(project.Name, body.Name, kind, input, output);
        return new StudyRun(kind, body.Name, input, output, report);
    }

    private static StudyRun SolveEnvironment(SimProject project, List<Body> bodies, StudySettings settings,
        ProjectMaterialLookup lookup, Action<string> log, IProgress<SolverProgress>? progress, CancellationToken ct)
    {
        var environment = project.Environment
                          ?? throw new InvalidOperationException("The project has no environment settings; an environment heat-flow study needs them.");
        foreach (var body in bodies)
        {
            if (body.Mesh is null) throw new InvalidOperationException($"Body '{body.Name}' has no mesh.");
            if (body.Material is null) throw new InvalidOperationException($"Body '{body.Name}' has no material.");
            body.Material = lookup.Of(body.Material);
        }
        var detection = (project.Assembly ?? new AssemblySettings()).ToDetectionSettings();
        var merged = FeMeshAssembler.Assemble(bodies);
        log($"Merged {bodies.Count} bodies: {merged.Mesh.NodeCount} nodes, {merged.Mesh.ElementCount} elements.");
        var contacts = ContactDetector.Find(merged.Mesh, merged.NodeBases, detection, log);
        var source = FeMeshAssembler.BuildElementHeatSource(merged, bodies, log);
        log($"Environment: {environment.Describe()}.");
        var input = new SolveInput
        {
            Mesh = merged.Mesh,
            Material = bodies[0].Material!,
            RegionMaterials = merged.RegionMaterials,
            BoundaryConditions = merged.BoundaryConditions,
            ElementHeatSource = source,
            ThermalContacts = contacts,
            Environment = environment,
            TransientThermal = settings.EnvironmentSteady
                ? null
                : new TransientThermalSettings
                {
                    InitialTemperature = settings.InitialTemperature, Duration = settings.Duration, TimeStep = settings.TimeStep,
                    Integrator = settings.Integrator, Tolerance = settings.Tolerance, Capacity = settings.Capacity
                }
        };
        ISolver solver = settings.EnvironmentSteady ? new HeatConductionSolver() : new TransientThermalSolver();
        solver.Validate(input);
        var output = solver.Solve(input, progress, ct);
        foreach (var line in output.Log) log(line);
        string bodyNames = string.Join(" + ", bodies.Select(b => b.Name));
        var report = StudyReport.FromSolve(project.Name, bodyNames, "EnvironmentThermal", input, output);
        return new StudyRun("EnvironmentThermal", bodyNames, input, output, report);
    }

    /// <summary>
    /// Conjugate heat flow — every solid body merged and joined at its contacts, coupled to
    /// the project's CFD case (<see cref="SimProject.Cfd"/>, as the application last saved
    /// it) in the project's environment; steady with --steady, else the transient. The
    /// flow's notes and the outlet ledger join the log.
    /// </summary>
    private static StudyRun SolveConjugate(SimProject project, List<Body> bodies, StudySettings settings,
        ProjectMaterialLookup lookup, Action<string> log, IProgress<SolverProgress>? progress, CancellationToken ct)
    {
        var environment = project.Environment
                          ?? throw new InvalidOperationException("The project has no environment settings; a conjugate study needs them.");
        var cfd = project.Cfd
                  ?? throw new InvalidOperationException(
                      "The project has no CFD case; set one up and save it from the application's conjugate panel.");
        cfd = cfd with
        {
            MaxSteps = settings.MaxFlowSteps ?? cfd.MaxSteps,
            TransientMomentum = settings.TransientMomentum || cfd.TransientMomentum
        };
        foreach (var body in bodies)
        {
            if (body.Mesh is null) throw new InvalidOperationException($"Body '{body.Name}' has no mesh.");
            if (body.Material is null) throw new InvalidOperationException($"Body '{body.Name}' has no material.");
            body.Material = lookup.Of(body.Material);
        }
        var detection = (project.Assembly ?? new AssemblySettings()).ToDetectionSettings();
        var merged = FeMeshAssembler.Assemble(bodies);
        log($"Merged {bodies.Count} bodies: {merged.Mesh.NodeCount} nodes, {merged.Mesh.ElementCount} elements.");
        var contacts = ContactDetector.Find(merged.Mesh, merged.NodeBases, detection, log);
        var source = FeMeshAssembler.BuildElementHeatSource(merged, bodies, log);
        log($"Environment (resolved by CFD): {environment.Describe()}; flow march cap {cfd.MaxSteps} steps" +
            (cfd.TransientMomentum ? "; the transient marches the flow." : "."));
        var input = new SolveInput
        {
            Mesh = merged.Mesh,
            Material = bodies[0].Material!,
            RegionMaterials = merged.RegionMaterials,
            BoundaryConditions = merged.BoundaryConditions,
            ElementHeatSource = source,
            ThermalContacts = contacts,
            TransientThermal = settings.EnvironmentSteady
                ? null
                : new TransientThermalSettings
                {
                    InitialTemperature = settings.InitialTemperature, Duration = settings.Duration, TimeStep = settings.TimeStep,
                    Integrator = settings.Integrator, Tolerance = settings.Tolerance, Capacity = settings.Capacity
                }
        };
        var result = OpenSim.Cfd.ConjugateHeatStudy.Run(input, merged.NodeBases, environment, cfd, progress,
            cancellationToken: ct);
        foreach (var line in result.Thermal.Log) log(line);
        foreach (var line in result.Flow.Notes) log("Flow: " + line);
        if (result.Outlets is { } outlets)
            log($"Outlets: the stream carries {outlets.HeatRemoved:G4} W away.");
        string bodyNames = string.Join(" + ", bodies.Select(b => b.Name));
        var report = StudyReport.FromSolve(project.Name, bodyNames, "ConjugateThermal", input, result.Thermal);
        return new StudyRun("ConjugateThermal", bodyNames, input, result.Thermal, report);
    }

    /// <summary>
    /// A DC or AC electrical solve of the whole assembly: the bodies merged, their joints
    /// detected and given the project's electrical contact conductance, every body's
    /// voltages and currents on the merged mesh (FU-27).
    /// </summary>
    private static StudyRun SolveElectricalAssembly(SimProject project, string kind, List<Body> bodies,
        StudySettings settings, double jointConductance, ProjectMaterialLookup lookup, Action<string> log,
        IProgress<SolverProgress>? progress, CancellationToken ct)
    {
        foreach (var body in bodies)
        {
            if (body.Mesh is null) throw new InvalidOperationException($"Body '{body.Name}' has no mesh.");
            if (body.Material is null) throw new InvalidOperationException($"Body '{body.Name}' has no material.");
            body.Material = lookup.Of(body.Material);
        }
        var detection = (project.Assembly ?? new AssemblySettings()).ToDetectionSettings();
        var merged = FeMeshAssembler.Assemble(bodies);
        log($"Merged {bodies.Count} bodies: {merged.Mesh.NodeCount} nodes, {merged.Mesh.ElementCount} elements.");
        var contacts = ContactDetector.Find(merged.Mesh, merged.NodeBases, detection, log)
            .Select(c => c.WithConductance(jointConductance)).ToList();
        log($"Electrical joints: {contacts.Count} contact(s) at {jointConductance:g4} S/m².");
        var input = new SolveInput
        {
            Mesh = merged.Mesh,
            Material = bodies[0].Material!,
            RegionMaterials = merged.RegionMaterials,
            BoundaryConditions = merged.BoundaryConditions,
            ElectricalContacts = contacts,
            HarmonicElectric = kind == "AcElectrical"
                ? new HarmonicElectricSettings
                {
                    MinFrequency = settings.MinFrequency, MaxFrequency = settings.MaxFrequency, PointCount = settings.PointCount
                }
                : null,
            Electrostatic = kind == "Electrostatic" && settings.OpenBoundary
                ? new ElectrostaticSettings { OpenBoundary = true }
                : null,
            FluxRecovery = Recovery(kind, settings, log)
        };
        ISolver solver = kind == "AcElectrical" ? new HarmonicElectricSolver() : new ElectricalConductionSolver();
        solver.Validate(input);
        var output = solver.Solve(input, progress, ct);
        foreach (var line in output.Log) log(line);
        string bodyNames = string.Join(" + ", bodies.Select(b => b.Name));
        var report = StudyReport.FromSolve(project.Name, bodyNames, kind, input, output);
        return new StudyRun(kind, bodyNames, input, output, report);
    }

    /// <summary>The scalar outputs of a run as a sweep sees them: the summary values, and
    /// the min and max of every field.</summary>
    /// <summary>Flux recovery and probes belong to the DC conduction and the steady heat
    /// solve; for any other analysis they are dropped, and the log says so.</summary>
    private static FluxRecoverySettings? Recovery(string kind, StudySettings settings, Action<string> log)
    {
        if (settings.FluxRecovery is null || kind is "Electrical" or "Thermal") return settings.FluxRecovery;
        log($"Flux recovery and probes apply to the Electrical and Thermal analyses; not to {kind}.");
        return null;
    }

    public static IReadOnlyDictionary<string, double> Outputs(StudyRun run)
    {
        var outputs = new Dictionary<string, double>();
        if (run.Output.Summary is { } summary)
            foreach (var (k, v) in summary) outputs[k] = v;
        foreach (var field in run.Output.Fields)
        {
            if (field.Count == 0) continue;
            var s = FieldStatistics.Compute(field, run.Input.Mesh);
            outputs[$"Max {field.Name} ({field.Unit})"] = s.Max;
            outputs[$"Min {field.Name} ({field.Unit})"] = s.Min;
        }
        return outputs;
    }
}
