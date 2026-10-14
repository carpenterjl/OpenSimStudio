using System.Numerics;
using MathNet.Numerics.LinearAlgebra;
using OpenSim.Core.Numerics;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>FU-31's dense factorizations against MathNet.Numerics (test-only reference).</summary>
public class DenseFactorizationTests
{
    [Fact]
    public void LeastSquares_MatchesMathNetQr()
    {
        var random = new Random(7);
        int rows = 120, columns = 17;
        var a = new double[rows, columns];
        var b = new double[rows];
        for (int i = 0; i < rows; i++)
        {
            b[i] = random.NextDouble() - 0.5;
            // Columns of very different sizes, as fitting bases have.
            for (int j = 0; j < columns; j++) a[i, j] = (random.NextDouble() - 0.5) * Math.Pow(10, j % 5 - 2);
        }
        var x = HouseholderLeastSquares.Solve(a, b);
        var reference = Matrix<double>.Build.DenseOfArray(a).QR().Solve(MathNet.Numerics.LinearAlgebra.Vector<double>.Build.DenseOfArray(b));
        for (int j = 0; j < columns; j++)
            Assert.Equal(reference[j], x[j], 1e-9 * Math.Max(1, Math.Abs(reference[j])));
    }

    [Fact]
    public void NonsymmetricEigenvalues_MatchMathNetEvd()
    {
        var random = new Random(11);
        foreach (int n in new[] { 1, 2, 5, 24, 80 })
        {
            var a = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) a[i, j] = random.NextDouble() - 0.5;
            var values = NonsymmetricEigenvalues.Compute(a);
            var reference = Matrix<double>.Build.DenseOfArray(a).Evd().EigenValues.ToArray();
            // Each reference eigenvalue has a computed one beside it, used once.
            var left = values.ToList();
            foreach (Complex r in reference)
            {
                var nearest = left.MinBy(v => (v - r).Magnitude);
                Assert.True((nearest - r).Magnitude < 1e-9 * Math.Max(1, r.Magnitude), $"n = {n}: {r} not found ({nearest})");
                left.Remove(nearest);
            }
        }
    }

    [Fact]
    public void ComplexSvd_MatchesMathNet_AndReconstructs()
    {
        var random = new Random(3);
        int m = 6, n = 6;
        var a = new Complex[m, n];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++) a[i, j] = new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5);
        var (u, sigma, v) = ComplexJacobiSvd.Decompose(a);
        var reference = Matrix<Complex>.Build.DenseOfArray(a).Svd().S.Select(s => s.Real).ToArray();
        for (int k = 0; k < n; k++) Assert.Equal(reference[k], sigma[k], 1e-12);
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++)
            {
                Complex sum = 0;
                for (int k = 0; k < n; k++) sum += u[i, k] * sigma[k] * Complex.Conjugate(v[j, k]);
                Assert.True((sum - a[i, j]).Magnitude < 1e-12);
            }
    }
}
