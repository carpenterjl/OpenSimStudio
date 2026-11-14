using OpenSim.Core.Numerics;
using OpenSim.Core.Numerics.Optimization;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Core;

/// <summary>
/// OP-5's gate: damped least squares on the MINPACK/MGH least-squares set reaches the documented
/// minima (see <see cref="MghProblems"/> for the sources), a residual that fails mid-run is
/// penalised rather than fatal, and the result is bitwise identical at any thread count. The
/// derivative-free minimisers reach the Rosenbrock and Powell singular minima.
/// </summary>
public class OptimizerTests
{
    private readonly ITestOutputHelper _output;

    public OptimizerTests(ITestOutputHelper output) => _output = output;

    private static ResidualFunction Problem(int problem) => (x, f) => MghProblems.Residuals(problem, x, f);

    private static double SumOfSquares(int problem, double[] x, int m)
    {
        var f = new double[m];
        MghProblems.Residuals(problem, x, f);
        return f.Sum(v => v * v);
    }

    /// <summary>The documented minimum values (ANL-80-74, section 3) of the sum of squares that a
    /// solver may legitimately reach from the standard start; several problems have more than
    /// one.</summary>
    public static IEnumerable<object[]> DocumentedCases() => new[]
    {
        new object[] { 4, 2, 2, new[] { 0.0 } },
        new object[] { 5, 3, 3, new[] { 0.0 } },
        new object[] { 6, 4, 4, new[] { 0.0 } },
        new object[] { 7, 2, 2, new[] { 0.0, 48.9842 } },
        new object[] { 8, 3, 15, new[] { 8.21487e-3, 17.4286 } },
        new object[] { 9, 4, 11, new[] { 3.07505e-4, 1.02734e-3 } },
        new object[] { 10, 3, 16, new[] { 87.9458 } },
        new object[] { 11, 6, 31, new[] { 2.28767e-3 } },
        new object[] { 11, 9, 31, new[] { 1.39976e-6 } },
        new object[] { 11, 12, 31, new[] { 4.72238e-10 } },
        new object[] { 12, 3, 10, new[] { 0.0 } },
        new object[] { 13, 2, 10, new[] { 124.362 } },
        new object[] { 14, 4, 20, new[] { 85822.2 } },
        new object[] { 15, 7, 7, new[] { 0.0 } },
        new object[] { 15, 9, 9, new[] { 0.0 } },
        new object[] { 15, 10, 10, new[] { 6.50395e-3 } },
        new object[] { 16, 10, 10, new[] { 0.0, 1.0 } },
        new object[] { 17, 5, 33, new[] { 5.46489e-5 } },
        new object[] { 18, 11, 65, new[] { 4.01377e-2 } },
    };

    [Theory]
    [MemberData(nameof(DocumentedCases))]
    public void DampedLeastSquares_ReachesTheDocumentedMinimum(int problem, int n, int m, double[] minima)
    {
        var result = DampedLeastSquares.Solve(Problem(problem), m, MghProblems.Start(problem, n));
        _output.WriteLine($"{MghProblems.Name(problem)} n={n} m={m}: f = {result.SumOfSquares:R}, {result.Status}, " +
                          $"{result.Iterations} iterations, {result.Evaluations} evaluations");
        Assert.Equal(SumOfSquares(problem, result.X, m), result.SumOfSquares);
        // The documented values carry six significant digits, truncated.
        bool matches = minima.Any(f => f == 0 ? result.SumOfSquares <= 1e-20 : Math.Abs(result.SumOfSquares - f) <= 1e-5 * f);
        Assert.True(matches, $"f = {result.SumOfSquares:R} is none of the documented minima {string.Join(", ", minima)}");
        Assert.True(result.Status is not (LeastSquaresStatus.IterationLimit or LeastSquaresStatus.EvaluationLimit));
    }

    [Theory]
    [InlineData(1, 5, 10)]
    [InlineData(1, 5, 50)]
    [InlineData(2, 5, 10)]
    [InlineData(2, 5, 50)]
    [InlineData(3, 5, 10)]
    [InlineData(3, 5, 50)]
    public void DampedLeastSquares_LinearProblems_MatchTheLinearSolution(int problem, int n, int m)
    {
        // The residuals are affine, r = A·x + c: read A and c off exactly and solve the linear
        // least-squares problem by Householder QR as the independent oracle.
        var c = new double[m];
        MghProblems.Residuals(problem, new double[n], c);
        var a = new double[m, n];
        for (int j = 0; j < n; j++)
        {
            var unit = new double[n];
            unit[j] = 1;
            var f = new double[m];
            MghProblems.Residuals(problem, unit, f);
            for (int i = 0; i < m; i++) a[i, j] = f[i] - c[i];
        }
        var oracleX = HouseholderLeastSquares.Solve(a, c.Select(v => -v).ToArray());
        double oracle = SumOfSquares(problem, oracleX, m);

        var result = DampedLeastSquares.Solve(Problem(problem), m, MghProblems.Start(problem, n));
        _output.WriteLine($"{MghProblems.Name(problem)} m={m}: f = {result.SumOfSquares:R}, oracle {oracle:R}, {result.Status}");
        Assert.Equal(oracle, result.SumOfSquares, 1e-10 * oracle);

        // MGH's closed forms for the two rank-1 problems.
        if (problem == 2) Assert.Equal(m * (m - 1.0) / (2 * (2 * m + 1.0)), result.SumOfSquares, 1e-10 * oracle);
        if (problem == 3) Assert.Equal((m * m + 3 * m - 6.0) / (2 * (2 * m - 3.0)), result.SumOfSquares, 1e-10 * oracle);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public void DampedLeastSquares_Rosenbrock_FromFarStarts(double factor)
    {
        var start = MghProblems.Start(4, 2).Select(v => v * factor).ToArray();
        var result = DampedLeastSquares.Solve(Problem(4), 2, start);
        Assert.True(result.SumOfSquares <= 1e-20);
        Assert.Equal(1, result.X[0], 1e-10);
        Assert.Equal(1, result.X[1], 1e-10);
    }

    [Fact]
    public void DampedLeastSquares_AnalyticJacobian_ReachesTheSameMinimum()
    {
        var options = new LeastSquaresOptions
        {
            Jacobian = (x, j) =>
            {
                j[0, 0] = -20 * x[0];
                j[0, 1] = 10;
                j[1, 0] = -1;
                j[1, 1] = 0;
            }
        };
        var result = DampedLeastSquares.Solve(Problem(4), 2, MghProblems.Start(4, 2), options);
        Assert.True(result.SumOfSquares <= 1e-20);
        Assert.Equal(1, result.X[0], 1e-12);
        Assert.Equal(1, result.X[1], 1e-12);
    }

    [Fact]
    public void DampedLeastSquares_CentralDifferences_ReachTheMinimum()
    {
        var result = DampedLeastSquares.Solve(Problem(17), 33, MghProblems.Start(17, 5),
            new LeastSquaresOptions { Differences = FiniteDifferenceMode.Central });
        Assert.Equal(5.46489e-5, result.SumOfSquares, 1e-5 * 5.46489e-5);
    }

    [Fact]
    public void DampedLeastSquares_KeepsBounds()
    {
        // r = x − (2, −3) in the box [0, 1] × [−1, 1]: the nearest corner (1, −1).
        ResidualFunction shifted = (x, f) => { f[0] = x[0] - 2; f[1] = x[1] + 3; };
        var box = new LeastSquaresOptions { LowerBounds = new[] { 0.0, -1 }, UpperBounds = new[] { 1.0, 1 } };
        var corner = DampedLeastSquares.Solve(shifted, 2, new[] { 0.5, 0.5 }, box);
        Assert.Equal(new[] { 1.0, -1.0 }, corner.X);
        Assert.Equal(1 + 4, corner.SumOfSquares, 1e-12);

        // Rosenbrock with x₁ ≤ 0.5: along the valley the cost (1 − x₁)² falls all the way to the
        // bound, so the constrained minimum is (0.5, 0.25) with f = 0.25.
        var capped = new LeastSquaresOptions { UpperBounds = new[] { 0.5, double.PositiveInfinity } };
        var result = DampedLeastSquares.Solve(Problem(4), 2, MghProblems.Start(4, 2), capped);
        Assert.Equal(0.5, result.X[0]);
        // At a nonzero minimum the cost is flat to second order, so x is resolved to about the
        // square root of the cost tolerance while the cost itself is exact.
        Assert.Equal(0.25, result.X[1], 1e-7);
        Assert.Equal(0.25, result.SumOfSquares, 1e-14);

        // A start outside the box is moved into it.
        var outside = DampedLeastSquares.Solve(shifted, 2, new[] { 9.0, -9 }, box);
        Assert.Equal(new[] { 1.0, -1.0 }, outside.X);
    }

    [Fact]
    public void DampedLeastSquares_ResidualThatThrowsMidRun_IsPenalisedNotFatal()
    {
        // Rosenbrock that cannot be evaluated beyond x₁ = 1, right at the minimum: steps that
        // overshoot fail, and finite differences there must be taken backwards.
        ResidualFunction fragile = (x, f) =>
        {
            if (x[0] > 1) throw new InvalidOperationException("outside the model's domain");
            MghProblems.Residuals(4, x, f);
        };
        var result = DampedLeastSquares.Solve(fragile, 2, MghProblems.Start(4, 2));
        Assert.True(result.FailedEvaluations > 0);
        Assert.True(result.SumOfSquares <= 1e-20, $"f = {result.SumOfSquares:R}");

        // Failures on particular calls (here the second and third), and NaN as a failure too.
        int calls = 0;
        var options = new LeastSquaresOptions { Jacobian = (x, j) => { j[0, 0] = -20 * x[0]; j[0, 1] = 10; j[1, 0] = -1; j[1, 1] = 0; } };
        ResidualFunction flaky = (x, f) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 2) throw new ArithmeticException("solver did not converge");
            MghProblems.Residuals(4, x, f);
            if (call == 3) f[1] = double.NaN;
        };
        var recovered = DampedLeastSquares.Solve(flaky, 2, MghProblems.Start(4, 2), options);
        Assert.Equal(2, recovered.FailedEvaluations);
        Assert.True(recovered.SumOfSquares <= 1e-20);
        Assert.Equal(1, recovered.X[0], 1e-12);

        // At the start there is nothing to fall back on.
        Assert.Throws<InvalidOperationException>(() =>
            DampedLeastSquares.Solve((x, f) => throw new ArithmeticException(), 2, new[] { 0.0, 0.0 }));
    }

    [Fact]
    public void DampedLeastSquares_IsBitwiseIdenticalAtAnyThreadCount()
    {
        LeastSquaresResult Run(int? threads) => DampedLeastSquares.Solve(Problem(18), 65, MghProblems.Start(18, 11),
            new LeastSquaresOptions { MaxDegreeOfParallelism = threads });
        var one = Run(1);
        foreach (var other in new[] { Run(4), Run(null) })
        {
            Assert.Equal(one.X, other.X);
            Assert.Equal(one.Residuals, other.Residuals);
            Assert.Equal(one.Iterations, other.Iterations);
            Assert.Equal(one.Evaluations, other.Evaluations);
        }
    }

    [Fact]
    public void DampedLeastSquares_StopsAtTheIterationLimit()
    {
        var result = DampedLeastSquares.Solve(Problem(10), 16, MghProblems.Start(10, 3),
            new LeastSquaresOptions { MaxIterations = 3 });
        Assert.Equal(LeastSquaresStatus.IterationLimit, result.Status);
        Assert.Equal(3, result.Iterations);
        Assert.True(result.SumOfSquares < SumOfSquares(10, MghProblems.Start(10, 3), 16));
    }

    // ---- Derivative-free minimisers ----

    private static Func<double[], double> Merit(int problem, int m) => x => SumOfSquares(problem, x, m);

    public static IEnumerable<object[]> Minimizers() => new[] { new object[] { "NelderMead" }, new object[] { "Powell" } };

    private static MinimizerResult Run(string method, Func<double[], double> f, double[] start, MinimizerOptions? options = null)
        => method == "NelderMead" ? NelderMead.Minimize(f, start, options) : PowellMinimizer.Minimize(f, start, options);

    [Theory]
    [MemberData(nameof(Minimizers))]
    public void DerivativeFree_Rosenbrock_ReachesItsMinimum(string method)
    {
        var result = Run(method, Merit(4, 2), MghProblems.Start(4, 2));
        _output.WriteLine($"{method}: f = {result.Value:R} at ({result.X[0]:R}, {result.X[1]:R}), {result.Evaluations} evaluations");
        Assert.Equal(MinimizerStatus.Converged, result.Status);
        Assert.True(result.Value <= 1e-14);
        Assert.Equal(1, result.X[0], 1e-6);
        Assert.Equal(1, result.X[1], 1e-6);
    }

    [Theory]
    [MemberData(nameof(Minimizers))]
    public void DerivativeFree_PowellSingular_ReachesItsMinimum(string method)
    {
        var result = Run(method, Merit(6, 4), MghProblems.Start(6, 4));
        _output.WriteLine($"{method}: f = {result.Value:R}, |x| = {result.X.Max(Math.Abs):E2}, {result.Evaluations} evaluations");
        Assert.Equal(MinimizerStatus.Converged, result.Status);
        // f is quartic in the distance from the origin along its singular directions.
        Assert.True(result.Value <= 1e-12, $"f = {result.Value:R}");
        Assert.All(result.X, v => Assert.True(Math.Abs(v) <= 1e-2));
    }

    [Theory]
    [MemberData(nameof(Minimizers))]
    public void DerivativeFree_HelicalValleyAndBard_ReachTheirMinima(string method)
    {
        var helical = Run(method, Merit(5, 3), MghProblems.Start(5, 3));
        Assert.True(helical.Value <= 1e-14, $"helical valley f = {helical.Value:R}");
        Assert.Equal(1, helical.X[0], 1e-6);

        var bard = Run(method, Merit(8, 15), MghProblems.Start(8, 3));
        _output.WriteLine($"{method}: Bard f = {bard.Value:R}");
        Assert.Equal(8.21487e-3, bard.Value, 1e-5 * 8.21487e-3);
    }

    [Theory]
    [MemberData(nameof(Minimizers))]
    public void DerivativeFree_KeepsBoundsAndSurvivesFailures(string method)
    {
        // The nearest corner of a box to (2, −3).
        Func<double[], double> bowl = x => (x[0] - 2) * (x[0] - 2) + (x[1] + 3) * (x[1] + 3);
        var options = new MinimizerOptions { LowerBounds = new[] { 0.0, -1 }, UpperBounds = new[] { 1.0, 1 } };
        var corner = Run(method, bowl, new[] { 0.5, 0.5 }, options);
        Assert.Equal(1, corner.X[0], 1e-8);
        Assert.Equal(-1, corner.X[1], 1e-8);

        // A merit function that throws beyond x₁ = 1 still leads to the Rosenbrock minimum.
        Func<double[], double> fragile = x => x[0] > 1 ? throw new InvalidOperationException() : SumOfSquares(4, x, 2);
        var result = Run(method, fragile, MghProblems.Start(4, 2));
        Assert.True(result.FailedEvaluations > 0);
        Assert.True(result.Value <= 1e-12, $"f = {result.Value:R}");

        Func<double[], double> broken = x => double.NaN;
        Assert.Throws<InvalidOperationException>(() => Run(method, broken, new[] { 1.0 }));
    }

    [Theory]
    [MemberData(nameof(Minimizers))]
    public void DerivativeFree_StopsAtTheEvaluationLimit(string method)
    {
        var result = Run(method, Merit(18, 65), MghProblems.Start(18, 11), new MinimizerOptions { MaxEvaluations = 200 });
        Assert.Equal(MinimizerStatus.EvaluationLimit, result.Status);
        Assert.True(result.Evaluations < 200 + 400);
    }

    [Fact]
    public void BrentMinimizer_FindsKnownMinima()
    {
        var (x, value) = BrentMinimizer.Minimize(t => (t - 0.3) * (t - 0.3) + 2, -1, 2);
        Assert.Equal(0.3, x, 1e-7);
        Assert.Equal(2, value, 1e-14);

        // cos has its minimum at π in [2, 4].
        (x, value) = BrentMinimizer.Minimize(Math.Cos, 2, 4);
        Assert.Equal(Math.PI, x, 1e-7);
        Assert.Equal(-1, value, 1e-14);

        // A minimum at the end of the interval.
        (x, _) = BrentMinimizer.Minimize(t => t, 0, 1);
        Assert.True(x <= 1e-7);
    }
}
