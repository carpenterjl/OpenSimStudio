using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Core;

/// <summary>OP-22: the damped two-dimensional Newton solver behind ray aiming. The roots are
/// derived in each test's comment.</summary>
public sealed class DampedNewton2DTests
{
    /// <summary>x² + y² = 4 and x = y meet at (√2, √2) in the first quadrant; from (3, 1) Newton
    /// converges there quadratically.</summary>
    [Fact]
    public void ACircleAndALine_MeetAtRootTwoRootTwo()
    {
        var r = DampedNewton2D.Solve((double x, double y, out double fx, out double fy) =>
        {
            fx = x * x + y * y - 4;
            fy = x - y;
            return true;
        }, 3, 1, 1e-8, 1e-14, 1e-12);
        Assert.True(r.Converged, r.Failure);
        Assert.Equal(Math.Sqrt(2), r.X, 12);
        Assert.Equal(Math.Sqrt(2), r.Y, 12);
        Assert.True(r.Iterations < 12);
    }

    /// <summary>A residual undefined for x &gt; 1.5 (as a ray that misses a surface): the root
    /// of (x − 1, y + 2) is (1, −2); from (0, 0) the full Newton step lands on it directly, and
    /// from (1.4, 0) the forward difference in x crosses the boundary, so the backward one is
    /// used. Both converge.</summary>
    [Fact]
    public void AResidualUndefinedBeyondABoundary_IsHandledByTheBackwardDifference()
    {
        static bool F(double x, double y, out double fx, out double fy)
        {
            fx = x - 1;
            fy = y + 2;
            return x <= 1.5;
        }
        foreach (var (x0, y0) in new[] { (0.0, 0.0), (1.5, 0.0) })
        {
            var r = DampedNewton2D.Solve(F, x0, y0, 0.1, 1e-14, 1e-12);
            Assert.True(r.Converged, r.Failure);
            Assert.Equal(1, r.X, 13);
            Assert.Equal(-2, r.Y, 13);
        }
    }

    /// <summary>x² + y² + 1 = 0 has no real root: the solver says so instead of returning a point.</summary>
    [Fact]
    public void NoRoot_IsReported_NotReturnedAsAnAnswer()
    {
        var r = DampedNewton2D.Solve((double x, double y, out double fx, out double fy) =>
        {
            fx = x * x + y * y + 1;
            fy = x - y;
            return true;
        }, 1, 1, 1e-8, 1e-14, 1e-12);
        Assert.False(r.Converged);
        Assert.False(string.IsNullOrEmpty(r.Failure));
        Assert.False(DampedNewton2D.Solve((double x, double y, out double fx, out double fy) => { fx = fy = 0; return false; },
            0, 0, 1, 0, 0).Converged);
    }
}
