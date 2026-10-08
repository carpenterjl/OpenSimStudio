using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Si;

/// <summary>
/// The side-wall model (<see cref="RlgcModel.SideWalls"/>): traces solved as the trapezoids
/// they are. Every oracle is exact, taken where the edges cancel: the parallel-plate capacitance
/// per width of a wide trace, which sees the top face's real height in a stripline, and the
/// parallel-plate capacitance per height between the side walls of two tall traces close
/// together, which is the coupling the effective-width model cannot carry.
/// </summary>
public class SideWallTests
{
    private readonly ITestOutputHelper _output;
    public SideWallTests(ITestOutputHelper output) => _output = output;

    private const double Epsilon0 = 8.8541878128e-12;
    private static readonly RlgcModel Walls = RlgcModel.Kernel with { SideWalls = true };

    private static TraceCrossSection Trace(double center, double width, double thickness) =>
        TraceCrossSection.Copper(center, width, thickness);

    private static double[,] C(CoupledLineCrossSection section, int panels = 48) =>
        RlgcExtractor.Extract(section, Walls, panels).CapacitanceFaradsPerMeter;

    [Fact]
    public void AWideMicrostrip_GainsTheParallelPlateCapacitancePerWidth()
    {
        // Between two wide traces the edges are alike: the difference is ε·Δw/h, but for the
        // charge on the top face, which over an open top decays only as 1/x² from each edge and
        // leaves a remainder ∝ h/w (0.36 % at w/h = 30, 0.19 % at 60, 0.09 % at 120, measured,
        // and the same in the strip solver). At w/h = 120:
        const double h = 0.05e-3, epsR = 4.4, t = 35e-6;
        var stack = new LayeredStackup(new[] { new LayeredStackup.Layer(epsR, 0, h) });
        double c1 = C(new CoupledLineCrossSection(stack, 0, new[] { Trace(0, 6e-3, t) }))[0, 0];
        double c2 = C(new CoupledLineCrossSection(stack, 0, new[] { Trace(0, 7e-3, t) }))[0, 0];
        double expected = Epsilon0 * epsR * 1e-3 / h;
        _output.WriteLine($"ΔC {c2 - c1:e5} F/m against ε·Δw/h {expected:e5} ({((c2 - c1) / expected - 1) * 100:f3} %)");
        Assert.Equal(expected, c2 - c1, 0.002 * expected);
    }

    [Fact]
    public void AWideStripline_SeesItsTopFaceAtItsThickness()
    {
        // Between two planes the difference is ε/h_below + ε/h_above, h_above the dielectric over
        // the top face: the planes are h_below + t + h_above apart, as in a board stackup, and
        // the strip's two faces see them at their real distances.
        const double below = 0.2e-3, above = 0.25e-3, t = 70e-6, epsBelow = 4.0, epsAbove = 3.2;
        var stack = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(epsBelow, 0, below), new LayeredStackup.Layer(epsAbove, 0, above)
        });
        double c1 = C(new CoupledLineCrossSection(stack, 0, new[] { Trace(0, 4e-3, t) }, topGround: true))[0, 0];
        double c2 = C(new CoupledLineCrossSection(stack, 0, new[] { Trace(0, 5e-3, t) }, topGround: true))[0, 0];
        double expected = Epsilon0 * 1e-3 * (epsBelow / below + epsAbove / above);
        _output.WriteLine($"ΔC {c2 - c1:e5} F/m against ε/h₁ + ε/h₂ {expected:e5} ({((c2 - c1) / expected - 1) * 100:f3} %)");
        Assert.Equal(expected, c2 - c1, 0.002 * expected);
    }

    [Fact]
    public void TwoTallTracesCloseTogether_CoupleThroughTheirSideWalls()
    {
        // Side walls s apart: raising both traces adds ε₀·Δt/s of mutual capacitance through the
        // air between the walls, plus a little through the outside of the taller pair that does
        // not depend on s. Measured, the excess is 0.65 / 0.35 / 0.18 % at s = 20 / 10 / 5 µm:
        // proportional to s, as that says. Two gaps take it out: 2·r(5 µm) − r(10 µm) → 1.
        // (Under a top plane the fringe over the gap also moves as the tops come closer to it,
        // −0.87 % at 20 µm; the open top keeps that out of the oracle.)
        const double epsR = 3.5, w = 0.3e-3;
        var stack = new LayeredStackup(new[] { new LayeredStackup.Layer(epsR, 0, 0.3e-3) });
        double Ratio(double s)
        {
            double Mutual(double t) => -C(new CoupledLineCrossSection(stack, 0, new[]
            {
                Trace(-(w + s) / 2, w, t), Trace((w + s) / 2, w, t)
            }))[0, 1];
            return (Mutual(0.15e-3) - Mutual(0.10e-3)) / (Epsilon0 * 0.05e-3 / s);
        }
        double r10 = Ratio(10e-6), r5 = Ratio(5e-6);
        _output.WriteLine($"ΔC_m / (ε₀·Δt/s): {r10:f5} at 10 µm, {r5:f5} at 5 µm, extrapolated {2 * r5 - r10:f5}");
        Assert.Equal(1.0, 2 * r5 - r10, 1e-3);
        Assert.InRange(r5, 1.0, 1.004);
    }

    [Fact]
    public void AThickPairInOneDielectric_IsTem()
    {
        // One dielectric between two planes: every entry of C is εr times the air solve's.
        const double epsR = 3.8;
        var stack = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(epsR, 0, 0.2e-3), new LayeredStackup.Layer(epsR, 0, 0.2e-3)
        });
        var section = new CoupledLineCrossSection(stack, 0, new[]
        {
            Trace(-0.15e-3, 0.1e-3, 35e-6), Trace(0.15e-3, 0.18e-3, 35e-6) with { TopWidthMeters = 0.15e-3 }
        }, topGround: true);
        var rlgc = RlgcExtractor.Extract(section, Walls);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.Equal(epsR * rlgc.AirCapacitanceFaradsPerMeter[i, j], rlgc.CapacitanceFaradsPerMeter[i, j],
                    1e-6 * rlgc.CapacitanceFaradsPerMeter[0, 0]);
    }

    [Fact]
    public void AnEtchedTrace_LiesBetweenItsTopAndItsBase()
    {
        // A conductor that contains another has the larger capacitance: the trapezoid's lies
        // between the rectangles of its top and of its base width.
        var spec = new LineSpec { WidthMeters = 0.2e-3, TopWidthMeters = 0.16e-3, ThicknessMeters = 35e-6,
            HeightMeters = 0.1e-3, LossTangent = 0, Model = Walls };
        double Cap(LineSpec s) => ImpedanceCalculator.Solve(s).Rlgc.CapacitanceFaradsPerMeter[0, 0];
        double trapezoid = Cap(spec);
        double top = Cap(spec with { WidthMeters = 0.16e-3, TopWidthMeters = null });
        double bottom = Cap(spec with { TopWidthMeters = null });
        _output.WriteLine($"C top-width {top:e5}, trapezoid {trapezoid:e5}, base-width {bottom:e5} F/m");
        Assert.InRange(trapezoid, top * (1 + 1e-4), bottom * (1 - 1e-4));
        Assert.Contains(ImpedanceCalculator.Solve(spec).Assumptions, a => a.Contains("solved as that trapezoid"));
    }

    [Theory]
    [InlineData(0.3e-3, 0.2e-3, 35e-6, false)]
    [InlineData(0.15e-3, 0.1e-3, 35e-6, false)]
    [InlineData(0.1e-3, 0.15e-3, 17e-6, true)]
    public void AgainstTheEffectiveWidth_WhereThatHolds(double w, double h, double t, bool stripline)
    {
        // Information for the owner's choice of default: the side-wall solve against the
        // effective-width model (Hammerstad–Jensen over one plane, Wheeler between two) on
        // ordinary single lines, where that correction is meant to hold.
        var spec = new LineSpec
        {
            Structure = stripline ? LineStructure.Stripline : LineStructure.Microstrip,
            WidthMeters = w, ThicknessMeters = t, HeightMeters = h, UpperHeightMeters = h, LossTangent = 0, UpperLossTangent = 0,
            Model = RlgcModel.Kernel with { ThicknessCorrection = true }
        };
        double width = ImpedanceCalculator.Solve(spec).ImpedanceOhms;
        double walls = ImpedanceCalculator.Solve(spec with { Model = Walls }).ImpedanceOhms;
        _output.WriteLine($"w {w * 1e3} h {h * 1e3} t {t * 1e6} {(stripline ? "stripline" : "microstrip")}: "
            + $"effective width {width:f3} Ω, side walls {walls:f3} Ω ({(walls / width - 1) * 100:f2} %)");
        // Measured: 0.17 %, 0.04 % and −0.02 %.
        Assert.Equal(width, walls, 0.005 * width);
    }

    [Theory]
    [InlineData(0.1e-3, 0.05e-3, 35e-6)]
    [InlineData(0.1e-3, 0.03e-3, 70e-6)]
    public void WhereTheEffectiveWidthRunsOut_TheSideWallsCoupleMore(double w, double s, double t)
    {
        // A pair whose gap is not much wider than the copper is thick: the effective width is
        // capped at half the gap and cannot carry the field between the walls.
        var spec = new LineSpec { WidthMeters = w, PairGapMeters = s, ThicknessMeters = t, HeightMeters = 0.1e-3,
            LossTangent = 0, Model = RlgcModel.Kernel with { ThicknessCorrection = true } };
        var width = ImpedanceCalculator.Solve(spec);
        var walls = ImpedanceCalculator.Solve(spec with { Model = Walls });
        _output.WriteLine($"w {w * 1e6} s {s * 1e6} t {t * 1e6} µm: Z_odd {width.Modes[0].ImpedanceOhms:f2} → {walls.Modes[0].ImpedanceOhms:f2} Ω, "
            + $"Z_even {width.Modes[1].ImpedanceOhms:f2} → {walls.Modes[1].ImpedanceOhms:f2} Ω, "
            + $"near-end {width.NearEndCoupling * 100:f2} → {walls.NearEndCoupling * 100:f2} %");
        Assert.True(walls.Modes[0].ImpedanceOhms < width.Modes[0].ImpedanceOhms, "the walls add odd-mode capacitance");
    }

    [Fact]
    public void TheBoardModel_WithSideWalls_CarriesLossAndDispersion()
    {
        // The incremental-inductance rule recedes every face of the trapezoid; on an ordinary
        // line its loss lands where the effective-width rule's does.
        var stack = new LayeredStackup(new[] { new LayeredStackup.Layer(4.4, 0.02, 0.2e-3) });
        var section = new CoupledLineCrossSection(stack, 0, new[] { Trace(-0.3e-3, 0.3e-3, 35e-6), Trace(0.3e-3, 0.3e-3, 35e-6) });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var walls = RlgcExtractor.Extract(section, RlgcModel.Board with { SideWalls = true });
        double seconds = clock.Elapsed.TotalSeconds;
        var width = RlgcExtractor.Extract(section, RlgcModel.Board);
        double rWalls = walls.ResistanceMatrixOhmsPerMeter!(1e9)[0, 0], rWidth = width.ResistanceMatrixOhmsPerMeter!(1e9)[0, 0];
        _output.WriteLine($"R(1 GHz) {rWidth:f3} → {rWalls:f3} Ω/m; C(10 GHz) {width.CapacitancePerMeter(1e10)[0, 0]:e5} → "
            + $"{walls.CapacitancePerMeter(1e10)[0, 0]:e5}; side-wall extraction {seconds:f2} s");
        Assert.InRange(rWalls / rWidth, 0.9, 1.1);
        Assert.NotNull(walls.DielectricSolves);
        Assert.Contains(walls.Assumptions, a => a.Contains("trapezoids"));
    }

    [Fact]
    public void ThePanels_AreConverged()
    {
        var stack = new LayeredStackup(new[] { new LayeredStackup.Layer(4.4, 0, 0.2e-3) });
        var section = new CoupledLineCrossSection(stack, 0, new[] { Trace(-0.2e-3, 0.3e-3, 35e-6), Trace(0.2e-3, 0.3e-3, 35e-6) });
        var coarse = C(section, 48);
        var fine = C(section, 96);
        _output.WriteLine($"C11 {coarse[0, 0]:e6} / {fine[0, 0]:e6}, C12 {coarse[0, 1]:e6} / {fine[0, 1]:e6}");
        Assert.Equal(fine[0, 0], coarse[0, 0], 2e-3 * fine[0, 0]);
        Assert.Equal(fine[0, 1], coarse[0, 1], 5e-3 * Math.Abs(fine[0, 1]));
    }
}
