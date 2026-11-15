using OpenSim.Optics.Tolerancing;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-66's gate (plan): "The quick estimate falls within the Monte Carlo confidence band on a
/// linear-regime lens."
/// <para>
/// The lens: OP-64's F/25 singlet (<see cref="ToleranceRunFixtures"/>), criterion RMS wavefront with the
/// paraxial focus compensator. Its RMS wavefront is |W040|·2/(3√5) (derived there), and the tolerances
/// below move W040 by a few per cent of its nominal without changing its sign, so the criterion is close
/// to linear in them: the regime the plan names. Rows (arbitrary test inputs): TRAD 1 ±1 mm, TRAD 2 ±1 mm,
/// TCON 1 ±0.1, TIND 1 ±0.002; default statistics, normal with 2 standard deviations to the extremes.
/// </para>
/// <para>
/// The band (derived). The Monte Carlo yield p̂ over N trials has the binomial standard error
/// √(p̂(1 − p̂)/N); Quick Yield's q over its M samples has √(q(1 − q)/M). The two runs draw from different
/// seeds, so they are independent, and the quick estimate is accepted when
/// |q − p̂| ≤ 3·√(p̂(1 − p̂)/N + q(1 − q)/M), three combined standard errors (a two-sided 99.7 % band under the
/// normal approximation, adequate at N = 1000 with p near 0.7). The limit is the Monte Carlo run's 70th
/// percentile, so p̂ ≈ 0.7; the sample quantile's own spread, F(q̂₀.₇) having variance p(1 − p)/N, is of the
/// same size as the first term and so inside the band's allowance for it. The seeds are fixed, so the test
/// is fixed; the band says why its numbers may differ from the population values.
/// </para>
/// </summary>
public sealed class QuickYieldGateTests
{
    [Fact]
    [Trait("Category", "Slow")]
    public void TheQuickEstimate_FallsWithinTheMonteCarloBand_OnALinearRegimeLens()
    {
        const int n = 1000, m = 20000;
        var lens = X.Singlet();
        var table = X.Table(X.Row("TRAD", 1, 0, -1, 1), X.Row("TRAD", 2, 0, -1, 1), X.Row("TCON", 1, 0, -0.1, 0.1), X.Row("TIND", 1, 0, -0.002, 0.002));

        var mc = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront,
            new MonteCarloOptions { Trials = n, Seed = 21, Percentiles = new double[] { 70 } });
        Assert.Equal(0, mc.Failed);
        double limit = mc.Statistics!.Percentiles[0].Value;
        int passed = mc.Trials.Count(t => t.Criterion <= limit);
        double p = (double)passed / n;

        var quick = QuickYield.Run(lens, table, X.NoCatalogs, X.Wavefront,
            new MonteCarloOptions { Seed = 22, YieldLimit = limit }, new QuickYieldOptions { Samples = m });
        Assert.True(quick.Succeeded, string.Join("; ", quick.Failures));
        Assert.Equal(1 + 2 * 4 + 6, quick.Evaluations);
        Assert.Equal(mc.Nominal, quick.Nominal);
        double q = quick.Yield!.Fraction;

        double band = 3 * Math.Sqrt(p * (1 - p) / n + q * (1 - q) / m);
        Assert.True(Math.Abs(q - p) <= band, $"quick yield {q:R} against Monte Carlo {p:R} ± {band:R} (limit {limit:R} waves)");

        // The spread agrees too: the samples' standard deviation against the trials', within five standard errors of a
        // standard deviation at N trials, σ/√(2N) (normal approximation, derived).
        double sd = mc.Statistics.StandardDeviation;
        double se = sd / Math.Sqrt(2.0 * n);
        Assert.True(Math.Abs(quick.Statistics!.StandardDeviation - sd) <= 5 * se,
            $"quick σ {quick.Statistics.StandardDeviation:R} against Monte Carlo σ {sd:R} ± {5 * se:R}");
    }
}
