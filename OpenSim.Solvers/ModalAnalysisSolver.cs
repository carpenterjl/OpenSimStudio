using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Modal analysis: the lowest natural frequencies and mode shapes of K·φ = ω²·M·φ over
/// TET4 or TET10 elements, with consistent mass and the same fixed-support reduction as
/// the static solver (K stays SPD, so the subspace eigensolver's inner CG applies).
/// Mode shapes are normalized to unit maximum displacement magnitude — the physical
/// scale of a mode is arbitrary, and unit-max makes the deform-scale slider a direct
/// amplitude knob.
/// </summary>
public sealed class ModalAnalysisSolver : ISolver
{
    private const int MaxModeCount = 30;

    public string Name => "Modal (natural frequencies)";

    public void Validate(SolveInput input) =>
        ValidateFreeDofs(input, ValidateModel(input), BuildPrescribedDofs(input, log: null).Count);

    /// <summary>
    /// Everything that can be checked without deriving the constrained DOF set, returning the
    /// requested mode count. Split from the free-DOF check so <see cref="Solve"/> can build the
    /// prescribed set ONCE — for the check and the reduction both — while keeping the order in
    /// which the two kinds of error surface exactly as the public <see cref="Validate"/> has it.
    /// </summary>
    private static int ValidateModel(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        input.Material.ValidateMechanical();

        if (input.RegionMaterials is { Count: > 0 })
            throw new InvalidOperationException(
                "The modal solver supports a single material; multi-material (region) solves " +
                "are only available for the electrical and thermal solvers.");

        foreach (var bc in input.BoundaryConditions)
        {
            if (bc is not (FixedSupport or ForceLoad or PressureLoad))
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to a modal solve. " +
                    "Use fixed supports (loads are ignored — they cannot affect natural frequencies).");
            BoundaryScope.Validate(bc, input.Mesh);
        }

        if (!input.BoundaryConditions.OfType<FixedSupport>().Any())
            throw new InvalidOperationException(
                "At least one fixed support is required: an unconstrained body has six rigid-body " +
                "modes at zero frequency, which the eigensolver cannot separate from a singular system.");

        int modeCount = (input.Modal ?? new ModalSettings()).ModeCount;
        if (modeCount < 1 || modeCount > MaxModeCount)
            throw new InvalidOperationException($"The mode count must lie in [1, {MaxModeCount}].");
        return modeCount;
    }

    private static void ValidateFreeDofs(SolveInput input, int modeCount, int prescribedCount)
    {
        int freeDofs = input.Mesh.NodeCount * 3 - prescribedCount;
        int blockSize = Math.Min(2 * modeCount, modeCount + 8);
        if (freeDofs <= blockSize)
            throw new InvalidOperationException(
                $"Only {freeDofs} free DOFs remain after the supports — too few for {modeCount} modes " +
                $"(the iteration needs more than {blockSize}). Refine the mesh or reduce the mode count.");
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        var mesh = input.Mesh;

        // Built once and used for BOTH the free-DOF check and the reduction below: deriving it
        // walks every support scope and, on a quadratic mesh, the whole element list to map
        // mid-edge nodes.
        int modeCount = ValidateModel(input);
        var prescribed = BuildPrescribedDofs(input, log);
        ValidateFreeDofs(input, modeCount, prescribed.Count);

        if (input.BoundaryConditions.Any(bc => bc is ForceLoad or PressureLoad))
            log.Add("Loads are ignored in a modal analysis (natural frequencies are load-independent).");

        progress?.Report(new SolverProgress("Assembling stiffness and mass", 0.05));
        var assembler = StructuralSurfaceLoads.AssemblerFor(mesh, input.Material);
        var stiffness = assembler.AssembleStiffness(cancellationToken);
        var mass = assembler.AssembleMass(cancellationToken);
        log.Add($"Assembled {stiffness.RowCount} DOF system " +
                $"({StructuralSurfaceLoads.ElementName(mesh)}), " +
                $"{stiffness.NonZeroCount} stiffness non-zeros."); 

        var reducedK = ConstrainedSystemSolver.Reduce(stiffness, prescribed);
        var reducedM = ConstrainedSystemSolver.Reduce(mass, prescribed);

        progress?.Report(new SolverProgress("Subspace iteration", 0.15));
        var eigenProgress = progress is null ? null : new Progress<double>(
            f => progress.Report(new SolverProgress("Subspace iteration", 0.15 + 0.75 * f)));
        var eigen = new SubspaceEigensolver { ModeCount = modeCount }
            .Solve(reducedK.Reduced, reducedM.Reduced, eigenProgress, cancellationToken);
        if (!eigen.Converged)
            throw new InvalidOperationException(
                $"The subspace iteration did not converge in {eigen.Iterations} iterations. " +
                "Check mesh quality, or request fewer modes.");
        log.Add($"Subspace iteration converged in {eigen.Iterations} iterations; " +
                $"worst mode residual ‖Kφ−λMφ‖/‖Kφ‖ = {eigen.ResidualNorms.Max():g3}.");

        progress?.Report(new SolverProgress("Building mode shapes", 0.92));
        var frames = new List<ResultFrame>();
        var summary = new Dictionary<string, double>();
        for (int i = 0; i < modeCount; i++)
        {
            double frequency = Math.Sqrt(Math.Max(eigen.Eigenvalues[i], 0)) / (2 * Math.PI);
            var full = reducedM.Expand(eigen.Eigenvectors[i]);   // zeros at the supports

            var shape = new Vector3D[mesh.NodeCount];
            double maxMagnitude = 0;
            for (int nd = 0; nd < mesh.NodeCount; nd++)
            {
                shape[nd] = new Vector3D(full[nd * 3], full[nd * 3 + 1], full[nd * 3 + 2]);
                maxMagnitude = Math.Max(maxMagnitude, shape[nd].Length);
            }
            if (maxMagnitude > 0)
                for (int nd = 0; nd < mesh.NodeCount; nd++)
                    shape[nd] /= maxMagnitude;

            var fields = new IResultField[]
            {
                new NodalVectorField("Mode shape", "-", shape)
            };
            frames.Add(new ResultFrame($"Mode {i + 1} — {FormatFrequency(frequency)}", i + 1.0, fields)
            {
                Summary = new Dictionary<string, double> { ["Frequency (Hz)"] = frequency }
            });
            summary[$"f{i + 1} (Hz)"] = frequency;
            log.Add($"Mode {i + 1}: {FormatFrequency(frequency)} " +
                    $"(residual {eigen.ResidualNorms[i]:g3}).");
        }

        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput
        {
            Fields = frames[0].Fields,   // default frame: the fundamental mode
            Log = log,
            Frames = frames,
            FrameAxis = "Mode",
            Summary = summary
        };
    }

    /// <summary>Zero-valued prescribed DOFs of the fixed supports — same reduction as
    /// the static solver, including mid-edge pinning on quadratic meshes.</summary>
    private static Dictionary<int, double> BuildPrescribedDofs(SolveInput input, List<string>? log)
    {
        var mesh = input.Mesh;
        var edgeMid = mesh.IsQuadratic ? QuadraticMeshBuilder.BuildEdgeMidMap(mesh) : null;
        return StructuralSurfaceLoads.BuildPrescribedDofs(mesh, input.BoundaryConditions, edgeMid, log);
    }

    private static string FormatFrequency(double hz) => hz switch
    {
        >= 1e6 => $"{hz / 1e6:g4} MHz",
        >= 1e3 => $"{hz / 1e3:g4} kHz",
        _ => $"{hz:g4} Hz"
    };
}
