using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// "Is this point within r of any feature edge?" over a uniform grid whose cell IS the query
/// radius: every segment is registered in each cell it passes through, so the 27-cell
/// neighbourhood of a query point holds every segment that could be within r. Exact — the
/// grid only prunes, the test itself is the true point-to-segment distance.
/// </summary>
internal sealed class FeatureEdgeProximity
{
    private readonly double _cell;
    private readonly List<(Vector3D A, Vector3D B)> _segments = new();
    private readonly Dictionary<(long, long, long), List<int>> _grid = new();

    /// <param name="cell">Grid cell size. Queries must use a radius no larger than this,
    /// or the 27-cell neighbourhood could miss a segment.</param>
    public FeatureEdgeProximity(double cell) => _cell = cell;

    public bool IsEmpty => _segments.Count == 0;

    public void Add(Vector3D a, Vector3D b)
    {
        int index = _segments.Count;
        _segments.Add((a, b));

        // Walk the segment in half-cell steps, so no cell it crosses is skipped.
        double length = (b - a).Length;
        int steps = Math.Max(1, (int)Math.Ceiling(2 * length / _cell));
        (long, long, long)? previous = null;
        for (int s = 0; s <= steps; s++)
        {
            var key = Cell(a + (b - a) * ((double)s / steps));
            if (previous is { } p && p == key) continue;
            previous = key;
            if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<int>();
            if (!list.Contains(index)) list.Add(index);
        }
    }

    public bool IsWithin(Vector3D p, double radius)
    {
        if (_segments.Count == 0) return false;
        var (cx, cy, cz) = Cell(p);
        for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
                for (long dz = -1; dz <= 1; dz++)
                {
                    if (!_grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                    foreach (int i in list)
                        if (SegmentDistance.PointToSegment(p, _segments[i].A, _segments[i].B) < radius)
                            return true;
                }
        return false;
    }

    private (long, long, long) Cell(Vector3D p) => (
        (long)Math.Floor(p.X / _cell),
        (long)Math.Floor(p.Y / _cell),
        (long)Math.Floor(p.Z / _cell));
}
