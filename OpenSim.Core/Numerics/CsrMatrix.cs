namespace OpenSim.Core.Numerics;

/// <summary>
/// Immutable sparse matrix in Compressed Sparse Row format.
/// Built via <see cref="SparseMatrixBuilder"/>, consumed by iterative solvers.
/// </summary>
public sealed class CsrMatrix
{
    /// <summary>Row pointers, length RowCount + 1.</summary>
    public int[] RowPointers { get; }
    /// <summary>Column index of each stored entry.</summary>
    public int[] ColumnIndices { get; }
    /// <summary>Value of each stored entry.</summary>
    public double[] Values { get; }
    public int RowCount { get; }
    public int ColumnCount { get; }
    public int NonZeroCount => Values.Length;

    internal CsrMatrix(int rows, int cols, int[] rowPointers, int[] columnIndices, double[] values)
    {
        RowCount = rows;
        ColumnCount = cols;
        RowPointers = rowPointers;
        ColumnIndices = columnIndices;
        Values = values;
    }

    /// <summary>Computes y = A·x.</summary>
    public void Multiply(ReadOnlySpan<double> x, Span<double> y)
    {
        if (x.Length != ColumnCount) throw new ArgumentException("x length must equal ColumnCount.", nameof(x));
        if (y.Length != RowCount) throw new ArgumentException("y length must equal RowCount.", nameof(y));

        var rp = RowPointers;
        var ci = ColumnIndices;
        var v = Values;
        for (int row = 0; row < RowCount; row++)
        {
            double sum = 0;
            int end = rp[row + 1];
            for (int k = rp[row]; k < end; k++)
                sum += v[k] * x[ci[k]];
            y[row] = sum;
        }
    }

    /// <summary>Returns the diagonal entries (0 where the diagonal is not stored).</summary>
    public double[] GetDiagonal()
    {
        var diag = new double[Math.Min(RowCount, ColumnCount)];
        for (int row = 0; row < diag.Length; row++)
        {
            int end = RowPointers[row + 1];
            for (int k = RowPointers[row]; k < end; k++)
            {
                if (ColumnIndices[k] == row)
                {
                    diag[row] = Values[k];
                    break;
                }
            }
        }
        return diag;
    }

    /// <summary>Returns the stored value at (row, col), or 0 if the position is not stored.</summary>
    public double GetValue(int row, int col)
    {
        int end = RowPointers[row + 1];
        for (int k = RowPointers[row]; k < end; k++)
            if (ColumnIndices[k] == col)
                return Values[k];
        return 0;
    }
}

/// <summary>
/// Accumulates entries (duplicates are summed — the natural fit for FEM assembly)
/// and produces a <see cref="CsrMatrix"/> with sorted column indices per row.
/// </summary>
public sealed class SparseMatrixBuilder
{
    private readonly Dictionary<int, double>[] _rows;
    public int RowCount { get; }
    public int ColumnCount { get; }

    public SparseMatrixBuilder(int rows, int cols)
    {
        RowCount = rows;
        ColumnCount = cols;
        _rows = new Dictionary<int, double>[rows];
    }

    /// <summary>Adds <paramref name="value"/> to the entry at (row, col).</summary>
    public void Add(int row, int col, double value)
    {
        if ((uint)row >= (uint)RowCount) throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)col >= (uint)ColumnCount) throw new ArgumentOutOfRangeException(nameof(col));
        var r = _rows[row] ??= new Dictionary<int, double>();
        r.TryGetValue(col, out double existing);
        r[col] = existing + value;
    }

    /// <summary>Overwrites the entry at (row, col).</summary>
    public void Set(int row, int col, double value)
    {
        var r = _rows[row] ??= new Dictionary<int, double>();
        r[col] = value;
    }

    /// <summary>Removes all entries in the given row.</summary>
    public void ClearRow(int row) => _rows[row]?.Clear();

    public CsrMatrix Build()
    {
        var rowPointers = new int[RowCount + 1];
        int nnz = 0;
        for (int i = 0; i < RowCount; i++)
        {
            rowPointers[i] = nnz;
            nnz += _rows[i]?.Count ?? 0;
        }
        rowPointers[RowCount] = nnz;

        var columnIndices = new int[nnz];
        var values = new double[nnz];
        for (int i = 0; i < RowCount; i++)
        {
            var r = _rows[i];
            if (r is null) continue;
            int k = rowPointers[i];
            foreach (var kv in r.OrderBy(kv => kv.Key))
            {
                columnIndices[k] = kv.Key;
                values[k] = kv.Value;
                k++;
            }
        }
        return new CsrMatrix(RowCount, ColumnCount, rowPointers, columnIndices, values);
    }
}
