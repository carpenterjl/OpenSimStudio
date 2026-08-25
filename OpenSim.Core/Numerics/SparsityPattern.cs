namespace OpenSim.Core.Numerics;

/// <summary>
/// The non-zero POSITIONS of a matrix, derived once and filled many times.
///
/// A mesh's connectivity fixes where the non-zeros of every matrix assembled over it are. The
/// stiffness and the mass have the same pattern; so does every subsequent assembly on the same
/// mesh. Discovering it with a dictionary per row and a sort per row — which is what
/// <see cref="SparseMatrixBuilder"/> must do, knowing nothing in advance — is then paid for
/// repeatedly to learn the same answer.
///
/// With the pattern in hand an assembly is a values array plus a binary search per scatter, and
/// the result is bitwise what the builder produced: the same entries, summed in the same order
/// by the same element loop, with the same sorted columns.
/// </summary>
public sealed class SparsityPattern
{
    public int RowCount { get; }
    public int ColumnCount { get; }
    public int NonZeroCount => ColumnIndices.Length;

    /// <summary>CSR row offsets, one per row plus the total.</summary>
    public int[] RowPointers { get; }

    /// <summary>Column index of every stored entry, ascending within each row.</summary>
    public int[] ColumnIndices { get; }

    private SparsityPattern(int rows, int columns, int[] rowPointers, int[] columnIndices)
    {
        RowCount = rows;
        ColumnCount = columns;
        RowPointers = rowPointers;
        ColumnIndices = columnIndices;
    }

    /// <summary>
    /// The pattern of a finite-element assembly: every DOF of a block couples to every other
    /// DOF of that block. One block per element, listing its DOFs.
    /// <para>
    /// Built by counting, filling, then sorting each row in place — NOT by accumulating a set
    /// per row. A per-row set costs an allocation per distinct entry, and on a real mesh there
    /// are tens of millions of them: the first version of this used <c>SortedSet</c> and
    /// exhausted memory on a 5,000-element hexahedral beam, which the benchmark caught as a
    /// crashed test host. Two integer passes and one sort per row use a single flat buffer and
    /// are cheaper than the dictionary-per-row assembly this replaces, not merely cheaper than
    /// the set.
    /// </para>
    /// </summary>
    public static SparsityPattern FromBlocks(int rows, int columns, IEnumerable<int[]> blocks)
    {
        var blockList = blocks as IReadOnlyList<int[]> ?? blocks.ToList();

        // Pass 1: an upper bound per row — every block contributes its own size to each of its
        // rows, before duplicates are removed.
        var offsets = new int[rows + 1];
        foreach (var block in blockList)
            foreach (int row in block)
                offsets[row + 1] += block.Length;
        for (int r = 0; r < rows; r++)
            offsets[r + 1] += offsets[r];

        // Pass 2: fill one flat buffer, duplicates and all.
        var cursor = new int[rows];
        Array.Copy(offsets, cursor, rows);
        var scratch = new int[offsets[rows]];
        foreach (var block in blockList)
            foreach (int row in block)
            {
                int at = cursor[row];
                foreach (int column in block) scratch[at++] = column;
                cursor[row] = at;
            }

        // Sort each row and drop duplicates, compacting into the final arrays as we go.
        var rowPointers = new int[rows + 1];
        var columnIndices = new int[scratch.Length];
        int nnz = 0;
        for (int r = 0; r < rows; r++)
        {
            rowPointers[r] = nnz;
            int from = offsets[r], to = offsets[r + 1];
            if (from == to) continue;

            var span = scratch.AsSpan(from, to - from);
            span.Sort();

            int previous = span[0];
            columnIndices[nnz++] = previous;
            for (int i = 1; i < span.Length; i++)
                if (span[i] != previous)
                    columnIndices[nnz++] = previous = span[i];
        }
        rowPointers[rows] = nnz;

        Array.Resize(ref columnIndices, nnz);
        return new SparsityPattern(rows, columns, rowPointers, columnIndices);
    }

    /// <summary>A zeroed values array for this pattern.</summary>
    public double[] CreateValues() => new double[NonZeroCount];

    /// <summary>
    /// Adds to the entry at (row, column). The position must already be in the pattern — a
    /// scatter to somewhere it does not cover means the pattern and the assembly disagree
    /// about the connectivity, which is a bug rather than a case to grow into.
    /// </summary>
    public void Add(double[] values, int row, int column, double value)
    {
        int index = IndexOf(row, column);
        if (index < 0)
            throw new ArgumentException(
                $"({row}, {column}) is not in this sparsity pattern; the assembly is scattering " +
                "outside the connectivity the pattern was built from.");
        values[index] += value;
    }

    /// <summary>The index into a values array of one entry, or -1 when the pattern has none.</summary>
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

    /// <summary>
    /// Wraps a filled values array as a matrix. The pattern arrays are shared, not copied —
    /// they are immutable and every matrix built from this pattern has the same ones.
    /// </summary>
    public CsrMatrix ToMatrix(double[] values)
    {
        if (values.Length != NonZeroCount)
            throw new ArgumentException(
                $"The values array has {values.Length} entries but this pattern has {NonZeroCount}.",
                nameof(values));
        return CsrMatrix.FromArrays(RowCount, ColumnCount, RowPointers, ColumnIndices, values);
    }
}
