using OpenSim.Core.Numerics;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-33: least-squares Zernike fitting. The gate: a synthetic wavefront built from known
/// coefficients is fitted back to 1e-12, on full, vignetted, masked and weighted pupils, in all
/// three orderings; the fit agrees with an independent least-squares solver (MathNet's QR) when
/// the wavefront is not in the span; and rank deficiency is reported, not hidden.
/// </summary>
public sealed class ZernikeFitTests
{
    /// <summary>A square grid of pixel centres over [−1, 1]², kept where ε ≤ ρ ≤ 1.</summary>
    private static (List<double> X, List<double> Y) Grid(int size, double e = 0)
    {
        var x = new List<double>();
        var y = new List<double>();
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                double px = -1 + (2 * column + 1.0) / size, py = -1 + (2 * row + 1.0) / size;
                double rho = Math.Sqrt(px * px + py * py);
                if (rho > 1 || rho < e) continue;
                x.Add(px);
                y.Add(py);
            }
        return (x, y);
    }

    private static double[] RandomCoefficients(int count, long seed)
    {
        var rng = new SplitMix64(seed);
        var c = new double[count];
        for (int j = 0; j < count; j++) c[j] = 2 * rng.NextDouble() - 1;
        return c;
    }

    private static List<double> Synthesize(ZernikeBasis basis, double[] c, List<double> x, List<double> y)
    {
        var values = new List<double>(x.Count);
        for (int i = 0; i < x.Count; i++) values.Add(basis.Sum(c, x[i], y[i]));
        return values;
    }

    private static void AssertRecovered(double[] expected, ZernikeFitResult fit, double tolerance)
    {
        Assert.Equal(expected.Length, fit.TermCount);
        for (int j = 0; j < expected.Length; j++)
            Assert.True(Math.Abs(expected[j] - fit.Coefficients[j]) <= tolerance,
                $"term {j + 1}: expected {expected[j]:R}, got {fit.Coefficients[j]:R}");
    }

    [Theory]
    [InlineData(ZernikeOrdering.Fringe, 37, 0.0)]
    [InlineData(ZernikeOrdering.Standard, 66, 0.0)]
    [InlineData(ZernikeOrdering.Annular, 45, 0.4)]
    public void Fit_RecoversKnownCoefficientsOnTheFullPupil(ZernikeOrdering ordering, int count, double e)
    {
        var basis = new ZernikeBasis(ordering, count, e);
        var (x, y) = Grid(64, e);
        var c = RandomCoefficients(count, 101 + count);
        var fit = ZernikeFit.Fit(basis, x, y, Synthesize(basis, c, x, y));

        AssertRecovered(c, fit, 1e-12);
        Assert.True(fit.IsFullRank);
        Assert.Equal(x.Count, fit.PointsUsed);
        Assert.True(fit.ResidualRms < 1e-12, $"residual RMS {fit.ResidualRms:g3}");
        Assert.True(fit.MaxResidual < 1e-11, $"max residual {fit.MaxResidual:g3}");
        // A dense grid on the whole pupil: the scaled columns are nearly orthonormal.
        Assert.InRange(fit.ConditionNumber, 1.0, 10.0);
        Assert.Equal(count, fit.SingularValues.Count);
    }

    /// <summary>
    /// A vignetted pupil (the part with x &gt; 0.3 and y &gt; −0.2 masked out, as a clipped beam
    /// would leave it) with uneven positive weights. The sampled terms are far from orthogonal
    /// here, yet least squares still returns the coefficients exactly, and the condition number
    /// reports the loss of orthogonality.
    /// </summary>
    [Fact]
    public void Fit_RecoversKnownCoefficientsOnAVignettedWeightedPupil()
    {
        var basis = new ZernikeBasis(ZernikeOrdering.Standard, 66);
        var (x, y) = Grid(64);
        var c = RandomCoefficients(66, 7);
        var values = Synthesize(basis, c, x, y);
        var rng = new SplitMix64(8);
        var weights = new double[x.Count];
        var mask = new bool[x.Count];
        int kept = 0;
        for (int i = 0; i < x.Count; i++)
        {
            weights[i] = 0.5 + rng.NextDouble();
            mask[i] = !(x[i] > 0.3 && y[i] > -0.2);
            if (mask[i]) kept++;
            else values[i] = double.NaN; // a masked point is never read
        }
        var fit = ZernikeFit.Fit(basis, x, y, values, weights, mask);

        AssertRecovered(c, fit, 1e-12);
        Assert.Equal(kept, fit.PointsUsed);
        Assert.True(fit.IsFullRank);
        Assert.True(fit.ConditionNumber > 10, $"condition {fit.ConditionNumber:g4} should show the vignetting");
    }

    /// <summary>
    /// Why the fit is never a projection: on the same vignetted pupil, the inner products
    /// ⟨W, Z_j⟩ / ⟨Z_j, Z_j⟩ over the samples are wrong by far more than the fit's error.
    /// </summary>
    [Fact]
    public void Projection_FailsWhereTheFitSucceeds()
    {
        var basis = new ZernikeBasis(ZernikeOrdering.Standard, 15);
        var (x, y) = Grid(48);
        var keepX = new List<double>();
        var keepY = new List<double>();
        for (int i = 0; i < x.Count; i++)
            if (y[i] < 0.5)
            {
                keepX.Add(x[i]);
                keepY.Add(y[i]);
            }
        var c = RandomCoefficients(15, 15);
        var values = Synthesize(basis, c, keepX, keepY);
        var fit = ZernikeFit.Fit(basis, keepX, keepY, values);
        AssertRecovered(c, fit, 1e-12);

        var row = new double[15];
        var dot = new double[15];
        var norm = new double[15];
        for (int i = 0; i < keepX.Count; i++)
        {
            basis.EvaluateAll(keepX[i], keepY[i], row);
            for (int j = 0; j < 15; j++)
            {
                dot[j] += values[i] * row[j];
                norm[j] += row[j] * row[j];
            }
        }
        double worst = 0;
        for (int j = 0; j < 15; j++) worst = Math.Max(worst, Math.Abs(dot[j] / norm[j] - c[j]));
        Assert.True(worst > 0.05, $"projection error {worst:g3} unexpectedly small");
    }

    /// <summary>
    /// A wavefront outside the span (higher terms the fit does not include, plus deterministic
    /// noise), weighted: the coefficients equal MathNet's QR solution of the same weighted
    /// problem, and the reported residual RMS is the weighted RMS of the actual residuals.
    /// </summary>
    [Fact]
    public void Fit_IsTheWeightedLeastSquaresSolution()
    {
        var full = new ZernikeBasis(ZernikeOrdering.Standard, 91);
        var basis = new ZernikeBasis(ZernikeOrdering.Standard, 36);
        var (x, y) = Grid(40);
        var c = RandomCoefficients(91, 91);
        var values = Synthesize(full, c, x, y);
        var rng = new SplitMix64(92);
        var weights = new double[x.Count];
        for (int i = 0; i < x.Count; i++)
        {
            values[i] += 1e-3 * rng.NextNormal();
            weights[i] = 0.2 + rng.NextDouble();
        }
        var fit = ZernikeFit.Fit(basis, x, y, values, weights);

        var a = MathNet.Numerics.LinearAlgebra.Matrix<double>.Build.Dense(x.Count, 36);
        var b = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.Dense(x.Count);
        var row = new double[36];
        for (int i = 0; i < x.Count; i++)
        {
            basis.EvaluateAll(x[i], y[i], row);
            double root = Math.Sqrt(weights[i]);
            for (int j = 0; j < 36; j++) a[i, j] = root * row[j];
            b[i] = root * values[i];
        }
        var oracle = a.QR().Solve(b);
        for (int j = 0; j < 36; j++)
            Assert.True(Math.Abs(oracle[j] - fit.Coefficients[j]) <= 1e-12 * (1 + Math.Abs(oracle[j])),
                $"term {j + 1}: MathNet {oracle[j]:R}, fit {fit.Coefficients[j]:R}");

        double squares = 0, weightSum = 0, max = 0;
        for (int i = 0; i < x.Count; i++)
        {
            double r = basis.Sum(fit.Coefficients.ToArray(), x[i], y[i]) - values[i];
            squares += weights[i] * r * r;
            weightSum += weights[i];
            max = Math.Max(max, Math.Abs(r));
        }
        Assert.Equal(Math.Sqrt(squares / weightSum), fit.ResidualRms, 12);
        Assert.Equal(max, fit.MaxResidual, 12);
        Assert.True(fit.ResidualRms > 1e-4, "the higher terms and the noise must leave a residual");
    }

    /// <summary>Samples on the x axis only cannot tell the sin terms (zero there) apart, nor the
    /// cos terms of equal radial polynomial in x: the rank says so, the condition number is
    /// infinite, and the data are still fitted exactly by the minimum-norm solution.</summary>
    [Fact]
    public void Fit_ReportsRankDeficiency()
    {
        var basis = new ZernikeBasis(ZernikeOrdering.Standard, 10);
        var x = new List<double>();
        var y = new List<double>();
        for (int i = 0; i < 41; i++)
        {
            x.Add(-1 + 2.0 * i / 40);
            y.Add(0);
        }
        var c = RandomCoefficients(10, 3);
        var fit = ZernikeFit.Fit(basis, x, y, Synthesize(basis, c, x, y));

        Assert.False(fit.IsFullRank);
        Assert.True(fit.Rank < 10);
        Assert.Equal(double.PositiveInfinity, fit.ConditionNumber);
        Assert.True(fit.MaxResidual < 1e-12, $"max residual {fit.MaxResidual:g3}");
        // The sin terms (3, 5, 7, 9) are zero on y = 0, so the minimum-norm fit gives them 0.
        foreach (int j in new[] { 3, 5, 7, 9 }) Assert.Equal(0, fit.Coefficients[j - 1], 12);
    }

    [Fact]
    public void Fit_RejectsBadInput()
    {
        var basis = new ZernikeBasis(ZernikeOrdering.Annular, 6, 0.3);
        var x = new List<double> { 0.5, 0.6, -0.5, 0, 0.7, -0.4, 0.35 };
        var y = new List<double> { 0, 0.3, 0.2, 0.9, -0.4, -0.6, 0.35 };
        var v = new List<double> { 1, 2, 3, 4, 5, 6, 7 };
        // Too few points.
        Assert.Throws<ArgumentException>(() => ZernikeFit.Fit(basis, x.Take(5).ToList(), y.Take(5).ToList(), v.Take(5).ToList()));
        // A point inside the obscuration.
        Assert.Throws<ArgumentException>(() => ZernikeFit.Fit(basis, x.Append(0.1).ToList(), y.Append(0).ToList(), v.Append(0).ToList()));
        // A point outside the unit circle.
        Assert.Throws<ArgumentException>(() => ZernikeFit.Fit(basis, x.Append(1.01).ToList(), y.Append(0).ToList(), v.Append(0).ToList()));
        // NaN on a point that is not masked, and a negative weight.
        Assert.Throws<ArgumentException>(() => ZernikeFit.Fit(basis, x, y, v.Select((value, i) => i == 2 ? double.NaN : value).ToList()));
        Assert.Throws<ArgumentException>(() => ZernikeFit.Fit(basis, x, y, v, weights: v.Select(_ => -1.0).ToList()));
        // Masked out, the same bad point is fine (and a zero weight leaves a point out).
        var mask = v.Select(_ => true).Append(false).ToList();
        var fit = ZernikeFit.Fit(basis, x.Append(0.1).ToList(), y.Append(0).ToList(), v.Append(double.NaN).ToList(), mask: mask);
        Assert.Equal(7, fit.PointsUsed);
        var weights = v.Select((_, i) => i == 0 ? 0.0 : 1.0).ToList();
        Assert.Throws<ArgumentException>(() => ZernikeFit.Fit(basis, x.Take(6).ToList(), y.Take(6).ToList(), v.Take(6).ToList(), weights.Take(6).ToList()));
    }
}
