using OpenSim.Core.Numerics;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>Linear least squares with equality constraints (OP-14's model-glass fit).</summary>
public class ConstrainedLeastSquaresTests
{
    /// <summary>
    /// A line y = c0 + c1·x fitted to points subject to passing through (x0, y0). Substituting
    /// c0 = y0 − c1·x0 gives the closed form c1 = Σ(xᵢ − x0)(yᵢ − y0) / Σ(xᵢ − x0)², derived here,
    /// computed in the test. The constraint holds to rounding.
    /// </summary>
    [Fact]
    public void ALineThroughAPoint_MatchesTheClosedForm()
    {
        double[] x = { 0.1, 0.7, 1.3, 2.0, 2.9, 3.4, 4.8 };
        double[] y = { 1.2, 1.9, 2.1, 3.3, 3.8, 4.9, 5.7 };
        const double x0 = 2.5, y0 = 3.0;
        var a = new double[x.Length, 2];
        for (int i = 0; i < x.Length; i++) { a[i, 0] = 1; a[i, 1] = x[i]; }
        var c = ConstrainedLeastSquares.Solve(a, y, new double[,] { { 1, x0 } }, new[] { y0 });

        double num = 0, den = 0;
        for (int i = 0; i < x.Length; i++) { num += (x[i] - x0) * (y[i] - y0); den += (x[i] - x0) * (x[i] - x0); }
        double c1 = num / den, c0 = y0 - c1 * x0;
        Assert.Equal(c1, c[1], 13);
        Assert.Equal(c0, c[0], 13);
        Assert.Equal(y0, c[0] + c[1] * x0, 14);
    }

    /// <summary>With as many constraints as unknowns the constraints alone decide the answer.</summary>
    [Fact]
    public void FullyConstrained_SolvesTheConstraints()
    {
        var a = new double[,] { { 1, 0 }, { 0, 1 }, { 1, 1 } };
        var c = ConstrainedLeastSquares.Solve(a, new double[] { 9, 9, 9 }, new double[,] { { 2, 1 }, { 1, -1 } }, new double[] { 5, 1 });
        Assert.Equal(2.0, c[0], 14);
        Assert.Equal(1.0, c[1], 14);
    }

    [Fact]
    public void DependentConstraints_AreRefused()
    {
        var a = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 }, { 1, 1, 1 } };
        var g = new double[,] { { 1, 1, 0 }, { 2, 2, 0 } };
        var e = Assert.Throws<ArgumentException>(() => ConstrainedLeastSquares.Solve(a, new double[4], g, new double[] { 1, 2 }));
        Assert.Contains("linearly dependent", e.Message);
    }
}
