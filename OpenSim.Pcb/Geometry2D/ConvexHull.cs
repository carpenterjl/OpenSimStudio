using OpenSim.Core.Geometry2D;

namespace OpenSim.Pcb.Geometry2D;

/// <summary>Convex hull of a 2D point set (Andrew's monotone chain).</summary>
public static class ConvexHull
{
    /// <summary>Counter-clockwise hull ring. The input list is sorted in place.</summary>
    public static IReadOnlyList<Point2> Compute(List<Point2> points)
    {
        points.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        var hull = new List<Point2>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            var source = pass == 0
                ? points
                : Enumerable.Range(0, points.Count).Select(i => points[^(i + 1)]).ToList();
            foreach (var p in source)
            {
                while (hull.Count >= start + 2
                       && Point2.Cross(hull[^1] - hull[^2], p - hull[^1]) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);                        // endpoint repeats as next pass's start
        }
        return hull;
    }
}
