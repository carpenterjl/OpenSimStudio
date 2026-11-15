using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Optimization.HighYield;
using OpenSim.Optics.Tolerancing;
using F = OpenSim.Tests.Optics.HighYieldFixtures;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-62's gate (plan): "On a lens with one known sensitive surface, the optimised design shows
/// lower Monte Carlo spread at equal nominal performance."
/// <para>
/// <b>The lens</b> (<see cref="HighYieldFixtures.Shell"/>): a zero-thickness glass shell
/// (radius 15 mm, the variable; its back surface a curvature pickup of its front) in front of
/// an ideal lens, EPD 10 mm. The shell changes no ray whatever its curvature c (derived on the
/// fixture), so every radius has the same, perfect, nominal image: the start is already at the
/// nominal optimum, and a nominal-only optimization has nothing to change. The sensitive
/// surface is the shell's front: decentered by d it no longer matches its back, and the layer
/// between them, of thickness s(y − d) − s(y) ≈ −d·s′(y) = −d(cy + c³y³/2 + …) (derived from
/// the sphere's sag), adds a wavefront (n − 1) times that: the linear term only tilts the beam,
/// which the ideal lens images without aberration, but the cubic term is coma of size
/// ∝ d·c³, so the decenter's effect on the RMS spot shrinks with the curvature and vanishes for
/// a flat shell (a decentered plane is the same plane).
/// </para>
/// <para>
/// <b>The run.</b> The merit function is the Optimization Wizard's default (RMS spot radius,
/// centroid) plus the High-Yield rows at weight 1. The High-Yield term of the shell grows as
/// c² (τ ≈ |n − n′|θ²/2, sin θ = ch), and the nominal rows do not change with c, so the
/// optimizer can only lower the merit by flattening the shell. The Monte Carlo analysis
/// (OP-65, as it is) then draws surface 1's decenter in x and y (TSDX, TSDY, ±0.05 mm, the
/// default normal statistics, an arbitrary test input) with a fixed seed, no compensator, RMS
/// spot radius on the system's one field, for the start and for the optimised design: the same
/// seed and table give both the same draws.
/// </para>
/// <para>
/// <b>Assertions.</b> (1) The curvature fell. (2) Equal nominal performance: the Monte Carlo
/// nominal RMS spot radii of the two designs differ by at most 1e-9 lens units — the derived
/// difference is zero, and 1e-9 mm is five orders above the rounding of a 105 mm trace and six
/// below the decentered spots. (3) Lower spread: the optimised design's standard deviation of
/// the RMS spot radius over the trials is below half the start's. The derived ratio is about
/// (c/c₀)³ for the coma; requiring a half asks only that the optimizer flattened the shell by
/// a fifth (0.8³ ≈ 0.51), and the strict curvature fall of (1) is checked separately so a
/// failure says which part did not happen. Its mean is lower too.
/// </para>
/// </summary>
public sealed class HighYieldGateTests
{
    private const double StartRadius = 15;          // mm, a test input
    private const double Decenter = 0.05;           // mm, ± (a test input)

    private static OpticsSettings WithMerit(OpticsSettings lens, bool highYield)
    {
        var configurations = F.Configurations(lens);
        var options = new DefaultMeritOptions();
        var rows = DefaultMeritWizard.Generate(configurations, 1, options).Rows.ToList();
        if (highYield) rows.AddRange(HighYieldMerit.Generate(configurations, 1, options, weight: 1).Rows);
        return lens with { MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(rows)) };
    }

    private static MonteCarloResult MonteCarlo(OpticsSettings lens) => MonteCarloAnalysis.Run(lens,
        X.Table(X.Row("TSDX", 1, 0, -Decenter, Decenter), X.Row("TSDY", 1, 0, -Decenter, Decenter)), F.NoCatalogs,
        new ToleranceRunOptions
        {
            Criterion = ToleranceCriterion.RmsSpotRadius, Compensation = ToleranceCompensation.None, Fields = ToleranceFieldSet.UserDefined,
        },
        new MonteCarloOptions { Trials = 40, Seed = 5 });

    [Fact]
    [Trait("Category", "Slow")]
    public void HighYield_FlattensTheSensitiveSurface_LoweringTheMonteCarloSpread_AtEqualNominal()
    {
        var start = WithMerit(F.Shell(StartRadius, variable: true), highYield: false);
        // The start is at the nominal optimum: its default merit function is zero to rounding (derived).
        var nominalMerit = MeritEvaluator.Evaluate(MeritFunctionSettings.FromSettings(start.MeritFunction, MeritOperandRegistry.CreateDefault(), new List<string>()),
            F.Configurations(start));
        Assert.False(nominalMerit.HasFailures, string.Join(Environment.NewLine, nominalMerit.Errors));
        Assert.True(nominalMerit.Value < 1e-9, $"nominal merit {nominalMerit.Value:R}");

        var run = LocalOptimizer.Run(WithMerit(F.Shell(StartRadius, variable: true), highYield: true), F.NoCatalogs,
            new LocalOptimizationOptions { Cycles = LocalOptimizationCycles.Fixed, CycleCount = 10 });
        double c0 = 1 / StartRadius, c = run.Values.Single();
        Assert.True(Math.Abs(c) < c0, $"the shell's curvature went from {c0:R} to {c:R}: {string.Join(" | ", run.Log)}");
        Assert.True(run.Merit < run.StartMerit);
        var optimised = run.Optimized with { MeritFunction = start.MeritFunction };

        var before = MonteCarlo(start);
        var after = MonteCarlo(optimised);
        Assert.True(before.Failed == 0, $"start: {before.Failed} trials failed, first: {before.Trials.FirstOrDefault(t => !t.Succeeded)?.Failure}");
        Assert.True(after.Failed == 0, $"optimised: {after.Failed} trials failed, first: {after.Trials.FirstOrDefault(t => !t.Succeeded)?.Failure}");
        Assert.True(Math.Abs(after.Nominal - before.Nominal) <= 1e-9,
            $"nominal RMS spot radius {after.Nominal:R} {after.Unit} against {before.Nominal:R}");
        double sBefore = before.Statistics!.StandardDeviation, sAfter = after.Statistics!.StandardDeviation;
        Assert.True(sBefore > 0, "the start's decenters change nothing");
        Assert.True(sAfter < 0.5 * sBefore,
            $"Monte Carlo σ {sAfter:R} {after.Unit} (curvature {c:R}) against {sBefore:R} (curvature {c0:R})");
        Assert.True(after.Statistics.Mean < before.Statistics.Mean);
    }
}
