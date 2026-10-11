using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using Xunit;
using Xunit.Abstractions;
using Layer = OpenSim.Rf.Layered.LayeredStackup.Layer;

namespace OpenSim.Tests.Si;

/// <summary>
/// FU-29: a trace corner solved by the full-wave layered moment method and read as a lumped T
/// (<see cref="CornerExtractor"/>) — a 0.3 mm trace over 0.2 mm of εr 4.2.
/// </summary>
public class CornerTests
{
    private readonly ITestOutputHelper _out;
    public CornerTests(ITestOutputHelper output) => _out = output;

    private static readonly LayeredStackup Microstrip = new(new[] { new Layer(4.2, 0, 0.2e-3) });
    private const double W = 0.3e-3;

    private CornerNetwork Report(string what, CornerNetwork c)
    {
        _out.WriteLine($"{what}: L {c.SeriesInductanceHenries * 1e12:f3} pH, C {c.ShuntCapacitanceFarads * 1e15:f3} fF at " +
                       $"{c.ExtractionFrequencyHz / 1e9:f2} GHz on a {c.LineOhms:f2} Ω line, fit residual {c.FitResidual:e1}");
        return c;
    }

    [Fact]
    public void StraightOn_IsNoCornerAtAll()
    {
        // At 0° the bent strip is meshed exactly as the straight calibration line, so what the
        // de-embedding leaves is the identity — the arms carry nothing of their own.
        var straight = Report("0°", CornerExtractor.Extract(Microstrip, 0, false, W, 0));
        Assert.InRange(Math.Abs(straight.SeriesInductanceHenries), 0, 1e-16);
        Assert.InRange(Math.Abs(straight.ShuntCapacitanceFarads), 0, 1e-19);
    }

    [Fact]
    public void ARightAngle_IsTheSameNetwork_WithLongerArmsAtAnotherFrequency()
    {
        // A property of the corner, not of how it was measured: the arms half as long again, at 1.3
        // times the frequency, give the same L and C. And it is a symmetric T: the de-embedded chain
        // matrix's two diagonal terms agree, and agree with 1 + ZY/2.
        var first = Report("90°", CornerExtractor.Extract(Microstrip, 0, false, W, 90));
        var arm = 6 * Math.Max(W, 0.2e-3);
        var second = Report("90°, arms 1.5×, f 1.3×", CornerExtractor.Extract(Microstrip, 0, false, W, 90,
            frequencyHz: 1.3 * first.ExtractionFrequencyHz, armMeters: arm));
        var gentle = Report("45°", CornerExtractor.Extract(Microstrip, 0, false, W, 45));
        Assert.InRange(first.FitResidual, 0, 1e-6);
        Assert.InRange(second.SeriesInductanceHenries / first.SeriesInductanceHenries, 0.9, 1.1);
        Assert.InRange(second.ShuntCapacitanceFarads / first.ShuntCapacitanceFarads, 0.9, 1.1);
        // A right angle departs from its path length further than a 45° turn does.
        Assert.True(Math.Abs(gentle.SeriesInductanceHenries) < Math.Abs(first.SeriesInductanceHenries));
        Assert.True(Math.Abs(gentle.ShuntCapacitanceFarads) < Math.Abs(first.ShuntCapacitanceFarads));
    }
}
