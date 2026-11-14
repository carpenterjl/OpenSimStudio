using OpenSim.Core.Numerics.Optimization;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// OP-57's additions to the optimizer kernel: <see cref="OrthogonalDescent"/> and the accepted-step
/// callback of <see cref="DampedLeastSquares"/>. Minima are derived: Rosenbrock's function
/// (1 − x)² + 100(y − x²)² is a sum of squares that vanishes only at (1, 1).
/// </summary>
public class OrthogonalDescentTests
{
    private static double Rosenbrock(double[] p) => (1 - p[0]) * (1 - p[0]) + 100 * (p[1] - p[0] * p[0]) * (p[1] - p[0] * p[0]);

    /// <summary>From the usual start (−1.2, 1) to (1, 1). The steps shrink to 1e-9 of their
    /// first size (about 5e-2), so the position is resolved far below the 1e-5 asked; the
    /// valley's curvature ratio limits it to well above rounding.</summary>
    [Fact]
    public void OrthogonalDescent_FindsTheRosenbrockMinimum()
    {
        var result = OrthogonalDescent.Minimize(Rosenbrock, new[] { -1.2, 1.0 });
        Assert.Contains(result.Status, new[] { OrthogonalDescentStatus.StepConverged, OrthogonalDescentStatus.ZeroValue });
        Assert.Equal(1, result.X[0], 1e-5);
        Assert.Equal(1, result.X[1], 1e-5);
        Assert.True(result.Value < 1e-9);
    }

    [Fact]
    public void OrthogonalDescent_StopsAtTheCycleLimit_AndReportsOnlyImprovements()
    {
        var reports = new List<OrthogonalDescentIterate>();
        var result = OrthogonalDescent.Minimize(Rosenbrock, new[] { -1.2, 1.0 },
            new OrthogonalDescentOptions { MaxCycles = 7, OnImprovedCycle = reports.Add });
        Assert.Equal(OrthogonalDescentStatus.CycleLimit, result.Status);
        Assert.Equal(7, result.Cycles);
        Assert.NotEmpty(reports);
        for (int i = 1; i < reports.Count; i++) Assert.True(reports[i].Value < reports[i - 1].Value);
        Assert.Equal(reports[^1].X, result.X);
        Assert.Equal(reports[^1].Value, result.Value);
    }

    [Fact]
    public void OrthogonalDescent_StepsAwayFromFailures_AndCancels()
    {
        // NaN (a failed evaluation) for x > 0.5: the minimum of (x − 1)² there is out of reach,
        // so the best reachable point is at the boundary, approached from below.
        var result = OrthogonalDescent.Minimize(p => p[0] > 0.5 ? double.NaN : (p[0] - 1) * (p[0] - 1), new[] { 0.0 });
        Assert.True(result.X[0] <= 0.5 && result.X[0] > 0.49);
        Assert.True(result.FailedEvaluations > 0);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            OrthogonalDescent.Minimize(Rosenbrock, new[] { -1.2, 1.0 }, new OrthogonalDescentOptions { CancellationToken = cancel.Token }));
    }

    /// <summary>The callback sees every accepted step, the cost falling, and the last one is the result.</summary>
    [Fact]
    public void DampedLeastSquares_ReportsEveryAcceptedStep()
    {
        var steps = new List<LeastSquaresIterate>();
        var result = DampedLeastSquares.Solve((p, r) => { r[0] = 1 - p[0]; r[1] = 10 * (p[1] - p[0] * p[0]); }, 2, new[] { -1.2, 1.0 },
            new LeastSquaresOptions { OnAcceptedStep = steps.Add });
        Assert.NotEmpty(steps);
        for (int i = 1; i < steps.Count; i++) Assert.True(steps[i].SumOfSquares < steps[i - 1].SumOfSquares);
        Assert.Equal(steps[^1].X, result.X);
        Assert.Equal(steps[^1].SumOfSquares, result.SumOfSquares);
    }
}
