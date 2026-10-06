using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Core.PostProcessing;
using OpenSim.Meshing;
using OpenSim.Solvers;

namespace OpenSim.Cli;

/// <summary>Solver settings the project file does not carry (the app keeps them in its
/// panels), given on the command line instead.</summary>
public sealed record StudySettings
{
    public double InitialTemperature { get; init; } = 293.15;
    public double Duration { get; init; } = 10;
    public double TimeStep { get; init; } = 0.1;
    public double MinFrequency { get; init; } = 1e3;
    public double MaxFrequency { get; init; } = 1e8;
    public int PointCount { get; init; } = 15;
    public int ModeCount { get; init; } = 6;

    /// <summary>Environment heat flow: the final equilibrium instead of the time history.</summary>
    public bool EnvironmentSteady { get; init; }

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
        "Electrostatic", "EnvironmentThermal"
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
        Material? Lookup(string name) =>
            materialOverrides is not null && materialOverrides.TryGetValue(name, out var over) ? over
            : materials.Materials.FirstOrDefault(m => m.Name == name);

        if (kind == "EnvironmentThermal")
            return SolveEnvironment(project, solids, settings, Lookup, log, progress, cancellationToken);

        var body = settings.BodyName is null
            ? solids[0]
            : solids.FirstOrDefault(b => string.Equals(b.Name, settings.BodyName, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"No body named '{settings.BodyName}'. Bodies: {string.Join(", ", solids.Select(b => b.Name))}.");
        if (body.Mesh is null) throw new InvalidOperationException($"Body '{body.Name}' has no mesh.");
        var material = body.Material ?? throw new InvalidOperationException($"Body '{body.Name}' has no material.");
        // A material the library knows by this name takes the library's current values.
        material = Lookup(material.Name) ?? material;

        bool transient = kind == "TransientThermal";
        var input = new SolveInput
        {
            Mesh = body.Mesh,
            Material = material,
            BoundaryConditions = GeometryScopeResolver.ResolveForBody(body),
            RegionMaterials = RegionMaterialResolver.Resolve(body.Name, body.RegionMaterialNames, Lookup),
            TransientThermal = transient
                ? new TransientThermalSettings
                {
                    InitialTemperature = settings.InitialTemperature, Duration = settings.Duration, TimeStep = settings.TimeStep
                }
                : null,
            Modal = kind == "Modal" ? new ModalSettings { ModeCount = settings.ModeCount } : null,
            HarmonicElectric = kind == "AcElectrical"
                ? new HarmonicElectricSettings
                {
                    MinFrequency = settings.MinFrequency, MaxFrequency = settings.MaxFrequency, PointCount = settings.PointCount
                }
                : null
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
        Func<string, Material?> lookup, Action<string> log, IProgress<SolverProgress>? progress, CancellationToken ct)
    {
        var environment = project.Environment
                          ?? throw new InvalidOperationException("The project has no environment settings; an environment heat-flow study needs them.");
        foreach (var body in bodies)
        {
            if (body.Mesh is null) throw new InvalidOperationException($"Body '{body.Name}' has no mesh.");
            if (body.Material is null) throw new InvalidOperationException($"Body '{body.Name}' has no material.");
            body.Material = lookup(body.Material.Name) ?? body.Material;
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
                    InitialTemperature = settings.InitialTemperature, Duration = settings.Duration, TimeStep = settings.TimeStep
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

    /// <summary>The scalar outputs of a run as a sweep sees them: the summary values, and
    /// the min and max of every field.</summary>
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
