using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class InductanceTests
{
    // Ruehli's rectangular-bar self-inductance, hand-evaluated for a 10 mm × 1 mm ×
    // 35 µm trace: L = (µ₀/2π)·l·[ln(2l/(w+t)) + 0.5 + (w+t)/(3l)] ≈ 6.99 nH.
    [Fact]
    public void SelfInductance_MatchesRuehliClosedForm()
    {
        double l = PartialInductance.SelfInductance(10e-3, 1e-3, 35e-6);
        Assert.Equal(6.99e-9, l, 0.05e-9);

        // ~0.7 nH/mm is the expected order for a wide PCB trace (below the 1 nH/mm
        // round-wire rule of thumb because the trace is wide).
        Assert.InRange(l / 10e-3, 0.5e-6, 0.9e-6);
    }

    [Fact]
    public void SelfInductance_GrowsWithLengthAndShrinksWithWidth()
    {
        double shortBar = PartialInductance.SelfInductance(5e-3, 1e-3, 35e-6);
        double longBar = PartialInductance.SelfInductance(20e-3, 1e-3, 35e-6);
        Assert.True(longBar > 2 * shortBar, "Longer bar should have more than proportional inductance (ln term).");

        double narrow = PartialInductance.SelfInductance(10e-3, 0.3e-3, 35e-6);
        double wide = PartialInductance.SelfInductance(10e-3, 3e-3, 35e-6);
        Assert.True(narrow > wide, "A narrower trace has higher self-inductance.");
    }

    [Fact]
    public void MutualInductance_IsPositiveBelowSelfAndDecaysWithSeparation()
    {
        double self = PartialInductance.SelfInductance(10e-3, 1e-3, 35e-6);
        double near = PartialInductance.MutualInductanceParallel(10e-3, 2e-3, 1e-3, 35e-6);
        double far = PartialInductance.MutualInductanceParallel(10e-3, 8e-3, 1e-3, 35e-6);

        Assert.True(near > 0 && near < self, "Mutual must be positive and below the self-inductance.");
        Assert.True(far < near, "Mutual coupling decreases with separation.");

        // Hand value at 2 mm separation ≈ 2.99 nH.
        Assert.Equal(2.99e-9, near, 0.05e-9);
    }

    [Fact]
    public void LoopComposer_ReturnPathReducesLoopInductance()
    {
        // Go-and-return: two anti-parallel 10 mm traces 2 mm apart.
        var outbound = new TraceSegment(new(0, 0), new(10e-3, 0), 1e-3, 35e-6);
        var ret = new TraceSegment(new(10e-3, 2e-3), new(0, 2e-3), 1e-3, 35e-6);

        var report = new LoopComposer().Compose(new[] { outbound, ret });

        // Loop L = L₁ + L₂ − 2M < ΣL_self, because the return current subtracts.
        Assert.True(report.LoopInductance > 0, "Loop inductance must be positive.");
        Assert.True(report.LoopInductance < report.TotalSelf,
            "An anti-parallel return path must reduce the loop inductance below the summed self terms.");

        double self = PartialInductance.SelfInductance(10e-3, 1e-3, 35e-6);
        double mutual = PartialInductance.MutualInductanceParallel(10e-3, 2e-3, 1e-3, 35e-6);
        Assert.Equal(2 * self - 2 * mutual, report.LoopInductance, 0.1e-9);
        Assert.NotEmpty(report.Assumptions);
    }

    [Fact]
    public void Segmenter_ExtractsTraceSegmentsFromDraws()
    {
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.25*%\nD10*\n" +
            "X0Y0D02*\nX5000000Y0D01*\nX5000000Y5000000D01*\nM02*");
        var segments = new TraceSegmenter().Segment(doc, 35e-6);

        Assert.Equal(2, segments.Count);
        Assert.Equal(5e-3, segments[0].Length, 1e-9);
        Assert.Equal(0.25e-3, segments[0].Width, 1e-12);
        Assert.Equal(35e-6, segments[0].Thickness, 1e-12);
    }
}
