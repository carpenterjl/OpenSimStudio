using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;

namespace OpenSim.Tests.PostProcessing;

/// <summary>
/// The board field-overlay grid gates (SI Stage S10): the lattice + outline masking + quad
/// topology that paint the field over the board. Pure geometry (no WPF), so the masking
/// and the "only fully-interior cells" contract are testable without the App layer; the
/// live per-layer render is a UIA smoke.
/// </summary>
public class OverlayGridTests
{
    // A concave L-shape outline with a rectangular hole punched in the thick arm.
    private static PolygonSetIndex LShapeWithHole()
    {
        var outer = new[]
        {
            new Point2(0, 0), new Point2(10, 0), new Point2(10, 4),
            new Point2(4, 4), new Point2(4, 10), new Point2(0, 10),
        };
        var hole = new[]
        {
            new Point2(1, 1), new Point2(3, 1), new Point2(3, 3), new Point2(1, 3),
        };
        return new PolygonSetIndex(new[] { new Polygon2(outer, new[] { (IReadOnlyList<Point2>)hole }) });
    }

    [Fact]
    public void RectPoints_IsARowMajorLatticeAtTheGivenHeight()
    {
        var pts = OverlayGrid.RectPoints(-1, -2, 3, 6, 0.5, 5, 9);
        Assert.Equal(45, pts.Length);
        Assert.Equal(new Vector3D(-1, -2, 0.5), pts[0]);        // first = (minX, minY)
        Assert.Equal(new Vector3D(3, 6, 0.5), pts[^1]);         // last = (maxX, maxY)
        Assert.All(pts, p => Assert.Equal(0.5, p.Z));
        // x is the inner index: point 1 steps in x, point nx steps in y.
        Assert.Equal(pts[1].Y, pts[0].Y);
        Assert.True(pts[5].Y > pts[0].Y && pts[5].X == pts[0].X);
    }

    [Fact]
    public void InteriorMask_RespectsTheConcaveOutlineAndHole()
    {
        var outline = LShapeWithHole();
        var pts = new[]
        {
            new Vector3D(2, 2, 0),       // inside outer, inside the HOLE [1,3]² → out
            new Vector3D(8, 2, 0),       // inside the thick arm → in
            new Vector3D(1, 8, 0),       // inside the tall arm → in
            new Vector3D(8, 8, 0),       // the concave notch (outside) → out
            new Vector3D(-1, -1, 0),     // outside the board → out
        };
        var inside = OverlayGrid.InteriorMask(pts, outline);
        Assert.False(inside[0], "point in the hole is not painted");
        Assert.True(inside[1]);
        Assert.True(inside[2]);
        Assert.False(inside[3], "the concave notch is outside the board");
        Assert.False(inside[4]);
    }

    [Fact]
    public void InteriorQuads_EmitOnlyFullyInteriorCells()
    {
        // A 2×2 grid (1 quad). All-inside ⇒ 1 quad; any corner out ⇒ 0.
        Assert.Equal(1, OverlayGrid.InteriorQuadCount(new[] { true, true, true, true }, 2, 2));
        Assert.Equal(0, OverlayGrid.InteriorQuadCount(new[] { true, true, true, false }, 2, 2));

        // A 3×3 grid: 4 cells. Knock out the center vertex ⇒ all four cells touch it ⇒ 0.
        var mask = new bool[9];
        Array.Fill(mask, true);
        mask[4] = false;
        Assert.Equal(0, OverlayGrid.InteriorQuadCount(mask, 3, 3));
        // Knock out one corner ⇒ only its single incident cell drops (3 remain).
        Array.Fill(mask, true);
        mask[0] = false;
        Assert.Equal(3, OverlayGrid.InteriorQuadCount(mask, 3, 3));

        // The emitted quads reference only in-mask corners, and never a degenerate index.
        foreach (var (v00, v10, v11, v01) in OverlayGrid.InteriorQuads(mask, 3, 3))
        {
            Assert.True(mask[v00] && mask[v10] && mask[v11] && mask[v01]);
            Assert.True(v00 != v10 && v10 != v11 && v11 != v01 && v01 != v00);
        }
    }

    [Fact]
    public void PooledScale_EqualsAutoOverTheConcatenatedInsideValues()
    {
        // The per-layer overlay shares ONE FieldScale pooled over every layer's in-outline
        // samples — identical to FieldScale.Auto over the concatenation (so colors compare
        // layer-to-layer). This pins that contract.
        var layerA = new[] { 1.0, 5.0, 2.0 };
        var layerB = new[] { 8.0, 0.5, 3.0 };
        var pooled = layerA.Concat(layerB).ToList();
        var scale = FieldScale.Auto(FieldScaleMode.Logarithmic, pooled, 3);
        var reference = FieldScale.Auto(FieldScaleMode.Logarithmic, new[]
            { 1.0, 5.0, 2.0, 8.0, 0.5, 3.0 }, 3);
        Assert.Equal(reference.Max, scale.Max);
        Assert.Equal(reference.EffectiveMin, scale.EffectiveMin);
        Assert.Equal(8.0, scale.Max);   // the global peak across both layers
    }
}
