namespace OpenSim.Core.Numerics;

/// <summary>Outcome of an iterative linear solve.</summary>
public sealed record IterativeSolveResult(bool Converged, int Iterations, double ResidualNorm);

/// <summary>
/// Jacobi-preconditioned Conjugate Gradient solver for symmetric positive-definite systems,
/// the workhorse for FEM stiffness systems in the linear static and thermal solvers.
/// </summary>
public sealed class ConjugateGradientSolver
{
    /// <summary>Relative residual tolerance: converged when ||r|| ≤ Tolerance · ||b||.</summary>
    public double Tolerance { get; init; } = 1e-10;

    /// <summary>Maximum iterations; 0 means 2·n (twice the theoretical exact-arithmetic bound).</summary>
    public int MaxIterations { get; init; }

    /// <summary>
    /// The Jacobi preconditioner M⁻¹ = diag(A)⁻¹ for one matrix, as
    /// <see cref="Solve(CsrMatrix, ReadOnlySpan{double}, Span{double}, double[], CancellationToken)"/>
    /// consumes it. Building it walks every stored entry of A, so a caller that solves the
    /// SAME matrix repeatedly — subspace iteration, a time march holding its step matrix —
    /// should build it once and pass it in rather than pay that walk per solve.
    /// </summary>
    /// <exception cref="InvalidOperationException">A zero diagonal entry: the system is
    /// singular or unconstrained, named by row.</exception>
    public static double[] BuildJacobiPreconditioner(CsrMatrix a)
    {
        double[] invDiag = a.GetDiagonal();
        for (int i = 0; i < invDiag.Length; i++)
        {
            if (invDiag[i] == 0)
                throw new InvalidOperationException($"Zero diagonal at row {i}; system is singular or unconstrained.");
            invDiag[i] = 1.0 / invDiag[i];
        }
        return invDiag;
    }

    /// <summary>
    /// Solves A·x = b for SPD matrix A. The initial content of <paramref name="x"/> is used
    /// as the starting guess (pass zeros for a cold start).
    /// </summary>
    public IterativeSolveResult Solve(CsrMatrix a, ReadOnlySpan<double> b, Span<double> x,
        CancellationToken cancellationToken = default) =>
        Solve(a, b, x, BuildJacobiPreconditioner(a), cancellationToken);

    /// <summary>
    /// Solves A·x = b with a preconditioner the caller already holds (see
    /// <see cref="BuildJacobiPreconditioner"/>). Identical arithmetic to the overload that
    /// builds its own — the same divisions in the same order — so results are bitwise equal;
    /// only the per-call rebuild of an unchanged diagonal is avoided.
    /// </summary>
    public IterativeSolveResult Solve(CsrMatrix a, ReadOnlySpan<double> b, Span<double> x,
        double[] invDiag, CancellationToken cancellationToken = default)
    {
        int n = a.RowCount;
        if (a.ColumnCount != n) throw new ArgumentException("Matrix must be square.", nameof(a));
        if (b.Length != n || x.Length != n) throw new ArgumentException("Vector length mismatch.");
        if (invDiag.Length != n)
            throw new ArgumentException(
                $"Preconditioner has {invDiag.Length} entries but the matrix has {n} rows.", nameof(invDiag));

        int maxIter = MaxIterations > 0 ? MaxIterations : 2 * n;

        double bNorm = Norm(b);
        if (bNorm == 0)
        {
            x.Clear();
            return new IterativeSolveResult(true, 0, 0);
        }
        double targetNorm = Tolerance * bNorm;

        var r = new double[n];
        var z = new double[n];
        var p = new double[n];
        var ap = new double[n];

        // r = b - A·x
        a.Multiply(x, r);
        for (int i = 0; i < n; i++) r[i] = b[i] - r[i];

        for (int i = 0; i < n; i++) z[i] = invDiag[i] * r[i];
        Array.Copy(z, p, n);
        double rz = Dot(r, z);

        double residual = Norm(r);
        int iter = 0;
        while (residual > targetNorm && iter < maxIter)
        {
            cancellationToken.ThrowIfCancellationRequested();

            a.Multiply(p, ap);
            double pAp = Dot(p, ap);
            if (pAp <= 0)
                throw new InvalidOperationException(
                    "Encountered non-positive curvature; the matrix is not positive definite. " +
                    "Check that the model is fully constrained.");

            double alpha = rz / pAp;
            for (int i = 0; i < n; i++)
            {
                x[i] += alpha * p[i];
                r[i] -= alpha * ap[i];
            }

            for (int i = 0; i < n; i++) z[i] = invDiag[i] * r[i];
            double rzNew = Dot(r, z);
            double beta = rzNew / rz;
            rz = rzNew;
            for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];

            residual = Norm(r);
            iter++;
        }

        return new IterativeSolveResult(residual <= targetNorm, iter, residual);
    }

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static double Norm(ReadOnlySpan<double> v) => Math.Sqrt(Dot(v, v));
}
