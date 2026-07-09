using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// Immutable complex sparse matrix in CSR format, built by zipping two real
/// <see cref="CsrMatrix"/> instances with identical sparsity (the FEM case: the σ and
/// ε stiffness matrices come from the same connectivity, so their patterns match by
/// construction). The integer structure arrays are SHARED with the real matrices —
/// only the values are new.
/// </summary>
public sealed class ComplexCsrMatrix
{
    public int[] RowPointers { get; }
    public int[] ColumnIndices { get; }
    public Complex[] Values { get; }
    public int RowCount { get; }
    public int ColumnCount { get; }
    public int NonZeroCount => Values.Length;

    private ComplexCsrMatrix(int rows, int cols, int[] rowPointers, int[] columnIndices, Complex[] values)
    {
        RowCount = rows;
        ColumnCount = cols;
        RowPointers = rowPointers;
        ColumnIndices = columnIndices;
        Values = values;
    }

    /// <summary>
    /// Builds A = real + i·imagScale·imag. Both inputs must have EXACTLY the same
    /// sparsity structure (row pointers and column indices element-wise identical);
    /// anything else would silently misalign values, so it throws instead.
    /// </summary>
    public static ComplexCsrMatrix Combine(CsrMatrix real, CsrMatrix imag, double imagScale)
    {
        if (real.RowCount != imag.RowCount || real.ColumnCount != imag.ColumnCount)
            throw new ArgumentException("The real and imaginary matrices must have the same dimensions.");
        if (!real.RowPointers.AsSpan().SequenceEqual(imag.RowPointers)
            || !real.ColumnIndices.AsSpan().SequenceEqual(imag.ColumnIndices))
            throw new ArgumentException(
                "The real and imaginary matrices must share an identical sparsity pattern " +
                "(assemble both over the same mesh connectivity).");

        var values = new Complex[real.Values.Length];
        for (int k = 0; k < values.Length; k++)
            values[k] = new Complex(real.Values[k], imagScale * imag.Values[k]);
        return new ComplexCsrMatrix(real.RowCount, real.ColumnCount,
            real.RowPointers, real.ColumnIndices, values);
    }

    /// <summary>Computes y = A·x.</summary>
    public void Multiply(ReadOnlySpan<Complex> x, Span<Complex> y)
    {
        if (x.Length != ColumnCount) throw new ArgumentException("x length must equal ColumnCount.", nameof(x));
        if (y.Length != RowCount) throw new ArgumentException("y length must equal RowCount.", nameof(y));

        var rp = RowPointers;
        var ci = ColumnIndices;
        var v = Values;
        for (int row = 0; row < RowCount; row++)
        {
            Complex sum = Complex.Zero;
            int end = rp[row + 1];
            for (int k = rp[row]; k < end; k++)
                sum += v[k] * x[ci[k]];
            y[row] = sum;
        }
    }

    /// <summary>Returns the diagonal entries (0 where the diagonal is not stored).</summary>
    public Complex[] GetDiagonal()
    {
        var diag = new Complex[Math.Min(RowCount, ColumnCount)];
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
}
