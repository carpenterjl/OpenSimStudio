
namespace OpenSim.Core.Geometry2D;

/// <summary>
/// Uniform-grid proximity index over constraint segments, for "is any segment within r
/// of p?" queries with r ≤ the cell size. Every meshing keep-out test compares against a
/// fraction of the target edge length h, so with cell = h the 3×3 neighbourhood is a
/// guaranteed superset of candidates and the exact point–segment distance is only
/// computed for those — the decisions match the brute-force scan exactly.
/// </summary>
public sealed class SegmentGrid
{
    private readonly double _cell;
    private readonly Dictionary<(long, long), List<(Point2 A, Point2 B)>> _cells = new();

    public SegmentGrid(IReadOnlyList<(Point2 A, Point2 B)> segments, double cell)
    {
        _cell = cell;
        foreach (var seg in segments)
        {
            // Conservative: every cell overlapped by the segment's bbox.
            long x0 = Cell(Math.Min(seg.A.X, seg.B.X)), x1 = Cell(Math.Max(seg.A.X, seg.B.X));
            long y0 = Cell(Math.Min(seg.A.Y, seg.B.Y)), y1 = Cell(Math.Max(seg.A.Y, seg.B.Y));
            for (long cx = x0; cx <= x1; cx++)
                for (long cy = y0; cy <= y1; cy++)
                {
                    if (!_cells.TryGetValue((cx, cy), out var list))
                        _cells[(cx, cy)] = list = new List<(Point2, Point2)>();
                    list.Add(seg);
                }
        }
    }

    private long Cell(double v) => (long)Math.Floor(v / _cell);

    /// <summary>
    /// True when some segment lies within <paramref name="r"/> of <paramref name="p"/>.
    /// Requires r ≤ the grid cell size (asserted): beyond that the 3×3 neighbourhood
    /// would no longer be a superset of candidates.
    /// </summary>
    public bool AnyWithin(Point2 p, double r)
    {
        if (r > _cell)
            throw new ArgumentOutOfRangeException(nameof(r), "Query radius exceeds the grid cell size.");
        long cx = Cell(p.X), cy = Cell(p.Y);
        for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
                if (_cells.TryGetValue((cx + dx, cy + dy), out var list))
                    foreach (var (a, b) in list)
                        if (Distance(p, a, b) < r)
                            return true;
        return false;
    }

    private static double Distance(Point2 p, Point2 a, Point2 b)
    {
        var ab = b - a;
        double t = Math.Clamp(Point2.Dot(p - a, ab) / Math.Max(Point2.Dot(ab, ab), 1e-300), 0, 1);
        return (p - (a + ab * t)).Length;
    }
}
