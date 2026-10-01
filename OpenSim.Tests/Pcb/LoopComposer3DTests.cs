using OpenSim.Core.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The 3D chain composition against Grover's closed-form rectangle loop, and the
/// 2D-lift contract (planar chains must compose identically through either API).
/// </summary>
public class LoopComposer3DTests
{
    // ------------------------------------------------------------------
    // Rectangle loop of round wire — the classic full-loop benchmark. The composed
    // 2·L_a + 2·L_b − 2M(a; d=b) − 2M(b; d=a) must match Grover's closed form
    // L = (µ₀/π)[a·ln(2a/r) + b·ln(2b/r) − a·asinh(a/b) − b·asinh(b/a)
    //            + 2√(a²+b²) − 2(a+b) + (a+b)/4]
    // (the (a+b)/4 term is the wires' internal inductance, carried by the e^(−¼)
    // self-GMD). Adjacent sides are perpendicular ⇒ exactly zero mutual. The only
    // difference is the exact-vs-asymptotic self form, ~(µ₀/2π)·r per corner ≈ 0.04%.
    // ------------------------------------------------------------------
    [Fact]
    public void RectangleLoop_MatchesGroverClosedForm()
    {
        const double a = 40e-3, b = 20e-3, r = 0.5e-3;
        var chain = new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(a, 0, 0), 2 * r, 0, SegmentProfile.RoundWire),
            new TraceSegment3D(new Vector3D(a, 0, 0), new Vector3D(a, b, 0), 2 * r, 0, SegmentProfile.RoundWire),
            new TraceSegment3D(new Vector3D(a, b, 0), new Vector3D(0, b, 0), 2 * r, 0, SegmentProfile.RoundWire),
            new TraceSegment3D(new Vector3D(0, b, 0), new Vector3D(0, 0, 0), 2 * r, 0, SegmentProfile.RoundWire)
        };

        var report = new LoopComposer().Compose(chain);

        double mu0OverPi = 4e-7;
        double grover = mu0OverPi * (
            a * Math.Log(2 * a / r) + b * Math.Log(2 * b / r)
            - a * Math.Asinh(a / b) - b * Math.Asinh(b / a)
            + 2 * Math.Sqrt(a * a + b * b) - 2 * (a + b) + (a + b) / 4);

        Assert.Equal(grover, report.LoopInductance, grover * 1e-2);
        Assert.True(report.LoopInductance < report.TotalSelf,
            "The opposing return sides must reduce the loop below the self sum.");
        Assert.Equal(4, report.SelfInductances.Count);
    }

    [Fact]
    public void PlanarChain_ComposesIdenticallyThroughThe2DAnd3DApis()
    {
        // The 2D overload IS the 3D path at z = 0 — bitwise identical by construction.
        var chain2D = new[]
        {
            new TraceSegment(new Point2(0, 0), new Point2(10e-3, 0), 4e-4, 35e-6),
            new TraceSegment(new Point2(10e-3, 0), new Point2(10e-3, 5e-3), 4e-4, 35e-6),
            new TraceSegment(new Point2(10e-3, 5e-3), new Point2(2e-3, 5e-3), 4e-4, 35e-6)
        };
        var chain3D = chain2D.Select(s => new TraceSegment3D(
            new Vector3D(s.Start.X, s.Start.Y, 0), new Vector3D(s.End.X, s.End.Y, 0),
            s.Width, s.Thickness)).ToList();

        var composer = new LoopComposer();
        Assert.Equal(composer.Compose(chain2D).LoopInductance,
                     composer.Compose(chain3D).LoopInductance);
        Assert.Equal(composer.Compose(chain2D).LoopInductance,
                     composer.Compose(chain2D).LoopInductance);   // deterministic
    }

    [Fact]
    public void GoAndReturnPair_StillReducesToTwoSelfMinusTwoMutual()
    {
        // The equal-antiparallel pair: the composer's frame arithmetic must hand the bar
        // kernel exactly the side-by-side geometry — the historic LoopComposer contract.
        const double l = 10e-3, d = 2e-3, w = 4e-4, t = 35e-6;
        var pair = new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(l, 0, 0), w, t),
            new TraceSegment3D(new Vector3D(l, d, 0), new Vector3D(0, d, 0), w, t)
        };
        var report = new LoopComposer().Compose(pair);

        double self = PartialInductance.SelfInductance(l, w, t);
        double mutual = PartialInductance.BarBarMutual(w, t, l, w, t, l, d, 0, 0);
        Assert.Equal(2 * self - 2 * mutual, report.LoopInductance, Math.Abs(report.LoopInductance) * 1e-9);
    }

    // ------------------------------------------------------------------
    // Subdivision invariance. The inductance of a piece of copper cannot depend on how
    // many segments the file drew it with. It did: a bar self term with a section in it
    // and collinear mutuals without one composed the 10 × 1 × 0.035 mm bar to 6.99,
    // 7.61 and 13.82 nH as 1, 10 and 100 segments. One kernel for every parallel bar
    // pair makes the sum a Riemann partition of a single double integral.
    // ------------------------------------------------------------------

    private static TraceSegment3D[] StraightBar(Vector3D start, Vector3D end, int segments,
        double width, double thickness) =>
        Enumerable.Range(0, segments)
            .Select(i => new TraceSegment3D(
                start + (end - start) * ((double)i / segments),
                start + (end - start) * ((double)(i + 1) / segments),
                width, thickness))
            .ToArray();

    private static double Compose(Vector3D start, Vector3D end, int segments, double width, double thickness) =>
        new LoopComposer().Compose(StraightBar(start, end, segments, width, thickness)).LoopInductance;

    [Theory]
    [InlineData(10e-3)]
    [InlineData(100e-3)]
    public void AStraightBar_ComposesToOneValue_HoweverItIsSubdivided(double length)
    {
        const double w = 1e-3, t = 35e-6;
        var start = new Vector3D(0, 0, 0);
        var end = new Vector3D(length, 0, 0);
        double whole = Compose(start, end, 1, w, t);

        Assert.Equal(PartialInductance.SelfInductance(length, w, t), whole);
        // 1e-9: the quadrature kernel carries no (L/s)⁴ conditioning, so the gate is
        // three and four decades inside the 1e-6 / 1e-5 the closed form alone allows.
        Assert.Equal(whole, Compose(start, end, 10, w, t), whole * 1e-9);
        Assert.Equal(whole, Compose(start, end, 100, w, t), whole * 1e-9);
    }

    [Fact]
    public void AStraightBar_IsSubdivisionInvariant_OnADiagonal_AndOffTheOrigin()
    {
        // The pair frame is built from each segment's own endpoints; on a 3-4-5 diagonal
        // at board height those carry rounding in every coordinate.
        const double w = 0.4e-3, t = 35e-6;
        var start = new Vector3D(12.3e-3, -4.1e-3, 1.6e-3);
        var end = start + new Vector3D(6e-3, 8e-3, 0);
        double whole = Compose(start, end, 1, w, t);

        Assert.Equal(PartialInductance.SelfInductance(10e-3, w, t), whole, whole * 1e-12);
        Assert.Equal(whole, Compose(start, end, 7, w, t), whole * 1e-9);
        Assert.Equal(whole, Compose(start, end, 50, w, t), whole * 1e-9);
    }

    [Fact]
    public void AStubbyBar_IsSubdivisionInvariant_AcrossBothKernelRegimes()
    {
        // 2 × 0.5 × 0.5 mm. Whole, it is a closed-form bar; in 16 pieces the near pairs
        // are closed-form and the far ones quadrature; in 40 every pair is quadrature.
        // The three sums agree only if the two evaluations are one function.
        const double w = 0.5e-3, t = 0.5e-3;
        var start = new Vector3D(0, 0, 0);
        var end = new Vector3D(2e-3, 0, 0);
        double whole = Compose(start, end, 1, w, t);

        Assert.True(RectangularBarKernel.ClosedFormIsCertified(w, t, 2e-3, w, t, 2e-3, 0, 0, 0));
        double piece = 2e-3 / 16;
        Assert.True(RectangularBarKernel.ClosedFormIsCertified(w, t, piece, w, t, piece, 0, 0, piece));
        Assert.False(RectangularBarKernel.ClosedFormIsCertified(w, t, piece, w, t, piece, 0, 0, 15 * piece));
        Assert.False(RectangularBarKernel.ClosedFormIsCertified(w, t, 0.05e-3, w, t, 0.05e-3, 0, 0, 0.05e-3));

        Assert.Equal(whole, Compose(start, end, 16, w, t), whole * 1e-8);
        Assert.Equal(whole, Compose(start, end, 40, w, t), whole * 1e-8);
    }

    [Fact]
    public void SplittingOneLegOfAChain_DoesNotChangeTheChain()
    {
        // An L: the straight leg drawn as one 8 mm segment or as 4 + 4 mm.
        const double w = 4e-4, t = 35e-6;
        var corner = new Vector3D(8e-3, 0, 0);
        var up = new TraceSegment3D(corner, new Vector3D(8e-3, 5e-3, 0), w, t);
        var composer = new LoopComposer();
        double one = composer.Compose(new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, 0), corner, w, t), up
        }).LoopInductance;
        double two = composer.Compose(new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(4e-3, 0, 0), w, t),
            new TraceSegment3D(new Vector3D(4e-3, 0, 0), corner, w, t), up
        }).LoopInductance;
        Assert.Equal(one, two, one * 1e-9);
    }

    // ------------------------------------------------------------------
    // What the composer refuses.
    // ------------------------------------------------------------------

    [Fact]
    public void AComposedValueThatIsNotPositive_IsRefused()
    {
        // Two anti-parallel 2 mm-diameter wires with their axes 0.1 mm apart: the same
        // copper carrying current both ways. The mutual exceeds the self term and the
        // "loop" comes out negative — refused, not returned.
        var there = new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(50e-3, 0, 0), 2e-3, 0,
            SegmentProfile.RoundWire);
        var back = new TraceSegment3D(new Vector3D(50e-3, 0.1e-3, 0), new Vector3D(0, 0.1e-3, 0), 2e-3, 0,
            SegmentProfile.RoundWire);
        var ex = Assert.Throws<InvalidOperationException>(() => new LoopComposer().Compose(new[] { there, back }));
        Assert.Contains("not a physical value", ex.Message);
    }

    [Fact]
    public void CollinearOverlappingBars_AreRefusedAsDegenerate()
    {
        var first = new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(10e-3, 0, 0), 4e-4, 35e-6);
        var second = new TraceSegment3D(new Vector3D(5e-3, 0, 0), new Vector3D(15e-3, 0, 0), 4e-4, 35e-6);
        var ex = Assert.Throws<InvalidOperationException>(() => new LoopComposer().Compose(new[] { first, second }));
        Assert.Contains("degenerate", ex.Message);
    }

    [Fact]
    public void ParallelBarsAlongTheBoardNormal_AreRefused_TheirSectionHasNoOrientation()
    {
        // A bar's section is width-in-plane by thickness-along-z; standing on end it has
        // neither. Its self term does not care, a pair's mutual does.
        var first = new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(0, 0, 1e-3), 4e-4, 35e-6);
        var second = new TraceSegment3D(new Vector3D(1e-3, 0, 0), new Vector3D(1e-3, 0, 1e-3), 4e-4, 35e-6);
        Assert.True(new LoopComposer().Compose(new[] { first }).LoopInductance > 0);
        var ex = Assert.Throws<InvalidOperationException>(() => new LoopComposer().Compose(new[] { first, second }));
        Assert.Contains("section orientation", ex.Message);
    }

    [Fact]
    public void Assumptions_NameTheBarKernel_AndWhatIsStillNotModelled()
    {
        var report = new LoopComposer().Compose(StraightBar(
            new Vector3D(0, 0, 0), new Vector3D(10e-3, 0, 0), 2, 1e-3, 35e-6));
        Assert.Contains(report.Assumptions, a => a.Contains("finite-section kernel"));
        Assert.Contains(report.Assumptions, a => a.Contains("bends are not modelled"));
        Assert.DoesNotContain(report.Assumptions, a => a.Contains("geometric-mean-distance"));
    }

    [Fact]
    public void RoundProfiles_AreUntouched_ByTheBarKernel()
    {
        // A circle's GMD is its centre distance: round wires and via barrels stay on
        // the filament kernel, bit for bit.
        var a = new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(10e-3, 0, 0), 0.4e-3, 0, SegmentProfile.RoundWire);
        var b = new TraceSegment3D(new Vector3D(0, 2e-3, 0), new Vector3D(10e-3, 2e-3, 0), 0.4e-3, 0, SegmentProfile.RoundWire);
        var bar = new TraceSegment3D(new Vector3D(0, 2e-3, 0), new Vector3D(10e-3, 2e-3, 0), 0.4e-3, 35e-6);
        double filament = FilamentMutual.Between(a.Start, a.End, b.Start, b.End);
        Assert.Equal(filament, LoopComposer.MutualTerm(a, b));
        Assert.Equal(filament, LoopComposer.MutualTerm(a, bar));       // mixed pair: filament too
    }
}
