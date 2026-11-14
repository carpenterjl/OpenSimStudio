namespace OpenSim.Core.Numerics;

/// <summary>
/// The thin singular value decomposition A = U·diag(σ)·Vᵀ of a dense real matrix, by one-sided
/// Jacobi rotations (Hestenes): pairs of columns are rotated until every pair is orthogonal, and
/// the column lengths are then the singular values. The method is chosen for accuracy, not
/// speed: it finds small singular values to high relative accuracy, which is what a
/// rank-revealing fit or a damped optimizer step needs. The cost is a few sweeps of
/// rows × columns² work, which suits matrices up to a few thousand rows by a few hundred columns.
/// <para>
/// With k = min(rows, columns): U is rows × k and V is columns × k, both with orthonormal columns,
/// and σ has k values in descending order. Sequential and deterministic.
/// </para>
/// </summary>
public sealed class RealSvd
{
    private const double Epsilon = 2.220446049250313e-16;
    private const int MaxSweeps = 60;

    /// <summary>The left singular vectors as columns: rows × min(rows, columns).</summary>
    public double[,] U { get; }

    /// <summary>The singular values, largest first.</summary>
    public double[] SingularValues { get; }

    /// <summary>The right singular vectors as columns: columns × min(rows, columns).</summary>
    public double[,] V { get; }

    public int Rows => U.GetLength(0);
    public int Columns => V.GetLength(0);

    private RealSvd(double[,] u, double[] sigma, double[,] v)
    {
        U = u;
        SingularValues = sigma;
        V = v;
    }

    /// <summary>Decomposes a matrix of any shape. Throws if an entry is not finite or the
    /// rotations do not converge.</summary>
    public static RealSvd Decompose(double[,] a)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        if (m == 0 || n == 0) throw new ArgumentException("The matrix must not be empty.", nameof(a));
        double largest = 0;
        foreach (double value in a)
        {
            if (!double.IsFinite(value)) throw new ArgumentException("Every entry must be finite.", nameof(a));
            largest = Math.Max(largest, Math.Abs(value));
        }
        // A power-of-two scale keeps the sums of squares in range and changes no digit.
        double scale = largest > 0 ? Math.ScaleB(1.0, -Math.ILogB(largest)) : 1.0;

        // The rotations run down columns, and there must be at least as many rows as columns;
        // a wide matrix is decomposed as its transpose with U and V exchanged.
        bool transposed = m < n;
        int rows = transposed ? n : m, columns = transposed ? m : n;
        var w = new double[columns][];
        for (int j = 0; j < columns; j++)
        {
            var column = new double[rows];
            for (int i = 0; i < rows; i++) column[i] = scale * (transposed ? a[j, i] : a[i, j]);
            w[j] = column;
        }
        var (left, sigma, right) = DecomposeTall(w, rows, columns);
        for (int k = 0; k < sigma.Length; k++) sigma[k] /= scale;
        return transposed ? new RealSvd(right, sigma, left) : new RealSvd(left, sigma, right);
    }

    private static (double[,] U, double[] Sigma, double[,] V) DecomposeTall(double[][] w, int rows, int columns)
    {
        var v = new double[columns][];
        for (int j = 0; j < columns; j++)
        {
            v[j] = new double[columns];
            v[j][j] = 1;
        }
        // The same stopping rule as LAPACK's Jacobi SVD: every pair orthogonal to rounding level
        // relative to the two column lengths.
        double tolerance = Math.Sqrt(rows) * Epsilon;
        bool converged = false;
        for (int sweep = 0; sweep < MaxSweeps && !converged; sweep++)
        {
            converged = true;
            for (int p = 0; p < columns - 1; p++)
                for (int q = p + 1; q < columns; q++)
                {
                    double[] wp = w[p], wq = w[q];
                    double alpha = 0, beta = 0, gamma = 0;
                    for (int i = 0; i < rows; i++)
                    {
                        alpha += wp[i] * wp[i];
                        beta += wq[i] * wq[i];
                        gamma += wp[i] * wq[i];
                    }
                    if (gamma == 0 || Math.Abs(gamma) <= tolerance * Math.Sqrt(alpha * beta)) continue;
                    converged = false;
                    double zeta = (beta - alpha) / (2 * gamma);
                    double t = (zeta >= 0 ? 1 : -1) / (Math.Abs(zeta) + Math.Sqrt(1 + zeta * zeta));
                    double c = 1 / Math.Sqrt(1 + t * t), s = c * t;
                    Rotate(wp, wq, c, s, rows);
                    Rotate(v[p], v[q], c, s, columns);
                }
        }
        if (!converged) throw new InvalidOperationException("The singular value iteration did not converge.");

        var lengths = new double[columns];
        for (int j = 0; j < columns; j++)
        {
            double sum = 0;
            for (int i = 0; i < rows; i++) sum += w[j][i] * w[j][i];
            lengths[j] = Math.Sqrt(sum);
        }
        var order = Enumerable.Range(0, columns).OrderByDescending(j => lengths[j]).ThenBy(j => j).ToArray();
        var u = new double[rows, columns];
        var vOut = new double[columns, columns];
        var sigma = new double[columns];
        var empty = new List<int>();
        for (int k = 0; k < columns; k++)
        {
            int j = order[k];
            sigma[k] = lengths[j];
            if (lengths[j] > 0)
                for (int i = 0; i < rows; i++) u[i, k] = w[j][i] / lengths[j];
            else
                empty.Add(k);
            for (int i = 0; i < columns; i++) vOut[i, k] = v[j][i];
        }
        foreach (int k in empty) Complete(u, k, rows, columns, empty);
        return (u, sigma, vOut);
    }

    private static void Rotate(double[] x, double[] y, double c, double s, int length)
    {
        for (int i = 0; i < length; i++)
        {
            double a = x[i], b = y[i];
            x[i] = c * a - s * b;
            y[i] = s * a + c * b;
        }
    }

    /// <summary>Fills column <paramref name="target"/> of U, which belongs to a zero singular
    /// value, with a unit vector orthogonal to every column already set, so that U keeps
    /// orthonormal columns for a rank-deficient matrix.</summary>
    private static void Complete(double[,] u, int target, int rows, int columns, List<int> empty)
    {
        var candidate = new double[rows];
        // The projections of the axis vectors off the set columns have squared lengths that sum
        // to at least 1, so one of them is at least 1/rows long.
        double accept = 0.5 / rows;
        for (int axis = 0; axis < rows; axis++)
        {
            Array.Clear(candidate);
            candidate[axis] = 1;
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < columns; k++)
                {
                    if (k == target || (empty.Contains(k) && k > target)) continue;
                    double dot = 0;
                    for (int i = 0; i < rows; i++) dot += u[i, k] * candidate[i];
                    for (int i = 0; i < rows; i++) candidate[i] -= dot * u[i, k];
                }
            double sum = 0;
            for (int i = 0; i < rows; i++) sum += candidate[i] * candidate[i];
            if (sum < accept) continue;
            double length = Math.Sqrt(sum);
            for (int i = 0; i < rows; i++) u[i, target] = candidate[i] / length;
            return;
        }
        throw new InvalidOperationException("No vector completes the orthonormal set.");
    }

    /// <summary>The number of singular values above <paramref name="relativeTolerance"/> times the
    /// largest. The default tolerance is max(rows, columns) × machine epsilon.</summary>
    public int Rank(double relativeTolerance = -1)
    {
        double floor = Floor(relativeTolerance);
        int rank = 0;
        foreach (double value in SingularValues)
            if (value > floor) rank++;
        return rank;
    }

    /// <summary>σ(largest) / σ(smallest); infinity for a singular matrix.</summary>
    public double ConditionNumber
        => SingularValues[^1] > 0 ? SingularValues[0] / SingularValues[^1] : double.PositiveInfinity;

    /// <summary>The least-squares solution of A·x = b of smallest length: the pseudo-inverse
    /// applied to b. Singular values at or below <paramref name="relativeTolerance"/> times the
    /// largest are treated as zero (default max(rows, columns) × machine epsilon).</summary>
    public double[] Solve(double[] b, double relativeTolerance = -1)
    {
        if (b.Length != Rows) throw new ArgumentException("One right-hand side value per row.", nameof(b));
        double floor = Floor(relativeTolerance);
        var x = new double[Columns];
        for (int k = 0; k < SingularValues.Length; k++)
        {
            if (SingularValues[k] <= floor) continue;
            double dot = 0;
            for (int i = 0; i < b.Length; i++) dot += U[i, k] * b[i];
            double factor = dot / SingularValues[k];
            for (int j = 0; j < x.Length; j++) x[j] += factor * V[j, k];
        }
        return x;
    }

    /// <summary>U·diag(σ)·Vᵀ, for checking a decomposition.</summary>
    public double[,] Reconstruct()
    {
        var a = new double[Rows, Columns];
        for (int k = 0; k < SingularValues.Length; k++)
            for (int i = 0; i < Rows; i++)
            {
                double scaled = U[i, k] * SingularValues[k];
                for (int j = 0; j < Columns; j++) a[i, j] += scaled * V[j, k];
            }
        return a;
    }

    private double Floor(double relativeTolerance)
    {
        if (relativeTolerance < 0) relativeTolerance = Math.Max(Rows, Columns) * Epsilon;
        return relativeTolerance * SingularValues[0];
    }
}
