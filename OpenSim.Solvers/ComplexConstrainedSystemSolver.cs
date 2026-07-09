using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// Complex mirror of <see cref="ConstrainedSystemSolver"/>: solves A·φ = f with
/// prescribed DOF phasors by static condensation to the free DOFs (right-hand-side
/// correction f_free −= A_fc·φ_c), then COCG on the reduced complex-symmetric system.
/// </summary>
internal static class ComplexConstrainedSystemSolver
{
    public sealed record Result(Complex[] Solution, IterativeSolveResult Iterations);

    public static Result Solve(ComplexCsrMatrix matrix, Complex[] loads,
        IReadOnlyDictionary<int, Complex> prescribed, Complex[]? warmStart = null,
        double tolerance = 1e-10, CancellationToken cancellationToken = default)
    {
        int n = matrix.RowCount;
        if (prescribed.Count == 0)
            throw new InvalidOperationException(
                "The system has no prescribed potentials; without a reference the solution is not unique.");

        var freeIndex = new int[n];
        int freeCount = 0;
        for (int i = 0; i < n; i++)
            freeIndex[i] = prescribed.ContainsKey(i) ? -1 : freeCount++;

        // The reduced complex matrix is built as two real builders zipped back together —
        // both are filled from the same entry walk, so their patterns are identical by
        // construction (the invariant Combine enforces).
        var reBuilder = new SparseMatrixBuilder(freeCount, freeCount);
        var imBuilder = new SparseMatrixBuilder(freeCount, freeCount);
        var rhs = new Complex[freeCount];
        for (int row = 0; row < n; row++)
        {
            int r = freeIndex[row];
            if (r < 0) continue;
            rhs[r] = loads[row];
            int end = matrix.RowPointers[row + 1];
            for (int k = matrix.RowPointers[row]; k < end; k++)
            {
                int col = matrix.ColumnIndices[k];
                int c = freeIndex[col];
                if (c >= 0)
                {
                    reBuilder.Add(r, c, matrix.Values[k].Real);
                    imBuilder.Add(r, c, matrix.Values[k].Imaginary);
                }
                else
                {
                    rhs[r] -= matrix.Values[k] * prescribed[col];
                }
            }
        }
        var reduced = ComplexCsrMatrix.Combine(reBuilder.Build(), imBuilder.Build(), 1.0);

        var solution = new Complex[freeCount];
        if (warmStart is not null)
            for (int i = 0; i < n; i++)
                if (freeIndex[i] >= 0)
                    solution[freeIndex[i]] = warmStart[i];

        var cocg = new CocgSolver { Tolerance = tolerance, MaxIterations = Math.Max(4 * freeCount, 1000) };
        var iterations = cocg.Solve(reduced, rhs, solution, cancellationToken);
        if (!iterations.Converged)
            throw new InvalidOperationException(
                $"COCG did not converge after {iterations.Iterations} iterations " +
                $"(residual {iterations.ResidualNorm:g3}). Check materials and mesh quality.");

        var full = new Complex[n];
        for (int i = 0; i < n; i++)
            full[i] = freeIndex[i] >= 0 ? solution[freeIndex[i]] : prescribed[i];
        return new Result(full, iterations);
    }
}
