using System.Numerics;
using MathNet.Numerics;
using OpenSim.Core.Numerics;
using MathNetMatrix = MathNet.Numerics.LinearAlgebra.Matrix<System.Numerics.Complex>;

namespace OpenSim.Tests.Core;

/// <summary>
/// OP-47: the first-party complex eigen-decomposition (<see cref="ComplexEigenSolver"/>) and
/// integer-order Bessel functions (<see cref="Bessel.Jn"/>), against MathNet.Numerics 5.0.0 as
/// the independent oracle (referenced by the tests only). The matrices are seeded random inputs,
/// not reference data.
/// </summary>
public sealed class ComplexEigenSolverTests
{
    private static ComplexDenseMatrix Random(int n, int seed, bool hermitian = false)
    {
        var rng = new System.Random(seed);
        var a = new ComplexDenseMatrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) a[i, j] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
        if (hermitian)
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    var v = 0.5 * (a[i, j] + Complex.Conjugate(a[j, i]));
                    a[i, j] = v;
                    a[j, i] = Complex.Conjugate(v);
                    if (i == j) a[i, i] = v.Real;
                }
        return a;
    }

    private static double MaxAbs(ComplexDenseMatrix a)
    {
        double m = 0;
        for (int i = 0; i < a.Rows; i++)
            for (int j = 0; j < a.Columns; j++) m = Math.Max(m, a[i, j].Magnitude);
        return m;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 3)]
    [InlineData(31, 4)]
    [InlineData(81, 5)]
    public void Eigenpairs_SatisfyAvEqualsLambdaV_AndMatchMathNet(int n, int seed)
    {
        var a = Random(n, seed);
        var e = ComplexEigenSolver.Compute(a);
        double scale = MaxAbs(a) * n;
        for (int k = 0; k < n; k++)
        {
            var v = new Complex[n];
            double norm = 0;
            for (int i = 0; i < n; i++) { v[i] = e.Vectors![i, k]; norm += v[i].Magnitude * v[i].Magnitude; }
            Assert.True(Math.Abs(Math.Sqrt(norm) - 1) < 1e-12, $"eigenvector {k} norm {Math.Sqrt(norm):R}");
            var av = a.Multiply(v);
            double residual = 0;
            for (int i = 0; i < n; i++) residual = Math.Max(residual, (av[i] - e.Values[k] * v[i]).Magnitude);
            Assert.True(residual <= 1e-12 * scale, $"n = {n}: residual {residual:R} for eigenvalue {k}");
        }

        // Oracle: MathNet's eigenvalues, matched one to one (nearest unused).
        var m = MathNetMatrix.Build.Dense(n, n, (i, j) => a[i, j]);
        var oracle = m.Evd().EigenValues.ToArray().ToList();
        foreach (var lambda in e.Values)
        {
            int best = 0;
            for (int j = 1; j < oracle.Count; j++)
                if ((oracle[j] - lambda).Magnitude < (oracle[best] - lambda).Magnitude) best = j;
            Assert.True((oracle[best] - lambda).Magnitude <= 1e-10 * MaxAbs(a) * Math.Sqrt(n),
                $"n = {n}: eigenvalue {lambda} has no MathNet match (nearest {oracle[best]}).");
            oracle.RemoveAt(best);
        }
    }

    [Fact]
    public void HermitianMatrix_HasRealEigenvalues_AndOrthogonalVectors()
    {
        // Derived: a Hermitian matrix's eigenvalues are real and its eigenvectors of distinct
        // eigenvalues orthogonal; the TE coupled-wave matrix of a lossless grating is of this kind.
        int n = 25;
        var a = Random(n, 11, hermitian: true);
        var e = ComplexEigenSolver.Compute(a);
        foreach (var lambda in e.Values) Assert.True(Math.Abs(lambda.Imaginary) < 1e-12, $"eigenvalue {lambda}");
        for (int p = 0; p < n; p++)
            for (int q = p + 1; q < n; q++)
            {
                Complex dot = Complex.Zero;
                for (int i = 0; i < n; i++) dot += Complex.Conjugate(e.Vectors![i, p]) * e.Vectors[i, q];
                Assert.True(dot.Magnitude < 1e-10, $"vectors {p} and {q}: inner product {dot}");
            }
    }

    [Fact]
    public void TriangularAndDiagonalInputs_ReturnTheirDiagonal()
    {
        var a = new ComplexDenseMatrix(3, 3);
        a[0, 0] = new Complex(1, 2); a[1, 1] = new Complex(-3, 0); a[2, 2] = new Complex(0, 5);
        a[0, 1] = 4; a[0, 2] = new Complex(0, 1); a[1, 2] = 2;
        var e = ComplexEigenSolver.Compute(a);
        var values = e.Values.OrderBy(v => v.Real).ThenBy(v => v.Imaginary).ToArray();
        Assert.Equal(new Complex(-3, 0), values[0]);
        Assert.Equal(new Complex(0, 5), values[1]);
        Assert.Equal(new Complex(1, 2), values[2]);
    }

    [Fact]
    public void Refuses_NonSquare_AndNonFinite()
    {
        Assert.Throws<ArgumentException>(() => ComplexEigenSolver.Compute(new ComplexDenseMatrix(2, 3)));
        var a = new ComplexDenseMatrix(2, 2);
        a[1, 0] = double.NaN;
        Assert.Contains("non-finite", Assert.Throws<ArgumentException>(() => ComplexEigenSolver.Compute(a)).Message);
    }

    [Fact]
    public void Jn_MatchesMathNet_OverOrdersAndArguments()
    {
        foreach (int n in new[] { 0, 1, 2, 3, 5, 10, 20, 40 })
            foreach (double x in new[] { 0.0, 0.3, 1.0, 2.5, 7.0, 15.0, 33.0, 60.0 })
            {
                double ours = Bessel.Jn(n, x);
                double oracle = SpecialFunctions.BesselJ(n, x);
                Assert.True(Math.Abs(ours - oracle) <= 1e-12, $"J_{n}({x}): {ours:R} vs MathNet {oracle:R}");
                // J_{−n} = (−1)ⁿJ_n (DLMF 10.4.1); J_n(−x) = (−1)ⁿJ_n(x) (the series is (x/2)ⁿ times a series in x²).
                double sign = n % 2 == 0 ? 1 : -1;
                Assert.Equal(sign * ours, Bessel.Jn(-n, x));
                Assert.Equal(sign * ours, Bessel.Jn(n, -x));
            }
    }

    [Fact]
    public void Jn_SquaresSumToOne()
    {
        // DLMF 10.12.1 at |t| = 1 gives Σ J_m(z)² = 1 for real z (Parseval of the Jacobi–Anger series).
        foreach (double x in new[] { 0.5, 2.405, 8.0, 25.0 })
        {
            double sum = 0;
            for (int m = -80; m <= 80; m++) sum += Bessel.Jn(m, x) * Bessel.Jn(m, x);
            Assert.True(Math.Abs(sum - 1) < 1e-12, $"x = {x}: Σ J_m² = {sum:R}");
        }
    }
}
