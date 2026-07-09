using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// A dense complex matrix in column-major storage. The moment-method impedance matrices
/// this backs are DENSE and complex-symmetric — the sparse CSR/COCG machinery is the
/// wrong tool there (no sparsity to exploit, and iterative convergence is unnecessary
/// at method-of-moments sizes), so dense LU is the transparent first-party answer.
/// </summary>
public sealed class ComplexDenseMatrix
{
    private readonly Complex[] _values;

    public ComplexDenseMatrix(int rows, int columns)
    {
        if (rows <= 0 || columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Matrix dimensions must be positive.");
        Rows = rows;
        Columns = columns;
        _values = new Complex[rows * columns];
    }

    public int Rows { get; }
    public int Columns { get; }

    public Complex this[int row, int column]
    {
        get => _values[column * Rows + row];
        set => _values[column * Rows + row] = value;
    }

    /// <summary>Deep copy — LU factorization is destructive and must never mutate the
    /// caller's assembled matrix.</summary>
    public ComplexDenseMatrix Clone()
    {
        var copy = new ComplexDenseMatrix(Rows, Columns);
        Array.Copy(_values, copy._values, _values.Length);
        return copy;
    }

    /// <summary>y = A·x.</summary>
    public Complex[] Multiply(IReadOnlyList<Complex> x)
    {
        if (x.Count != Columns)
            throw new ArgumentException($"Vector length {x.Count} does not match {Columns} columns.");
        var y = new Complex[Rows];
        for (int j = 0; j < Columns; j++)
        {
            Complex xj = x[j];
            int offset = j * Rows;
            for (int i = 0; i < Rows; i++)
                y[i] += _values[offset + i] * xj;
        }
        return y;
    }
}
