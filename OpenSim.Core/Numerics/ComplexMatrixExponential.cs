using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// First-party complex matrix exponential by scaling-and-squaring with the Padé(13,13)
/// approximant (Higham 2005). Chosen for the SI multiconductor-transmission-line chain
/// matrices deliberately: expm([[0, −Z′ℓ], [−Y′ℓ, 0]]) IS the exact coupled-line chain
/// matrix, with no general complex eigensolver needed (defective/near-defective Z′Y′
/// products break eigendecomposition-based propagation; expm has no such failure mode).
/// Matrices here are tiny (2N ≤ ~16), so cost is irrelevant — robustness is the point.
/// </summary>
public static class ComplexMatrixExponential
{
    // Padé-13 coefficients (Higham, Table 10.4 style — the classic b vector).
    private static readonly double[] B =
    {
        64764752532480000, 32382376266240000, 7771770303897600, 1187353796428800,
        129060195264000, 10559470521600, 670442572800, 33522128640, 1323241920,
        40840800, 960960, 16380, 182, 1
    };

    /// <summary>θ₁₃: the largest scaled 1-norm for which Padé(13) reaches double precision.</summary>
    private const double Theta13 = 5.371920351148152;

    public static ComplexDenseMatrix Exponential(ComplexDenseMatrix a)
    {
        if (a.Rows != a.Columns)
            throw new ArgumentException("Matrix exponential requires a square matrix.", nameof(a));
        int n = a.Rows;

        double norm = OneNorm(a);
        int squarings = 0;
        var scaled = a.Clone();
        if (norm > Theta13)
        {
            squarings = Math.Max(0, (int)Math.Ceiling(Math.Log2(norm / Theta13)));
            double scale = Math.Pow(2, -squarings);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    scaled[i, j] = a[i, j] * scale;
        }

        // Padé(13): U = A·(b13·A6³ + …), V = even part; expm ≈ (V − U)⁻¹(V + U).
        var a2 = Multiply(scaled, scaled);
        var a4 = Multiply(a2, a2);
        var a6 = Multiply(a2, a4);

        var u = Multiply(scaled,
            Add(Multiply(a6, Combine(a6, B[13], a4, B[11], a2, B[9])),
                Combine(a6, B[7], a4, B[5], a2, B[3], identityScale: B[1])));
        var v = Add(Multiply(a6, Combine(a6, B[12], a4, B[10], a2, B[8])),
            Combine(a6, B[6], a4, B[4], a2, B[2], identityScale: B[0]));

        var numerator = Add(v, u);                    // V + U
        var denominator = Subtract(v, u);             // V − U
        var result = SolveMatrix(denominator, numerator);

        for (int s = 0; s < squarings; s++)
            result = Multiply(result, result);
        return result;
    }

    /// <summary>C = A·B for square complex matrices.</summary>
    public static ComplexDenseMatrix Multiply(ComplexDenseMatrix a, ComplexDenseMatrix b)
    {
        int n = a.Rows;
        if (a.Columns != b.Rows || b.Columns != n)
            throw new ArgumentException("Dimension mismatch in matrix multiply.");
        var c = new ComplexDenseMatrix(n, n);
        for (int j = 0; j < n; j++)
            for (int k = 0; k < n; k++)
            {
                Complex bkj = b[k, j];
                if (bkj == Complex.Zero) continue;
                for (int i = 0; i < n; i++)
                    c[i, j] += a[i, k] * bkj;
            }
        return c;
    }

    /// <summary>X with D·X = N (column-by-column through the shared LU).</summary>
    public static ComplexDenseMatrix SolveMatrix(ComplexDenseMatrix d, ComplexDenseMatrix rhs)
    {
        int n = d.Rows;
        var lu = ComplexLu.Factor(d);
        var x = new ComplexDenseMatrix(n, n);
        var column = new Complex[n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++) column[i] = rhs[i, j];
            var solved = lu.Solve(column);
            for (int i = 0; i < n; i++) x[i, j] = solved[i];
        }
        return x;
    }

    private static double OneNorm(ComplexDenseMatrix a)
    {
        double norm = 0;
        for (int j = 0; j < a.Columns; j++)
        {
            double sum = 0;
            for (int i = 0; i < a.Rows; i++) sum += a[i, j].Magnitude;
            norm = Math.Max(norm, sum);
        }
        return norm;
    }

    private static ComplexDenseMatrix Add(ComplexDenseMatrix a, ComplexDenseMatrix b)
    {
        var c = new ComplexDenseMatrix(a.Rows, a.Columns);
        for (int i = 0; i < a.Rows; i++)
            for (int j = 0; j < a.Columns; j++)
                c[i, j] = a[i, j] + b[i, j];
        return c;
    }

    private static ComplexDenseMatrix Subtract(ComplexDenseMatrix a, ComplexDenseMatrix b)
    {
        var c = new ComplexDenseMatrix(a.Rows, a.Columns);
        for (int i = 0; i < a.Rows; i++)
            for (int j = 0; j < a.Columns; j++)
                c[i, j] = a[i, j] - b[i, j];
        return c;
    }

    /// <summary>s6·A6 + s4·A4 + s2·A2 (+ scale·I).</summary>
    private static ComplexDenseMatrix Combine(ComplexDenseMatrix a6, double s6,
        ComplexDenseMatrix a4, double s4, ComplexDenseMatrix a2, double s2,
        double identityScale = 0)
    {
        int n = a6.Rows;
        var c = new ComplexDenseMatrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                c[i, j] = s6 * a6[i, j] + s4 * a4[i, j] + s2 * a2[i, j];
        if (identityScale != 0)
            for (int i = 0; i < n; i++)
                c[i, i] += identityScale;
        return c;
    }
}
