using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// Solves K·u = f with prescribed DOF values by static condensation to the free DOFs
/// (keeps the reduced system symmetric positive definite for CG). Supports non-zero
/// prescribed values via the right-hand-side correction f_free −= K_fc·u_c.
/// </summary>
public static class ConstrainedSystemSolver
{
    public sealed record Result(double[] Displacements, IterativeSolveResult Iterations);

    /// <summary>
    /// A system reduced to its free DOFs, reusable across many right-hand sides with the
    /// SAME matrix and prescribed values (e.g. every step of an implicit time integration,
    /// or every column of a block eigensolve). The K_fc·u_c correction is precomputed at
    /// reduction time, so prescribed values must not change afterwards.
    /// </summary>
    public sealed class ReducedSystem
    {
        private readonly int[] _freeIndex;          // full DOF → free index, -1 when prescribed
        private readonly double[] _rhsCorrection;   // (K_fc·u_c) per free DOF
        private readonly IReadOnlyDictionary<int, double> _prescribed;

        internal ReducedSystem(CsrMatrix reduced, int[] freeIndex, double[] rhsCorrection,
            IReadOnlyDictionary<int, double> prescribed)
        {
            Reduced = reduced;
            _freeIndex = freeIndex;
            _rhsCorrection = rhsCorrection;
            _prescribed = prescribed;
        }

        /// <summary>The free-DOF submatrix K_ff (SPD when the full matrix is).</summary>
        public CsrMatrix Reduced { get; }

        public int FreeCount => Reduced.RowCount;

        /// <summary>Free-DOF right-hand side: f_free − K_fc·u_c.</summary>
        public double[] ReduceLoads(IReadOnlyList<double> fullLoads)
        {
            var rhs = new double[FreeCount];
            for (int i = 0; i < _freeIndex.Length; i++)
            {
                int r = _freeIndex[i];
                if (r >= 0)
                    rhs[r] = fullLoads[i] - _rhsCorrection[r];
            }
            return rhs;
        }

        /// <summary>The free entries of a full-length vector (e.g. a CG warm start).</summary>
        public double[] Restrict(IReadOnlyList<double> fullVector)
        {
            var free = new double[FreeCount];
            for (int i = 0; i < _freeIndex.Length; i++)
                if (_freeIndex[i] >= 0)
                    free[_freeIndex[i]] = fullVector[i];
            return free;
        }

        /// <summary>Re-inserts the prescribed values around a free-DOF solution.</summary>
        public double[] Expand(IReadOnlyList<double> freeSolution)
        {
            var full = new double[_freeIndex.Length];
            for (int i = 0; i < _freeIndex.Length; i++)
                full[i] = _freeIndex[i] >= 0 ? freeSolution[_freeIndex[i]] : _prescribed[i];
            return full;
        }
    }

    /// <param name="allowUnconstrained">
    /// Permit an empty prescribed set when the matrix is already non-singular
    /// (e.g. a thermal system regularized by convection/Robin or capacity terms).
    /// </param>
    public static ReducedSystem Reduce(CsrMatrix matrix, IReadOnlyDictionary<int, double> prescribed,
        bool allowUnconstrained = false)
    {
        int n = matrix.RowCount;
        if (prescribed.Count == 0 && !allowUnconstrained)
            throw new InvalidOperationException(
                "The model has no constraints; the stiffness matrix is singular. Add a fixed support.");

        var freeIndex = new int[n];
        int freeCount = 0;
        for (int i = 0; i < n; i++)
            freeIndex[i] = prescribed.ContainsKey(i) ? -1 : freeCount++;

        var builder = new SparseMatrixBuilder(freeCount, freeCount);
        var correction = new double[freeCount];
        for (int row = 0; row < n; row++)
        {
            int r = freeIndex[row];
            if (r < 0) continue;
            int end = matrix.RowPointers[row + 1];
            for (int k = matrix.RowPointers[row]; k < end; k++)
            {
                int col = matrix.ColumnIndices[k];
                int c = freeIndex[col];
                if (c >= 0)
                    builder.Add(r, c, matrix.Values[k]);
                else
                    correction[r] += matrix.Values[k] * prescribed[col];
            }
        }

        return new ReducedSystem(builder.Build(), freeIndex, correction, prescribed);
    }

    /// <inheritdoc cref="Reduce"/>
    public static Result Solve(CsrMatrix stiffness, double[] loads,
        IReadOnlyDictionary<int, double> prescribed, double tolerance = 1e-10,
        CancellationToken cancellationToken = default, bool allowUnconstrained = false)
    {
        var reduced = Reduce(stiffness, prescribed, allowUnconstrained);
        var rhs = reduced.ReduceLoads(loads);

        var solution = new double[reduced.FreeCount];
        var cg = new ConjugateGradientSolver
        {
            Tolerance = tolerance,
            MaxIterations = Math.Max(4 * reduced.FreeCount, 1000)
        };
        var iterations = cg.Solve(reduced.Reduced, rhs, solution, cancellationToken);
        if (!iterations.Converged)
            throw new InvalidOperationException(
                $"Linear solver did not converge after {iterations.Iterations} iterations " +
                $"(residual {iterations.ResidualNorm:g3}). Check constraints and mesh quality.");

        return new Result(reduced.Expand(solution), iterations);
    }
}
