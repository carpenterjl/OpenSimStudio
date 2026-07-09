namespace OpenSim.Core.Numerics;

/// <summary>
/// Eigen-decomposition of a small dense symmetric matrix by cyclic Jacobi rotations.
/// Sized for the projected (Rayleigh–Ritz) problems of the subspace eigensolver — tens
/// of rows, not thousands. The sweep order is fixed (row-major over the upper triangle),
/// so results are deterministic.
/// </summary>
public static class JacobiEigenSolver
{
    /// <summary>
    /// Returns eigenvalues in ascending order with matching orthonormal eigenvectors
    /// (<c>Vectors[k]</c> is the eigenvector of <c>Values[k]</c>). The input is copied,
    /// not modified. Convergence: off-diagonal Frobenius norm below
    /// <paramref name="tolerance"/> · (Frobenius norm of the input).
    /// </summary>
    public static (double[] Values, double[][] Vectors) Solve(double[][] symmetric,
        double tolerance = 1e-12)
    {
        int n = symmetric.Length;
        var a = new double[n][];
        for (int i = 0; i < n; i++)
        {
            if (symmetric[i].Length != n)
                throw new ArgumentException("The matrix must be square.", nameof(symmetric));
            a[i] = (double[])symmetric[i].Clone();
        }
        var v = new double[n][];
        for (int i = 0; i < n; i++)
        {
            v[i] = new double[n];
            v[i][i] = 1.0;
        }

        double norm = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                norm += a[i][j] * a[i][j];
        norm = Math.Sqrt(norm);
        double threshold = Math.Max(tolerance * norm, double.Epsilon);

        const int maxSweeps = 100;
        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            double off = 0;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    off += 2 * a[i][j] * a[i][j];
            if (Math.Sqrt(off) <= threshold)
                break;

            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = a[p][q];
                    if (Math.Abs(apq) <= threshold / (n * n))
                        continue;

                    // Classic symmetric Schur rotation annihilating a[p][q].
                    double theta = (a[q][q] - a[p][p]) / (2 * apq);
                    double t = Math.Sign(theta == 0 ? 1 : theta)
                               / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1);
                    double s = t * c;

                    for (int i = 0; i < n; i++)
                    {
                        double aip = a[i][p], aiq = a[i][q];
                        a[i][p] = c * aip - s * aiq;
                        a[i][q] = s * aip + c * aiq;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        double api = a[p][i], aqi = a[q][i];
                        a[p][i] = c * api - s * aqi;
                        a[q][i] = s * api + c * aqi;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        double vip = v[i][p], viq = v[i][q];
                        v[i][p] = c * vip - s * viq;
                        v[i][q] = s * vip + c * viq;
                    }
                }
            }
        }

        var order = Enumerable.Range(0, n).OrderBy(i => a[i][i]).ToArray();
        var values = new double[n];
        var vectors = new double[n][];
        for (int k = 0; k < n; k++)
        {
            values[k] = a[order[k]][order[k]];
            vectors[k] = new double[n];
            for (int i = 0; i < n; i++)
                vectors[k][i] = v[i][order[k]];
        }
        return (values, vectors);
    }
}
