using System.Numerics;
using MathNet.Numerics.LinearAlgebra;
using OpenSim.Core.Numerics;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// OP-4's gate: the real SVD and Cholesky against their defining identities and MathNet.Numerics
/// (test-only reference), the 2-D FFT against the direct double sum and Parseval's theorem, and
/// the matrix Fourier transform against the FFT it generalises.
/// </summary>
public class DenseTransformsTests
{
    // ---- Real SVD ----

    private static double[,] RandomMatrix(Random random, int rows, int columns)
    {
        var a = new double[rows, columns];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < columns; j++) a[i, j] = random.NextDouble() - 0.5;
        return a;
    }

    /// <summary>A rows × columns matrix of exactly the given rank: a product of two random factors.</summary>
    private static double[,] RankDeficient(Random random, int rows, int columns, int rank)
    {
        var left = RandomMatrix(random, rows, rank);
        var right = RandomMatrix(random, rank, columns);
        var a = new double[rows, columns];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < columns; j++)
                for (int k = 0; k < rank; k++) a[i, j] += left[i, k] * right[k, j];
        return a;
    }

    private static void AssertDecomposition(double[,] a, RealSvd svd)
    {
        int rows = a.GetLength(0), columns = a.GetLength(1), k = Math.Min(rows, columns);
        Assert.Equal(k, svd.SingularValues.Length);
        Assert.Equal((rows, k), (svd.U.GetLength(0), svd.U.GetLength(1)));
        Assert.Equal((columns, k), (svd.V.GetLength(0), svd.V.GetLength(1)));
        double top = Math.Max(svd.SingularValues[0], 1e-300);
        for (int i = 1; i < k; i++) Assert.True(svd.SingularValues[i] <= svd.SingularValues[i - 1]);
        Assert.True(svd.SingularValues[^1] >= 0);

        var rebuilt = svd.Reconstruct();
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < columns; j++)
                Assert.True(Math.Abs(rebuilt[i, j] - a[i, j]) <= 1e-12 * top,
                    $"reconstruction off by {Math.Abs(rebuilt[i, j] - a[i, j]):E2} at ({i},{j})");

        AssertOrthonormalColumns(svd.U);
        AssertOrthonormalColumns(svd.V);

        var reference = Matrix<double>.Build.DenseOfArray(a).Svd(false).S;
        for (int i = 0; i < k; i++)
            Assert.True(Math.Abs(reference[i] - svd.SingularValues[i]) <= 1e-12 * top,
                $"singular value {i}: {svd.SingularValues[i]:R} against MathNet {reference[i]:R}");
    }

    private static void AssertOrthonormalColumns(double[,] q)
    {
        int rows = q.GetLength(0), columns = q.GetLength(1);
        for (int a = 0; a < columns; a++)
            for (int b = a; b < columns; b++)
            {
                double dot = 0;
                for (int i = 0; i < rows; i++) dot += q[i, a] * q[i, b];
                Assert.True(Math.Abs(dot - (a == b ? 1 : 0)) <= 1e-12, $"columns {a},{b}: dot {dot:R}");
            }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 7)]
    [InlineData(40, 40)]
    [InlineData(60, 9)]
    [InlineData(9, 60)]
    [InlineData(400, 36)]
    [InlineData(1, 12)]
    [InlineData(12, 1)]
    public void Svd_RandomMatrix_ReconstructsAndMatchesMathNet(int rows, int columns)
    {
        var a = RandomMatrix(new Random(1000 * rows + columns), rows, columns);
        AssertDecomposition(a, RealSvd.Decompose(a));
    }

    [Theory]
    [InlineData(30, 12, 5)]
    [InlineData(12, 30, 5)]
    [InlineData(20, 20, 1)]
    [InlineData(50, 10, 9)]
    public void Svd_RankDeficientMatrix_ReconstructsAndReportsItsRank(int rows, int columns, int rank)
    {
        var a = RankDeficient(new Random(rows + 31 * columns + 977 * rank), rows, columns, rank);
        var svd = RealSvd.Decompose(a);
        AssertDecomposition(a, svd);
        Assert.Equal(rank, svd.Rank());
        Assert.True(svd.SingularValues[rank] <= 1e-13 * svd.SingularValues[0]);
    }

    [Fact]
    public void Svd_MatrixWithRepeatedAndZeroColumns_KeepsOrthonormalFactors()
    {
        // Two equal columns and one of zeros: singular values that are exactly zero.
        var random = new Random(5);
        var a = new double[8, 5];
        for (int i = 0; i < 8; i++)
        {
            a[i, 0] = random.NextDouble() - 0.5;
            a[i, 1] = a[i, 0];
            a[i, 3] = random.NextDouble() - 0.5;
            a[i, 4] = 2 * a[i, 3];
        }
        var svd = RealSvd.Decompose(a);
        AssertDecomposition(a, svd);
        Assert.Equal(2, svd.Rank());

        var zero = RealSvd.Decompose(new double[4, 3]);
        Assert.All(zero.SingularValues, value => Assert.Equal(0, value));
        AssertOrthonormalColumns(zero.U);
        AssertOrthonormalColumns(zero.V);
        Assert.Equal(double.PositiveInfinity, zero.ConditionNumber);
    }

    [Fact]
    public void Svd_DiagonalAndScaledMatrices_GiveTheirKnownSingularValues()
    {
        // A diagonal matrix has the absolute values of its diagonal as singular values.
        var diagonal = new double[4, 4];
        double[] entries = { 3, -7, 0.5, 2 };
        for (int i = 0; i < 4; i++) diagonal[i, i] = entries[i];
        Assert.Equal(new double[] { 7, 3, 2, 0.5 }, RealSvd.Decompose(diagonal).SingularValues);
        Assert.Equal(14, RealSvd.Decompose(diagonal).ConditionNumber, 12);

        // Scaling a matrix scales its singular values and nothing else, far from 1 too.
        var a = RandomMatrix(new Random(3), 9, 6);
        var big = new double[9, 6];
        var small = new double[9, 6];
        for (int i = 0; i < 9; i++)
            for (int j = 0; j < 6; j++) { big[i, j] = a[i, j] * 1e200; small[i, j] = a[i, j] * 1e-200; }
        var sigma = RealSvd.Decompose(a).SingularValues;
        var sigmaBig = RealSvd.Decompose(big).SingularValues;
        var sigmaSmall = RealSvd.Decompose(small).SingularValues;
        for (int k = 0; k < 6; k++)
        {
            Assert.Equal(1, sigmaBig[k] / 1e200 / sigma[k], 12);
            Assert.Equal(1, sigmaSmall[k] / 1e-200 / sigma[k], 12);
        }
    }

    [Fact]
    public void Svd_HilbertMatrix_FindsSmallSingularValuesLikeMathNet()
    {
        // H[i,j] = 1/(i+j+1), the standard ill-conditioned test matrix.
        int n = 8;
        var h = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) h[i, j] = 1.0 / (i + j + 1);
        var svd = RealSvd.Decompose(h);
        AssertDecomposition(h, svd);
        Assert.Equal(n, svd.Rank());
        var reference = Matrix<double>.Build.DenseOfArray(h).Svd(false);
        Assert.Equal(1, svd.ConditionNumber / reference.ConditionNumber, 4);
    }

    [Fact]
    public void Svd_Solve_IsThePseudoInverse()
    {
        var random = new Random(21);
        // Full rank and tall, rank deficient, and wide (minimum-norm solution).
        var cases = new[]
        {
            RandomMatrix(random, 25, 6), RankDeficient(random, 25, 8, 4), RandomMatrix(random, 5, 11)
        };
        foreach (var a in cases)
        {
            var b = new double[a.GetLength(0)];
            for (int i = 0; i < b.Length; i++) b[i] = random.NextDouble() - 0.5;
            var x = RealSvd.Decompose(a).Solve(b);
            var reference = Matrix<double>.Build.DenseOfArray(a).PseudoInverse()
                * MathNet.Numerics.LinearAlgebra.Vector<double>.Build.DenseOfArray(b);
            for (int j = 0; j < x.Length; j++) Assert.Equal(reference[j], x[j], 10);
        }
    }

    [Fact]
    public void Svd_RejectsEmptyAndNonFiniteInput()
    {
        Assert.Throws<ArgumentException>(() => RealSvd.Decompose(new double[0, 3]));
        var bad = new double[2, 2];
        bad[1, 0] = double.NaN;
        Assert.Throws<ArgumentException>(() => RealSvd.Decompose(bad));
    }

    // ---- Cholesky ----

    private static double[,] RandomPositiveDefinite(Random random, int n)
    {
        var g = RandomMatrix(random, n + 3, n);
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int k = 0; k < n + 3; k++) a[i, j] += g[k, i] * g[k, j];
        return a;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(20)]
    [InlineData(75)]
    public void Cholesky_FactorsSolvesAndMatchesMathNet(int n)
    {
        var random = new Random(40 + n);
        var a = RandomPositiveDefinite(random, n);
        var factorization = CholeskyFactorization.Factor(a);
        var l = factorization.L;
        double scale = 0;
        foreach (double value in a) scale = Math.Max(scale, Math.Abs(value));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (j > i) Assert.Equal(0, l[i, j]);
                double sum = 0;
                for (int k = 0; k < n; k++) sum += l[i, k] * l[j, k];
                Assert.True(Math.Abs(sum - a[i, j]) <= 1e-13 * scale);
            }

        var reference = Matrix<double>.Build.DenseOfArray(a).Cholesky();
        var b = new double[n];
        for (int i = 0; i < n; i++) b[i] = random.NextDouble() - 0.5;
        var x = factorization.Solve(b);
        var expected = reference.Solve(MathNet.Numerics.LinearAlgebra.Vector<double>.Build.DenseOfArray(b));
        for (int i = 0; i < n; i++) Assert.Equal(expected[i], x[i], 1e-9 * Math.Max(1, Math.Abs(expected[i])));
        Assert.Equal(reference.DeterminantLn, factorization.LogDeterminant, 1e-10 * Math.Max(1, Math.Abs(reference.DeterminantLn)));
    }

    [Fact]
    public void Cholesky_ReadsOnlyTheLowerTriangle()
    {
        var a = RandomPositiveDefinite(new Random(8), 6);
        var lower = (double[,])a.Clone();
        for (int i = 0; i < 6; i++)
            for (int j = i + 1; j < 6; j++) lower[i, j] = double.NaN;
        Assert.Equal(CholeskyFactorization.Factor(a).L, CholeskyFactorization.Factor(lower).L);
    }

    [Fact]
    public void Cholesky_ReportsAMatrixThatIsNotPositiveDefinite()
    {
        // Eigenvalues 3 and −1.
        var indefinite = new double[,] { { 1, 2 }, { 2, 1 } };
        Assert.False(CholeskyFactorization.TryFactor(indefinite, out var none));
        Assert.Null(none);
        Assert.Throws<InvalidOperationException>(() => CholeskyFactorization.Factor(indefinite));
        // Singular: the second pivot is exactly zero.
        Assert.False(CholeskyFactorization.TryFactor(new double[,] { { 4, 2 }, { 2, 1 } }, out _));
        Assert.False(CholeskyFactorization.TryFactor(new double[,] { { double.NaN } }, out _));
        Assert.Throws<ArgumentException>(() => CholeskyFactorization.Factor(new double[2, 3]));
    }

    // ---- 2-D FFT ----

    private static Complex[,] RandomField(Random random, int rows, int columns)
    {
        var f = new Complex[rows, columns];
        for (int i = 0; i < rows; i++)
            for (int k = 0; k < columns; k++) f[i, k] = new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5);
        return f;
    }

    /// <summary>The defining double sum, with the angle reduced in exact integer arithmetic.</summary>
    private static Complex[,] DirectDft(Complex[,] f, bool inverse)
    {
        int rows = f.GetLength(0), columns = f.GetLength(1);
        var result = new Complex[rows, columns];
        double sign = inverse ? 1 : -1;
        for (int p = 0; p < rows; p++)
            for (int q = 0; q < columns; q++)
            {
                Complex sum = Complex.Zero;
                for (int j = 0; j < rows; j++)
                    for (int k = 0; k < columns; k++)
                    {
                        long numerator = ((long)j * p % rows) * columns + ((long)k * q % columns) * rows;
                        double angle = sign * 2 * Math.PI * (numerator % ((long)rows * columns)) / ((long)rows * columns);
                        sum += f[j, k] * new Complex(Math.Cos(angle), Math.Sin(angle));
                    }
                result[p, q] = inverse ? sum / (rows * columns) : sum;
            }
        return result;
    }

    private static double LargestDifference(Complex[,] a, Complex[,] b)
    {
        Assert.Equal((a.GetLength(0), a.GetLength(1)), (b.GetLength(0), b.GetLength(1)));
        double worst = 0;
        for (int i = 0; i < a.GetLength(0); i++)
            for (int k = 0; k < a.GetLength(1); k++) worst = Math.Max(worst, (a[i, k] - b[i, k]).Magnitude);
        return worst;
    }

    private static double Energy(Complex[,] a)
    {
        double sum = 0;
        foreach (var value in a) sum += value.Real * value.Real + value.Imaginary * value.Imaginary;
        return sum;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 8)]
    [InlineData(4, 4)]
    [InlineData(3, 5)]
    [InlineData(8, 6)]
    [InlineData(7, 7)]
    [InlineData(16, 12)]
    [InlineData(15, 32)]
    public void Fft2D_MatchesTheDirectSum(int rows, int columns)
    {
        var f = RandomField(new Random(100 * rows + columns), rows, columns);
        Assert.True(LargestDifference(Fft2D.Forward(f), DirectDft(f, inverse: false)) <= 1e-12);
        Assert.True(LargestDifference(Fft2D.Inverse(f), DirectDft(f, inverse: true)) <= 1e-12);
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(100, 37)]
    [InlineData(127, 128)]
    [InlineData(1, 1000)]
    [InlineData(243, 250)]
    public void Fft2D_SatisfiesParsevalAndInvertsItself(int rows, int columns)
    {
        var f = RandomField(new Random(rows ^ (columns << 8)), rows, columns);
        var spectrum = Fft2D.Forward(f);
        // Σ|x|² = Σ|X|² / (rows × columns).
        double space = Energy(f), frequency = Energy(spectrum) / ((double)rows * columns);
        Assert.True(Math.Abs(space - frequency) <= 1e-12 * space, $"Parseval off by {Math.Abs(space - frequency) / space:E2}");
        Assert.True(LargestDifference(Fft2D.Inverse(spectrum), f) <= 1e-12);
    }

    [Fact]
    public void Fft2D_KnownTransforms()
    {
        // A unit impulse at the origin has a flat spectrum; a constant has only the zero bin.
        var impulse = new Complex[5, 6];
        impulse[0, 0] = 1;
        foreach (var value in Fft2D.Forward(impulse)) Assert.True((value - Complex.One).Magnitude <= 1e-12);

        var constant = new Complex[5, 6];
        for (int i = 0; i < 5; i++)
            for (int k = 0; k < 6; k++) constant[i, k] = 2;
        var spectrum = Fft2D.Forward(constant);
        for (int i = 0; i < 5; i++)
            for (int k = 0; k < 6; k++)
                Assert.True((spectrum[i, k] - (i == 0 && k == 0 ? 60 : 0)).Magnitude <= 1e-12);

        // One complex exponential with (2, 3) cycles lands in bin (2, 3) alone.
        var wave = new Complex[8, 9];
        for (int i = 0; i < 8; i++)
            for (int k = 0; k < 9; k++)
            {
                double angle = 2 * Math.PI * (2.0 * i / 8 + 3.0 * k / 9);
                wave[i, k] = new Complex(Math.Cos(angle), Math.Sin(angle));
            }
        spectrum = Fft2D.Forward(wave);
        for (int i = 0; i < 8; i++)
            for (int k = 0; k < 9; k++)
                Assert.True((spectrum[i, k] - (i == 2 && k == 3 ? 72 : 0)).Magnitude <= 1e-12);
    }

    [Fact]
    public void Fft2D_IsBitwiseIdenticalAtAnyThreadCount()
    {
        var f = RandomField(new Random(77), 48, 53);
        var one = Fft2D.Forward(f, 1);
        Assert.Equal(one, Fft2D.Forward(f, 8));
        Assert.Equal(one, Fft2D.Forward(f, null));
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(4, 7)]
    [InlineData(1, 3)]
    public void Shift_MovesTheOriginToTheCentreAndBack(int rows, int columns)
    {
        var index = new int[rows, columns];
        for (int i = 0; i < rows; i++)
            for (int k = 0; k < columns; k++) index[i, k] = i * 100 + k;
        var shifted = Fft2D.Shift(index);
        Assert.Equal(0, shifted[rows / 2, columns / 2]);
        Assert.Equal(index, Fft2D.InverseShift(shifted));
        Assert.Equal(index, Fft2D.Shift(Fft2D.InverseShift(index)));
    }

    [Fact]
    public void PadAndCrop_KeepTheCentreSampleAtTheCentre()
    {
        var random = new Random(9);
        foreach (var (rows, columns, toRows, toColumns) in new[] { (4, 4, 8, 8), (5, 4, 9, 12), (4, 5, 7, 8), (3, 3, 3, 3) })
        {
            var f = RandomField(random, rows, columns);
            var padded = Fft2D.PadCentered(f, toRows, toColumns);
            Assert.Equal(f[rows / 2, columns / 2], padded[toRows / 2, toColumns / 2]);
            Assert.Equal(Energy(f), Energy(padded), 12);
            Assert.Equal(f, Fft2D.CropCentered(padded, rows, columns));
        }
        Assert.Throws<ArgumentException>(() => Fft2D.PadCentered(new Complex[4, 4], 3, 4));
        Assert.Throws<ArgumentException>(() => Fft2D.CropCentered(new Complex[4, 4], 5, 4));
    }

    // ---- Matrix Fourier transform ----

    [Theory]
    [InlineData(8, 8)]
    [InlineData(9, 12)]
    [InlineData(16, 7)]
    public void Zoom_AtFullSpan_IsTheShiftedFft(int rows, int columns)
    {
        var f = RandomField(new Random(rows * 17 + columns), rows, columns);
        var expected = Fft2D.Shift(Fft2D.Forward(Fft2D.InverseShift(f)));
        Assert.True(LargestDifference(MatrixFourierTransform.Zoom(f, rows, columns, rows, columns), expected) <= 1e-12);

        var expectedInverse = Fft2D.Shift(Fft2D.Inverse(Fft2D.InverseShift(f)));
        var inverse = MatrixFourierTransform.Zoom(f, rows, columns, rows, columns, inverse: true);
        for (int i = 0; i < rows; i++)
            for (int k = 0; k < columns; k++) inverse[i, k] /= rows * columns;
        Assert.True(LargestDifference(inverse, expectedInverse) <= 1e-12);
    }

    [Fact]
    public void Zoom_OnAPatch_MatchesAPaddedFft()
    {
        // Padding by 8 samples the transform 8 times more finely; the zoom reads the same
        // samples over a 4-bin patch (32 samples at 1/8 bin) without the padding.
        int n = 20, pad = 8, m = 32;
        var f = RandomField(new Random(12), n, n);
        var padded = Fft2D.Shift(Fft2D.Forward(Fft2D.InverseShift(Fft2D.PadCentered(f, pad * n, pad * n))));
        foreach (var (rowCentre, columnCentre) in new[] { (0.0, 0.0), (3.0, -2.5) })
        {
            var zoom = MatrixFourierTransform.Zoom(f, m, m, m / (double)pad, m / (double)pad, rowCentre, columnCentre);
            int rowStart = pad * n / 2 + (int)Math.Round(rowCentre * pad) - m / 2;
            int columnStart = pad * n / 2 + (int)Math.Round(columnCentre * pad) - m / 2;
            double worst = 0;
            for (int p = 0; p < m; p++)
                for (int q = 0; q < m; q++)
                    worst = Math.Max(worst, (zoom[p, q] - padded[rowStart + p, columnStart + q]).Magnitude);
            Assert.True(worst <= 1e-12, $"zoom differs from the padded FFT by {worst:E2}");
        }
    }

    [Fact]
    public void Transform_AtArbitrarySamples_MatchesTheDirectSum()
    {
        var random = new Random(31);
        var f = RandomField(random, 6, 5);
        double[] x = { -1.3, -0.2, 0.05, 0.9, 1.7, 2.2 }, y = { -2.0, -0.75, 0.1, 0.6, 3.1 };
        double[] u = { -0.41, 0.0, 0.33, 1.9 }, v = { -1.2, 0.27, 0.5 };
        foreach (bool inverse in new[] { false, true })
        {
            var result = MatrixFourierTransform.Transform(f, x, y, u, v, inverse);
            for (int p = 0; p < u.Length; p++)
                for (int q = 0; q < v.Length; q++)
                {
                    Complex sum = Complex.Zero;
                    for (int j = 0; j < x.Length; j++)
                        for (int k = 0; k < y.Length; k++)
                        {
                            double angle = (inverse ? 1 : -1) * 2 * Math.PI * (x[j] * u[p] + y[k] * v[q]);
                            sum += f[j, k] * new Complex(Math.Cos(angle), Math.Sin(angle));
                        }
                    Assert.True((result[p, q] - sum).Magnitude <= 1e-12);
                }
        }
        Assert.Throws<ArgumentException>(() => MatrixFourierTransform.Transform(f, y, y, u, v));
    }

    [Fact]
    public void Transform_IsBitwiseIdenticalAtAnyThreadCount()
    {
        var f = RandomField(new Random(4), 30, 26);
        var one = MatrixFourierTransform.Zoom(f, 41, 37, 6.5, 5.25, 1.5, -0.5, maxDegreeOfParallelism: 1);
        Assert.Equal(one, MatrixFourierTransform.Zoom(f, 41, 37, 6.5, 5.25, 1.5, -0.5, maxDegreeOfParallelism: 8));
        Assert.Equal(one, MatrixFourierTransform.Zoom(f, 41, 37, 6.5, 5.25, 1.5, -0.5, maxDegreeOfParallelism: null));
    }
}
