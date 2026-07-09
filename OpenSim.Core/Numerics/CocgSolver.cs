using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// Conjugate Orthogonal Conjugate Gradient (COCG) for COMPLEX-SYMMETRIC systems
/// (A = Aᵀ, not Hermitian) — the structure of quasi-static FEM matrices K_σ + jωK_ε.
/// It is CG with every inner product replaced by the UNCONJUGATED bilinear form
/// ⟨x,y⟩ = Σ xᵢyᵢ, which is what A's transpose-symmetry pairs with; plain CG (or any
/// Hermitian-form method) is mathematically wrong here. Diagonal (complex-Jacobi)
/// preconditioning; convergence is measured on the true residual 2-norm relative to
/// ‖b‖. Unlike CG on an SPD matrix, the bilinear form can vanish for a nonzero
/// vector — that breakdown is detected and thrown, never iterated through.
/// </summary>
public sealed class CocgSolver
{
    /// <summary>Relative tolerance: converged when ‖b − A·x‖₂ ≤ Tolerance·‖b‖₂.</summary>
    public double Tolerance { get; init; } = 1e-10;

    /// <summary>Maximum iterations; 0 means 2·n.</summary>
    public int MaxIterations { get; init; }

    public IterativeSolveResult Solve(ComplexCsrMatrix a, ReadOnlySpan<Complex> b,
        Span<Complex> x, CancellationToken cancellationToken = default)
    {
        int n = a.RowCount;
        if (b.Length != n || x.Length != n)
            throw new ArgumentException("Vector lengths must match the matrix dimension.");
        int maxIterations = MaxIterations > 0 ? MaxIterations : 2 * n;

        var diag = a.GetDiagonal();
        var invDiag = new Complex[n];
        double maxDiag = 0;
        for (int i = 0; i < n; i++)
        {
            double magnitude = diag[i].Magnitude;
            maxDiag = Math.Max(maxDiag, magnitude);
            if (magnitude == 0)
                throw new InvalidOperationException(
                    $"Zero diagonal at row {i}: the system is singular or the matrix is malformed.");
            invDiag[i] = Complex.One / diag[i];
        }

        var r = new Complex[n];
        var z = new Complex[n];
        var p = new Complex[n];
        var ap = new Complex[n];

        // r = b − A·x (x may carry a warm start).
        a.Multiply(x, r);
        double bNorm = 0;
        for (int i = 0; i < n; i++)
        {
            r[i] = b[i] - r[i];
            bNorm += b[i].Real * b[i].Real + b[i].Imaginary * b[i].Imaginary;
        }
        bNorm = Math.Sqrt(bNorm);
        double threshold = Tolerance * (bNorm > 0 ? bNorm : 1);

        double residualNorm = Norm(r);
        if (residualNorm <= threshold)
            return new IterativeSolveResult(true, 0, residualNorm);

        for (int i = 0; i < n; i++)
        {
            z[i] = invDiag[i] * r[i];
            p[i] = z[i];
        }
        Complex rz = Bilinear(r, z);

        for (int iteration = 1; iteration <= maxIterations; iteration++)
        {
            if ((iteration & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            a.Multiply(p, ap);
            Complex pap = Bilinear(p, ap);
            if (pap.Magnitude <= 1e-14 * maxDiag * NormSquared(p))
                throw new InvalidOperationException(
                    "COCG breakdown — the unconjugated form pᵀ·A·p vanished, so the system is " +
                    "(numerically) singular at this frequency. Check the materials and boundary conditions.");

            Complex alpha = rz / pap;
            for (int i = 0; i < n; i++)
            {
                x[i] += alpha * p[i];
                r[i] -= alpha * ap[i];
            }

            residualNorm = Norm(r);
            if (residualNorm <= threshold)
                return new IterativeSolveResult(true, iteration, residualNorm);

            for (int i = 0; i < n; i++)
                z[i] = invDiag[i] * r[i];
            Complex rzNew = Bilinear(r, z);
            if (rz.Magnitude == 0)
                throw new InvalidOperationException(
                    "COCG breakdown — the residual form rᵀ·z vanished before convergence. " +
                    "Check the materials and boundary conditions.");
            Complex beta = rzNew / rz;
            rz = rzNew;
            for (int i = 0; i < n; i++)
                p[i] = z[i] + beta * p[i];
        }

        return new IterativeSolveResult(false, maxIterations, residualNorm);
    }

    /// <summary>The UNCONJUGATED bilinear form Σ xᵢyᵢ (not the Hermitian inner product).</summary>
    private static Complex Bilinear(ReadOnlySpan<Complex> x, ReadOnlySpan<Complex> y)
    {
        Complex sum = Complex.Zero;
        for (int i = 0; i < x.Length; i++)
            sum += x[i] * y[i];
        return sum;
    }

    private static double Norm(ReadOnlySpan<Complex> v) => Math.Sqrt(NormSquared(v));

    private static double NormSquared(ReadOnlySpan<Complex> v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++)
            sum += v[i].Real * v[i].Real + v[i].Imaginary * v[i].Imaginary;
        return sum;
    }
}
