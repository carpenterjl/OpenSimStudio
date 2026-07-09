using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// LU factorization of a square complex matrix with partial (row) pivoting, plus
/// forward/back substitution. Deterministic: the pivot is the strictly largest
/// magnitude in the column, ties resolving to the lowest row index. An exactly zero
/// pivot throws — a singular moment-method matrix means degenerate input geometry, and
/// a garbage solution must never propagate into field values.
/// </summary>
public sealed class ComplexLu
{
    private readonly ComplexDenseMatrix _lu;
    private readonly int[] _pivots;

    private ComplexLu(ComplexDenseMatrix lu, int[] pivots)
    {
        _lu = lu;
        _pivots = pivots;
    }

    public int Order => _lu.Rows;

    /// <summary>Factors a copy of <paramref name="matrix"/> (the input is not mutated).</summary>
    public static ComplexLu Factor(ComplexDenseMatrix matrix)
    {
        if (matrix.Rows != matrix.Columns)
            throw new ArgumentException($"LU requires a square matrix, got {matrix.Rows}×{matrix.Columns}.");
        int n = matrix.Rows;
        var lu = matrix.Clone();
        var pivots = new int[n];

        for (int k = 0; k < n; k++)
        {
            int pivotRow = k;
            double pivotMagnitude = lu[k, k].Magnitude;
            for (int i = k + 1; i < n; i++)
            {
                double magnitude = lu[i, k].Magnitude;
                if (magnitude > pivotMagnitude)
                {
                    pivotMagnitude = magnitude;
                    pivotRow = i;
                }
            }
            if (pivotMagnitude == 0)
                throw new InvalidOperationException(
                    $"The matrix is singular (zero pivot at column {k}) — " +
                    "the underlying geometry is degenerate or duplicated.");
            pivots[k] = pivotRow;
            if (pivotRow != k)
                for (int j = 0; j < n; j++)
                    (lu[k, j], lu[pivotRow, j]) = (lu[pivotRow, j], lu[k, j]);

            Complex pivot = lu[k, k];
            for (int i = k + 1; i < n; i++)
            {
                Complex factor = lu[i, k] / pivot;
                lu[i, k] = factor;
                if (factor == Complex.Zero) continue;
                for (int j = k + 1; j < n; j++)
                    lu[i, j] -= factor * lu[k, j];
            }
        }
        return new ComplexLu(lu, pivots);
    }

    /// <summary>Solves A·x = b for one right-hand side (b is not mutated). The
    /// factorization is reusable across right-hand sides — one factor, many feeds.</summary>
    public Complex[] Solve(IReadOnlyList<Complex> rhs)
    {
        int n = Order;
        if (rhs.Count != n)
            throw new ArgumentException($"Right-hand side length {rhs.Count} does not match order {n}.");
        var x = new Complex[n];
        for (int i = 0; i < n; i++) x[i] = rhs[i];

        // Apply the row permutation, then Ly = Pb (unit lower), then Ux = y.
        for (int k = 0; k < n; k++)
            if (_pivots[k] != k)
                (x[k], x[_pivots[k]]) = (x[_pivots[k]], x[k]);
        for (int i = 1; i < n; i++)
        {
            Complex sum = x[i];
            for (int j = 0; j < i; j++)
                sum -= _lu[i, j] * x[j];
            x[i] = sum;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            Complex sum = x[i];
            for (int j = i + 1; j < n; j++)
                sum -= _lu[i, j] * x[j];
            x[i] = sum / _lu[i, i];
        }
        return x;
    }
}
