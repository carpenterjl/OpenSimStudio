using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

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
                "The transient thermal solver supports linear (TET4) meshes only; " +
                "re-generate the mesh with linear elements.");

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
            if (bc.FaceIds.Count == 0)
                throw new InvalidOperationException($"Boundary condition '{bc.Name}' has no faces assigned.");
            if (input.Mesh.GetFaceNodes(bc.FaceIds).Count == 0)
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' targets faces that do not exist on the mesh.");
            if (bc is Convection { Coefficient: <= 0 } c)
                throw new InvalidOperationException(
                    $"Convection '{c.Name}': the heat transfer coefficient must be positive.");
        }
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
        var conduction = assembler.AssembleStiffness(robin, cancellationToken);
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

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.05));
        var constantLoads = ScalarSolverHelpers.AssembleThermalLoads(input, log);
        var prescribed = new Dictionary<int, double>();
        foreach (var temperature in input.BoundaryConditions.OfType<FixedTemperature>())
        {
            var nodes = mesh.GetFaceNodes(temperature.FaceIds);
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

        var frames = new List<ResultFrame> { MakeFrame(0.0, temperature_, mesh, assembler, input) };
        var cg = new ConjugateGradientSolver
        {
            Tolerance = 1e-10,
            MaxIterations = Math.Max(4 * reduced.FreeCount, 1000)
        };
        var massTimesT = new double[mesh.NodeCount];
        var fullRhs = new double[mesh.NodeCount];
        long totalIterations = 0;

        for (int n = 1; n <= steps; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mass.Multiply(temperature_, massTimesT);
            for (int i = 0; i < fullRhs.Length; i++)
                fullRhs[i] = massTimesT[i] / dt + constantLoads[i];

            var rhs = reduced.ReduceLoads(fullRhs);
            var free = reduced.Restrict(temperature_);   // warm start from the previous step
            var iterations = cg.Solve(reduced.Reduced, rhs, free, cancellationToken);
            if (!iterations.Converged)
                throw new InvalidOperationException(
                    $"Time step {n} did not converge after {iterations.Iterations} CG iterations " +
                    $"(residual {iterations.ResidualNorm:g3}). Check materials and mesh quality.");
            totalIterations += iterations.Iterations;
            temperature_ = reduced.Expand(free);

            if (n % stride == 0 || n == steps)
                frames.Add(MakeFrame(n * dt, temperature_, mesh, assembler, input));
            progress?.Report(new SolverProgress($"Time step {n}/{steps}", 0.05 + 0.95 * n / steps));
        }

        double endTime = steps * dt;
        log.Add($"Backward Euler: {steps} steps of Δt = {dt:g4} s to t = {endTime:g4} s " +
                $"({totalIterations} CG iterations total); {frames.Count} frames stored.");
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
        ScalarDiffusionAssembler assembler, SolveInput input)
    {
        // Flux recovery only for STORED frames — O(stored), not O(steps).
        var flux = new Vector3D[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
            flux[e] = assembler.ElementGradient(e, temperature)
                      * -input.MaterialOf(e).ThermalConductivity!.Value;   // q = −k∇T

        var fields = new IResultField[]
        {
            new NodalScalarField("Temperature", "K", (double[])temperature.Clone()),
            new NodalVectorField("Heat flux", "W/m²", ScalarSolverHelpers.NodalAverage(mesh, flux))
        };
        return new ResultFrame($"t = {time:g4} s", time, fields) { Unit = "s" };
    }

    private static void AddScaled(SparseMatrixBuilder builder, CsrMatrix matrix, double scale)
    {
        for (int row = 0; row < matrix.RowCount; row++)
            for (int k = matrix.RowPointers[row]; k < matrix.RowPointers[row + 1]; k++)
                builder.Add(row, matrix.ColumnIndices[k], scale * matrix.Values[k]);
    }
}
