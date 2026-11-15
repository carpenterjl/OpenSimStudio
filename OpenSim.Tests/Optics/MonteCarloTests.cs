using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Tolerancing;
using G = OpenSim.Tests.Optics.Gates.MonteCarloGateTests;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>OP-65: the Monte Carlo distributions (OpticStudio 2025 R2 help, "Normal / Uniform / Parabolic /
/// User Defined Statistical Distribution", saved under plans/sources/optics/op-65-help/), the run's
/// statistics, yield and histogram, failures, saved trial lenses, the MTF overlay and the saved settings.</summary>
public sealed class MonteCarloTests
{
    private static double[] Draws(ToleranceDistribution d, int n, long seed = 5)
    {
        var g = new SplitMix64(seed);
        return Enumerable.Range(0, n).Select(_ => d.Draw(g)).ToArray();
    }

    /// <summary>The help's example: "a thickness is nominally 100 mm with a tolerance of plus 3 or minus 1 mm ...
    /// drawn from a normal distribution with a 101 mm mean and a range of plus or minus 2 mm, with a standard
    /// deviation of 1.0. If "n" is 5, then the standard deviation will be 0.4" (help, "Normal Statistical
    /// Distribution"); and every draw stays within the extremes.</summary>
    [Fact]
    public void Normal_FollowsTheHelpsExample_AndStaysWithinTheExtremes()
    {
        var two = ToleranceDistribution.Normal(-1, 3, 2);
        Assert.Equal(1, two.Mid);
        Assert.Equal(2, two.HalfWidth);
        Assert.Equal(1.0, two.Sigma);
        Assert.Equal(0.4, ToleranceDistribution.Normal(-1, 3, 5).Sigma, 15);
        Assert.All(Draws(two, 5000), v => Assert.InRange(v, -1, 3));
        Assert.Throws<ArgumentException>(() => ToleranceDistribution.Normal(-1, 1, 0));
        Assert.Throws<ArgumentException>(() => ToleranceDistribution.Normal(-1, 1, 11));
    }

    /// <summary>The help's default example: "a radius of 100.00 mm with a tolerance of +4.0/ - 0.0 mm will be
    /// assigned a random radius between 100.00 and 104.00 mm, with a normal distribution centered on 102.00 mm and
    /// a standard deviation of 1.0 mm" (help, "Monte Carlo Analysis").</summary>
    [Fact]
    public void TheDefaultNormal_OfAOneSidedTolerance_IsTheHelpsExample()
    {
        var d = ToleranceDistribution.Normal(0, 4, new MonteCarloOptions().StandardDeviations);
        Assert.Equal(2, d.Mid);
        Assert.Equal(1, d.Sigma);
        Assert.All(Draws(d, 2000), v => Assert.InRange(v, 0, 4));
    }

    /// <summary>Uniform and parabolic stay within [Min, Max]; the parabolic draw is its inverse distribution
    /// function Δ∛(2u − 1) (derived from p = 3x²/(2Δ³)), so a fixed uniform gives a known value.</summary>
    [Fact]
    public void UniformAndParabolic_AreTheirInverseDistributionFunctions()
    {
        Assert.All(Draws(ToleranceDistribution.Uniform(-0.5, 1.5), 2000), v => Assert.InRange(v, -0.5, 1.5));
        var p = ToleranceDistribution.Parabolic(-2, 2);
        Assert.All(Draws(p, 2000), v => Assert.InRange(v, -2, 2));
        var g1 = new SplitMix64(9);
        var g2 = new SplitMix64(9);
        double u = g2.NextDouble();
        Assert.Equal(2 * Math.Cbrt(2 * u - 1), p.Draw(g1), 15);
        // More of the parabolic's draws lie in the outer half of the range than the uniform's half (derived:
        // P(|x| > Δ/2) = 1 − 1/8 = 7/8).
        double outer = Draws(p, 4000).Count(v => Math.Abs(v) > 1) / 4000.0;
        Assert.InRange(outer, 7.0 / 8 - 0.03, 7.0 / 8 + 0.03);   // 5σ of a binomial fraction at N = 4000 is 0.026
    }

    /// <summary>A radial operand with a zero limit is centred at 0 over ±max(|Min|, |Max|) for the normal and
    /// parabolic statistics (help: "if either Max or Min is 0 then the distribution will be centered at 0").</summary>
    [Fact]
    public void ARadialOperand_WithAZeroLimit_IsCentredAtZero()
    {
        var d = ToleranceDistribution.Normal(0, 0.02, 2, centredAtZero: true);
        Assert.Equal(0, d.Mid);
        Assert.Equal(0.02, d.HalfWidth);
        Assert.Contains(Draws(d, 200), v => v < 0);
    }

    /// <summary>A user defined table T(X) = X (a ramp) has the density 2X and the distribution function X²
    /// (derived), so its inverse is √u; the help's own example table parses and draws within [0, 1].</summary>
    [Fact]
    public void UserDefined_IsTheTablesExactInverse_AndRefusesBadTables()
    {
        var ramp = UserDefinedDistribution.Parse("0 0\n1 1\n", "ramp.udd");
        foreach (double u in new[] { 0.0, 0.04, 0.25, 0.5, 0.81, 1.0 })
            Assert.Equal(Math.Sqrt(u), ramp.Inverse(u), 14);
        Assert.Equal(1.0, ramp.Density(0.5), 14);

        // The help's example (help, "User Defined Statistical Distribution").
        var example = UserDefinedDistribution.Parse("0.0 0.0\n0.1 0.5\n0.2 1.0\n0.3 0.5\n0.4 0.0\n0.5 0.5\n0.8 4.0\n1.0 5.0\n", "example.udd");
        var d = ToleranceDistribution.UserDefined(100, 102, example);
        Assert.All(Draws(d, 2000), v => Assert.InRange(v, 100, 102));

        Assert.Contains("first X must be 0.0", Assert.Throws<ArgumentException>(() => UserDefinedDistribution.Parse("0.1 1\n1 1", "a")).Message);
        Assert.Contains("last X must be 1.0", Assert.Throws<ArgumentException>(() => UserDefinedDistribution.Parse("0 1\n0.9 1", "b")).Message);
        Assert.Contains("must increase", Assert.Throws<ArgumentException>(() => UserDefinedDistribution.Parse("0 1\n0.5 1\n0.5 2\n1 1", "c")).Message);
        Assert.Contains("line 2", Assert.Throws<ArgumentException>(() => UserDefinedDistribution.Parse("0 1\nx y\n1 1", "d")).Message);
        Assert.Contains("every probability is zero", Assert.Throws<ArgumentException>(() => UserDefinedDistribution.Parse("0 0\n1 0", "e")).Message);
    }

    /// <summary>A STAT 3 row reads its .UDD file by the name in its comment; a missing name or folder is refused by row.</summary>
    [Fact]
    public void AStatThreeRow_ReadsItsFileByTheCommentsName()
    {
        var table = X.Table(
            new ToleranceOperandRow { Code = "TWAV", Min = G.Lambda },
            new ToleranceOperandRow { Code = "STAT", Int1 = 3, Comment = "ramp.udd" },
            X.Row("TFRN", 1, 0, -G.Fringes, G.Fringes));
        string? asked = null;
        var r = MonteCarloAnalysis.Run(G.Lens(), table, X.NoCatalogs, G.Linear,
            new MonteCarloOptions { Trials = 20, ReadDistribution = name => { asked = name; return "0 0\n1 1"; } });
        Assert.Equal("ramp.udd", asked);
        Assert.Equal(ToleranceStatistics.UserDefined, r.Distributions.Single().Statistics);
        Assert.All(r.Trials, t => Assert.InRange(t.Draws[0].Value, -G.Fringes, G.Fringes));

        var noFolder = Assert.Throws<ArgumentException>(() => MonteCarloAnalysis.Run(G.Lens(), table, X.NoCatalogs, G.Linear, new MonteCarloOptions { Trials = 2 }));
        Assert.Contains("row 3", noFolder.Message);
        Assert.Contains("no distribution folder", noFolder.Message);

        var noName = X.Table(new ToleranceOperandRow { Code = "STAT", Int1 = 3 }, X.Row("TFRN", 1, 0, -1, 1));
        Assert.Contains("names no .UDD file", Assert.Throws<ArgumentException>(() =>
            MonteCarloAnalysis.Run(G.Lens(), noName, X.NoCatalogs, G.Linear, new MonteCarloOptions { Trials = 2, ReadDistribution = _ => "0 1\n1 1" })).Message);
    }

    [Fact]
    public void ANormalStatRow_WithNoStandardDeviations_IsRefusedByRow()
    {
        var table = X.Table(new ToleranceOperandRow { Code = "STAT", Int1 = 0, Int2 = 0 }, X.Row("TFRN", 1, 0, -1, 1));
        var e = Assert.Throws<ArgumentException>(() => MonteCarloAnalysis.Run(G.Lens(), table, X.NoCatalogs, G.Linear, new MonteCarloOptions { Trials = 2 }));
        Assert.Contains("Tolerance row 2 (TFRN)", e.Message);
        Assert.Contains("STAT row 1", e.Message);
    }

    /// <summary>The statistics, yield, best and worst trials and histogram follow from the trials' criteria.</summary>
    [Fact]
    public void Statistics_Yield_AndHistogram_FollowFromTheTrials()
    {
        var limit = G.C0;   // the nominal: about half the trials meet it
        var r = MonteCarloAnalysis.Run(G.Lens(), G.Table(1, 0), X.NoCatalogs, G.Linear,
            new MonteCarloOptions { Trials = 40, Seed = 8, YieldLimit = limit, Percentiles = new double[] { 0, 50, 100 }, HistogramBins = 4 });
        var values = r.Trials.Select(t => t.Criterion).ToArray();
        var s = r.Statistics!;
        Assert.Equal(values.Average(), s.Mean, 15);
        Assert.Equal(Math.Sqrt(values.Sum(v => (v - s.Mean) * (v - s.Mean)) / 39), s.StandardDeviation, 15);
        Assert.Equal(values.Min(), s.Percentiles[0].Value);
        Assert.Equal(values.Max(), s.Percentiles[2].Value);
        var sorted = values.OrderBy(v => v).ToArray();
        Assert.Equal(sorted[19] + 0.5 * (sorted[20] - sorted[19]), s.Percentiles[1].Value, 15);   // rank 0.5·39 = 19.5
        Assert.Equal(values.Count(v => v <= limit), r.Yield!.Passed);
        Assert.Equal(40, r.Yield.Evaluated);
        Assert.Equal(Array.IndexOf(values, values.Min()) + 1, r.Best);
        Assert.Equal(Array.IndexOf(values, values.Max()) + 1, r.Worst);

        var h = r.Histogram()!;
        Assert.Equal(4, h.Counts.Count);
        Assert.Equal(40, h.Counts.Sum());
        Assert.Equal(0, h.Below + h.Above);
        var narrow = r.Histogram(2, sorted[5], sorted[34])!;
        Assert.Equal(5, narrow.Below);
        Assert.Equal(5, narrow.Above);
        Assert.Equal(30, narrow.Counts.Sum());
        Assert.NotNull(r.HistogramPlot());
        Assert.Contains(r.ReportLines(), l => l.StartsWith("Yield:", StringComparison.Ordinal));
    }

    /// <summary>Element tilts make the lens non-axial, which the ray launcher refuses: the trials fail by name and
    /// are left out of the statistics with their count shown.</summary>
    [Fact]
    public void ElementTiltTrials_FailByName_AndAreCounted()
    {
        var table = X.Table(X.Row("TRAD", 1, 0, -1, 1), X.Row("TETX", 1, 2, -0.01, 0.01));
        var r = MonteCarloAnalysis.Run(X.Singlet(), table, X.NoCatalogs, X.Wavefront, new MonteCarloOptions { Trials = 3, YieldLimit = 1 });
        Assert.Equal(3, r.Failed);
        Assert.Null(r.Statistics);
        Assert.All(r.Trials, t => Assert.Contains("2 (TETX)", t.Failure));
        Assert.Equal(0, r.Yield!.Evaluated);
        Assert.Contains(r.Notes, n => n.Contains("3 of 3 trials failed"));
        Assert.Contains(r.ReportLines(), l => l.Contains("failed and left out of the statistics: 3"));
    }

    /// <summary>The saved lens of trial k, evaluated with no compensation, gives trial k's criterion: the paraxial
    /// focus shift is written into the thickness before the image (to the rounding of the shift through lens units).</summary>
    [Fact]
    public void TheSavedLensOfTrialK_GivesItsCriterion()
    {
        var lens = X.Singlet();
        var table = X.Table(X.Row("TRAD", 1, 0, -2, 2), X.Row("TTHI", 1, 1, -0.2, 0.2));
        var mc = new MonteCarloOptions { Trials = 3, Seed = 4 };
        var r = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront, mc);
        var trial = r.Trials[1];
        Assert.True(trial.Succeeded, trial.Failure);
        var saved = MonteCarloAnalysis.TrialLens(lens, table, X.NoCatalogs, X.Wavefront, mc, 2);
        Assert.NotEqual(lens.Surfaces![2].Thickness, saved.Surfaces![2].Thickness);
        var check = ToleranceAnalysis.Run(saved, X.Table(X.Row("TRAD", 1, 0, 0, 0)), X.NoCatalogs, X.Wavefront with { Compensation = ToleranceCompensation.None });
        Assert.True(Math.Abs(check.Nominal - trial.Criterion) <= 1e-6 * trial.Criterion, $"{check.Nominal:R} against {trial.Criterion:R}");
    }

    /// <summary>The overlay holds the nominal and each drawn trial's tangential and sagittal MTF; the MTF is 1 at
    /// zero frequency.</summary>
    [Fact]
    public void TheMtfOverlay_HasTheNominalAndEachTrial()
    {
        var mc = new MonteCarloOptions
        {
            Trials = 3, Overlay = new MonteCarloMtfOverlay { Field = 1, MaxFrequency = 50, Points = 6, Trials = 2 },
        };
        var r = MonteCarloAnalysis.Run(X.Singlet(), X.Table(X.Row("TRAD", 1, 0, -1, 1)), X.NoCatalogs, X.Wavefront with { PsfSampling = 32 }, mc);
        var plot = r.MtfOverlayPlot()!;
        Assert.Equal(2 + 2 * 2, plot.Curves.Count);
        Assert.Null(r.Trials[2].Mtf);
        Assert.Equal(1.0, r.NominalMtf!.Tangential[0], 9);
        Assert.Equal(6, r.Trials[0].Mtf!.Frequencies.Count);
        Assert.Throws<ArgumentException>(() => MonteCarloAnalysis.Run(X.Singlet(), X.Table(X.Row("TRAD", 1, 0, -1, 1)), X.NoCatalogs, X.Wavefront,
            mc with { Overlay = new MonteCarloMtfOverlay { Field = 2, MaxFrequency = 50 } }));
    }

    [Fact]
    public void BadOptions_AreRefusedByName()
    {
        var lens = G.Lens();
        var table = G.Table(1, 0);
        Assert.Throws<ArgumentException>(() => MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, G.Linear, new MonteCarloOptions { Trials = 0 }));
        Assert.Throws<ArgumentException>(() => MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, G.Linear, new MonteCarloOptions { Statistics = ToleranceStatistics.UserDefined }));
        Assert.Throws<ArgumentException>(() => MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, G.Linear, new MonteCarloOptions { Percentiles = new double[] { 101 } }));
    }

    [Fact]
    public void ASeedOfZero_IsDrawnFromTheClock_AndNamed()
    {
        var r = MonteCarloAnalysis.Run(G.Lens(), G.Table(1, 0), X.NoCatalogs, G.Linear, new MonteCarloOptions { Trials = 2, Seed = 0 });
        Assert.NotEqual(0, r.Seed);
        Assert.Contains(r.Notes, n => n.Contains($"drawn from the clock: {r.Seed}"));
        var again = MonteCarloAnalysis.Trial(G.Lens(), G.Table(1, 0), X.NoCatalogs, G.Linear, new MonteCarloOptions { Seed = r.Seed }, 2);
        Assert.Equal(r.Trials[1].Draws, again.Draws);
    }

    [Fact]
    public void Settings_RoundTripEveryField_AndNameAnUnknownValue()
    {
        var overlay = new MonteCarloMtfOverlay { Field = 2, MaxFrequency = 40, Points = 11, Trials = 5 };
        var o = new MonteCarloOptions
        {
            Trials = 300, Seed = 17, Statistics = ToleranceStatistics.Parabolic, StandardDeviations = 3, YieldLimit = 0.05,
            Percentiles = new double[] { 5, 95 }, DistributionFolder = @"C:\udd", Overlay = overlay, HistogramBins = 30,
        };
        var saved = MonteCarloSettings.ToSettings(o)!;
        var notes = new List<string>();
        var back = MonteCarloSettings.FromSettings(saved, notes, out var kept, out bool on);
        Assert.Empty(notes);
        Assert.True(on);
        Assert.Equal(overlay, kept);
        Assert.Equal(o with { Percentiles = back.Percentiles }, back);
        Assert.Equal(o.Percentiles, back.Percentiles);
        Assert.Null(MonteCarloSettings.ToSettings(new MonteCarloOptions()));

        // The overlay's settings are kept while it is off.
        var off = MonteCarloSettings.ToSettings(new MonteCarloOptions(), overlay)!;
        Assert.Null(off.OverlayMtf);
        MonteCarloSettings.FromSettings(off, notes, out var keptOff, out bool onOff);
        Assert.False(onOff);
        Assert.Equal(overlay, keptOff);

        MonteCarloSettings.FromSettings(new OpticsToleranceMonteCarloSettings { Statistics = "Gaussian" }, notes, out _, out _);
        Assert.Contains(notes, n => n.Contains("Gaussian"));
        Assert.Equal(17, new OpticsToleranceSettings { MonteCarlo = saved }.MonteCarlo!.Seed);
    }
}
