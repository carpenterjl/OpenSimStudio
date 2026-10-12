namespace OpenSim.Core.Geometry2D;

/// <summary>
/// Cleans polygon rings before meshing: collapses near-coincident vertices and removes
/// near-collinear ones. Real Gerber outlines from arc tessellation and stroke corner joins
/// carry hundreds of sub-micron spurs; left in, they sit below the mesher's jitter amplitude
/// and tangle into false self-intersections. Cleaning at a tolerance well below the smallest
/// real feature is invisible to the FEM — provided the cleaned ring stays within that
/// tolerance of the original everywhere, and the tolerance is small against the ring itself
/// (see <see cref="CleanRing"/>; FU-24).
/// </summary>
public static class PolygonCleaner
{
    /// <summary>Default cleaning tolerance [m]: 10 µm, far below any real copper feature.</summary>
    public const double DefaultTolerance = 10e-6;

    /// <summary>
    /// The sagitta a removal may introduce is also capped at this fraction of √|ring area|,
    /// so a small ring (a pad, a drill hole) keeps its shape as closely as a large one does at
    /// the absolute tolerance: a circle loses at most about 0.5 % of its area to cleaning,
    /// whatever its radius. A 0.25 mm pad drawn with fine arcs lost 9 % at the flat 10 µm.
    /// </summary>
    public const double RelativeFidelity = 0.002;

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
    /// Dedup near-coincident points at the tolerance (the mesher's scale), then iteratively
    /// drop vertices: a vertex goes when every ORIGINAL vertex between its two surviving
    /// neighbours lies within the ring's tolerance of the chord through them. Measured
    /// against the survivors alone, as before, removals compounded — each step moved the
    /// chord by less than the tolerance from the last chord, never from the ring, and a finely
    /// tessellated 2 mm circle was eaten to a 10-gon with a 93 µm sagitta at the 10 µm
    /// tolerance, 6 % of its area with it. The ring's tolerance is the lesser of the given
    /// one and <see cref="RelativeFidelity"/> × √|area|. Guards a minimum of three vertices
    /// so a ring never collapses.
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

        double ringTolerance = Math.Min(tolerance,
            RelativeFidelity * Math.Sqrt(Math.Abs(Polygon2.RingArea(pts))));
        var keep = new List<int>(pts.Count);
        for (int i = 0; i < pts.Count; i++) keep.Add(i);
        bool removed = true;
        while (removed && keep.Count > 3)
        {
            removed = false;
            for (int i = 0; i < keep.Count && keep.Count > 3; i++)
            {
                int prev = keep[(i - 1 + keep.Count) % keep.Count];
                int next = keep[(i + 1) % keep.Count];
                if (ChordWithin(pts, prev, next, ringTolerance))
                {
                    keep.RemoveAt(i);
                    removed = true;
                    i--;
                }
            }
        }
        var result = new List<Point2>(keep.Count);
        foreach (int k in keep) result.Add(pts[k]);
        return result;
    }

    /// <summary>Every vertex of the ring strictly between <paramref name="a"/> and
    /// <paramref name="b"/> (cyclically, a → b) lies within <paramref name="tolerance"/> of the
    /// chord a–b.</summary>
    private static bool ChordWithin(List<Point2> pts, int a, int b, double tolerance)
    {
        int n = pts.Count;
        for (int j = (a + 1) % n; j != b; j = (j + 1) % n)
            if (PointToSegment(pts[j], pts[a], pts[b]) > tolerance)
                return false;
        return true;
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
