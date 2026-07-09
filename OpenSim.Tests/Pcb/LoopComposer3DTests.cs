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
        // The equal-antiparallel case where the new kernel is algebraically identical to
        // the legacy parallel formula — the historic LoopComposer contract.
        const double l = 10e-3, d = 2e-3, w = 4e-4, t = 35e-6;
        var pair = new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, 0), new Vector3D(l, 0, 0), w, t),
            new TraceSegment3D(new Vector3D(l, d, 0), new Vector3D(0, d, 0), w, t)
        };
        var report = new LoopComposer().Compose(pair);

        double self = PartialInductance.SelfInductance(l, w, t);
        double mutual = PartialInductance.MutualInductanceParallel(l, d, w, t);
        Assert.Equal(2 * self - 2 * mutual, report.LoopInductance, Math.Abs(report.LoopInductance) * 1e-9);
    }
}
