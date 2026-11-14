using OpenSim.Core.Plotting;

namespace OpenSim.Tests.Plotting;

/// <summary>Axis ticks, labels, the data-to-pixel map and the scale bar (OP-7).</summary>
public class AxisTicksTests
{
    [Theory]
    [InlineData(10.0, 5, 2.0)]     // raw 2.0 → 2
    [InlineData(10.0, 6, 2.0)]     // raw 1.67 → 2
    [InlineData(1.0, 10, 0.1)]     // raw 0.1 → 0.1
    [InlineData(7.0, 2, 5.0)]      // raw 3.5 → 5 (the 3.5 boundary goes up)
    [InlineData(800.0, 1, 1000.0)] // raw 800 → 1000
    public void NiceStep_IsOneTwoOrFiveTimesAPowerOfTen(double span, int target, double expected) =>
        Assert.Equal(expected, AxisTicks.NiceStep(span, target), 12);

    [Fact]
    public void Linear_TicksAreIntegerMultiplesOfTheStep_InsideTheRange()
    {
        // Span 1.1 over 6 → raw 0.183 → step 0.2; multiples of 0.2 in [-0.13, 0.97] are 0 … 0.8.
        var ticks = AxisTicks.Linear(-0.13, 0.97, 6);
        Assert.Equal(5, ticks.Count);
        for (int k = 0; k < 5; k++) Assert.Equal(k * 0.2, ticks[k], 15);
        // Zero is exactly zero, not a rounding residue of a running sum.
        Assert.Equal(0.0, ticks[0]);
    }

    [Fact]
    public void Linear_AcceptsEitherOrder_AndDegenerateRanges()
    {
        Assert.Equal(AxisTicks.Linear(0, 10), AxisTicks.Linear(10, 0));
        Assert.Equal(new[] { 3.0 }, AxisTicks.Linear(3, 3));
        Assert.Empty(AxisTicks.Linear(double.NaN, 1));
    }

    [Fact]
    public void Linear_FarFromZero_DoesNotAccumulateRounding()
    {
        var ticks = AxisTicks.Linear(1e6, 1e6 + 1, 5);
        for (int i = 1; i < ticks.Count; i++)
            Assert.Equal(0.2, ticks[i] - ticks[i - 1], 9);
    }

    [Fact]
    public void Decades_ArePowersOfTenInside()
    {
        Assert.Equal(new[] { 1e-2, 1e-1, 1, 10, 100 }, AxisTicks.Decades(0.005, 300));
        Assert.Empty(AxisTicks.Decades(-1, 10));
    }

    [Theory]
    [InlineData(0.25, 0.25, "0.25")]
    [InlineData(4.0, 2.0, "4")]
    [InlineData(0.6000000000000001, 0.2, "0.6")]
    [InlineData(-1.5, 0.5, "-1.5")]
    [InlineData(2.5e6, 5e5, "2.5E+6")]
    [InlineData(0.0, 1.0, "0")]
    public void Format_UsesTheDigitsTheStepNeeds(double value, double step, string expected) =>
        Assert.Equal(expected, AxisTicks.Format(value, step));

    [Fact]
    public void AxisMap_LinearRoundTripsAndInvertsTheVerticalAxis()
    {
        var y = new AxisMap(-2, 6, 300, 20);   // bottom pixel first
        Assert.Equal(300, y.ToPixel(-2), 12);
        Assert.Equal(20, y.ToPixel(6), 12);
        Assert.Equal(160, y.ToPixel(2), 12);
        Assert.Equal(3.7, y.ToData(y.ToPixel(3.7)), 12);
        Assert.Equal(8.0 / 280, y.DataPerPixel, 15);
    }

    [Fact]
    public void AxisMap_LogMapsDecadesEvenly()
    {
        var x = new AxisMap(1, 1000, 0, 300, Log: true);
        Assert.Equal(100, x.ToPixel(10), 9);
        Assert.Equal(200, x.ToPixel(100), 9);
        Assert.Equal(31.6227766016838, x.ToData(150), 9);
        Assert.Equal(double.NegativeInfinity, x.ToPixel(0));
    }

    [Fact]
    public void AxisMap_ZoomKeepsTheAnchorAtItsPixel_AndPanMovesTheContent()
    {
        var x = new AxisMap(0, 10, 0, 500);
        var zoomed = x.ZoomedAbout(7, 2);
        Assert.Equal(x.ToPixel(7), zoomed.ToPixel(7), 9);
        Assert.Equal(5, zoomed.Max - zoomed.Min, 12);

        var panned = x.Panned(50);   // content moves 50 px right: the data at pixel 50 was at pixel 0
        Assert.Equal(x.ToData(0), panned.ToData(50), 12);

        var logZoom = new AxisMap(1, 1e4, 0, 400, Log: true).ZoomedAbout(100, 2);
        Assert.Equal(10, logZoom.Min, 9);
        Assert.Equal(1000, logZoom.Max, 9);
    }

    [Theory]
    [InlineData(0.01, 100.0, 1.0)]    // budget 1.0 → 1
    [InlineData(0.013, 100.0, 1.0)]   // budget 1.3 → 1
    [InlineData(0.03, 100.0, 2.0)]    // budget 3 → 2
    [InlineData(0.07, 100.0, 5.0)]    // budget 7 → 5
    [InlineData(1e-6, 120.0, 1e-4)]   // budget 1.2e-4 → 1e-4
    public void ScaleBar_IsTheLongestRoundLengthThatFits(double perPixel, double maxPixels, double expected)
    {
        double length = ScaleBar.Length(perPixel, maxPixels);
        Assert.Equal(expected, length, 12);
        Assert.True(length / perPixel <= maxPixels * (1 + 1e-12));
    }
}
