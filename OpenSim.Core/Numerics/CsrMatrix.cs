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

    /// <summary>Rows below this stay sequential: parallel dispatch costs more than the
    /// multiply itself on small systems.</summary>
    private const int ParallelRowThreshold = 20_000;

    /// <summary>Computes y = A·x (span form — always sequential; the hot CG loop calls
    /// the array overload below, which overload resolution prefers for arrays).</summary>
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

    /// <summary>
    /// Computes y = A·x. Large systems parallelize over ROWS — each row is an
    /// independently accumulated dot product whose in-row summation order is unchanged
    /// and whose write is disjoint, so the result is BITWISE identical to the sequential
    /// loop at any degree of parallelism (the Stage G slot-array argument; a 54k-cell CFD
    /// pressure march made the sequential matvec the visible bottleneck). The CG solver's
    /// dot-product REDUCTIONS stay sequential for the same reason in reverse:
    /// parallelizing a reduction would reorder its sums.
    /// </summary>
    public void Multiply(double[] x, double[] y)
    {
        if (RowCount < ParallelRowThreshold)
        {
            Multiply(x.AsSpan(), y.AsSpan());
            return;
        }
        if (x.Length != ColumnCount) throw new ArgumentException("x length must equal ColumnCount.", nameof(x));
        if (y.Length != RowCount) throw new ArgumentException("y length must equal RowCount.", nameof(y));

        var rp = RowPointers;
        var ci = ColumnIndices;
        var v = Values;
        Parallel.For(0, RowCount, row =>
        {
            double sum = 0;
            int end = rp[row + 1];
            for (int k = rp[row]; k < end; k++)
                sum += v[k] * x[ci[k]];
            y[row] = sum;
        });
    }

    /// <summary>Returns the diagonal entries (0 where the diagonal is not stored).</summary>
    /// <summary>
    /// The index into <see cref="Values"/> of one entry, or -1 when the pattern has no such
    /// entry. For a caller that rewrites the same positions repeatedly and resolves them once
    /// up front — the sparsity is fixed, so an index stays valid for the matrix's lifetime.
    /// </summary>
    public int IndexOf(int row, int column)
    {
        int lo = RowPointers[row], hi = RowPointers[row + 1] - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = ColumnIndices[mid];
            if (c == column) return mid;
            if (c < column) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

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

    /// <summary>
    /// Wraps pre-built CSR arrays (validated: monotone row pointers, in-range and
    /// strictly ascending column indices per row). For structured-grid operators whose
    /// sparsity pattern is known up front — the CFD 7-point stencils — building the
    /// arrays directly is far cheaper than the dictionary-per-row
    /// <see cref="SparseMatrixBuilder"/>, which allocates a hash map per row.
    /// The arrays are NOT copied: the caller hands over ownership.
    /// </summary>
    public static CsrMatrix FromArrays(int rows, int cols, int[] rowPointers,
        int[] columnIndices, double[] values)
    {
        if (rowPointers.Length != rows + 1)
            throw new ArgumentException($"rowPointers must have {rows + 1} entries.", nameof(rowPointers));
        if (rowPointers[0] != 0 || rowPointers[rows] != values.Length
            || columnIndices.Length != values.Length)
            throw new ArgumentException("Row pointers must span exactly the value array.");
        for (int r = 0; r < rows; r++)
        {
            if (rowPointers[r + 1] < rowPointers[r])
                throw new ArgumentException($"Row pointers must be monotone (row {r}).");
            for (int k = rowPointers[r]; k < rowPointers[r + 1]; k++)
            {
                if ((uint)columnIndices[k] >= (uint)cols)
                    throw new ArgumentException($"Column index {columnIndices[k]} out of range in row {r}.");
                if (k > rowPointers[r] && columnIndices[k] <= columnIndices[k - 1])
                    throw new ArgumentException($"Column indices must ascend strictly within row {r}.");
            }
        }
        return new CsrMatrix(rows, cols, rowPointers, columnIndices, values);
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
