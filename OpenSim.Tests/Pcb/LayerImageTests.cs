using OpenSim.Pcb.Excellon;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Polygons;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class LayerImageTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", name);

    private static LayerImageBuilder Builder() => new(new ClipperPolygonOps());

    [Fact]
    public void SingleTrace_AreaIsCapsule()
    {
        // A single 8 mm draw with a 0.4 mm round aperture: area = w·L + π·r².
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.4*%\nD10*\nX1000000Y1000000D02*\nX9000000Y1000000D01*\nM02*");
        var image = Builder().Build(doc);

        double w = 0.4e-3, len = 8e-3, r = w / 2;
        double expected = w * len + Math.PI * r * r;
        var polygon = Assert.Single(image.Polygons);
        Assert.Empty(polygon.Holes);
        // Round caps are tessellated polygons — slightly smaller than the true circle.
        Assert.Equal(expected, image.TotalArea(), expected * 5e-3);
    }

    [Fact]
    public void TwoPadsAndTrace_UnionIntoOneConnectedPolygon()
    {
        var doc = new GerberParser().ParseFile(Fixture("two_pads_trace.gbr"));
        var image = Builder().Build(doc);

        // Pads overlap the trace ends, so the union is one connected copper island.
        var polygon = Assert.Single(image.Polygons);
        Assert.Empty(polygon.Holes);

        // Exact union area: capsule + 2 circles − 2 lens overlaps is messy analytically;
        // bound it instead: at least the two pads, at most pads + full capsule.
        double pad = Math.PI * Math.Pow(0.5e-3, 2);
        double capsule = 0.4e-3 * 8e-3 + Math.PI * Math.Pow(0.2e-3, 2);
        Assert.InRange(image.TotalArea(), 2 * pad, 2 * pad + capsule);
    }

    [Fact]
    public void RegionWithClearCutout_ProducesOneHole()
    {
        var doc = new GerberParser().ParseFile(Fixture("region_cutout.gbr"));
        var image = Builder().Build(doc);

        var polygon = Assert.Single(image.Polygons);
        Assert.Single(polygon.Holes);

        double square = 10e-3 * 10e-3;
        double cut = Math.PI * Math.Pow(1e-3, 2);                 // 2 mm circle
        Assert.Equal(square - cut, image.TotalArea(), square * 1e-3);
    }

    [Fact]
    public void Drills_SubtractFromCopper()
    {
        // A 10×10 mm region with three drilled holes.
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\nG36*\nX0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\n" +
            "X0Y10000000D01*\nX0Y0D01*\nG37*\nM02*");
        var drills = new ExcellonParser().ParseFile(Fixture("holes.drl"));
        var image = Builder().Build(doc, drills);

        var polygon = Assert.Single(image.Polygons);
        Assert.Equal(3, polygon.Holes.Count);

        double expected = 10e-3 * 10e-3
                          - 2 * Math.PI * Math.Pow(0.3e-3, 2)     // two 0.6 mm drills
                          - Math.PI * Math.Pow(0.5e-3, 2);        // one 1.0 mm drill
        Assert.Equal(expected, image.TotalArea(), expected * 1e-3);
    }

    [Fact]
    public void Capsule_AreaMatchesAnalytic_AndIsRotationInvariant()
    {
        // Stadium area = L·w + π·(w/2)²; tessellated caps sit slightly inside the circle.
        double w = 0.6e-3, r = w / 2;
        var axisAligned = ApertureShapes.Capsule(
            new OpenSim.Core.Geometry2D.Point2(0, 0), new OpenSim.Core.Geometry2D.Point2(2e-3, 0), r, 5e-6);
        var diagonal = ApertureShapes.Capsule(
            new OpenSim.Core.Geometry2D.Point2(1e-3, 1e-3), new OpenSim.Core.Geometry2D.Point2(1e-3 + 2e-3 / Math.Sqrt(2), 1e-3 + 2e-3 / Math.Sqrt(2)), r, 5e-6);

        double expected = 2e-3 * w + Math.PI * r * r;
        Assert.Equal(expected, RingArea(axisAligned), expected * 5e-3);
        Assert.Equal(expected, RingArea(diagonal), expected * 5e-3);

        // Zero-length slot degenerates to a circle. An inscribed n-gon at this radius
        // and chord tolerance is ~2% under the true circle area — that is the
        // tessellation itself, not an error.
        var point = ApertureShapes.Capsule(
            new OpenSim.Core.Geometry2D.Point2(0, 0), new OpenSim.Core.Geometry2D.Point2(0, 0), r, 5e-6);
        Assert.Equal(Math.PI * r * r, RingArea(point), Math.PI * r * r * 3e-2);

        static double RingArea(IReadOnlyList<OpenSim.Core.Geometry2D.Point2> ring)
        {
            double sum = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Count];
                sum += p.X * q.Y - q.X * p.Y;
            }
            return sum / 2; // positive ⇒ counter-clockwise, as the generator promises
        }
    }

    [Fact]
    public void Slots_SubtractFromCopper()
    {
        // A 10×10 mm region with one 0.6 mm-wide, 2 mm-long routed slot.
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\nG36*\nX0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\n" +
            "X0Y10000000D01*\nX0Y0D01*\nG37*\nM02*");
        var drills = new ExcellonParser().Parse(
            "M48\nMETRIC\nT1C0.6\n%\nT1\nX4.0Y5.0G85X6.0Y5.0\nM30");
        var image = Builder().Build(doc, drills);

        var polygon = Assert.Single(image.Polygons);
        Assert.Single(polygon.Holes);

        double w = 0.6e-3;
        double expected = 10e-3 * 10e-3 - (2e-3 * w + Math.PI * Math.Pow(w / 2, 2));
        Assert.Equal(expected, image.TotalArea(), expected * 1e-3);
    }

    [Fact]
    public void MixedDrillFile_PopulatesHolesAndSlots()
    {
        var features = OpenSim.Pcb.Import.DrillExtractor.Extract(
            "M48\nMETRIC\nT1C0.6\nT2C1.0\n%\nT1\nX1.0Y1.0\nX1.0Y1.0G85X2.0Y1.0\nT2\nX5.0Y5.0\nM30");

        Assert.Equal(2, features.Holes.Count);
        var slot = Assert.Single(features.Slots);
        Assert.Equal(0.6e-3, slot.Diameter, 12);
        Assert.Equal(0.6e-3 /* T1 */, features.Holes[0].Diameter, 12);
        Assert.Equal(1.0e-3 /* T2 */, features.Holes[1].Diameter, 12);
    }

    [Fact]
    public void Output_IsCanonicallyOrdered_RegardlessOfDrawOrder()
    {
        // Islands drawn right-to-left; the image must come out sorted by geometric key
        // (outer-ring minX first) — island order is a contract (it assigns island ids),
        // independent of Clipper's PolyTree walk or how the booleans were staged.
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1.0*%\nD10*\n" +
            "X20000000Y0D03*\nX0Y0D03*\nX10000000Y0D03*\nM02*");
        var image = Builder().Build(doc);

        Assert.Equal(3, image.Polygons.Count);
        var minXs = image.Polygons.Select(p => p.Outer.Min(pt => pt.X)).ToList();
        for (int i = 1; i < minXs.Count; i++)
            Assert.True(minXs[i - 1] < minXs[i],
                $"islands not in canonical order: minX[{i - 1}]={minXs[i - 1]} ≥ minX[{i}]={minXs[i]}");
    }

    [Fact]
    public void ArcTrace_StrokedAreaMatchesQuarterAnnulusCapsule()
    {
        var doc = new GerberParser().ParseFile(Fixture("arc_trace.gbr"));
        var image = Builder().Build(doc);

        // Stroked arc of centerline radius R and width w: area = (π/2)·R·w + π·(w/2)².
        double R = 5e-3, w = 0.4e-3;
        double expected = Math.PI / 2 * R * w + Math.PI * Math.Pow(w / 2, 2);
        Assert.Single(image.Polygons);
        Assert.Equal(expected, image.TotalArea(), expected * 1e-2);
    }
}
