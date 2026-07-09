using System.IO.Compression;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The distributive suffix composition in <see cref="LayerImageBuilder"/> must be
/// SET-IDENTICAL to the original sequential fold (<see cref="ReferenceLayerImageBuilder"/>)
/// — same islands, same holes, same vertices. The synthetic gauntlets exercise exactly
/// the sequences where staging could go wrong: a later dark re-filling an earlier clear,
/// a clear re-cutting the re-fill, and clear shapes with holes overlapping other clears.
/// Vertex comparison is EXACT (up to the ring's cyclic start point — the only staging-
/// dependent freedom): Clipper snaps to a 1 nm integer grid, so exact double equality
/// is meaningful, and the suffix composition genuinely reproduces the fold bit for bit.
/// This exactness is why grouped same-radius stroking was REVERTED: batching paths into
/// one InflatePaths call made the offset engine pre-union overlapping capsules with
/// different boundary vertex lists than the boolean union produces (same region,
/// different vertices/counts) — it failed this gate on the real board and the gate won.
/// </summary>
public class PolarityComposeTests
{
    private static LayerImage BuildNew(GerberDocument doc) =>
        new LayerImageBuilder(new ClipperPolygonOps()).Build(doc);

    private static LayerImage BuildReference(GerberDocument doc) =>
        new ReferenceLayerImageBuilder(new ClipperPolygonOps()).Build(doc);

    private static void AssertEquivalent(string gerber)
    {
        var doc = new GerberParser().Parse(gerber);
        AssertEquivalent(doc);
    }

    private static void AssertEquivalent(GerberDocument doc)
    {
        var expected = BuildReference(doc);
        var actual = BuildNew(doc);

        Assert.Equal(expected.Polygons.Count, actual.Polygons.Count);
        for (int i = 0; i < expected.Polygons.Count; i++)
        {
            AssertRingEqualUpToRotation(expected.Polygons[i].Outer, actual.Polygons[i].Outer);
            Assert.Equal(expected.Polygons[i].Holes.Count, actual.Polygons[i].Holes.Count);
            for (int h = 0; h < expected.Polygons[i].Holes.Count; h++)
                AssertRingEqualUpToRotation(expected.Polygons[i].Holes[h], actual.Polygons[i].Holes[h]);
        }
    }

    /// <summary>Rings are cyclic: rotate both to start at the lexicographically smallest
    /// vertex, then require exact vertex equality — no tolerance.</summary>
    private static void AssertRingEqualUpToRotation(
        IReadOnlyList<Point2> expected, IReadOnlyList<Point2> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        int n = expected.Count;
        int eStart = MinIndex(expected);
        int aStart = MinIndex(actual);
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

    private const string Header = "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1.0*%\n%ADD11C,0.4*%\n";

    /// <summary>10×10 mm dark region from (x0,y0) mm, as a Gerber region block.</summary>
    private static string Square(double x0Mm, double y0Mm, double sizeMm = 10)
    {
        long x0 = (long)(x0Mm * 1e6), y0 = (long)(y0Mm * 1e6), s = (long)(sizeMm * 1e6);
        return $"G36*\nX{x0}Y{y0}D02*\nX{x0 + s}Y{y0}D01*\nX{x0 + s}Y{y0 + s}D01*\n" +
               $"X{x0}Y{y0 + s}D01*\nX{x0}Y{y0}D01*\nG37*\n";
    }

    [Fact]
    public void DarkPour_ClearThermals_DarkTraceRefill_ClearRecut()
    {
        // The full sequence-sensitivity gauntlet:
        //   dark pour → clear "thermal" flashes → dark trace re-crossing the clears
        //   → a final clear re-cutting part of the re-fill.
        AssertEquivalent(
            Header +
            Square(0, 0) +
            "%LPC*%\nD10*\nX3000000Y5000000D03*\nX7000000Y5000000D03*\n" +
            "%LPD*%\nD11*\nX1000000Y5000000D02*\nX9000000Y5000000D01*\n" +
            "%LPC*%\nD11*\nX5000000Y4000000D02*\nX5000000Y6000000D01*\n" +
            "M02*");
    }

    [Fact]
    public void LeadingClear_ClearsNothing()
    {
        // A clear before any dark is a no-op in the fold; the suffix composition must
        // not let it cut the LATER dark (it is not in any dark batch's suffix... it IS —
        // it occurs before the dark, so it must NOT be in that dark's suffix).
        AssertEquivalent(
            Header +
            "%LPC*%\nD10*\nX5000000Y5000000D03*\n" +
            "%LPD*%\n" + Square(0, 0) +
            "M02*");
    }

    [Fact]
    public void TrailingClear_CutsEverything()
    {
        AssertEquivalent(
            Header +
            Square(0, 0) +
            "%LPD*%\nD10*\nX12000000Y5000000D03*\n" +
            "%LPC*%\nD10*\nX10000000Y5000000D03*\n" +
            "M02*");
    }

    [Fact]
    public void OverlappingClearsAcrossBatches_UnionNotParity()
    {
        // Two overlapping clear batches separated by a dark: the combined suffix of the
        // FIRST dark contains both clears — their overlap must clear (region union),
        // not toggle back to copper (even-odd parity would).
        AssertEquivalent(
            Header +
            Square(0, 0) +
            "%LPC*%\nD10*\nX4500000Y5000000D03*\n" +
            "%LPD*%\nD11*\nX0Y1000000D02*\nX10000000Y1000000D01*\n" +
            "%LPC*%\nD10*\nX5500000Y5000000D03*\n" +
            "M02*");
    }

    [Fact]
    public void ClearWithHole_OverlappedByLaterClear()
    {
        // An annular clear (flash with a hole parameter) leaves copper in its hole; a
        // later clear covering that hole must still remove it. In the combined suffix
        // the hole ring's −1 winding meets the second clear's +1 — the outer's +1 makes
        // the total nonzero, which is exactly the region union. This is the case that
        // breaks if holes are ever separated from their outers.
        AssertEquivalent(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD20C,3.0X1.0*%\n%ADD21C,1.5*%\n" +
            Square(0, 0) +
            "%LPC*%\nD20*\nX5000000Y5000000D03*\n" +
            "%LPD*%\n" +   // polarity flip so the two clears land in different batches
            "%LPC*%\nD21*\nX5000000Y5000000D03*\n" +
            "M02*");
    }

    [Fact]
    public void StrokedDraws_TwoWidthsInterleavedAcrossAFlip_AreEquivalent()
    {
        // Two trace widths interleaved with a polarity flip in the middle — stroked
        // draws crossing polarity boundaries must compose exactly like the fold.
        AssertEquivalent(
            Header +
            "D10*\nX0Y0D02*\nX20000000Y0D01*\n" +          // wide dark
            "D11*\nX0Y2000000D02*\nX20000000Y2000000D01*\n" + // narrow dark, same batch
            "%LPC*%\nD11*\nX10000000Y-2000000D02*\nX10000000Y4000000D01*\n" + // narrow CLEAR
            "%LPD*%\nD10*\nX5000000Y-2000000D02*\nX5000000Y4000000D01*\n" +   // wide dark re-fill
            "M02*");
    }

    [Fact]
    public void CrossingTracesOfOneWidth_AreEquivalent()
    {
        // Two crossing same-width traces: the configuration that killed grouped
        // stroking (one InflatePaths call pre-unions the capsules with different
        // boundary vertices than the boolean union produces). Per-trace stroking must
        // stay bit-identical to the fold here.
        AssertEquivalent(
            Header +
            "D11*\nX0Y0D02*\nX10000000Y10000000D01*\n" +
            "X0Y10000000D02*\nX10000000Y0D01*\n" +
            "M02*");
    }

    [Fact]
    public void ExistingFixtures_AreEquivalent()
    {
        foreach (var name in new[] { "two_pads_trace.gbr", "region_cutout.gbr", "arc_trace.gbr" })
        {
            var doc = new GerberParser().ParseFile(
                Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", name));
            AssertEquivalent(doc);
        }
    }

    [Fact]
    public void ExampleBoard_EveryCopperLayer_IsEquivalent()
    {
        string zip = Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");
        using var archive = ZipFile.OpenRead(zip);
        int copperLayers = 0;
        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(".gbr", StringComparison.OrdinalIgnoreCase)) continue;
            using var reader = new StreamReader(entry.Open());
            string text = reader.ReadToEnd();
            var layer = GerberLayerClassifier.Classify(entry.Name, text);
            if (layer.Type is not (GerberLayerType.CopperSignal or GerberLayerType.CopperPlane)) continue;
            copperLayers++;
            AssertEquivalent(new GerberParser().Parse(text));
        }
        Assert.True(copperLayers >= 2, $"expected the real board to have copper layers, found {copperLayers}");
    }
}
