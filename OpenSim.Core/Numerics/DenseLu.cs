namespace OpenSim.Core.Numerics;

/// <summary>
/// A small REAL dense LU with partial pivoting, factored in place.
///
/// <para>It exists for inner loops that solve the same tiny system thousands of times — the
/// nonlinear transient's Newton step is a 2N×2N real solve at every sample, with N ≤ 8. Routing
/// that through <see cref="ComplexLu"/> would do twice the arithmetic on data with no imaginary
/// part and allocate a complex buffer per iteration, in the hottest loop the engine has.</para>
///
/// <para>The matrix is stored ROW-MAJOR in a caller-owned array and overwritten by the
/// factorization, so a caller that solves repeatedly can reuse one buffer and allocate nothing
/// per step. Partial pivoting is unconditional: the Newton Jacobian of a piecewise-linear
/// network is well-conditioned in the interior of a region but can present a zero pivot exactly
/// at a table breakpoint, which is a routine occurrence rather than an error.</para>
/// </summary>
public sealed class DenseLu
{
    private readonly double[] _lu;
    private readonly int[] _pivot;
    private readonly int _n;

    private DenseLu(double[] lu, int[] pivot, int n) { _lu = lu; _pivot = pivot; _n = n; }

    /// <summary>Factor an n×n row-major matrix IN PLACE. The array is overwritten with the
    /// combined L/U factors; the returned object borrows it, so do not reuse the array for
    /// anything else until the last <see cref="Solve"/>.</summary>
    /// <param name="a">Row-major n×n matrix, overwritten.</param>
    /// <param name="n">The dimension.</param>
    /// <param name="pivot">Scratch of length n, reused across calls to avoid allocating.</param>
    /// <exception cref="InvalidOperationException">The matrix is singular to working precision.</exception>
    public static DenseLu FactorInPlace(double[] a, int n, int[] pivot)
    {
        if (a.Length < n * n) throw new ArgumentException("Matrix buffer is too small.", nameof(a));
        if (pivot.Length < n) throw new ArgumentException("Pivot buffer is too small.", nameof(pivot));
        for (int i = 0; i < n; i++) pivot[i] = i;

        for (int k = 0; k < n; k++)
        {
            // Partial pivot: the largest magnitude in the column at or below the diagonal.
            int best = k;
            double bestMag = Math.Abs(a[k * n + k]);
            for (int i = k + 1; i < n; i++)
            {
                double m = Math.Abs(a[i * n + k]);
                if (m > bestMag) { bestMag = m; best = i; }
            }
            if (bestMag == 0)
                throw new InvalidOperationException(
                    $"The matrix is singular at column {k} — no non-zero pivot remains.");
            if (best != k)
            {
                for (int j = 0; j < n; j++)
                    (a[k * n + j], a[best * n + j]) = (a[best * n + j], a[k * n + j]);
                (pivot[k], pivot[best]) = (pivot[best], pivot[k]);
            }

            double diag = a[k * n + k];
            for (int i = k + 1; i < n; i++)
            {
                double f = a[i * n + k] / diag;
                a[i * n + k] = f;
                for (int j = k + 1; j < n; j++)
                    a[i * n + j] -= f * a[k * n + j];
            }
        }
        return new DenseLu(a, pivot, n);
    }

    /// <summary>Solve A·x = b into <paramref name="x"/> (which may alias nothing else).
    /// <paramref name="b"/> is not modified.</summary>
    public void Solve(ReadOnlySpan<double> b, Span<double> x)
    {
        int n = _n;
        for (int i = 0; i < n; i++) x[i] = b[_pivot[i]];
        // Forward substitution (unit lower triangle).
        for (int i = 1; i < n; i++)
        {
            double sum = x[i];
            for (int j = 0; j < i; j++) sum -= _lu[i * n + j] * x[j];
            x[i] = sum;
        }
        // Back substitution.
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = x[i];
            for (int j = i + 1; j < n; j++) sum -= _lu[i * n + j] * x[j];
            x[i] = sum / _lu[i * n + i];
        }
    }
}
