using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class MutualCouplingTests
{
    private const double W = 4e-4, T = 35e-6;

    private static TraceSegment3D Seg(double x1, double y1, double x2, double y2) =>
        new(new Vector3D(x1 * 1e-3, y1 * 1e-3, 0), new Vector3D(x2 * 1e-3, y2 * 1e-3, 0), W, T);

    [Fact]
    public void ParallelChains_MutualMatchesTheClosedForm_AndKIsInRange()
    {
        var a = new[] { Seg(0, 0, 10, 0) };
        var b = new[] { Seg(0, 2, 10, 2) };

        var analyzer = new MutualCouplingAnalyzer();
        double m = analyzer.MutualBetween(a, b);
        double expected = PartialInductance.MutualInductanceParallel(10e-3, 2e-3, W, T);
        Assert.Equal(expected, m, expected * 1e-12);

        var report = analyzer.Analyze(a, b);
        double self = PartialInductance.SelfInductance(10e-3, W, T);
        Assert.Equal(m / self, report.CouplingK, 1e-12);
        Assert.InRange(report.CouplingK, 0.0, 1.0);
    }

    [Fact]
    public void PerpendicularChains_HaveExactlyZeroMutualAndCoupling()
    {
        var a = new[] { Seg(0, 0, 10, 0) };
        var b = new[] { Seg(15, 0, 15, 10) };
        var report = new MutualCouplingAnalyzer().Analyze(a, b);
        Assert.Equal(0.0, report.MutualHenries);
        Assert.Equal(0.0, report.CouplingK);
    }

    [Fact]
    public void SeriesLoopClosure_EqualsTheSingleChainGoAndReturnComposition()
    {
        // The historic go-and-return pair, split into two "nets": the analyzer's series
        // loop must equal composing both segments as ONE chain — proving the
        // endpoint-proximity pairing and sign convention against the tested composition.
        var go = Seg(0, 0, 10, 0);
        var back = Seg(10, 2, 0, 2);            // anti-parallel; starts near go's end

        var report = new MutualCouplingAnalyzer().Analyze(new[] { go }, new[] { back });
        double composed = new LoopComposer().Compose(new[] { go, back }).LoopInductance;
        Assert.True(report.ReturnTraversedForward);
        Assert.Equal(composed, report.LoopInductanceHenries, Math.Abs(composed) * 1e-15);
    }

    [Fact]
    public void SeriesLoop_IsInvariantToTheReturnChainsDrawDirection()
    {
        // Same copper, opposite draw direction: the pairing flips the traversal flag and
        // the mutual's sign together, so the physical loop value cannot change.
        var go = new[] { Seg(0, 0, 10, 0) };
        var backDrawnLeft = new[] { Seg(10, 2, 0, 2) };
        var backDrawnRight = new[] { Seg(0, 2, 10, 2) };

        var analyzer = new MutualCouplingAnalyzer();
        var left = analyzer.Analyze(go, backDrawnLeft);
        var right = analyzer.Analyze(go, backDrawnRight);
        Assert.True(left.ReturnTraversedForward);
        Assert.False(right.ReturnTraversedForward);
        Assert.Equal(left.LoopInductanceHenries, right.LoopInductanceHenries,
            Math.Abs(left.LoopInductanceHenries) * 1e-15);
        // And the loop is smaller than the open pair (the return opposes).
        Assert.True(left.LoopInductanceHenries
                    < 2 * PartialInductance.SelfInductance(10e-3, W, T));
    }
}
