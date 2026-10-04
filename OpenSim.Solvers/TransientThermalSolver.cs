using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers.Environment;

namespace OpenSim.Solvers;

/// <summary>
/// Transient heat conduction over TET4 elements: ρc_p·∂T/∂t = ∇·(k∇T) + q, integrated
/// with backward Euler. BE is unconditionally stable AND monotone — a step change (the
/// dominant use case) never produces the non-physical temperature oscillations
/// Crank–Nicolson shows on stiff modes at practical step sizes; its O(Δt) truncation is
/// controlled by the step size instead. Each step solves the SPD system
/// (M/Δt + K)·Tⁿ = (M/Δt)·Tⁿ⁻¹ + f with the shared Jacobi-CG, reduced once and
/// warm-started from the previous step. No Dirichlet/Robin condition is required:
/// M/Δt regularizes the matrix, so a purely adiabatic heating ramp is well-posed.
/// </summary>
public sealed class TransientThermalSolver : ISolver
{
    /// <summary>Hard cap on stored result frames — beyond this, memory (every frame
    /// keeps full nodal temperature + flux) outweighs any scrubbing benefit.</summary>
    private const int MaxStoredFrames = 500;

    public string Name => "Transient heat conduction (thermal)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The transient thermal solver supports linear tetrahedral (TET4) meshes only; " +
                "re-generate the mesh with linear tetrahedral elements.");

        var settings = input.TransientThermal ?? throw new InvalidOperationException(
            "Transient thermal settings (initial temperature, duration, time step) are missing.");
        if (settings.TimeStep <= 0)
            throw new InvalidOperationException("The time step must be positive.");
        if (settings.Duration < settings.TimeStep)
            throw new InvalidOperationException("The duration must be at least one time step.");
        if (settings.InitialTemperature <= 0)
            throw new InvalidOperationException("The initial temperature must be positive (absolute kelvin).");
        if (settings.OutputStride < 0)
            throw new InvalidOperationException("The output stride cannot be negative.");

        var (steps, stride) = PlanSteps(settings);
        int stored = 2 + (steps - 1) / stride;   // initial state + strided steps + final
        if (stored > MaxStoredFrames)
            throw new InvalidOperationException(
                $"This run would store {stored} result frames ({steps} steps at stride {stride}); " +
                $"the limit is {MaxStoredFrames}. Increase OutputStride (or the time step).");

        input.Material.ValidateThermalTransient();
        if (input.RegionMaterials is not null)
            foreach (var material in input.RegionMaterials.Values)
                material.ValidateThermalTransient();

        if (input.ElementHeatSource is not null && input.ElementHeatSource.Count != input.Mesh.ElementCount)
            throw new InvalidOperationException(
                $"ElementHeatSource has {input.ElementHeatSource.Count} entries but the mesh has " +
                $"{input.Mesh.ElementCount} elements.");

        foreach (var bc in input.BoundaryConditions)
        {
            if (bc is not (FixedTemperature or HeatFlux or Convection))
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to a thermal solve. " +
                    "Use fixed temperatures, heat fluxes, or convection.");
            BoundaryScope.Validate(bc, input.Mesh);
            if (bc is Convection { Coefficient: <= 0 } c)
                throw new InvalidOperationException(
                    $"Convection '{c.Name}': the heat transfer coefficient must be positive.");
        }

        EnvironmentBoundaryModel.ValidateMaterials(input);
        HeatConductionSolver.ValidatePrescribedFilm(input);

        // No anchoring requirement here, unlike the steady solver: M/Δt regularizes every
        // step, so a body that only stores and receives heat is perfectly well-posed.
        if (input.ThermalContacts is not null)
            foreach (var contact in input.ThermalContacts)
                contact.Validate(input.Mesh.NodeCount);
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;
        var settings = input.TransientThermal!;
        double dt = settings.TimeStep;
        var (steps, stride) = PlanSteps(settings);

        progress?.Report(new SolverProgress("Assembling matrices", 0.02));
        var assembler = new ScalarDiffusionAssembler(mesh,
            el => input.MaterialOf(el).ThermalConductivity!.Value);
        var robin = new List<ScalarDiffusionAssembler.RobinTerm>();
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
            foreach (var t in mesh.GetFaceTriangles(convection.FaceIds))
                robin.Add(new ScalarDiffusionAssembler.RobinTerm(t, convection.Coefficient));
        // Contacts enter the conduction matrix only: an interface exchanges heat, it stores none.
        var conduction = assembler.AssembleStiffness(robin, input.ThermalContacts, cancellationToken);
        var mass = assembler.AssembleMass(el =>
        {
            var m = input.MaterialOf(el);
            return m.Density * m.SpecificHeat!.Value;
        }, cancellationToken);

        // System matrix of one backward-Euler step: A = M/Δt + K (SPD; the capacity term
        // regularizes it even without any Dirichlet/Robin condition).
        var systemBuilder = new SparseMatrixBuilder(mesh.NodeCount, mesh.NodeCount);
        AddScaled(systemBuilder, mass, 1.0 / dt);
        AddScaled(systemBuilder, conduction, 1.0);
        var system = systemBuilder.Build();
        log.Add($"Assembled {system.RowCount} DOF system, {system.NonZeroCount} non-zeros" +
                (robin.Count > 0 ? $" including {robin.Count} convective surface triangles." : "."));
        HeatConductionSolver.LogContacts(input, log);

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.05));
        var constantLoads = ScalarSolverHelpers.AssembleThermalLoads(input, log);

        // A prescribed film is FIXED over the whole transient (the frozen-flow contract of
        // the conjugate study): fold its Robin terms into the step matrix and loads once,
        // and the plain linear time loop below runs unchanged — no Picard needed.
        // A SCHEDULE means the film changes every step, so it must NOT be folded into the
        // base system here: the base stays film-free and each step folds its own.
        var filmSchedule = input.PrescribedFilmSchedule;
        SurfaceFilmModel? prescribedFilm = input.PrescribedFilm;
        if (filmSchedule is not null)
        {
            log.Add("Prescribed film schedule: the film is re-evaluated every time step " +
                    "(time-accurate conjugate coupling), so the step matrix is refolded per step" +
                    (input.PrescribedFilmStepAccepted is null
                        ? "."
                        : ", and each step is repeated until the schedule's owner accepts it."));
        }
        else if (prescribedFilm is not null)
        {
            system = EnvironmentThermalTerms.WithFilm(system, mesh, prescribedFilm);
            constantLoads = EnvironmentThermalTerms.WithFilmLoads(constantLoads, mesh, prescribedFilm);
            log.Add($"Prescribed film ({prescribedFilm.Origin}): {prescribedFilm.WettedCount} wetted " +
                    "triangles, held constant over the transient.");
        }
        var prescribed = new Dictionary<int, double>();
        foreach (var temperature in input.BoundaryConditions.OfType<FixedTemperature>())
        {
            var nodes = mesh.GetScopeNodes(temperature);
            foreach (int node in nodes)
                prescribed[node] = temperature.Kelvin;
            log.Add($"Temperature '{temperature.Name}': {temperature.Kelvin:g4} K on {nodes.Count} nodes.");
        }
        var reduced = ConstrainedSystemSolver.Reduce(system, prescribed, allowUnconstrained: true);

        // Initial state: uniform, except nodes held at their prescribed values.
        var temperature_ = new double[mesh.NodeCount];
        Array.Fill(temperature_, settings.InitialTemperature);
        foreach (var (node, value) in prescribed)
            temperature_[node] = value;

        var environment = EnvironmentBoundaryModel.Build(input, log);
        var film = filmSchedule?.Invoke(0, 0.0, temperature_) ?? prescribedFilm ?? environment?.Evaluate(temperature_);
        var frames = new List<ResultFrame>
        {
            MakeFrame(0.0, temperature_, mesh, assembler, input, film)
        };
        var cg = new ConjugateGradientSolver
        {
            Tolerance = 1e-10,
            MaxIterations = Math.Max(4 * reduced.FreeCount, 1000)
        };
        // On the linear path every step solves the SAME reduced matrix, so its Jacobi
        // preconditioner is built once here instead of per step. A film or environment
        // refolds the matrix each pass and gets a fresh one; the numbers are identical
        // either way, only the rebuild of an unchanged diagonal is avoided.
        double[] reducedInvDiag = ConjugateGradientSolver.BuildJacobiPreconditioner(reduced.Reduced);

        // The film paths reduce ONCE too, and rewrite only the entries the film touches. Built
        // lazily: a run with neither a schedule nor an environment must not pay for it.
        FilmUpdatableSystem? updatable = filmSchedule is not null || environment is not null
            ? FilmUpdatableSystem.Prepare(system, mesh, prescribed)
            : null;
        var massTimesT = new double[mesh.NodeCount];
        var fullRhs = new double[mesh.NodeCount];
        long totalIterations = 0;
        long nonlinearIterations = 0;
        long couplingIterations = 0;

        for (int n = 1; n <= steps; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mass.Multiply(temperature_, massTimesT);

            // Without an environment this loop runs exactly once and reuses the system
            // reduced before the time loop — the linear path, unchanged to the last bit.
            // With one, each pass re-evaluates the surface film coefficients at the latest
            // iterate: the lagged-coefficient fixed point of the backward-Euler step.
            var iterate = temperature_;
            int picard = 0;
            double change = 0;
            while (true)
            {
                picard++;
                ConstrainedSystemSolver.ReducedSystem stepSystem;
                if (filmSchedule is not null)
                {
                    // Linear step, like the fixed film — only the film's own numbers move, so
                    // there is nothing to iterate on: pull this step's film, rewrite the
                    // surface entries, solve once.
                    film = filmSchedule(n, n * dt, iterate);
                    updatable!.Apply(film);
                    stepSystem = updatable.System;
                    var scheduledLoads = EnvironmentThermalTerms.WithFilmLoads(constantLoads, mesh, film);
                    for (int i = 0; i < fullRhs.Length; i++)
                        fullRhs[i] = massTimesT[i] / dt + scheduledLoads[i];
                }
                else if (environment is null)
                {
                    stepSystem = reduced;
                    for (int i = 0; i < fullRhs.Length; i++)
                        fullRhs[i] = massTimesT[i] / dt + constantLoads[i];
                }
                else
                {
                    film = environment.Evaluate(iterate);
                    updatable!.Apply(film);
                    stepSystem = updatable.System;
                    var stepLoads = EnvironmentThermalTerms.WithFilmLoads(constantLoads, mesh, film);
                    for (int i = 0; i < fullRhs.Length; i++)
                        fullRhs[i] = massTimesT[i] / dt + stepLoads[i];
                }

                var rhs = stepSystem.ReduceLoads(fullRhs);
                var free = stepSystem.Restrict(iterate);   // warm start from the previous step
                double[] invDiag = ReferenceEquals(stepSystem, reduced)
                    ? reducedInvDiag
                    : ConjugateGradientSolver.BuildJacobiPreconditioner(stepSystem.Reduced);
                var iterations = cg.Solve(stepSystem.Reduced, rhs, free, invDiag, cancellationToken);
                if (!iterations.Converged)
                    throw new InvalidOperationException(
                        $"Time step {n} did not converge after {iterations.Iterations} CG iterations " +
                        $"(residual {iterations.ResidualNorm:g3}). Check materials and mesh quality.");
                totalIterations += iterations.Iterations;
                var next = stepSystem.Expand(free);

                if (filmSchedule is not null && input.PrescribedFilmStepAccepted is { } accepted)
                {
                    // Implicit coupling owned by the schedule's author: the step is solved
                    // again, from the same start state, until the film it was given agrees
                    // with the temperatures it produced.
                    iterate = next;
                    couplingIterations++;
                    if (accepted(n, next)) break;
                    continue;
                }
                if (environment is null)
                {
                    iterate = next;
                    break;
                }
                nonlinearIterations++;
                change = EnvironmentThermalTerms.MaxChange(iterate, next);
                iterate = next;
                if (picard >= 2 && change < EnvironmentThermalTerms.Threshold(iterate)) break;
                if (picard >= EnvironmentThermalTerms.MaxTransientIterations)
                    throw new InvalidOperationException(
                        $"Time step {n}: the environment's surface coefficients did not settle " +
                        $"after {picard} iterations (last change {change:g3} K). Reduce the time " +
                        "step — a step that moves the surface temperature far in one go makes the " +
                        "film coefficients chase it.");
            }
            temperature_ = iterate;

            if (n % stride == 0 || n == steps)
                frames.Add(MakeFrame(n * dt, temperature_, mesh, assembler, input, film));
            progress?.Report(new SolverProgress($"Time step {n}/{steps}", 0.05 + 0.95 * n / steps));
        }

        double endTime = steps * dt;
        log.Add($"Backward Euler: {steps} steps of Δt = {dt:g4} s to t = {endTime:g4} s " +
                $"({totalIterations} CG iterations total); {frames.Count} frames stored.");
        if (couplingIterations > 0)
            log.Add($"Film schedule: {couplingIterations} coupled solves over {steps} steps " +
                    $"({(double)couplingIterations / steps:F1} per step).");
        if (environment is not null)
        {
            log.Add($"Environment: {nonlinearIterations} nonlinear iterations over {steps} steps " +
                    $"({(double)nonlinearIterations / steps:F1} per step).");
            var (minFilm, maxFilm) = film!.CoefficientRange();
            log.Add($"Final film coefficient over the exposed surface: " +
                    $"{minFilm:g4} … {maxFilm:g4} W/(m²·K).");
            foreach (string note in environment.DrainNotes())
                log.Add("  " + note);
        }
        double min = temperature_.Min(), max = temperature_.Max();
        log.Add($"Final temperature range: {min:g4} … {max:g4} K.");

        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput
        {
            Fields = frames[^1].Fields,   // default frame: the final (most steady) state
            Log = log,
            Frames = frames,
            FrameAxis = "Time",
            Summary = new Dictionary<string, double>
            {
                ["Final min temperature (K)"] = min,
                ["Final max temperature (K)"] = max,
                ["Time steps"] = steps,
                ["End time (s)"] = endTime
            }
        };
    }

    /// <summary>Step count and frame stride for the given settings (shared by Validate
    /// and Solve so the frame-cap check and the actual run can never disagree).</summary>
    private static (int Steps, int Stride) PlanSteps(TransientThermalSettings settings)
    {
        // The tiny tolerance keeps "Duration divisible by TimeStep" runs at exactly
        // Duration instead of one step past it (floating-point division).
        int steps = Math.Max(1, (int)Math.Ceiling(settings.Duration / settings.TimeStep - 1e-9));
        int stride = settings.OutputStride > 0
            ? settings.OutputStride
            : Math.Max(1, (int)Math.Ceiling(steps / 60.0));
        return (steps, stride);
    }

    private static ResultFrame MakeFrame(double time, double[] temperature, FeMesh mesh,
        ScalarDiffusionAssembler assembler, SolveInput input, SurfaceFilmModel? film)
    {
        // Flux recovery only for STORED frames — O(stored), not O(steps).
        var flux = new Vector3D[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
            flux[e] = assembler.ElementGradient(e, temperature)
                      * -input.MaterialOf(e).ThermalConductivity!.Value;   // q = −k∇T

        var fields = new List<IResultField>
        {
            new NodalScalarField("Temperature", "K", (double[])temperature.Clone()),
            new NodalVectorField("Heat flux", "W/m²", ScalarSolverHelpers.NodalAverage(mesh, flux))
        };
        // Present in EVERY frame of an environment run, so the frame field sets stay
        // identical and the UI keeps its selected field while scrubbing.
        if (film is not null)
            fields.Add(new NodalScalarField("Film coefficient", "W/(m²·K)",
                EnvironmentThermalTerms.NodalFilmField(mesh, film)));
        return new ResultFrame($"t = {time:g4} s", time, fields) { Unit = "s" };
    }

    private static void AddScaled(SparseMatrixBuilder builder, CsrMatrix matrix, double scale)
    {
        for (int row = 0; row < matrix.RowCount; row++)
            for (int k = matrix.RowPointers[row]; k < matrix.RowPointers[row + 1]; k++)
                builder.Add(row, matrix.ColumnIndices[k], scale * matrix.Values[k]);
    }
}
