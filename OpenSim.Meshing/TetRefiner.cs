using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Quality-driven Delaunay refinement: inserts Steiner points at the circumcenters of
/// bad tets in an existing Bowyer–Watson triangulation until every tet the clip stage
/// will keep reaches the target radius-ratio quality (or the budget runs out). The 3D
/// analog of the 2D <c>PlanarMesher.Refine</c> pass. Only circumcenters that stay
/// inside the solid and clear of the surface are inserted — this pass improves the
/// interior; boundary slivers (circumcenters escape, midpoint splits thrash into new
/// slivers) are the clip stage's sliver cull's job, not insertion's.
/// </summary>
public static class TetRefiner
{
    private const int MaxPasses = 8;

    /// <summary>
    /// Refines in place; appends every attempted candidate to <paramref name="points"/>
    /// so its indices stay aligned with the triangulation's internal list. Returns the
    /// number of points actually inserted.
    /// </summary>
    public static int Refine(
        BowyerWatson triangulation,
        List<Vector3D> points,
        SolidClassifier classifier,
        SurfaceDistanceField distanceField,
        double h,
        double targetMinQuality,
        int maxInsertions,
        CancellationToken cancellationToken = default)
    {
        // Termination proof: passes are capped, insertions are budgeted, and the
        // spacing grid rejects any candidate within 0.2·h of an existing point, so
        // point density (and thus work) is bounded regardless of geometry.
        var spacing = new SpacingGrid(0.2 * h);
        foreach (var p in points) spacing.Add(p);

        // Exact circumcenters (equidistant from four vertices) and exact edge midpoints
        // (collinear with their endpoints) are precisely the degenerate inputs that
        // break the floating-point Delaunay predicates — the same reason the mesher
        // jitters its seed points and Cdt2D jitters every inserted 2D point. Without
        // this the cavity mechanism can produce overlapping tets.
        var rng = new Random(246813579);
        Vector3D Jitter(Vector3D p)
        {
            double amplitude = 2e-3 * h;
            return p + new Vector3D(
                (rng.NextDouble() - 0.5) * amplitude,
                (rng.NextDouble() - 0.5) * amplitude,
                (rng.NextDouble() - 0.5) * amplitude);
        }

        double surfaceTolerance = 5e-2 * h;    // same near-surface band as the clip stage
        int inserted = 0;
        int budget = maxInsertions;

        for (int pass = 0; pass < MaxPasses && budget > 0; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Snapshot the bad tets first, then insert: inserts invalidate tets, and a
            // stale candidate is harmless (it either still helps or lands duplicate).
            var candidates = new List<Vector3D>();
            foreach (var (a, b, c, d) in triangulation.FiniteTets())
            {
                var pa = points[a];
                var pb = points[b];
                var pc = points[c];
                var pd = points[d];

                // Don't spend budget on tets the clip stage discards anyway (outside
                // the solid and not near the surface) — same keep test as the clip.
                var centroid = (pa + pb + pc + pd) / 4.0;
                bool nearSurface = distanceField.Distance(centroid) <= surfaceTolerance;
                if (!nearSurface && !classifier.IsInside(centroid))
                    continue;
                if (MeshQuality.RadiusRatio(pa, pb, pc, pd) >= targetMinQuality)
                    continue;

                // The circumcenter destroys the offending circumsphere, but only
                // qualifies when it stays inside and clear of the surface — surface
                // midpoint insertion was tried and thrashes (each midpoint spawns new
                // marginal tets and burns the budget without raising the floor).
                var cc = MeshQuality.Circumcenter(pa, pb, pc, pd);
                if (cc is not null && classifier.IsInside(cc.Value)
                    && distanceField.Distance(cc.Value) >= 0.3 * h)
                    candidates.Add(cc.Value);
            }
            if (candidates.Count == 0)
                break;

            int acceptedThisPass = 0;
            foreach (var raw in candidates)
            {
                if (budget <= 0) break;
                var candidate = Jitter(raw);
                if (!spacing.IsClear(candidate)) continue;
                budget--;
                points.Add(candidate);          // keep index parity with the triangulation
                spacing.Add(candidate);
                if (triangulation.InsertPoint(candidate))
                {
                    inserted++;
                    acceptedThisPass++;
                }
            }
            if (acceptedThisPass == 0)
                break;                          // every remaining candidate is blocked
        }
        return inserted;
    }

    /// <summary>Hash-grid minimum-spacing filter (cell size = the spacing itself).</summary>
    private sealed class SpacingGrid
    {
        private readonly double _spacing;
        private readonly Dictionary<(long, long, long), List<Vector3D>> _cells = new();

        public SpacingGrid(double spacing) => _spacing = spacing;

        private (long, long, long) Cell(Vector3D p) => (
            (long)Math.Floor(p.X / _spacing),
            (long)Math.Floor(p.Y / _spacing),
            (long)Math.Floor(p.Z / _spacing));

        public bool IsClear(Vector3D p)
        {
            var (cx, cy, cz) = Cell(p);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket))
                            foreach (var q in bucket)
                                if (Vector3D.Distance(p, q) < _spacing)
                                    return false;
            return true;
        }

        public void Add(Vector3D p)
        {
            var cell = Cell(p);
            if (!_cells.TryGetValue(cell, out var bucket))
                _cells[cell] = bucket = new List<Vector3D>();
            bucket.Add(p);
        }
    }
}
