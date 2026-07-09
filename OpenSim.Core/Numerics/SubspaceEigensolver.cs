namespace OpenSim.Core.Numerics;

/// <summary>Result of a generalized eigensolve: the lowest eigenpairs of K·φ = λ·M·φ.
/// Eigenvectors are M-orthonormal (φᵢᵀMφⱼ = δᵢⱼ), ordered by ascending eigenvalue.</summary>
public sealed record EigenResult(
    double[] Eigenvalues,
    double[][] Eigenvectors,
    bool Converged,
    int Iterations,
    double[] ResidualNorms);

/// <summary>
/// Lowest generalized eigenpairs of K·φ = λ·M·φ (K and M symmetric positive definite)
/// by block subspace (inverse) iteration with Rayleigh–Ritz projection. Chosen over
/// Lanczos deliberately: with K SPD the inner solves K·x̄ = M·x run on the existing
/// Jacobi-preconditioned CG (no factorization, no shift), the iteration has none of
/// Lanczos's ghost-eigenvalue/reorthogonalization pathologies, and every step is a
/// small, auditable dense operation — the right risk profile for a correctness-first
/// solver. Fully deterministic: Bathe's standard starting vectors (diag(M), then unit
/// vectors at the largest mᵢᵢ/kᵢᵢ ratios), no randomness anywhere.
/// </summary>
public sealed class SubspaceEigensolver
{
    /// <summary>Number of eigenpairs to compute (the block is oversized internally).</summary>
    public int ModeCount { get; init; } = 6;

    public int MaxIterations { get; init; } = 60;

    /// <summary>Relative eigenvalue change between iterations required of every
    /// requested mode before the iteration stops.</summary>
    public double Tolerance { get; init; } = 1e-8;

    /// <summary>Maximum ‖Kφ − λMφ‖₂/‖Kφ‖₂ over the requested modes required before the
    /// iteration stops. Eigenvalues converge quadratically faster than eigenvectors, so
    /// a value-change test alone would report shapes far less accurate than the values —
    /// this bounds the vectors too.</summary>
    public double ResidualTolerance { get; init; } = 1e-6;

    /// <summary>Relative CG tolerance of the inner solves K·x̄ = M·x. This sets the
    /// achievable residual floor: the CG error re-injects the high modes each iteration,
    /// so mode p stalls near InnerTolerance·(λ_p/λ_1) — hence a default well below
    /// <see cref="ResidualTolerance"/>. Warm starts keep the extra CG cost small.</summary>
    public double InnerTolerance { get; init; } = 1e-10;

    public EigenResult Solve(CsrMatrix k, CsrMatrix m, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int n = k.RowCount;
        if (m.RowCount != n)
            throw new ArgumentException("K and M must have the same dimension.");
        int p = ModeCount;
        if (p < 1 || p > n)
            throw new ArgumentException($"ModeCount must lie in [1, {n}] for this system.");
        // Bathe's oversampling: converges the p-th mode at the rate of mode q+1.
        int q = Math.Min(Math.Min(2 * p, p + 8), n);

        var x = BuildStartVectors(k, m, n, q);
        var xbar = new double[q][];
        var y = new double[q][];        // y_j = M·x_j
        var ybar = new double[q][];     // ȳ_j = M·x̄_j
        for (int j = 0; j < q; j++)
        {
            xbar[j] = new double[n];
            y[j] = new double[n];
            ybar[j] = new double[n];
        }

        var cg = new ConjugateGradientSolver
        {
            Tolerance = InnerTolerance,
            MaxIterations = Math.Max(4 * n, 1000)
        };

        var values = new double[q];
        double[]? previous = null;
        bool converged = false;
        int iteration = 0;

        while (iteration < MaxIterations && !converged)
        {
            iteration++;
            cancellationToken.ThrowIfCancellationRequested();

            for (int j = 0; j < q; j++)
            {
                m.Multiply(x[j], y[j]);
                // Warm start: the previous x̄_j is an excellent guess once shapes settle.
                var inner = cg.Solve(k, y[j], xbar[j], cancellationToken);
                if (!inner.Converged)
                    throw new InvalidOperationException(
                        $"Inner CG solve of subspace iteration {iteration} did not converge " +
                        $"(residual {inner.ResidualNorm:g3}). Check constraints and mesh quality.");
            }

            // Projected q×q problem: K̃ = X̄ᵀ(KX̄) = X̄ᵀY (since KX̄ = Y), M̃ = X̄ᵀMX̄.
            var kt = new double[q][];
            var mt = new double[q][];
            for (int i = 0; i < q; i++)
            {
                kt[i] = new double[q];
                mt[i] = new double[q];
            }
            for (int j = 0; j < q; j++)
                m.Multiply(xbar[j], ybar[j]);
            for (int i = 0; i < q; i++)
            {
                for (int j = i; j < q; j++)
                {
                    double ktij = Dot(xbar[i], y[j]);
                    double mtij = Dot(xbar[i], ybar[j]);
                    kt[i][j] = kt[j][i] = ktij;   // symmetrize: CG noise breaks exact symmetry
                    mt[i][j] = mt[j][i] = mtij;
                }
            }

            // Reduce K̃·Q = M̃·Q·Λ to a standard problem via M̃ = L·Lᵀ:
            // (L⁻¹·K̃·L⁻ᵀ)·Z = Z·Λ with Q = L⁻ᵀ·Z.
            var l = Cholesky(mt, q);
            var reduced = new double[q][];
            for (int i = 0; i < q; i++)
                reduced[i] = (double[])kt[i].Clone();
            // reduced ← L⁻¹·K̃ (forward-substitute each column), then ·L⁻ᵀ on the right.
            for (int col = 0; col < q; col++)
                ForwardColumn(l, reduced, col, q);
            for (int row = 0; row < q; row++)
                ForwardRow(l, reduced, row, q);
            // The two triangular solves leave epsilon-level asymmetry (CG noise);
            // Jacobi assumes exact symmetry, so restore it explicitly.
            for (int i = 0; i < q; i++)
                for (int j = i + 1; j < q; j++)
                    reduced[i][j] = reduced[j][i] = 0.5 * (reduced[i][j] + reduced[j][i]);

            var (lambda, z) = JacobiEigenSolver.Solve(reduced);
            for (int kk = 0; kk < q; kk++)
                values[kk] = lambda[kk];

            // Q columns: back-substitute Lᵀ·q_k = z_k, then X ← X̄·Q (M-orthonormal by
            // construction: QᵀM̃Q = ZᵀZ = I).
            var xNew = new double[q][];
            for (int kk = 0; kk < q; kk++)
            {
                var qk = (double[])z[kk].Clone();
                BackSubstituteTranspose(l, qk, q);
                var col = new double[n];
                for (int j = 0; j < q; j++)
                {
                    double w = qk[j];
                    if (w == 0) continue;
                    var src = xbar[j];
                    for (int i = 0; i < n; i++)
                        col[i] += w * src[i];
                }
                xNew[kk] = col;
            }
            x = xNew;

            if (previous is not null)
            {
                bool valuesSettled = true;
                for (int i = 0; i < p; i++)
                {
                    double denom = Math.Max(Math.Abs(values[i]), double.Epsilon);
                    if (Math.Abs(values[i] - previous[i]) / denom > Tolerance)
                    {
                        valuesSettled = false;
                        break;
                    }
                }
                // Only pay the residual matvecs once the values have settled.
                converged = valuesSettled
                            && ComputeResiduals(k, m, x, values, p).Max() <= ResidualTolerance;
            }
            previous ??= new double[q];
            Array.Copy(values, previous, q);
            progress?.Report((double)iteration / MaxIterations);
        }

        var eigenvalues = new double[p];
        var eigenvectors = new double[p][];
        for (int i = 0; i < p; i++)
        {
            eigenvalues[i] = values[i];
            eigenvectors[i] = x[i];
        }
        var residuals = ComputeResiduals(k, m, x, values, p);
        return new EigenResult(eigenvalues, eigenvectors, converged, iteration, residuals);
    }

    /// <summary>True generalized residuals ‖Kφ − λMφ‖₂/‖Kφ‖₂ of the first
    /// <paramref name="p"/> columns.</summary>
    private static double[] ComputeResiduals(CsrMatrix k, CsrMatrix m,
        double[][] x, double[] values, int p)
    {
        int n = k.RowCount;
        var residuals = new double[p];
        var kphi = new double[n];
        var mphi = new double[n];
        for (int i = 0; i < p; i++)
        {
            k.Multiply(x[i], kphi);
            m.Multiply(x[i], mphi);
            double num = 0, den = 0;
            for (int j = 0; j < n; j++)
            {
                double r = kphi[j] - values[i] * mphi[j];
                num += r * r;
                den += kphi[j] * kphi[j];
            }
            residuals[i] = den > 0 ? Math.Sqrt(num / den) : 0;
        }
        return residuals;
    }

    /// <summary>Bathe's deterministic start: diag(M) excites everything mass-bearing;
    /// the remaining columns are unit vectors where mᵢᵢ/kᵢᵢ is largest (the DOFs the
    /// lowest modes favor), ties broken by lowest index.</summary>
    private static double[][] BuildStartVectors(CsrMatrix k, CsrMatrix m, int n, int q)
    {
        var kDiag = k.GetDiagonal();
        var mDiag = m.GetDiagonal();
        var x = new double[q][];
        x[0] = (double[])mDiag.Clone();

        var order = Enumerable.Range(0, n)
            .OrderByDescending(i => kDiag[i] > 0 ? mDiag[i] / kDiag[i] : 0)
            .ThenBy(i => i)
            .ToArray();
        for (int j = 1; j < q; j++)
        {
            x[j] = new double[n];
            x[j][order[j - 1]] = 1.0;
        }
        return x;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    /// <summary>Lower-triangular Cholesky factor of a small dense SPD matrix.</summary>
    private static double[][] Cholesky(double[][] a, int n)
    {
        var l = new double[n][];
        for (int i = 0; i < n; i++)
            l[i] = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i][j];
                for (int kk = 0; kk < j; kk++)
                    sum -= l[i][kk] * l[j][kk];
                if (i == j)
                {
                    if (sum <= 0)
                        throw new InvalidOperationException(
                            "The projected mass matrix lost positive definiteness — the iteration " +
                            "subspace became degenerate. Check that M is a valid mass matrix.");
                    l[i][j] = Math.Sqrt(sum);
                }
                else
                {
                    l[i][j] = sum / l[j][j];
                }
            }
        }
        return l;
    }

    /// <summary>Solves L·x = column <paramref name="col"/> of <paramref name="a"/> in place.</summary>
    private static void ForwardColumn(double[][] l, double[][] a, int col, int n)
    {
        for (int i = 0; i < n; i++)
        {
            double sum = a[i][col];
            for (int j = 0; j < i; j++)
                sum -= l[i][j] * a[j][col];
            a[i][col] = sum / l[i][i];
        }
    }

    /// <summary>Applies L⁻ᵀ on the right: row <paramref name="row"/> of a ← row·L⁻ᵀ,
    /// i.e. solves the same triangular system along the row.</summary>
    private static void ForwardRow(double[][] l, double[][] a, int row, int n)
    {
        for (int i = 0; i < n; i++)
        {
            double sum = a[row][i];
            for (int j = 0; j < i; j++)
                sum -= l[i][j] * a[row][j];
            a[row][i] = sum / l[i][i];
        }
    }

    /// <summary>Solves Lᵀ·x = b in place (b becomes x).</summary>
    private static void BackSubstituteTranspose(double[][] l, double[] b, int n)
    {
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = b[i];
            for (int j = i + 1; j < n; j++)
                sum -= l[j][i] * b[j];
            b[i] = sum / l[i][i];
        }
    }
}
