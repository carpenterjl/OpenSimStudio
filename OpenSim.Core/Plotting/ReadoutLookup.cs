using OpenSim.Core.Geometry2D;

namespace OpenSim.Core.Plotting;

/// <summary>
/// The plotted point under a cursor. Works in whatever coordinates the caller passes (plots pass
/// screen pixels, so "near" means near on screen whatever the axis scales). A linear scan: a
/// spot diagram's few thousand points cost microseconds per mouse move, and a scan has no index
/// to rebuild after every zoom.
/// </summary>
public static class ReadoutLookup
{
    /// <summary>Index of the point nearest <paramref name="cursor"/> within
    /// <paramref name="maxDistance"/>, the first on a tie; -1 when none is that close. Non-finite
    /// points are skipped.</summary>
    public static int Nearest(IReadOnlyList<Point2> points, Point2 cursor, double maxDistance)
    {
        int best = -1;
        double bestSquared = maxDistance * maxDistance;
        for (int i = 0; i < points.Count; i++)
        {
            double dx = points[i].X - cursor.X, dy = points[i].Y - cursor.Y;
            double d = dx * dx + dy * dy;
            if (d <= bestSquared && (best < 0 || d < bestSquared) && double.IsFinite(d))
            {
                best = i;
                bestSquared = d;
            }
        }
        return best;
    }

    /// <summary>The (series, index) of the nearest point over several series, earlier series
    /// winning ties; null when none is within <paramref name="maxDistance"/>.</summary>
    public static (int Series, int Index)? Nearest(IReadOnlyList<IReadOnlyList<Point2>> series, Point2 cursor,
        double maxDistance)
    {
        (int, int)? best = null;
        double bestDistance = maxDistance;
        for (int s = 0; s < series.Count; s++)
        {
            int i = Nearest(series[s], cursor, bestDistance);
            if (i < 0) continue;
            double d = (series[s][i] - cursor).Length;
            if (best is null || d < bestDistance)
            {
                best = (s, i);
                bestDistance = d;
            }
        }
        return best;
    }

    /// <summary>Distance from <paramref name="p"/> to the segment <paramref name="a"/>-<paramref name="b"/>.</summary>
    public static double DistanceToSegment(Point2 p, Point2 a, Point2 b)
    {
        var ab = b - a;
        double lengthSquared = Point2.Dot(ab, ab);
        double t = lengthSquared > 0 ? Math.Clamp(Point2.Dot(p - a, ab) / lengthSquared, 0, 1) : 0;
        return (a + ab * t - p).Length;
    }
}
