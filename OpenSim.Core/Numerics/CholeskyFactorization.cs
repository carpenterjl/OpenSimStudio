namespace OpenSim.Core.Numerics;

/// <summary>
/// The Cholesky factorization A = L·Lᵀ of a dense real symmetric positive-definite matrix, with L
/// lower triangular. Only the lower triangle of A is read. Used where a matrix is known to be
/// positive definite (normal equations with damping, covariance matrices); a matrix that is not
/// is reported, never factored approximately.
/// </summary>
public sealed class CholeskyFactorization
{
    private readonly double[,] _l;

    private CholeskyFactorization(double[,] l) => _l = l;

    public int Size => _l.GetLength(0);

    /// <summary>A copy of the lower-triangular factor L.</summary>
    public double[,] L => (double[,])_l.Clone();

    /// <summary>Factors the matrix; throws <see cref="InvalidOperationException"/> if it is not
    /// positive definite.</summary>
    public static CholeskyFactorization Factor(double[,] a)
        => TryFactor(a, out var factorization)
            ? factorization!
            : throw new InvalidOperationException("The matrix is not positive definite.");

    /// <summary>Factors the matrix, or returns false if a pivot is not positive (the matrix is
    /// not positive definite to working precision) or an entry is not finite.</summary>
    public static bool TryFactor(double[,] a, out CholeskyFactorization? factorization)
    {
        int n = a.GetLength(0);
        if (a.GetLength(1) != n) throw new ArgumentException("The matrix must be square.", nameof(a));
        var l = new double[n, n];
        factorization = null;
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i, j];
                for (int k = 0; k < j; k++) sum -= l[i, k] * l[j, k];
                if (i == j)
                {
                    // The comparison is written so that a NaN fails it.
                    if (!(sum > 0) || double.IsInfinity(sum)) return false;
                    l[i, i] = Math.Sqrt(sum);
                }
                else
                {
                    l[i, j] = sum / l[j, j];
                }
            }
        factorization = new CholeskyFactorization(l);
        return true;
    }

    /// <summary>Solves A·x = b by a forward and a back substitution.</summary>
    public double[] Solve(double[] b)
    {
        int n = Size;
        if (b.Length != n) throw new ArgumentException("One right-hand side value per row.", nameof(b));
        var x = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = b[i];
            for (int k = 0; k < i; k++) sum -= _l[i, k] * x[k];
            x[i] = sum / _l[i, i];
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = x[i];
            for (int k = i + 1; k < n; k++) sum -= _l[k, i] * x[k];
            x[i] = sum / _l[i, i];
        }
        return x;
    }

    /// <summary>ln det A = 2·Σ ln L[i,i].</summary>
    public double LogDeterminant
    {
        get
        {
            double sum = 0;
            for (int i = 0; i < Size; i++) sum += Math.Log(_l[i, i]);
            return 2 * sum;
        }
    }
}
