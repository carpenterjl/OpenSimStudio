using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;

namespace OpenSim.Tests.Plotting;

/// <summary>The polar mapping and the readout lookup (OP-7).</summary>
public class PlotMappingTests
{
    [Fact]
    public void CosineLobe_IsTheCircleThroughTheOriginOfDiameterOne()
    {
        // r = cos θ for |θ| ≤ π/2 is the circle (x − ½)² + y² = ¼ in Cartesian coordinates.
        var map = new PolarMap(0, 1);
        for (int i = 0; i <= 90; i++)
        {
            double theta = -Math.PI / 2 + i * Math.PI / 90;
            var p = map.ToCartesian(theta, Math.Cos(theta));
            Assert.Equal(0.25, (p.X - 0.5) * (p.X - 0.5) + p.Y * p.Y, 14);
        }
    }

    [Fact]
    public void ZeroAtTopClockwise_IsTheCompassConvention()
    {
        var map = new PolarMap(0, 2, PolarZero.Top, Clockwise: true);
        var north = map.ToCartesian(0, 2);
        var east = map.ToCartesian(Math.PI / 2, 1);
        Assert.Equal(0, north.X, 15); Assert.Equal(1, north.Y, 15);
        Assert.Equal(0.5, east.X, 15); Assert.Equal(0, east.Y, 15);
    }

    [Theory]
    [InlineData(PolarZero.Right, false)]
    [InlineData(PolarZero.Right, true)]
    [InlineData(PolarZero.Top, false)]
    [InlineData(PolarZero.Top, true)]
    public void FromCartesian_InvertsToCartesian(PolarZero zero, bool clockwise)
    {
        var map = new PolarMap(-40, 0, zero, clockwise);   // a decibel plot: −40 dB at the centre
        foreach (double theta in new[] { -3.0, -1.2, 0, 0.4, 2.0, 3.1 })
            foreach (double r in new[] { -35.0, -12.5, -1.0, 0.0 })
            {
                var (t, rr) = map.FromCartesian(map.ToCartesian(theta, r));
                Assert.Equal(theta, t, 12);
                Assert.Equal(r, rr, 12);
            }
        // Below the floor clamps to the centre.
        Assert.Equal(new Point2(0, 0), map.ToCartesian(1, -60));
    }

    [Fact]
    public void Nearest_FindsTheClosestPointWithinTheRadius()
    {
        var points = new[] { new Point2(0, 0), new Point2(10, 0), new Point2(10.5, 0), new Point2(double.NaN, 3) };
        Assert.Equal(2, ReadoutLookup.Nearest(points, new Point2(10.4, 1), 5));
        Assert.Equal(-1, ReadoutLookup.Nearest(points, new Point2(5, 5), 4));
        Assert.Equal(0, ReadoutLookup.Nearest(points, new Point2(0, 3), 3));   // exactly at the radius counts
    }

    [Fact]
    public void Nearest_AcrossSeries_EarlierSeriesWinTies()
    {
        var a = new[] { new Point2(0, 0), new Point2(4, 0) };
        var b = new[] { new Point2(4, 0), new Point2(1, 1) };
        Assert.Equal((0, 1), ReadoutLookup.Nearest(new[] { a, b }, new Point2(4, 0.5), 2));
        Assert.Equal((1, 1), ReadoutLookup.Nearest(new[] { a, b }, new Point2(1.2, 1.1), 2));
        Assert.Null(ReadoutLookup.Nearest(new[] { a, b }, new Point2(20, 20), 2));
    }

    [Fact]
    public void DistanceToSegment_ClampsToTheEnds()
    {
        Assert.Equal(2, ReadoutLookup.DistanceToSegment(new Point2(1, 2), new Point2(0, 0), new Point2(3, 0)), 15);
        Assert.Equal(5, ReadoutLookup.DistanceToSegment(new Point2(7, 3), new Point2(0, 0), new Point2(3, 0)), 15);
    }
}
