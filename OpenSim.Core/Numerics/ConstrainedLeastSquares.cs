namespace OpenSim.Core.Numerics;

/// <summary>
/// Linear least squares with linear equality constraints: min ‖A·x − b‖₂ subject to G·x = h,
/// by eliminating the constrained unknowns. Gauss–Jordan elimination with complete pivoting on
/// the (row-scaled) constraints expresses one unknown per constraint in terms of the others,
/// x_p = h′ − M·x_f; substituting gives the unconstrained problem
/// min ‖(A_f − A_p·M)·x_f − (b − A_p·h′)‖, which <see cref="HouseholderLeastSquares"/> solves.
/// The constraints hold to rounding whatever the data, which is what a fit that must pass
/// exactly through given values needs. Sized for a handful of constraints.
/// </summary>
public static class ConstrainedLeastSquares
{
    /// <summary>Solves the constrained problem. Throws when the constraints are linearly
    /// dependent or inconsistent, or when fewer rows remain than free unknowns.</summary>
    public static double[] Solve(double[,] a, double[] b, double[,] g, double[] h)
    {
        int rows = a.GetLength(0), n = a.GetLength(1), p = g.GetLength(0);
        if (b.Length != rows) throw new ArgumentException("One right-hand side value per row of A.", nameof(b));
        if (g.GetLength(1) != n) throw new ArgumentException("The constraints must have one column per unknown.", nameof(g));
        if (h.Length != p) throw new ArgumentException("One right-hand side value per constraint.", nameof(h));
        if (p > n) throw new ArgumentException("There are more constraints than unknowns.", nameof(g));
        if (rows < n - p) throw new ArgumentException("Least squares needs at least as many rows as free unknowns.", nameof(a));

        // Row-scaled copies of the constraints: each row's largest entry becomes 1.
        var c = new double[p, n];
        var d = new double[p];
        for (int i = 0; i < p; i++)
        {
            double largest = 0;
            for (int j = 0; j < n; j++) largest = Math.Max(largest, Math.Abs(g[i, j]));
            if (!(largest > 0) || !double.IsFinite(largest))
                throw new ArgumentException($"Constraint {i + 1} is zero or not finite.", nameof(g));
            for (int j = 0; j < n; j++) c[i, j] = g[i, j] / largest;
            d[i] = h[i] / largest;
        }

        // Gauss–Jordan with complete pivoting: pivot[k] is the unknown constraint k solves for.
        var pivot = new int[p];
        var used = new bool[n];
        for (int k = 0; k < p; k++)
        {
            int bestRow = -1, bestColumn = -1;
            double best = 0;
            for (int i = k; i < p; i++)
            for (int j = 0; j < n; j++)
                if (!used[j] && Math.Abs(c[i, j]) > best)
                {
                    best = Math.Abs(c[i, j]);
                    bestRow = i;
                    bestColumn = j;
                }
            if (best < 1e-13)
                throw new ArgumentException($"The constraints are linearly dependent (constraint {k + 1} adds nothing to the ones before it).", nameof(g));
            if (bestRow != k)
            {
                for (int j = 0; j < n; j++) (c[k, j], c[bestRow, j]) = (c[bestRow, j], c[k, j]);
                (d[k], d[bestRow]) = (d[bestRow], d[k]);
            }
            double inverse = 1 / c[k, bestColumn];
            for (int j = 0; j < n; j++) c[k, j] *= inverse;
            d[k] *= inverse;
            c[k, bestColumn] = 1;
            for (int i = 0; i < p; i++)
            {
                if (i == k || c[i, bestColumn] == 0) continue;
                double factor = c[i, bestColumn];
                for (int j = 0; j < n; j++) c[i, j] -= factor * c[k, j];
                d[i] -= factor * d[k];
                c[i, bestColumn] = 0;
            }
            pivot[k] = bestColumn;
            used[bestColumn] = true;
        }

        var free = Enumerable.Range(0, n).Where(j => !used[j]).ToArray();
        var x = new double[n];
        if (free.Length > 0)
        {
            // x_pivot[k] = d[k] − Σ_f c[k, f] x_f, substituted into A·x − b.
            var reduced = new double[rows, free.Length];
            var rhs = (double[])b.Clone();
            for (int r = 0; r < rows; r++)
            {
                for (int k = 0; k < p; k++) rhs[r] -= a[r, pivot[k]] * d[k];
                for (int f = 0; f < free.Length; f++)
                {
                    double value = a[r, free[f]];
                    for (int k = 0; k < p; k++) value -= a[r, pivot[k]] * c[k, free[f]];
                    reduced[r, f] = value;
                }
            }
            var xf = HouseholderLeastSquares.Solve(reduced, rhs);
            for (int f = 0; f < free.Length; f++) x[free[f]] = xf[f];
        }
        for (int k = 0; k < p; k++)
        {
            double value = d[k];
            foreach (int f in free) value -= c[k, f] * x[f];
            x[pivot[k]] = value;
        }
        return x;
    }
}
