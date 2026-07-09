namespace OpenSim.Core.Geometry2D;

/// <summary>
/// Cleans polygon rings before meshing: collapses near-coincident vertices and removes
/// near-collinear ones (Ramer–Douglas–Peucker). Real Gerber outlines from arc
/// tessellation and stroke corner joins carry hundreds of sub-micron spurs; left in,
/// they sit below the mesher's jitter amplitude and tangle into false self-intersections.
/// Cleaning at a tolerance well below the smallest real feature is invisible to the FEM.
/// </summary>
public static class PolygonCleaner
{
    /// <summary>Default cleaning tolerance [m]: 10 µm, far below any real copper feature.</summary>
    public const double DefaultTolerance = 10e-6;

    public static IReadOnlyList<Polygon2> Clean(IEnumerable<Polygon2> polygons, double tolerance = DefaultTolerance)
    {
        var result = new List<Polygon2>();
        foreach (var polygon in polygons)
        {
            var outer = CleanRing(polygon.Outer, tolerance);
            if (outer.Count < 3) continue;                      // ring collapsed to nothing
            var holes = polygon.Holes
                .Select(h => CleanRing(h, tolerance))
                .Where(h => h.Count >= 3)
                .ToList();
            result.Add(new Polygon2(outer, holes));
        }
        return result;
    }

    /// <summary>
    /// Dedup near-coincident points, then iteratively drop vertices whose perpendicular
    /// distance from the chord through their two neighbours is below tolerance. A couple
    /// of passes converge and remove the tessellation spurs without RDP's closed-ring
    /// bookkeeping. Guards a minimum of three vertices so a ring never collapses.
    /// </summary>
    public static IReadOnlyList<Point2> CleanRing(IReadOnlyList<Point2> ring, double tolerance)
    {
        var pts = new List<Point2>(ring.Count);
        foreach (var p in ring)
            if (pts.Count == 0 || (p - pts[^1]).Length > tolerance)
                pts.Add(p);
        while (pts.Count > 1 && (pts[^1] - pts[0]).Length <= tolerance)
            pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) return pts;

        bool removed = true;
        while (removed && pts.Count > 3)
        {
            removed = false;
            for (int i = 0; i < pts.Count && pts.Count > 3; i++)
            {
                var prev = pts[(i - 1 + pts.Count) % pts.Count];
                var cur = pts[i];
                var next = pts[(i + 1) % pts.Count];
                if (PointToSegment(cur, prev, next) <= tolerance)
                {
                    pts.RemoveAt(i);
                    removed = true;
                    i--;
                }
            }
        }
        return pts;
    }

    private static double PointToSegment(Point2 p, Point2 a, Point2 b)
    {
        var ab = b - a;
        double len2 = Point2.Dot(ab, ab);
        if (len2 < 1e-300) return (p - a).Length;
        double t = Math.Clamp(Point2.Dot(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }
}
