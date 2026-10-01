using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class InductanceTests
{
    // The reference trace, 10 mm × 1 mm × 35 µm. Ruehli's slender-bar form
    // L = (µ₀/2π)·l·[ln(2l/(w+t)) + 0.5 + (w+t)/(3l)] hand-evaluates to 6.99 nH; the
    // exact uniform-current value (Hoer–Love, evaluated to 60 digits outside this
    // code base) is 6.98638222352 nH — 0.076 % below it, inside the same band.
    [Fact]
    public void SelfInductance_OfTheReferenceTrace_IsTheExactUniformCurrentValue()
    {
        double l = PartialInductance.SelfInductance(10e-3, 1e-3, 35e-6);
        Assert.Equal(6.99e-9, l, 0.05e-9);
        Assert.Equal(6.9863822235186e-9, l, 6.99e-9 * 1e-10);

        double ruehli = 2e-7 * 10e-3 * (Math.Log(2 * 10e-3 / 1.035e-3) + 0.5 + 1.035e-3 / 30e-3);
        Assert.True(l < ruehli, "A slender straight bar sits BELOW the slender-bar formula.");
        Assert.Equal(ruehli, l, ruehli * 1e-3);

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
        double near = SideBySide(2e-3);
        double far = SideBySide(8e-3);

        Assert.True(near > 0 && near < self, "Mutual must be positive and below the self-inductance.");
        Assert.True(far < near, "Mutual coupling decreases with separation.");

        // Hand value at 2 mm separation: the centre FILAMENTS give
        // (µ₀/2π)·l·[asinh(l/d) − √(1+(d/l)²) + d/l] = 2.985 nH. Side by side, the 1 mm
        // widths put more copper nearer than farther on the log scale, so the exact
        // value sits ABOVE the filament one: 3.02837222806 nH (60-digit evaluation).
        Assert.Equal(2.99e-9, near, 0.05e-9);
        Assert.Equal(3.028372228056e-9, near, 3.03e-9 * 1e-10);
        double filament = 2e-7 * 10e-3 * (Math.Asinh(5) - Math.Sqrt(1 + 0.04) + 0.2);
        Assert.True(near > filament);
    }

    /// <summary>Two 10 mm × 1 mm × 35 µm bars side by side at the given centre pitch.</summary>
    private static double SideBySide(double pitch) =>
        PartialInductance.BarBarMutual(1e-3, 35e-6, 10e-3, 1e-3, 35e-6, 10e-3, pitch, 0, 0);

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
        double mutual = SideBySide(2e-3);
        Assert.Equal(2 * self - 2 * mutual, report.LoopInductance, report.LoopInductance * 1e-12);
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
