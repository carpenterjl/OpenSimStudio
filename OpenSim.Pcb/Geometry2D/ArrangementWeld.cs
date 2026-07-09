using OpenSim.Core.Geometry2D;

namespace OpenSim.Pcb.Geometry2D;

/// <summary>
/// Conformal repair for a planar arrangement of atomic faces produced by successive
/// polygon booleans. The two boolean outputs that share a face boundary (Intersect and
/// Difference against the same clips) can disagree by a snap-rounding grid unit at
/// grazing tangencies, leaving the two copies of the "same" boundary crossing each other
/// a nanometre deep — which a constrained triangulation can never recover. The repair is
/// two deterministic passes well below any real feature size: (1) weld all face vertices
/// to shared representatives within the tolerance, (2) imprint every representative that
/// lies on the interior of another ring's edge into that edge (T-junction resolution).
/// Grazing crossings then become chains meeting at shared vertices, which constrain fine.
/// </summary>
public static class ArrangementWeld
{
    public static IReadOnlyList<Polygon2> Apply(IReadOnlyList<Polygon2> faces, double tolerance)
    {
        // ---- Pass 1: global vertex weld (first-come representative, grid hash). ----
        var reps = new List<Point2>();
        var grid = new Dictionary<(long, long), List<int>>();
        long Cell(double v) => (long)Math.Floor(v / tolerance);

        int RepOf(Point2 p)
        {
            var (cx, cy) = (Cell(p.X), Cell(p.Y));
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((cx + dx, cy + dy), out var bucket))
                        foreach (int j in bucket)
                            if ((reps[j] - p).Length <= tolerance)
                                return j;
            if (!grid.TryGetValue((cx, cy), out var own)) grid[(cx, cy)] = own = new List<int>();
            own.Add(reps.Count);
            reps.Add(p);
            return reps.Count - 1;
        }

        // Rings as representative-id lists, tagged with their face and outer/hole role.
        var rings = new List<(int Face, bool IsOuter, List<int> Ids)>();
        for (int f = 0; f < faces.Count; f++)
        {
            rings.Add((f, true, WeldRing(faces[f].Outer, RepOf)));
            foreach (var hole in faces[f].Holes)
                rings.Add((f, false, WeldRing(hole, RepOf)));
        }

        // ---- Pass 2: imprint vertices onto foreign ring edges they nearly lie on. ----
        // Brute force with a bbox pre-check: faces are capped by the net-complexity guard,
        // so V·E stays small enough and the pass is exact and order-deterministic.
        for (int r = 0; r < rings.Count; r++)
        {
            var ids = rings[r].Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                var a = reps[ids[i]];
                var b = reps[ids[(i + 1) % ids.Count]];
                double minX = Math.Min(a.X, b.X) - tolerance, maxX = Math.Max(a.X, b.X) + tolerance;
                double minY = Math.Min(a.Y, b.Y) - tolerance, maxY = Math.Max(a.Y, b.Y) + tolerance;

                // Exclude only the edge's own endpoints. Every other vertex within tolerance
                // is imprinted — including ring neighbours and vertices this ring shares with
                // other rings. Imprinting a neighbour turns a back-folded spike into a
                // duplicated (harmless) constraint; NOT imprinting leaves a constrained chain
                // passing through a vertex that lies on another constrained edge, which no
                // flip/split recovery can represent.
                List<(double T, int Id)>? hits = null;
                for (int j = 0; j < reps.Count; j++)
                {
                    if (j == ids[i] || j == ids[(i + 1) % ids.Count]) continue;
                    var p = reps[j];
                    if (p.X < minX || p.X > maxX || p.Y < minY || p.Y > maxY) continue;
                    var ab = b - a;
                    double len2 = Point2.Dot(ab, ab);
                    if (len2 < 1e-300) continue;
                    double t = Point2.Dot(p - a, ab) / len2;
                    if (t <= 0 || t >= 1) continue;
                    if ((p - (a + ab * t)).Length > tolerance) continue;
                    (hits ??= new List<(double, int)>()).Add((t, j));
                }
                if (hits is null) continue;
                hits.Sort((x, y) => x.T.CompareTo(y.T));
                ids.InsertRange(i + 1, hits.Select(h => h.Id));
                i += hits.Count;                                 // skip past the inserted vertices
            }
        }

        // ---- Rebuild faces, dropping rings that collapsed. ----
        var result = new List<Polygon2>();
        for (int f = 0; f < faces.Count; f++)
        {
            IReadOnlyList<Point2>? outer = null;
            var holes = new List<IReadOnlyList<Point2>>();
            foreach (var (face, isOuter, ids) in rings)
            {
                if (face != f) continue;
                var ring = Dedup(ids).Select(id => reps[id]).ToList();
                if (ring.Count < 3) continue;
                if (isOuter) outer = ring;
                else holes.Add(ring);
            }
            if (outer is not null)
                result.Add(new Polygon2(outer, holes));
        }
        return result;
    }

    private static List<int> WeldRing(IReadOnlyList<Point2> ring, Func<Point2, int> repOf) =>
        Dedup(ring.Select(repOf).ToList());

    /// <summary>Removes consecutive duplicate ids (including wrap-around).</summary>
    private static List<int> Dedup(List<int> ids)
    {
        var result = new List<int>(ids.Count);
        foreach (int id in ids)
            if (result.Count == 0 || result[^1] != id)
                result.Add(id);
        while (result.Count > 1 && result[^1] == result[0])
            result.RemoveAt(result.Count - 1);
        return result;
    }
}
