using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>The eigenvalues of a square complex matrix and, when asked for, unit eigenvectors:
/// column k of <see cref="Vectors"/> belongs to <see cref="Values"/>[k].</summary>
public sealed record ComplexEigenResult(Complex[] Values, ComplexDenseMatrix? Vectors);

/// <summary>
/// Eigenvalues and right eigenvectors of a dense complex matrix that need not be Hermitian (the
/// coupled-wave matrices of rigorous grating analysis, OP-47). First-party, written for this
/// program; the steps are the textbook ones, derived here rather than copied:
/// <list type="number">
/// <item><b>Hessenberg form.</b> Householder reflections H = I − 2vv*, ‖v‖ = 1, chosen column by
/// column so the entries below the first subdiagonal vanish; A ← HAH and Q ← QH, so
/// A = Q·Hess·Q* with Q unitary. The reflection's leading entry is −e^{i·arg x₀}‖x‖, the sign
/// that avoids cancellation.</item>
/// <item><b>Schur form.</b> Shifted QR on the Hessenberg matrix in complex arithmetic: on the
/// active block, subtract the shift μ, factor with Givens rotations G = [[c̄, s̄], [−s, c]]
/// (|c|² + |s|² = 1, chosen so the subdiagonal entry becomes zero), multiply back in the other
/// order and add μ. Rotations act on whole rows and columns, and each is accumulated into Z, so
/// T = Z*·A·Z holds for the full matrix at every step. μ is the eigenvalue of the trailing 2 × 2
/// block nearer its last diagonal entry (Wilkinson's shift); every tenth step without deflation
/// an exceptional shift (the last diagonal entry plus ¾ of the last subdiagonal's magnitude)
/// breaks cycles. A subdiagonal entry at or below ε·(|t_{k−1,k−1}| + |t_{k,k}|) is set to zero
/// (deflation). No convergence in 30·n steps for one eigenvalue throws, naming the block.</item>
/// <item><b>Eigenvectors.</b> For the triangular T, the eigenvector of t_kk has x_k = 1, x_j = 0
/// for j &gt; k and, from i = k − 1 down, x_i = −(Σ_{j=i+1..k} t_ij·x_j)/(t_ii − t_kk); a
/// denominator below ε·‖T‖ is replaced by ε·‖T‖ (repeated eigenvalues, the usual guard), and the
/// partial vector is rescaled when it grows past 10¹⁰⁰. The eigenvector of A is Z·x, scaled to
/// unit 2-norm.</item>
/// </list>
/// The arithmetic is sequential and deterministic: the same matrix gives the same bits.
/// </summary>
public static class ComplexEigenSolver
{
    private const double Eps = 2.220446049250313e-16;   // 2⁻⁵², the double's unit roundoff × 2

    /// <summary>The eigen-decomposition of <paramref name="matrix"/> (not modified).</summary>
    /// <param name="vectors">False to skip the eigenvectors (and the accumulation of Z).</param>
    public static ComplexEigenResult Compute(ComplexDenseMatrix matrix, bool vectors = true)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Rows != matrix.Columns)
            throw new ArgumentException($"The eigen-decomposition needs a square matrix, got {matrix.Rows}×{matrix.Columns}.");
        int n = matrix.Rows;
        var a = new Complex[n, n];
        double norm = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var v = matrix[i, j];
                if (!double.IsFinite(v.Real) || !double.IsFinite(v.Imaginary))
                    throw new ArgumentException($"The matrix has a non-finite entry at ({i}, {j}): {v}.");
                a[i, j] = v;
                norm = Math.Max(norm, v.Magnitude);
            }
        Complex[,]? z = null;
        if (vectors)
        {
            z = new Complex[n, n];
            for (int i = 0; i < n; i++) z[i, i] = Complex.One;
        }
        if (norm == 0)
        {
            var zeros = new Complex[n];
            return new ComplexEigenResult(zeros, vectors ? ToMatrix(z!) : null);
        }

        ReduceToHessenberg(a, z);
        Schur(a, z, norm);
        var values = new Complex[n];
        for (int i = 0; i < n; i++) values[i] = a[i, i];
        if (!vectors) return new ComplexEigenResult(values, null);
        return new ComplexEigenResult(values, ToMatrix(Eigenvectors(a, z!)));
    }

    private static ComplexDenseMatrix ToMatrix(Complex[,] m)
    {
        int n = m.GetLength(0);
        var result = new ComplexDenseMatrix(n, n);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++) result[i, j] = m[i, j];
        return result;
    }

    // ---- 1. Hessenberg reduction ----

    private static void ReduceToHessenberg(Complex[,] a, Complex[,]? q)
    {
        int n = a.GetLength(0);
        var v = new Complex[n];
        for (int k = 0; k < n - 2; k++)
        {
            double xnorm2 = 0;
            for (int i = k + 1; i < n; i++) xnorm2 += Sq(a[i, k]);
            double xnorm = Math.Sqrt(xnorm2);
            double below = xnorm2 - Sq(a[k + 1, k]);
            if (below == 0) continue;   // already zero below the subdiagonal
            var x0 = a[k + 1, k];
            var phase = x0 == Complex.Zero ? Complex.One : x0 / x0.Magnitude;
            var alpha = -phase * xnorm;
            for (int i = 0; i < n; i++) v[i] = Complex.Zero;
            v[k + 1] = x0 - alpha;
            for (int i = k + 2; i < n; i++) v[i] = a[i, k];
            double vnorm = 0;
            for (int i = k + 1; i < n; i++) vnorm += Sq(v[i]);
            vnorm = Math.Sqrt(vnorm);
            if (vnorm == 0) continue;
            for (int i = k + 1; i < n; i++) v[i] /= vnorm;

            // A ← H·A (rows k+1.., columns k..).
            for (int j = k; j < n; j++)
            {
                Complex s = Complex.Zero;
                for (int i = k + 1; i < n; i++) s += Complex.Conjugate(v[i]) * a[i, j];
                s *= 2;
                for (int i = k + 1; i < n; i++) a[i, j] -= v[i] * s;
            }
            // A ← A·H (all rows, columns k+1..).
            for (int i = 0; i < n; i++)
            {
                Complex s = Complex.Zero;
                for (int j = k + 1; j < n; j++) s += a[i, j] * v[j];
                s *= 2;
                for (int j = k + 1; j < n; j++) a[i, j] -= s * Complex.Conjugate(v[j]);
            }
            if (q is not null)
                for (int i = 0; i < n; i++)
                {
                    Complex s = Complex.Zero;
                    for (int j = k + 1; j < n; j++) s += q[i, j] * v[j];
                    s *= 2;
                    for (int j = k + 1; j < n; j++) q[i, j] -= s * Complex.Conjugate(v[j]);
                }
            a[k + 1, k] = alpha;
            for (int i = k + 2; i < n; i++) a[i, k] = Complex.Zero;
        }
    }

    // ---- 2. Shifted QR to Schur form ----

    private static void Schur(Complex[,] h, Complex[,]? z, double norm)
    {
        int n = h.GetLength(0);
        var cs = new Complex[n];
        var sn = new Complex[n];
        int hi = n - 1, iterations = 0;
        while (hi > 0)
        {
            // Find the start l of the unreduced block ending at hi.
            int l = hi;
            while (l > 0)
            {
                double scale = h[l - 1, l - 1].Magnitude + h[l, l].Magnitude;
                if (scale == 0) scale = norm;
                if (h[l, l - 1].Magnitude <= Eps * scale)
                {
                    h[l, l - 1] = Complex.Zero;
                    break;
                }
                l--;
            }
            if (l == hi)
            {
                hi--;
                iterations = 0;
                continue;
            }
            iterations++;
            if (iterations > 30 * n)
                throw new InvalidOperationException(
                    $"Complex eigen-decomposition: the shifted QR iteration did not converge for the eigenvalue in row {hi} of a {n}×{n} matrix after {30 * n} steps.");

            Complex mu;
            if (iterations % 10 == 0)
                mu = h[hi, hi] + 0.75 * h[hi, hi - 1].Magnitude;
            else
            {
                var p = h[hi - 1, hi - 1];
                var q = h[hi - 1, hi];
                var r = h[hi, hi - 1];
                var s = h[hi, hi];
                var half = 0.5 * (p - s);
                var disc = Complex.Sqrt(half * half + q * r);
                var m1 = 0.5 * (p + s) + disc;
                var m2 = 0.5 * (p + s) - disc;
                mu = (m1 - s).Magnitude <= (m2 - s).Magnitude ? m1 : m2;
            }

            for (int k = l; k <= hi; k++) h[k, k] -= mu;
            // QR: rotations on rows k, k+1 (full rows to the right).
            for (int k = l; k < hi; k++)
            {
                var x = h[k, k];
                var y = h[k + 1, k];
                double rr = Math.Sqrt(Sq(x) + Sq(y));
                Complex c, s;
                if (rr == 0) { c = Complex.One; s = Complex.Zero; }
                else { c = x / rr; s = y / rr; }
                cs[k] = c;
                sn[k] = s;
                var cc = Complex.Conjugate(c);
                var sc = Complex.Conjugate(s);
                for (int j = k; j < n; j++)
                {
                    var t1 = h[k, j];
                    var t2 = h[k + 1, j];
                    h[k, j] = cc * t1 + sc * t2;
                    h[k + 1, j] = -s * t1 + c * t2;
                }
                h[k + 1, k] = Complex.Zero;
            }
            // RQ: the conjugate-transposed rotations on columns k, k+1 (rows 0 .. k+1), and into Z.
            for (int k = l; k < hi; k++)
            {
                var c = cs[k];
                var s = sn[k];
                var cc = Complex.Conjugate(c);
                var sc = Complex.Conjugate(s);
                for (int i = 0; i <= k + 1; i++)
                {
                    var t1 = h[i, k];
                    var t2 = h[i, k + 1];
                    h[i, k] = t1 * c + t2 * s;
                    h[i, k + 1] = -t1 * sc + t2 * cc;
                }
                if (z is not null)
                    for (int i = 0; i < n; i++)
                    {
                        var t1 = z[i, k];
                        var t2 = z[i, k + 1];
                        z[i, k] = t1 * c + t2 * s;
                        z[i, k + 1] = -t1 * sc + t2 * cc;
                    }
            }
            for (int k = l; k <= hi; k++) h[k, k] += mu;
        }
        // Clean the strictly lower triangle (zero by construction up to rounding of deflated entries).
        for (int i = 1; i < n; i++)
            for (int j = 0; j < i; j++) h[i, j] = Complex.Zero;
    }

    // ---- 3. Eigenvectors ----

    private static Complex[,] Eigenvectors(Complex[,] t, Complex[,] z)
    {
        int n = t.GetLength(0);
        double tnorm = 0;
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++) tnorm = Math.Max(tnorm, t[i, j].Magnitude);
        double small = Eps * Math.Max(tnorm, double.Epsilon);
        var result = new Complex[n, n];
        var x = new Complex[n];
        for (int k = n - 1; k >= 0; k--)
        {
            var lambda = t[k, k];
            for (int i = 0; i < n; i++) x[i] = Complex.Zero;
            x[k] = Complex.One;
            for (int i = k - 1; i >= 0; i--)
            {
                Complex sum = Complex.Zero;
                for (int j = i + 1; j <= k; j++) sum += t[i, j] * x[j];
                var denominator = t[i, i] - lambda;
                if (denominator.Magnitude < small) denominator = small;
                x[i] = -sum / denominator;
                double magnitude = x[i].Magnitude;
                if (magnitude > 1e100)
                {
                    double scale = 1 / magnitude;
                    for (int j = i; j <= k; j++) x[j] *= scale;
                }
            }
            double vnorm = 0;
            for (int i = 0; i < n; i++)
            {
                Complex sum = Complex.Zero;
                for (int j = 0; j <= k; j++) sum += z[i, j] * x[j];
                result[i, k] = sum;
                vnorm += Sq(sum);
            }
            vnorm = Math.Sqrt(vnorm);
            if (!(vnorm > 0) || !double.IsFinite(vnorm))
                throw new InvalidOperationException($"Complex eigen-decomposition: the eigenvector of eigenvalue {k} ({lambda}) could not be formed (norm {vnorm}).");
            for (int i = 0; i < n; i++) result[i, k] /= vnorm;
        }
        return result;
    }

    private static double Sq(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
}
