using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The ribbon quads behind the copper-outline preview (display-only geometry). The
/// invariants that matter: one quad per non-degenerate edge including the ring-closing
/// edge, corners offset by exactly the half-width along the edge normal, and degenerate
/// edges skipped instead of emitting zero-normal quads.
/// </summary>
public class OutlineRibbonTests
{
    private const double HalfWidth = 0.1;

    [Fact]
    public void UnitSquare_OneQuadPerEdge_CornersOffsetByHalfWidth()
    {
        var ring = new[]
        {
            new Point2(0, 0), new Point2(1, 0), new Point2(1, 1), new Point2(0, 1)
        };
        var quads = OutlineRibbon.Quads(ring, HalfWidth).ToList();
        Assert.Equal(4, quads.Count);

        // Bottom edge (0,0)→(1,0): direction +x, normal (0,+1) — corners straddle y = 0.
        var q = quads[0];
        Assert.Equal(0, q.A0.X, 12);
        Assert.Equal(-HalfWidth, q.A0.Y, 12);
        Assert.Equal(+HalfWidth, q.A1.Y, 12);
        Assert.Equal(1, q.B1.X, 12);
        Assert.Equal(+HalfWidth, q.B1.Y, 12);
        Assert.Equal(-HalfWidth, q.B0.Y, 12);

        // Every quad straddles its edge symmetrically: corner-pair midpoints are the endpoints.
        foreach (var quad in quads)
        {
            Assert.Equal(quad.A0.X + quad.A1.X, 2 * MidOf(quad.A0, quad.A1).X, 12);
            double half = Math.Sqrt(
                (quad.A1.X - quad.A0.X) * (quad.A1.X - quad.A0.X) +
                (quad.A1.Y - quad.A0.Y) * (quad.A1.Y - quad.A0.Y)) / 2;
            Assert.Equal(HalfWidth, half, 12);
        }
    }

    [Fact]
    public void ClosingEdge_IsIncluded()
    {
        var ring = new[] { new Point2(0, 0), new Point2(2, 0), new Point2(2, 2) };
        var quads = OutlineRibbon.Quads(ring, HalfWidth).ToList();
        Assert.Equal(3, quads.Count);
        // Last quad is the closing edge (2,2)→(0,0).
        Assert.Equal(2, quads[^1].A0.X + HalfWidth * Math.Sqrt(0.5), 12);
        Assert.Equal(0, MidOf(quads[^1].B0, quads[^1].B1).X, 12);
        Assert.Equal(0, MidOf(quads[^1].B0, quads[^1].B1).Y, 12);
    }

    [Fact]
    public void DegenerateEdge_IsSkipped()
    {
        // Duplicate consecutive vertex: 5 ring entries, only 4 drawable edges.
        var ring = new[]
        {
            new Point2(0, 0), new Point2(1, 0), new Point2(1, 0),
            new Point2(1, 1), new Point2(0, 1)
        };
        var quads = OutlineRibbon.Quads(ring, HalfWidth).ToList();
        Assert.Equal(4, quads.Count);
    }

    private static Point2 MidOf(Point2 a, Point2 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}
