using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Core;

/// <summary>
/// The real dense LU used by the nonlinear transient's Newton step. Small, hot, and easy to get
/// subtly wrong (a pivot permutation applied to the wrong side reproduces the right answer on
/// symmetric fixtures and fails on everything else), so the gates use ASYMMETRIC matrices and
/// check the residual rather than a stored answer.
/// </summary>
public class DenseLuTests
{
    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var flat = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) flat[i * n + j] = a[i, j];
        var lu = DenseLu.FactorInPlace(flat, n, new int[n]);
        var x = new double[n];
        lu.Solve(b, x);
        return x;
    }

    private static void AssertResidual(double[,] a, double[] b, double[] x, double tol)
    {
        int n = b.Length;
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++) sum += a[i, j] * x[j];
            Assert.True(Math.Abs(sum - b[i]) < tol,
                $"row {i}: A·x = {sum:R} vs b = {b[i]:R}");
        }
    }

    [Fact]
    public void SolvesAnAsymmetricSystem()
    {
        var a = new[,] { { 2.0, 1.0, -1.0 }, { -3.0, -1.0, 2.0 }, { -2.0, 1.0, 2.0 } };
        var b = new[] { 8.0, -11.0, -3.0 };
        var x = Solve(a, b);
        // The textbook answer for this system is (2, 3, −1).
        Assert.Equal(2.0, x[0], 12);
        Assert.Equal(3.0, x[1], 12);
        Assert.Equal(-1.0, x[2], 12);
        AssertResidual(a, b, x, 1e-12);
    }

    [Fact]
    public void PivotsWhenTheLeadingEntryIsZero()
    {
        // Without partial pivoting this divides by zero on the first column.
        var a = new[,] { { 0.0, 2.0 }, { 3.0, 4.0 } };
        var b = new[] { 4.0, 10.0 };
        var x = Solve(a, b);
        Assert.Equal(2.0 / 3 * 1.0 + 0, x[0], 9);   // 3x + 4y = 10, 2y = 4 → y = 2, x = 2/3
        Assert.Equal(2.0, x[1], 12);
        AssertResidual(a, b, x, 1e-12);
    }

    [Fact]
    public void SolvesALargerRandomSystemToMachinePrecision()
    {
        // Fixed seed — a deterministic gate, not a fuzz test.
        var rng = new Random(20260825);
        const int n = 16;                    // the engine's largest case: 8 coupled lines
        var a = new double[n, n];
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) a[i, j] = rng.NextDouble() * 2 - 1;
            a[i, i] += n;                    // diagonally dominant ⇒ well-conditioned
            b[i] = rng.NextDouble() * 2 - 1;
        }
        var x = Solve(a, b);
        AssertResidual(a, b, x, 1e-12);
    }

    [Fact]
    public void ASingularMatrixIsATypedFailure()
    {
        var flat = new double[] { 1, 2, 2, 4 };   // second row is twice the first
        var e = Assert.Throws<InvalidOperationException>(
            () => DenseLu.FactorInPlace(flat, 2, new int[2]));
        Assert.Contains("singular", e.Message);
    }

    [Fact]
    public void OneFactorizationServesManyRightHandSides()
    {
        // The reason it factors in place and keeps the pivots: the Newton loop re-solves the
        // same Jacobian against several residuals.
        var a = new[,] { { 4.0, 1.0 }, { 1.0, 3.0 } };
        int n = 2;
        var flat = new double[] { 4, 1, 1, 3 };
        var lu = DenseLu.FactorInPlace(flat, n, new int[n]);
        foreach (var b in new[] { new[] { 1.0, 2.0 }, new[] { -3.0, 0.5 }, new[] { 0.0, 0.0 } })
        {
            var x = new double[n];
            lu.Solve(b, x);
            AssertResidual(a, b, x, 1e-12);
        }
    }
}
