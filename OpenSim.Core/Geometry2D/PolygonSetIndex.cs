namespace OpenSim.Core.Geometry2D;

/// <summary>
/// Y-strip acceleration index for even-odd point-in-polygon-set queries. Containment
/// decisions are identical to the PCB planar mesher's ContainsPoint — the ray cast
/// only ever counts edges whose y-range brackets the query, so bucketing edges by
/// y-strip skips the rest without touching the predicate. Meshing queries containment
/// once per lattice point, refinement candidate, and triangle; on a pour net that is
/// millions of queries against thousands of edges, which is what this index amortizes.
/// </summary>
public sealed class PolygonSetIndex
{
    private sealed class RingIndex
    {
        public readonly double MinY, StripHeight;
        public readonly List<(Point2 A, Point2 B)>[] Strips;

        public RingIndex(IReadOnlyList<Point2> ring)
        {
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in ring)
            {
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }
            MinY = minY;
            int stripCount = Math.Clamp(ring.Count / 4, 1, 256);
            StripHeight = Math.Max((maxY - minY) / stripCount, 1e-30);
            Strips = new List<(Point2, Point2)>[stripCount];
            for (int s = 0; s < stripCount; s++) Strips[s] = new List<(Point2, Point2)>();

            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                int s0 = StripOf(Math.Min(a.Y, b.Y));
                int s1 = StripOf(Math.Max(a.Y, b.Y));
                for (int s = s0; s <= s1; s++) Strips[s].Add((a, b));
            }
        }

        private int StripOf(double y) =>
            Math.Clamp((int)((y - MinY) / StripHeight), 0, Strips.Length - 1);

        public bool Contains(Point2 p)
        {
            // Exact same crossing predicate as PlanarMesher.RingContains, over the one
            // strip that can contribute crossings.
            bool inside = false;
            foreach (var (a, b) in Strips[StripOf(p.Y)])
                if (a.Y > p.Y != b.Y > p.Y
                    && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            return inside;
        }
    }

    private readonly List<(RingIndex Outer, RingIndex[] Holes)> _polygons = new();

    public PolygonSetIndex(IReadOnlyList<Polygon2> polygons)
    {
        foreach (var polygon in polygons)
            _polygons.Add((new RingIndex(polygon.Outer),
                polygon.Holes.Select(h => new RingIndex(h)).ToArray()));
    }

    /// <summary>Even-odd containment; equivalent to the PCB planar mesher's ContainsPoint.</summary>
    public bool Contains(Point2 p)
    {
        foreach (var (outer, holes) in _polygons)
        {
            if (!outer.Contains(p)) continue;
            bool inHole = false;
            foreach (var hole in holes)
                if (hole.Contains(p)) { inHole = true; break; }
            if (!inHole) return true;
        }
        return false;
    }
}
