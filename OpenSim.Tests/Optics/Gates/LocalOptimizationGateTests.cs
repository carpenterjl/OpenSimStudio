using OpenSim.Core.Numerics.Optimization;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using F = OpenSim.Tests.Optics.LocalOptimizationFixtures;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-57's gate (plan): "A perturbed doublet returns to its known optimum; a singlet bends to the
/// Coddington minimum-spherical shape; results bitwise identical at any thread count;
/// cancellation leaves the best design so far." No design is taken from a source: the doublet's
/// optimum is built by optimizing it once in the test, and the singlet's shape is derived below.
/// </summary>
public sealed class LocalOptimizationGateTests
{
    private static double Relative(double expected, double actual) => Math.Abs(actual - expected) / Math.Abs(expected);

    /// <summary>
    /// The doublet optimized once from its start gives the optimum x*; each variable is then
    /// moved by a few percent and the optimizer run again. It must come back to x*.
    /// <para>
    /// Tolerances. The problem has two variables and six rows; the power and the third-order
    /// spherical aberration each depend strongly on both curvatures, so the minimum is isolated
    /// and the Jacobian well conditioned. The residuals are ray coordinates of about 1e-3 mm at
    /// most, computed to about 1e-14 mm, and the solver stops when a step changes the cost by
    /// less than 1e-14 of itself (<see cref="LeastSquaresOptions.CostTolerance"/>): the merit
    /// value is then resolved to far better than 1e-8 relative, and the curvatures, whose
    /// error enters the merit quadratically, to better than 1e-5 relative.
    /// </para>
    /// </summary>
    [Fact]
    public void APerturbedDoublet_ReturnsToItsOptimum()
    {
        var first = LocalOptimizer.Run(F.Doublet(), F.NoCatalogs);
        Assert.Equal(LocalOptimizationStop.Converged, first.Stop);
        Assert.True(first.Merit < first.StartMerit);
        var optimum = first.Values.ToArray();

        var perturbed = LensVariables.Apply(first.Optimized, first.Variables, new[] { optimum[0] * 1.03, optimum[1] * 0.97 });
        var second = LocalOptimizer.Run(perturbed, F.NoCatalogs);
        Assert.Equal(LocalOptimizationStop.Converged, second.Stop);
        Assert.True(second.StartMerit > first.Merit * (1 + 1e-3), "the perturbation must spoil the design noticeably");
        for (int i = 0; i < optimum.Length; i++)
            Assert.True(Relative(optimum[i], second.Values[i]) < 1e-5,
                $"{first.Variables[i].Label}: {second.Values[i]:R} against the optimum {optimum[i]:R}");
        Assert.True(Relative(first.Merit, second.Merit) < 1e-8, $"merit {second.Merit:R} against {first.Merit:R}");
    }

    /// <summary>
    /// The singlet bends to the Coddington shape of least spherical aberration.
    /// <para>
    /// Derived (no source open; the derivation is complete here). A thin lens of relative index
    /// N in air, object at infinity, marginal ray height h = 1 at both surfaces, front and back
    /// curvatures c₁ and c₂ = c₁ − d. With the paraxial refraction n′u′ = nu − h(n′ − n)c, the
    /// refraction invariant A = n(hc + u) and the Seidel spherical-aberration sum
    /// S_I = −Σ A²h Δ(u/n):
    /// surface 1 (1 → N, u = 0): A₁ = c₁, u₁′ = −(N − 1)c₁/N, so S₁ = (N − 1)c₁³/N²;
    /// surface 2 (N → 1): A₂ = N(c₂ + u₁′) = c₁ − Nd, Δ(u/n) = −(N − 1)(d − c₁/N²), so
    /// S₂ = (N − 1)(c₁ − Nd)²(d − c₁/N²).
    /// Their sum is (N − 1)[c₁²d(1 + 2/N) − c₁d²(2N + 1) + N²d³], least where its c₁-derivative
    /// vanishes: c₁ = dN(2N + 1)/(2(N + 2)). The shape factor X = (c₁ + c₂)/(c₁ − c₂) = 2c₁/d − 1 is
    /// then X = 2(N² − 1)/(N + 2), 5/7 for N = 1.5 (so R₂ = −6R₁).
    /// </para>
    /// <para>
    /// Tolerances. The lens is 0.2 mm thick at f = 100 mm. To first order, thickness lowers the
    /// marginal ray height at surface 2 by t(N − 1)c₁/N ≈ 0.11 % here, so surface 2's term, which
    /// goes as h⁴, by about 0.46 %; moving the minimum of the quadratic S(c₁) by that share of its
    /// slope gives |ΔX| ≈ 0.002. The aperture is F/50, so fifth-order aberration is a few 1e-4 of
    /// the third. The test allows |ΔX| ≤ 0.015, several times the estimate. Against an independent
    /// Brent minimisation of the same real-ray merit over c₁ the optimizer must agree to 1e-3
    /// relative in c₁: the merit is quadratic about its minimum, so both searches resolve c₁ only
    /// to about the square root of the merit's rounding.
    /// </para>
    /// </summary>
    [Fact]
    public void ASinglet_BendsToTheCoddingtonShapeOfLeastSphericalAberration()
    {
        var start = F.Singlet(r1: 100);   // equiconvex: far from the best shape
        var result = LocalOptimizer.Run(start, F.NoCatalogs);
        Assert.NotEqual(LocalOptimizationStop.Cancelled, result.Stop);
        Assert.True(result.Merit < result.StartMerit);

        var lens = ProjectLens.From(result.Optimized, F.NoCatalogs)!;
        double c1 = lens.Surfaces[1].Curvature, c2 = lens.Surfaces[2].Curvature;
        double shape = (c1 + c2) / (c1 - c2);
        double n = F.SingletIndex;
        double coddington = 2 * (n * n - 1) / (n + 2);   // derived above
        Assert.True(Math.Abs(shape - coddington) <= 0.015, $"shape factor {shape:R} against the thin-lens {coddington:R}");

        // The F/# solve kept the focal length, so the bending did not trade power for aberration.
        var system = SystemDataSettings.FromSettings(result.Optimized.SystemData, new List<string>());
        double efl = OpenSim.Optics.Paraxial.ParaxialSystem.Create(lens.ToParaxialLens(), system).EffectiveFocalLength!.Value;
        Assert.True(Relative(F.SingletFocalLength * 1e-3, efl) < 1e-9, $"focal length {efl:R} m");

        // An independent oracle: Brent's method on the same merit as a function of c₁ (1/mm).
        var function = MeritFunctionSettings.FromSettings(start.MeritFunction, MeritOperandRegistry.CreateDefault(), new List<string>());
        var variables = LensVariables.Find(start);
        double Merit(double c) => MeritEvaluator.Evaluate(function,
            new ProjectMeritConfigurations(LensVariables.Apply(start, variables, new[] { c }), F.NoCatalogs)).Value;
        var (brent, _) = BrentMinimizer.Minimize(Merit, 0.005, 0.03);
        Assert.True(Relative(brent, result.Values[0]) < 1e-3, $"c1 {result.Values[0]:R}/mm against Brent's {brent:R}/mm");
    }

    /// <summary>The same run gives the same bits with one thread, four, and no limit, for both
    /// algorithms (DLS differences its columns in parallel; both trace rays in parallel).</summary>
    [Theory]
    [InlineData(LocalOptimizationAlgorithm.DampedLeastSquares)]
    [InlineData(LocalOptimizationAlgorithm.OrthogonalDescent)]
    public void Results_AreBitwiseIdenticalAtAnyThreadCount(LocalOptimizationAlgorithm algorithm)
    {
        LocalOptimizationResult Run(int? threads) => LocalOptimizer.Run(F.Doublet(), F.NoCatalogs, new LocalOptimizationOptions
        {
            Algorithm = algorithm, Cycles = LocalOptimizationCycles.Fixed, CycleCount = 4, MaxDegreeOfParallelism = threads,
        });
        var one = Run(1);
        Assert.True(one.Merit < one.StartMerit);
        foreach (int? threads in new int?[] { 4, null })
        {
            var other = Run(threads);
            Assert.Equal(one.Values.Select(BitConverter.DoubleToInt64Bits), other.Values.Select(BitConverter.DoubleToInt64Bits));
            Assert.Equal(BitConverter.DoubleToInt64Bits(one.Merit), BitConverter.DoubleToInt64Bits(other.Merit));
            Assert.Equal(one.Cycles, other.Cycles);
            Assert.Equal(one.Evaluations, other.Evaluations);
        }
    }

    /// <summary>Stop after the second improving cycle: the result is the design that cycle
    /// reported (the best so far, since each accepted step lowers the merit), written into the
    /// optics record, and better than the start.</summary>
    [Theory]
    [InlineData(LocalOptimizationAlgorithm.DampedLeastSquares)]
    [InlineData(LocalOptimizationAlgorithm.OrthogonalDescent)]
    public void Cancellation_LeavesTheBestDesignSoFar(LocalOptimizationAlgorithm algorithm)
    {
        using var cancel = new CancellationTokenSource();
        var reports = new List<LocalOptimizationProgress>();
        var result = LocalOptimizer.Run(F.Doublet(), F.NoCatalogs, new LocalOptimizationOptions
        {
            Algorithm = algorithm,
            CancellationToken = cancel.Token,
            Progress = p => { reports.Add(p); if (reports.Count == 2) cancel.Cancel(); },
        });
        Assert.Equal(LocalOptimizationStop.Cancelled, result.Stop);
        Assert.True(result.Cancelled);
        Assert.Equal(2, reports.Count);
        Assert.True(reports[1].Merit < reports[0].Merit && reports[0].Merit < result.StartMerit);
        var last = reports[^1];
        Assert.Equal(last.Values.Select(BitConverter.DoubleToInt64Bits), result.Values.Select(BitConverter.DoubleToInt64Bits));
        // The progress merit is √(Σr²) in the solver's order; the result's is the evaluator's sum: same to rounding.
        Assert.True(Relative(last.Merit, result.Merit) < 1e-12, $"{result.Merit:R} against {last.Merit:R}");
        // Written into the record: a radius is stored as 1/c, so it reads back to rounding.
        var written = LensVariables.Values(result.Optimized, result.Variables);
        for (int i = 0; i < written.Length; i++) Assert.True(Relative(result.Values[i], written[i]) < 1e-15);
    }
}
