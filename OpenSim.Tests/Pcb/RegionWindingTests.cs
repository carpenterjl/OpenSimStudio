using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// A Gerber region contour is dark (or clear) whichever way it was drawn — contour
/// orientation carries no meaning in the format. The layer image is a NonZero-winding
/// boolean, so a clockwise contour that reached it raw would carry winding −1 and
/// cancel every counter-clockwise flash or stroke it overlaps (a pour would get a hole
/// at each pad it connects to). Every case here draws the same picture both ways and
/// requires the same image, plus an analytic area so "both wrong the same way" fails.
/// </summary>
public class RegionWindingTests
{
    private const string Header = "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1.0*%\n%ADD11C,0.4*%\n";

    /// <summary>size×size mm square region from (x0,y0) mm, drawn in the given direction.</summary>
    private static string Square(double x0Mm, double y0Mm, double sizeMm, bool clockwise)
    {
        long x0 = (long)(x0Mm * 1e6), y0 = (long)(y0Mm * 1e6), s = (long)(sizeMm * 1e6);
        return clockwise
            ? $"G36*\nX{x0}Y{y0}D02*\nX{x0}Y{y0 + s}D01*\nX{x0 + s}Y{y0 + s}D01*\n" +
              $"X{x0 + s}Y{y0}D01*\nX{x0}Y{y0}D01*\nG37*\n"
            : $"G36*\nX{x0}Y{y0}D02*\nX{x0 + s}Y{y0}D01*\nX{x0 + s}Y{y0 + s}D01*\n" +
              $"X{x0}Y{y0 + s}D01*\nX{x0}Y{y0}D01*\nG37*\n";
    }

    private static LayerImage Build(string gerber) =>
        new LayerImageBuilder(new ClipperPolygonOps()).Build(new GerberParser().Parse(gerber));

    /// <summary>Pour + a pad inside it + a trace crossing its edge, all in one dark run.</summary>
    private static string PourPadTrace(bool clockwise) =>
        Header +
        Square(0, 0, 10, clockwise) +
        "D10*\nX5000000Y5000000D03*\n" +                         // 1 mm pad inside the pour
        "D11*\nX8000000Y5000000D02*\nX12000000Y5000000D01*\n" +  // 0.4 mm trace leaving the pour
        "M02*";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DarkPour_WithPadAndTrace_IsOneSolidIsland(bool clockwise)
    {
        var image = Build(PourPadTrace(clockwise));

        var polygon = Assert.Single(image.Polygons);
        Assert.Empty(polygon.Holes);

        // Union = the square + the part of the trace capsule outside it (2 mm of body
        // and the far half-disc cap). The pad lies wholly inside and adds nothing.
        double expected = 100e-6 + 0.4e-3 * 2e-3 + Math.PI * 0.2e-3 * 0.2e-3 / 2;
        Assert.Equal(expected, image.TotalArea(), expected * 1e-4);
    }

    [Fact]
    public void DarkPour_ClockwiseAndCounterClockwise_GiveIdenticalImages()
    {
        AssertSameImage(Build(PourPadTrace(clockwise: false)), Build(PourPadTrace(clockwise: true)));
    }

    /// <summary>A clear region and a clear flash inside it, in one clear run: the cut is
    /// the region, whichever way the region was drawn.</summary>
    private static string PourWithClearRegionAndFlash(bool clockwise) =>
        Header +
        Square(0, 0, 10, clockwise: false) +
        "%LPC*%\n" + Square(2, 2, 2, clockwise) +
        "D10*\nX3000000Y3000000D03*\n" +
        "M02*";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearRegion_WithClearFlashInside_CutsTheWholeRegion(bool clockwise)
    {
        var image = Build(PourWithClearRegionAndFlash(clockwise));

        var polygon = Assert.Single(image.Polygons);
        Assert.Single(polygon.Holes);
        double expected = 100e-6 - 4e-6;
        Assert.Equal(expected, image.TotalArea(), expected * 1e-9);
    }

    [Fact]
    public void ClearRegion_ClockwiseAndCounterClockwise_GiveIdenticalImages()
    {
        AssertSameImage(Build(PourWithClearRegionAndFlash(clockwise: false)),
                        Build(PourWithClearRegionAndFlash(clockwise: true)));
    }

    [Fact]
    public void Parser_EmitsRegionContoursCounterClockwise()
    {
        var doc = new GerberParser().Parse(Header + Square(0, 0, 10, clockwise: true) + "M02*");
        var region = Assert.IsType<RegionOp>(Assert.Single(doc.Ops));
        Assert.True(Polygon2.RingArea(Assert.Single(region.Contours)) > 0);
    }

    [Fact]
    public void Builder_DoesNotTrustTheWindingOfARegionOpItIsHanded()
    {
        // A RegionOp built by hand (not by the parser) with a clockwise contour: the
        // image builder must orient it itself rather than rely on its caller.
        var cw = new List<Point2> { new(0, 0), new(0, 10e-3), new(10e-3, 10e-3), new(10e-3, 0) };
        Assert.True(Polygon2.RingArea(cw) < 0);
        var pad = new CircleAperture(10, 1e-3);
        var doc = new GerberDocument
        {
            Apertures = new Dictionary<int, Aperture> { [10] = pad },
            Ops = new GerberOp[]
            {
                new RegionOp(new IReadOnlyList<Point2>[] { cw }, GerberPolarity.Dark),
                new FlashOp(new Point2(5e-3, 5e-3), pad, GerberPolarity.Dark)
            },
            Warnings = Array.Empty<string>()
        };

        var image = new LayerImageBuilder(new ClipperPolygonOps()).Build(doc);

        var polygon = Assert.Single(image.Polygons);
        Assert.Empty(polygon.Holes);
        Assert.Equal(100e-6, image.TotalArea(), 100e-6 * 1e-9);
    }

    private static void AssertSameImage(LayerImage expected, LayerImage actual)
    {
        Assert.Equal(expected.Polygons.Count, actual.Polygons.Count);
        for (int i = 0; i < expected.Polygons.Count; i++)
        {
            AssertRingEqualUpToRotation(expected.Polygons[i].Outer, actual.Polygons[i].Outer);
            Assert.Equal(expected.Polygons[i].Holes.Count, actual.Polygons[i].Holes.Count);
            for (int h = 0; h < expected.Polygons[i].Holes.Count; h++)
                AssertRingEqualUpToRotation(expected.Polygons[i].Holes[h], actual.Polygons[i].Holes[h]);
        }
    }

    /// <summary>Exact vertex equality up to the ring's cyclic start (Clipper snaps to a
    /// 1 nm grid, so exact double equality is meaningful).</summary>
    private static void AssertRingEqualUpToRotation(IReadOnlyList<Point2> expected, IReadOnlyList<Point2> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        int n = expected.Count;
        int eStart = MinIndex(expected), aStart = MinIndex(actual);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(expected[(eStart + i) % n].X, actual[(aStart + i) % n].X);
            Assert.Equal(expected[(eStart + i) % n].Y, actual[(aStart + i) % n].Y);
        }
    }

    private static int MinIndex(IReadOnlyList<Point2> ring)
    {
        int start = 0;
        for (int i = 1; i < ring.Count; i++)
            if (ring[i].X < ring[start].X || (ring[i].X == ring[start].X && ring[i].Y < ring[start].Y))
                start = i;
        return start;
    }
}
