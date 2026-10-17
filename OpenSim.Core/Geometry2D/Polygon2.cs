namespace OpenSim.Core.Geometry2D;

/// <summary>
/// A simple polygon with optional holes. The outer ring winds counter-clockwise,
/// holes wind clockwise (the convention the polygon engine emits).
/// </summary>
public sealed record Polygon2(
    IReadOnlyList<Point2> Outer,
    IReadOnlyList<IReadOnlyList<Point2>> Holes)
{
    public Polygon2(IReadOnlyList<Point2> outer) : this(outer, Array.Empty<IReadOnlyList<Point2>>()) { }

    /// <summary>Signed area of one ring (positive for counter-clockwise winding).</summary>
    public static double RingArea(IReadOnlyList<Point2> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += Point2.Cross(a, b);
        }
        return sum / 2;
    }

    /// <summary>Net area: outer ring minus holes.</summary>
    public double Area() =>
        Math.Abs(RingArea(Outer)) - Holes.Sum(h => Math.Abs(RingArea(h)));

    /// <summary>Even–odd point-in-polygon test (holes excluded; points on an edge are
    /// either side, within rounding).</summary>
    public bool Contains(Point2 p)
    {
        bool inside = false;
        foreach (var ring in OrientedRings(this))
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y)
                    && p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                    inside = !inside;
        return inside;
    }

    /// <summary>
    /// The polygon's rings enforced to the boolean-engine winding convention: outer
    /// counter-clockwise, holes clockwise. NonZero fill treats a hole wound the same way
    /// as its outer as more copper instead of a cutout, so every path that feeds rings to
    /// a boolean union must go through this rather than trusting upstream winding.
    /// </summary>
    public static IEnumerable<IReadOnlyList<Point2>> OrientedRings(Polygon2 polygon)
    {
        yield return RingArea(polygon.Outer) >= 0 ? polygon.Outer : Reversed(polygon.Outer);
        foreach (var hole in polygon.Holes)
            yield return RingArea(hole) <= 0 ? hole : Reversed(hole);
    }

    private static IReadOnlyList<Point2> Reversed(IReadOnlyList<Point2> ring) =>
        ring.Reverse().ToList();
}
