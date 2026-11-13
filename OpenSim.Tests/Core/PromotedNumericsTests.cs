using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Core;

/// <summary>OP-3: the numerics the optics toolbox shares with the other modules — random
/// streams, Sobol points, the real J₁, spline derivatives and the bicubic spline, Brent's root
/// finder and the parallel-slot helper. Every expected value is a closed form, an independent
/// computation, or a published listing named beside it.</summary>
public class PromotedNumericsTests
{
    // ------------------------------------------------------------------ Sobol

    /// <summary>The first 10 points in 3 dimensions as listed on the authors' page for
    /// "./sobol 10 3 new-joe-kuo-6.21201" (web.maths.unsw.edu.au/~fkuo/sobol, read
    /// 2026-11-12). Every value is a dyadic fraction, so the comparison is exact.</summary>
    [Fact]
    public void Sobol_ReproducesThePublishedFirstTenPointsInThreeDimensions()
    {
        double[][] published =
        {
            new[] { 0, 0, 0.0 },
            new[] { 0.5, 0.5, 0.5 },
            new[] { 0.75, 0.25, 0.25 },
            new[] { 0.25, 0.75, 0.75 },
            new[] { 0.375, 0.375, 0.625 },
            new[] { 0.875, 0.875, 0.125 },
            new[] { 0.625, 0.125, 0.875 },
            new[] { 0.125, 0.625, 0.375 },
            new[] { 0.1875, 0.3125, 0.9375 },
            new[] { 0.6875, 0.8125, 0.4375 },
        };
        var sobol = new SobolSequence(3);
        for (int i = 0; i < published.Length; i++)
            Assert.Equal(published[i], sobol.Point(i));
    }

    /// <summary>In every dimension the first 2ᵏ points fall one in each of the 2ᵏ equal
    /// intervals: the defining property of a (0, 1)-sequence in base 2, which holds for any
    /// valid set of direction numbers. Checked in the first, a middle and the last dimension
    /// of the table, which also shows that the whole table loads.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1110)]
    [InlineData(21200)]
    public void Sobol_FirstPowerOfTwoPointsFillEveryIntervalOnce(int dimension)
    {
        var sobol = new SobolSequence(SobolSequence.MaxDimensions);
        var point = new double[sobol.Dimensions];
        foreach (int n in new[] { 16, 256, 4096 })
        {
            var seen = new bool[n];
            for (int i = 0; i < n; i++)
            {
                sobol.Point(i, point);
                Assert.InRange(point[dimension], 0, 1 - 1e-12);
                int cell = (int)(point[dimension] * n);
                Assert.False(seen[cell], $"dimension {dimension + 1}: interval {cell} of {n} hit twice");
                seen[cell] = true;
            }
        }
    }

    [Fact]
    public void Sobol_APointDependsOnlyOnItsIndex_AndLowerDimensionsDoNotChange()
    {
        var three = new SobolSequence(3);
        var fifty = new SobolSequence(50);
        var later = fifty.Point(123456);
        fifty.Point(7);
        Assert.Equal(later, fifty.Point(123456));
        Assert.Equal(three.Point(123456), later[..3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SobolSequence(SobolSequence.MaxDimensions + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SobolSequence(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => three.Point(-1));
    }

    /// <summary>∫∫ x·y over the unit square is 1/4. 4096 Sobol points give it far closer than
    /// the 1/√N of random sampling (about 5e-3 here).</summary>
    [Fact]
    public void Sobol_IntegratesASmoothFunctionBetterThanRandomSampling()
    {
        var sobol = new SobolSequence(2);
        var p = new double[2];
        double sum = 0;
        const int n = 4096;
        for (int i = 0; i < n; i++)
        {
            sobol.Point(i, p);
            sum += p[0] * p[1];
        }
        Assert.InRange(Math.Abs(sum / n - 0.25), 0, 5e-4);
    }

    // ------------------------------------------------------------------ random streams

    [Fact]
    public void RandomStreams_AreFixedByTheSeedAndTheItem_AndDifferBetweenItems()
    {
        var a = SplitMix64.ForStream(42, 7);
        var b = SplitMix64.ForStream(42, 7);
        var other = SplitMix64.ForStream(42, 8);
        var otherSeed = SplitMix64.ForStream(43, 7);
        ulong first = a.Next();
        Assert.Equal(first, b.Next());
        Assert.NotEqual(first, other.Next());
        Assert.NotEqual(first, otherSeed.Next());
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextDouble(), b.NextDouble());
    }

    /// <summary>The first draw of 20000 consecutive streams is itself uniform: mean 1/2 and
    /// variance 1/12 within five standard errors. A stream scheme that correlated neighbouring
    /// items would fail this.</summary>
    [Fact]
    public void RandomStreams_FirstDrawsOfNeighbouringItemsAreUniform()
    {
        const int n = 20000;
        double sum = 0, sumSquares = 0, sumLagged = 0, previous = 0;
        for (int i = 0; i < n; i++)
        {
            double u = SplitMix64.ForStream(1, i).NextDouble();
            sum += u;
            sumSquares += u * u;
            if (i > 0) sumLagged += (u - 0.5) * (previous - 0.5);
            previous = u;
        }
        double mean = sum / n, variance = sumSquares / n - mean * mean;
        Assert.InRange(mean, 0.5 - 5 * Math.Sqrt(1.0 / 12 / n), 0.5 + 5 * Math.Sqrt(1.0 / 12 / n));
        Assert.InRange(variance, 1.0 / 12 - 0.005, 1.0 / 12 + 0.005);
        // Lag-1 covariance of independent uniforms: 0 ± (1/12)/√n.
        Assert.InRange(Math.Abs(sumLagged / (n - 1)), 0, 5 * (1.0 / 12) / Math.Sqrt(n));
    }

    [Fact]
    public void ParallelSlots_WithRandomStreams_AreBitwiseTheSameAtAnyThreadCount()
    {
        static double Work(int item)
        {
            var random = SplitMix64.ForStream(2026, item);
            double sum = 0;
            for (int k = 0; k < 200; k++) sum += random.NextNormal();
            return sum;
        }

        var serial = ParallelSlots.Compute(500, 1, Work);
        var parallel = ParallelSlots.Compute(500, 8, Work);
        var unlimited = ParallelSlots.Compute(500, null, Work);
        Assert.Equal(serial, parallel);
        Assert.Equal(serial, unlimited);
        Assert.Equal(Work(499), serial[499]);
    }

    [Fact]
    public void ParallelSlots_KeepIndexOrder_AndSurfaceTheWorkersOwnException()
    {
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => i * i).ToArray(),
            ParallelSlots.Compute(1000, null, i => i * i));
        Assert.Empty(ParallelSlots.Compute(0, null, i => i));
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            ParallelSlots.Compute<int>(10, 2, i => i == 5 ? throw new InvalidOperationException("item 5") : i));
        Assert.Equal("item 5", thrown.Message);
    }

    // ------------------------------------------------------------------ Bessel J₁

    /// <summary>Bessel's integral, J₁(x) = (1/π)∫₀^π cos(θ − x·sin θ) dθ (DLMF 10.9.2), by the
    /// trapezoid rule. The integrand is even and 2π-periodic, so the rule converges faster than
    /// any power of the step and shares no algebra with the code under test.</summary>
    private static double J1Integral(double x)
    {
        const int n = 4000;
        double sum = 0;
        for (int i = 0; i <= n; i++)
        {
            double theta = Math.PI * i / n;
            double f = Math.Cos(theta - x * Math.Sin(theta));
            sum += i == 0 || i == n ? 0.5 * f : f;
        }
        return sum / n;
    }

    [Fact]
    public void J1_MatchesBesselsIntegralTo1e13_AcrossAllThreeRanges()
    {
        double worst = 0;
        for (double x = 0; x <= 80; x += 0.137)
            worst = Math.Max(worst, Math.Abs(Bessel.J1(x) - J1Integral(x)));
        foreach (double x in new[] { 1.999999, 2.0, 2.000001, 24.999999, 25.0, 25.000001, 3.8317059702075125 })
            worst = Math.Max(worst, Math.Abs(Bessel.J1(x) - J1Integral(x)));
        Assert.InRange(worst, 0, 1e-13);
    }

    /// <summary>The three methods share no algebra, so their agreement where two of them are
    /// both accurate is a check on each: series against recurrence up to x = 4, and recurrence
    /// against the asymptotic form from x = 22.</summary>
    [Fact]
    public void J1_SeriesRecurrenceAndAsymptoticFormsAgreeTo1e13()
    {
        for (double x = 0.5; x <= 4.0; x += 0.25)
            Assert.InRange(Math.Abs(Bessel.J1Series(x) - Bessel.J1Recurrence(x)), 0, 1e-13);
        for (double x = 22; x <= 32; x += 0.5)
            Assert.InRange(Math.Abs(Bessel.J1Asymptotic(x) - Bessel.J1Recurrence(x)), 0, 1e-13);
    }

    [Fact]
    public void J1_IsOdd_ZeroAtZero_AndHalfXForSmallX()
    {
        Assert.Equal(0.0, Bessel.J1(0));
        foreach (double x in new[] { 0.3, 1.7, 9.0, 40.0 })
            Assert.Equal(-Bessel.J1(x), Bessel.J1(-x));
        Assert.Equal(0.5e-8, Bessel.J1(1e-8), 15);              // J₁(x) = x/2 − x³/16 + …
        // The Airy pattern's first dark ring: 2·J₁(x)/x vanishes at the first zero of J₁,
        // and J₁ changes sign across it.
        double zero = RootFinding.Brent(Bessel.J1, 3, 5);
        Assert.InRange(Math.Abs(J1Integral(zero)), 0, 1e-14);
        Assert.True(Bessel.J1(zero - 1e-6) > 0 && Bessel.J1(zero + 1e-6) < 0);
    }

    // ------------------------------------------------------------------ splines

    [Fact]
    public void SplineDerivative_IsExactForALine_AndContinuousAcrossKnots()
    {
        double[] x = { 0, 0.7, 1.1, 2.5, 4.0 };
        var line = new NaturalCubicSpline(x, x.Select(v => 3 - 2 * v).ToArray());
        foreach (double v in new[] { 0, 0.3, 1.1, 3.9, 4.0 })
            Assert.Equal(-2.0, line.Derivative(v), 13);

        var curve = new NaturalCubicSpline(x, x.Select(v => Math.Sin(v)).ToArray());
        foreach (double knot in x[1..^1])
            Assert.Equal(curve.Derivative(knot - 1e-9), curve.Derivative(knot + 1e-9), 7);
        foreach (double v in new[] { 0.2, 0.9, 1.8, 3.3 })
        {
            const double h = 1e-5;
            double central = (curve.Evaluate(v + h) - curve.Evaluate(v - h)) / (2 * h);
            Assert.Equal(central, curve.Derivative(v), 8);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => curve.Derivative(4.1));
    }

    /// <summary>A natural spline reproduces a straight line, so the tensor product reproduces
    /// a + b·x + c·y + d·x·y and its gradient exactly, on an uneven grid.</summary>
    [Fact]
    public void BicubicSpline_ReproducesABilinearSurfaceAndItsGradient()
    {
        double[] x = { -1, -0.2, 0.5, 0.9, 2 };
        double[] y = { 0, 1, 1.5, 4 };
        static double F(double u, double v) => 1.5 - 2 * u + 0.75 * v + 3 * u * v;
        var spline = new BicubicSpline(x, y, Grid(x, y, F));
        foreach (var (u, v) in new[] { (-1.0, 0.0), (0.1, 0.4), (0.5, 1.5), (1.7, 3.9), (2.0, 4.0) })
        {
            var (value, dx, dy) = spline.EvaluateWithGradient(u, v);
            Assert.Equal(F(u, v), value, 12);
            Assert.Equal(F(u, v), spline.Evaluate(u, v), 12);
            Assert.Equal(-2 + 3 * v, dx, 12);
            Assert.Equal(0.75 + 3 * u, dy, 12);
        }
        Assert.Equal((-1.0, 2.0), spline.RangeX);
        Assert.Equal((0.0, 4.0), spline.RangeY);
        Assert.Throws<ArgumentOutOfRangeException>(() => spline.Evaluate(2.1, 1));
        Assert.Throws<ArgumentException>(() => new BicubicSpline(x, y, new double[4, 4]));
    }

    /// <summary>On a grid of step h a cubic spline's error falls as h⁴ away from the edges:
    /// sin(x)·cos(y) at step 0.05 is matched to 1e-7 and its gradient to 1e-5 in the middle,
    /// and halving the step cuts the value error by more than 8.</summary>
    [Fact]
    public void BicubicSpline_PassesThroughItsGrid_AndConvergesOnASmoothSurface()
    {
        static double F(double u, double v) => Math.Sin(u) * Math.Cos(v);
        static (BicubicSpline Spline, double[] Axis) Build(int n)
        {
            var axis = Enumerable.Range(0, n + 1).Select(i => 3.0 * i / n).ToArray();
            return (new BicubicSpline(axis, axis, Grid(axis, axis, F)), axis);
        }

        var (coarse, coarseAxis) = Build(30);
        var (fine, _) = Build(60);
        Assert.Equal(F(coarseAxis[7], coarseAxis[19]), coarse.Evaluate(coarseAxis[7], coarseAxis[19]), 14);

        // The error within a cell depends on where in the cell the point is, so the two grids
        // are compared by their largest error over the same patch in the middle.
        double coarseError = 0, fineError = 0, gradientError = 0;
        for (int i = 0; i <= 40; i++)
            for (int j = 0; j <= 40; j++)
            {
                double u = 1.0 + 0.0173 * i, v = 0.9 + 0.0191 * j;
                coarseError = Math.Max(coarseError, Math.Abs(coarse.Evaluate(u, v) - F(u, v)));
                var (value, dx, dy) = fine.EvaluateWithGradient(u, v);
                fineError = Math.Max(fineError, Math.Abs(value - F(u, v)));
                gradientError = Math.Max(gradientError, Math.Max(
                    Math.Abs(dx - Math.Cos(u) * Math.Cos(v)), Math.Abs(dy + Math.Sin(u) * Math.Sin(v))));
            }
        Assert.InRange(fineError, 0, 1e-7);
        Assert.InRange(gradientError, 0, 1e-5);
        Assert.True(coarseError > 8 * fineError, $"coarse {coarseError:g3}, fine {fineError:g3}");
    }

    private static double[,] Grid(double[] x, double[] y, Func<double, double, double> f)
    {
        var values = new double[x.Length, y.Length];
        for (int i = 0; i < x.Length; i++)
            for (int j = 0; j < y.Length; j++)
                values[i, j] = f(x[i], y[j]);
        return values;
    }

    // ------------------------------------------------------------------ Brent

    [Fact]
    public void Brent_FindsRootsWithClosedFormsToTheLastBits()
    {
        Assert.Equal(Math.Sqrt(2), RootFinding.Brent(x => x * x - 2, 0, 2), 14);
        Assert.Equal(Math.PI / 2, RootFinding.Brent(Math.Cos, 1, 2), 14);
        Assert.Equal(Math.Log(2), RootFinding.Brent(x => Math.Exp(x) - 2, -3, 5), 14);
        Assert.Equal(-1.0, RootFinding.Brent(x => x * x * x + 1, -4, 3), 14);
        // Either order of the ends, and a root at an end.
        Assert.Equal(Math.Sqrt(2), RootFinding.Brent(x => x * x - 2, 2, 0), 14);
        Assert.Equal(2.0, RootFinding.Brent(x => x - 2, 2, 5));
    }

    [Fact]
    public void Brent_HonoursItsTolerance_OnAFlatRootAndAStep()
    {
        int calls = 0;
        double root = RootFinding.Brent(x => { calls++; return Math.Pow(x, 9); }, -1, 1.5, tolerance: 1e-12);
        Assert.InRange(Math.Abs(root), 0, 1e-12);
        Assert.InRange(calls, 1, 200);
        // A sign change without a zero: the bracket closes on the jump.
        double jump = RootFinding.Brent(x => x < 0.3 ? -1 : 1, 0, 1, tolerance: 1e-12);
        Assert.InRange(Math.Abs(jump - 0.3), 0, 1e-12);
    }

    [Fact]
    public void Brent_RejectsAnIntervalWithoutASignChange_AndReportsNoConvergence()
    {
        Assert.Throws<ArgumentException>(() => RootFinding.Brent(x => x * x + 1, -1, 1));
        Assert.Throws<ArgumentException>(() => RootFinding.Brent(x => double.NaN, -1, 1));
        Assert.False(RootFinding.TryBrent(Math.Cos, 1, 2, out double estimate, maxIterations: 2));
        Assert.InRange(estimate, 1, 2);
        Assert.Throws<InvalidOperationException>(() => RootFinding.Brent(Math.Cos, 1, 2, maxIterations: 2));
    }
}
