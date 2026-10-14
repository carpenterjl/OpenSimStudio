using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// Overdetermined real least squares, min ‖A·x − b‖₂, by Householder QR. The columns are
/// scaled to unit length first (fitting problems mix columns of very different sizes), and
/// a column whose remaining part falls to rounding level is left out (its unknown is 0).
/// Sized for fitting: thousands of rows, hundreds of columns.
/// </summary>
public static class HouseholderLeastSquares
{
    public static double[] Solve(double[,] a, double[] b)
    {
        int rows = a.GetLength(0), columns = a.GetLength(1);
        if (b.Length != rows) throw new ArgumentException("One right-hand side value per row.", nameof(b));
        if (rows < columns) throw new ArgumentException("Least squares needs at least as many rows as unknowns.", nameof(a));
        // Column-major copy: every step below runs down columns.
        var q = new double[columns][];
        var scale = new double[columns];
        for (int j = 0; j < columns; j++)
        {
            var column = new double[rows];
            double norm = 0;
            for (int i = 0; i < rows; i++)
            {
                column[i] = a[i, j];
                norm += column[i] * column[i];
            }
            norm = Math.Sqrt(norm);
            scale[j] = norm > 0 ? 1 / norm : 0;
            for (int i = 0; i < rows; i++) column[i] *= scale[j];
            q[j] = column;
        }
        var rhs = (double[])b.Clone();

        var diagonal = new double[columns];
        var dropped = new bool[columns];
        for (int k = 0; k < columns; k++)
        {
            var v = q[k];
            double norm = 0;
            for (int i = k; i < rows; i++) norm += v[i] * v[i];
            norm = Math.Sqrt(norm);
            // Unit columns: what is left of one after the earlier columns are taken out.
            if (norm < 1e-13 || scale[k] == 0)
            {
                dropped[k] = true;
                continue;
            }
            double alpha = v[k] > 0 ? -norm : norm;
            // v = x − αe₁, stored in place; H = I − 2vvᵀ/(vᵀv).
            v[k] -= alpha;
            double vv = 0;
            for (int i = k; i < rows; i++) vv += v[i] * v[i];
            for (int j = k + 1; j < columns; j++) Reflect(v, q[j], k, rows, vv);
            Reflect(v, rhs, k, rows, vv);
            diagonal[k] = alpha;
        }

        var x = new double[columns];
        for (int k = columns - 1; k >= 0; k--)
        {
            if (dropped[k]) continue;
            double sum = rhs[k];
            for (int j = k + 1; j < columns; j++) sum -= q[j][k] * x[j];
            x[k] = sum / diagonal[k];
        }
        for (int j = 0; j < columns; j++) x[j] *= scale[j];
        return x;
    }

    private static void Reflect(double[] v, double[] target, int from, int rows, double vv)
    {
        double dot = 0;
        for (int i = from; i < rows; i++) dot += v[i] * target[i];
        double factor = 2 * dot / vv;
        for (int i = from; i < rows; i++) target[i] -= factor * v[i];
    }
}

/// <summary>
/// Eigenvalues of a small dense real matrix that need not be symmetric: Householder
/// reduction to upper Hessenberg form, then shifted QR iteration in complex arithmetic
/// (Wilkinson shift from the trailing 2 × 2, Givens rotations, deflation where a
/// subdiagonal entry falls to rounding level). The eigenvalues of a real matrix come in
/// conjugate pairs; complex shifts find them without the real double-shift bookkeeping.
/// </summary>
public static class NonsymmetricEigenvalues
{
    public static Complex[] Compute(double[,] matrix)
    {
        int n = matrix.GetLength(0);
        if (matrix.GetLength(1) != n) throw new ArgumentException("The matrix must be square.", nameof(matrix));
        if (n == 0) return Array.Empty<Complex>();
        var a = (double[,])matrix.Clone();
        ReduceToHessenberg(a);
        var h = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = Math.Max(0, i - 1); j < n; j++) h[i, j] = a[i, j];

        var values = new Complex[n];
        int hi = n - 1, iterations = 0;
        while (hi >= 0)
        {
            if (hi == 0) { values[0] = h[0, 0]; break; }
            // The active block is lo..hi: the last negligible subdiagonal entry above hi.
            int lo = hi;
            while (lo > 0)
            {
                double size = h[lo - 1, lo - 1].Magnitude + h[lo, lo].Magnitude;
                if (size == 0) size = 1;
                if (h[lo, lo - 1].Magnitude <= 4e-16 * size) { h[lo, lo - 1] = 0; break; }
                lo--;
            }
            if (lo == hi)
            {
                values[hi] = h[hi, hi];
                hi--;
                iterations = 0;
                continue;
            }
            if (++iterations > 60 * n)
                throw new InvalidOperationException("The eigenvalue iteration did not converge.");

            // Wilkinson shift: the eigenvalue of the trailing 2 × 2 nearer its last entry;
            // an occasional exceptional shift breaks a cycle.
            Complex shift;
            if (iterations % 20 == 0)
                shift = h[hi, hi] + 1.5 * h[hi, hi - 1].Magnitude;
            else
            {
                Complex p = h[hi - 1, hi - 1], q = h[hi - 1, hi], r = h[hi, hi - 1], s = h[hi, hi];
                Complex half = 0.5 * (p - s);
                Complex root = Complex.Sqrt(half * half + q * r);
                Complex first = s - q * r / (half + root), second = s - q * r / (half - root);
                if ((half + root).Magnitude == 0) first = s;
                if ((half - root).Magnitude == 0) second = s;
                shift = (first - s).Magnitude <= (second - s).Magnitude ? first : second;
            }

            // One QR step on the block: H − μI = QR, H ← RQ + μI.
            for (int k = lo; k <= hi; k++) h[k, k] -= shift;
            var cosines = new double[hi - lo];
            var sines = new Complex[hi - lo];
            for (int k = lo; k < hi; k++)
            {
                Complex x = h[k, k], y = h[k + 1, k];
                double norm = Math.Sqrt(x.Magnitude * x.Magnitude + y.Magnitude * y.Magnitude);
                double c;
                Complex sn;
                if (norm == 0) { c = 1; sn = 0; }
                else if (x.Magnitude == 0) { c = 0; sn = Complex.Conjugate(y) / y.Magnitude; }
                else
                {
                    c = x.Magnitude / norm;
                    sn = x / x.Magnitude * Complex.Conjugate(y) / norm;
                }
                cosines[k - lo] = c;
                sines[k - lo] = sn;
                // Rows k, k+1: [c, s; −s̄, c].
                for (int j = k; j <= hi; j++)
                {
                    Complex u = h[k, j], v = h[k + 1, j];
                    h[k, j] = c * u + sn * v;
                    h[k + 1, j] = -Complex.Conjugate(sn) * u + c * v;
                }
            }
            for (int k = lo; k < hi; k++)
            {
                double c = cosines[k - lo];
                Complex sn = sines[k - lo];
                // Columns k, k+1 by the conjugate transpose.
                for (int i = lo; i <= Math.Min(k + 2, hi); i++)
                {
                    Complex u = h[i, k], v = h[i, k + 1];
                    h[i, k] = c * u + Complex.Conjugate(sn) * v;
                    h[i, k + 1] = -sn * u + c * v;
                }
            }
            for (int k = lo; k <= hi; k++) h[k, k] += shift;
        }
        return values;
    }

    private static void ReduceToHessenberg(double[,] a)
    {
        int n = a.GetLength(0);
        var v = new double[n];
        for (int k = 0; k < n - 2; k++)
        {
            double norm = 0;
            for (int i = k + 1; i < n; i++) norm += a[i, k] * a[i, k];
            norm = Math.Sqrt(norm);
            if (norm == 0) continue;
            double alpha = a[k + 1, k] > 0 ? -norm : norm;
            for (int i = 0; i < n; i++) v[i] = 0;
            for (int i = k + 1; i < n; i++) v[i] = a[i, k];
            v[k + 1] -= alpha;
            double vv = 0;
            for (int i = k + 1; i < n; i++) vv += v[i] * v[i];
            if (vv == 0) continue;
            // A ← H·A·H, H = I − 2vvᵀ/(vᵀv).
            for (int j = 0; j < n; j++)
            {
                double dot = 0;
                for (int i = k + 1; i < n; i++) dot += v[i] * a[i, j];
                double factor = 2 * dot / vv;
                for (int i = k + 1; i < n; i++) a[i, j] -= factor * v[i];
            }
            for (int i = 0; i < n; i++)
            {
                double dot = 0;
                for (int j = k + 1; j < n; j++) dot += a[i, j] * v[j];
                double factor = 2 * dot / vv;
                for (int j = k + 1; j < n; j++) a[i, j] -= factor * v[j];
            }
        }
    }
}

/// <summary>
/// Singular value decomposition A = U·diag(σ)·Vᴴ of a small dense complex matrix by
/// one-sided Jacobi rotations (Hestenes): pairs of columns are rotated until every pair is
/// orthogonal; the column norms are then the singular values. Port-count sizes.
/// </summary>
public static class ComplexJacobiSvd
{
    /// <returns>U (m × n, orthonormal columns), σ (descending) and V (n × n, unitary).</returns>
    public static (Complex[,] U, double[] Sigma, Complex[,] V) Decompose(Complex[,] a)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        var w = (Complex[,])a.Clone();
        var v = new Complex[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 60; sweep++)
        {
            bool rotated = false;
            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++)
                {
                    double alpha = 0, beta = 0;
                    Complex gamma = 0;
                    for (int i = 0; i < m; i++)
                    {
                        alpha += w[i, p].Magnitude * w[i, p].Magnitude;
                        beta += w[i, q].Magnitude * w[i, q].Magnitude;
                        gamma += Complex.Conjugate(w[i, p]) * w[i, q];
                    }
                    double g = gamma.Magnitude;
                    if (g <= 1e-15 * Math.Sqrt(alpha * beta) || g == 0) continue;
                    rotated = true;
                    // Take γ's phase into column q, then a real Jacobi rotation zeroes the
                    // (now real) inner product.
                    Complex phase = gamma / g;
                    double zeta = (beta - alpha) / (2 * g);
                    double t = Math.Sign(zeta == 0 ? 1 : zeta) / (Math.Abs(zeta) + Math.Sqrt(1 + zeta * zeta));
                    double c = 1 / Math.Sqrt(1 + t * t), s = c * t;
                    for (int i = 0; i < m; i++)
                    {
                        Complex x = w[i, p], y = w[i, q] * Complex.Conjugate(phase);
                        w[i, p] = c * x - s * y;
                        w[i, q] = (s * x + c * y) * phase;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        Complex x = v[i, p], y = v[i, q] * Complex.Conjugate(phase);
                        v[i, p] = c * x - s * y;
                        v[i, q] = (s * x + c * y) * phase;
                    }
                }
            if (!rotated) break;
        }
        var sigma = new double[n];
        for (int j = 0; j < n; j++)
        {
            double norm = 0;
            for (int i = 0; i < m; i++) norm += w[i, j].Magnitude * w[i, j].Magnitude;
            sigma[j] = Math.Sqrt(norm);
        }
        var order = Enumerable.Range(0, n).OrderByDescending(j => sigma[j]).ToArray();
        var u = new Complex[m, n];
        var vSorted = new Complex[n, n];
        var sorted = new double[n];
        for (int k = 0; k < n; k++)
        {
            int j = order[k];
            sorted[k] = sigma[j];
            for (int i = 0; i < m; i++) u[i, k] = sigma[j] > 0 ? w[i, j] / sigma[j] : (i == k ? 1 : 0);
            for (int i = 0; i < n; i++) vSorted[i, k] = v[i, j];
        }
        return (u, sorted, vSorted);
    }
}
