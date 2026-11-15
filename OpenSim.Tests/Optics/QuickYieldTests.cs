using OpenSim.Core.Model;
using OpenSim.Optics.Tolerancing;
using G = OpenSim.Tests.Optics.Gates.MonteCarloGateTests;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-66: Quick Yield's quadratic model, its samples against the Monte Carlo trials, its failures and
/// determinism, and the Tolerance Data Viewer's data set (histogram, yield curve, statistics, CSV). Every
/// expected value is derived in the test; the lens data are OP-64's and OP-65's arbitrary test inputs.
/// </summary>
public sealed class QuickYieldTests
{
    /// <summary>The fit is exact for a quadratic: f = 1 + 2x − 3y + 0.5z + ½(4x² − 2y²) + 1.5xy − 0.25yz (an
    /// arbitrary quadratic; the points are arbitrary, including a one-sided pair).</summary>
    [Fact]
    public void TheModel_IsExactForAQuadratic()
    {
        static double F(double x, double y, double z) => 1 + 2 * x - 3 * y + 0.5 * z + 0.5 * (4 * x * x - 2 * y * y) + 1.5 * x * y - 0.25 * y * z;
        double[] a = { -0.3, 0.1, -2 }, b = { 0.4, 0.2, 1 };
        double Alone(int i, double v) => i switch { 0 => F(v, 0, 0), 1 => F(0, v, 0), _ => F(0, 0, v) };
        var fa = a.Select((v, i) => Alone(i, v)).ToArray();
        var fb = b.Select((v, i) => Alone(i, v)).ToArray();
        var fb2 = new double[3, 3];
        fb2[0, 1] = F(b[0], b[1], 0);
        fb2[0, 2] = F(b[0], 0, b[2]);
        fb2[1, 2] = F(0, b[1], b[2]);
        var m = ToleranceQuadraticModel.Fit(F(0, 0, 0), a, b, fa, fb, fb2);
        Assert.Equal(new[] { 2.0, -3.0, 0.5 }, m.Gradient.Select(v => Math.Round(v, 12)));
        Assert.Equal(new[] { 4.0, -2.0, 0.0 }, m.Curvature.Select(v => Math.Round(v, 12)));
        Assert.Equal(1.5, m.Cross[0, 1], 12);
        Assert.Equal(0.0, m.Cross[0, 2], 12);
        Assert.Equal(-0.25, m.Cross[2, 1], 12);
        foreach (var (x, y, z) in new[] { (0.37, -0.2, 0.9), (-1.0, 0.5, -0.3), (0.0, 0.0, 0.0) })
            Assert.Equal(F(x, y, z), m.Evaluate(new[] { x, y, z }), 12);

        // Without cross terms the model is separable; a zero-range row has no terms.
        var separable = ToleranceQuadraticModel.Fit(F(0, 0, 0), a, b, fa, fb, null);
        Assert.Equal(0.0, separable.Cross[0, 1]);
        var zero = ToleranceQuadraticModel.Fit(1, new[] { 0.0 }, new[] { 0.0 }, new[] { 5.0 }, new[] { 7.0 }, null);
        Assert.Equal(1, zero.Evaluate(new[] { 3.0 }));
        Assert.Throws<ArgumentException>(() => ToleranceQuadraticModel.Fit(1, new[] { 0.5 }, new[] { 0.5 }, new[] { 1.0 }, new[] { 1.0 }, null));
    }

    /// <summary>
    /// On OP-65's linear gate lens (criterion c₀ + sN of the drawn fringes N, derived there) the model's
    /// gradient is s, and sample k, drawn as Monte Carlo trial k is, equals that trial's criterion: the
    /// samples are the Monte Carlo run's to rounding, so the yields agree exactly. Three evaluations: the
    /// nominal and the row's two ends.
    /// </summary>
    [Fact]
    public void OnALinearLens_SampleK_IsMonteCarloTrialK()
    {
        var table = G.Table(1, 0);
        double limit = G.C0 + 0.3 * G.Slope;   // an arbitrary limit inside the range
        var mc = new MonteCarloOptions { Trials = 60, Seed = 3, YieldLimit = limit };
        var monteCarlo = MonteCarloAnalysis.Run(G.Lens(), table, X.NoCatalogs, G.Linear, mc);
        var quick = QuickYield.Run(G.Lens(), table, X.NoCatalogs, G.Linear, mc, new QuickYieldOptions { Samples = 60 });
        Assert.True(quick.Succeeded, string.Join("; ", quick.Failures));
        Assert.Equal(3, quick.Evaluations);
        Assert.Equal(3, Assert.Single(quick.Rows));
        Assert.Equal(G.C0, quick.Nominal, 15);
        var m = quick.Model!;
        Assert.True(Math.Abs(m.Gradient[0] - G.Slope) <= 1e-9 * G.Slope, $"gradient {m.Gradient[0]:R} against {G.Slope:R}");
        for (int k = 0; k < 60; k++)
            Assert.True(Math.Abs(quick.Samples[k] - monteCarlo.Trials[k].Criterion) <= 1e-12 * G.C0, $"sample {k + 1}");
        Assert.Equal(monteCarlo.Yield!.Passed, quick.Yield!.Passed);
        Assert.Equal(60, quick.Yield.Evaluated);
        Assert.NotNull(quick.YieldStandardError);
        Assert.NotNull(quick.HistogramPlot(monteCarlo: monteCarlo));
        Assert.Equal(2, quick.YieldPlot(monteCarlo)!.Curves.Count);
        Assert.Contains(quick.ReportLines(), l => l.StartsWith("Predicted yield:", StringComparison.Ordinal));
    }

    /// <summary>An element tilt makes the lens non-axial, which the ray launcher refuses (§15): the evaluation
    /// fails by row and value, and no model or yield is given.</summary>
    [Fact]
    public void AFailedEvaluation_IsNamed_AndNoEstimateIsGiven()
    {
        var table = X.Table(X.Row("TRAD", 1, 0, -0.5, 0.5), X.Row("TETX", 1, 2, -0.01, 0.01));
        var r = QuickYield.Run(X.Singlet(), table, X.NoCatalogs, X.Wavefront, new MonteCarloOptions { YieldLimit = 1 }, new QuickYieldOptions { Samples = 10 });
        Assert.False(r.Succeeded);
        Assert.Null(r.Yield);
        Assert.Empty(r.Samples);
        Assert.Contains(r.Failures, f => f.Contains("row 2 (TETX)"));
        Assert.Contains(r.ReportLines(), l => l.StartsWith("Failed evaluations", StringComparison.Ordinal));
    }

    /// <summary>One thread and no thread limit give the same bits (the evaluations are independent slots; the samples
    /// are drawn in order on one thread).</summary>
    [Fact]
    public void TheResult_IsTheSameAtAnyThreadCount()
    {
        var table = X.Table(X.Row("TRAD", 1, 0, -0.5, 0.5), X.Row("TCON", 2, 0, -0.2, 0.2), X.Row("TTHI", 1, 1, -0.05, 0.05));
        var mc = new MonteCarloOptions { Seed = 5, YieldLimit = 0.05 };
        var one = QuickYield.Run(X.Singlet(), table, X.NoCatalogs, X.Wavefront with { MaxDegreeOfParallelism = 1 }, mc, new QuickYieldOptions { Samples = 200 });
        var many = QuickYield.Run(X.Singlet(), table, X.NoCatalogs, X.Wavefront with { MaxDegreeOfParallelism = null }, mc, new QuickYieldOptions { Samples = 200 });
        Assert.True(one.Succeeded, string.Join("; ", one.Failures));
        Assert.Equal(1 + 2 * 3 + 3, one.Evaluations);
        Assert.Equal(one.Samples.Select(BitConverter.DoubleToInt64Bits), many.Samples.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(one.ReportLines(), many.ReportLines());
        var separable = QuickYield.Run(X.Singlet(), table, X.NoCatalogs, X.Wavefront, mc, new QuickYieldOptions { Samples = 10, CrossTerms = false });
        Assert.Equal(1 + 2 * 3, separable.Evaluations);
    }

    [Fact]
    public void Settings_RoundTripEveryField()
    {
        var o = new QuickYieldOptions { Samples = 1234, CrossTerms = false };
        var saved = QuickYieldSettings.ToSettings(o)!;
        Assert.Equal(o, QuickYieldSettings.FromSettings(saved));
        Assert.Null(QuickYieldSettings.ToSettings(new QuickYieldOptions()));
        Assert.Equal(new QuickYieldOptions(), QuickYieldSettings.FromSettings(null));
        Assert.Equal(1234, new OpticsToleranceSettings { QuickYield = saved }.QuickYield!.Samples);
        Assert.Throws<ArgumentException>(() => QuickYield.Run(G.Lens(), G.Table(1, 0), X.NoCatalogs, G.Linear, null, new QuickYieldOptions { Samples = 0 }));
    }

    /// <summary>The data viewer's table of a Monte Carlo run: one column each for the criterion, its change and the drawn
    /// tolerance; the histogram counts every trial once; the yield curve ends at 100 %; the CSV has a line per trial.</summary>
    [Fact]
    public void TheDataSet_ShowsEveryTrial_WithHistogramYieldAndCsv()
    {
        var r = MonteCarloAnalysis.Run(G.Lens(), G.Table(1, 0), X.NoCatalogs, G.Linear, new MonteCarloOptions { Trials = 40, Seed = 2 });
        var data = ToleranceDataSet.FromMonteCarlo(r);
        Assert.Equal(new[] { "Criterion", "Change", "Row 3 (TFRN)" }, data.Columns.Select(c => c.Name));
        Assert.Equal(Enumerable.Range(1, 40), data.Trials);
        Assert.Equal(r.Trials.Select(t => t.Draws[0].Value), data.Column("Row 3 (TFRN)").Values);

        var h = data.Histogram("Row 3 (TFRN)", 8)!;
        Assert.Equal(40, h.Counts.Sum() + h.Below + h.Above);
        var narrow = data.Histogram(ToleranceDataSet.CriterionColumn, 4, G.C0, G.C0 + G.Slope)!;
        Assert.Equal(40, narrow.Counts.Sum() + narrow.Below + narrow.Above);
        Assert.True(narrow.Below > 0);

        var curve = Assert.Single(data.YieldCurve(ToleranceDataSet.CriterionColumn)!.Curves);
        Assert.Equal(0, curve.Y[0]);
        Assert.Equal(100, curve.Y[^1]);
        Assert.Equal(curve.X.OrderBy(v => v), curve.X);

        var stats = data.Statistics();
        Assert.Equal(r.Statistics!.Mean, stats[0].Statistics!.Mean);
        Assert.Equal(41, data.ToCsv().TrimEnd('\n').Split('\n').Length);
        Assert.Contains("'Nope'", Assert.Throws<ArgumentException>(() => data.Column("Nope")).Message);
    }
}
