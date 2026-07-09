using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Meshing2D;

/// <summary>A classified 2D triangle: vertex indices plus the region it belongs to.</summary>
public readonly record struct Tri2(int A, int B, int C, int RegionId);

/// <summary>The finished 2D FEM triangulation with per-triangle region ids.</summary>
public sealed record PlanarMesh(IReadOnlyList<Point2> Points, IReadOnlyList<Tri2> Triangles)
{
    public Point2 Centroid(in Tri2 t) =>
        (Points[t.A] + Points[t.B] + Points[t.C]) * (1.0 / 3.0);
}

/// <summary>One meshing region: a set of polygons sharing a region id.</summary>
public sealed record PlanarRegion(int RegionId, IReadOnlyList<Polygon2> Polygons);

/// <summary>
/// Meshes a set of prioritized planar regions (e.g. copper over board) into a single
/// conformal triangulation: every polygon boundary is imprinted as constraint edges,
/// interior lattice points (deterministically jittered — exact grids feed cocircular
/// degeneracies to the predicates, same lesson as the 3D mesher) control element size,
/// and a bounded quality-refinement pass inserts circumcenters of skinny triangles.
/// Triangles are classified by centroid against the regions in priority order.
/// </summary>
public sealed class PlanarMesher
{
    /// <summary>Lattice/refinement keep-out distance from constraint edges, in units of h.</summary>
    private const double ConstraintClearance = 0.45;

    /// <summary>Refinement inserts circumcenters while a triangle's smallest angle is below this [deg].</summary>
    private const double MinAngleDegrees = 15.0;

    public PlanarMesh Mesh(IReadOnlyList<PlanarRegion> regionsByPriority, double targetEdgeLength,
        bool cleanPolygons = true)
    {
        if (targetEdgeLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetEdgeLength), "Target edge length must be positive.");
        double h = targetEdgeLength;

        // Clean each region's polygons: real Gerber outlines carry hundreds of sub-micron
        // spurs (arc tessellation, stroke joins) that sit below the mesher's jitter and
        // tangle the triangulation. Cleaning at min(10 µm, h/20) is invisible to the FEM.
        // Callers passing an ARRANGEMENT of atomic faces must disable this: adjacent faces
        // share boundaries vertex-for-vertex, and cleaning each face independently shifts
        // the two copies of a shared boundary differently — producing constraint chains
        // that genuinely cross, which no recovery can fix. Such callers pre-clean their
        // inputs before the booleans instead.
        if (cleanPolygons)
        {
            double cleanTol = Math.Min(PolygonCleaner.DefaultTolerance, h / 20);
            regionsByPriority = regionsByPriority
                .Select(r => new PlanarRegion(r.RegionId, PolygonCleaner.Clean(r.Polygons, cleanTol)))
                .Where(r => r.Polygons.Count > 0)
                .ToList();
        }
        if (regionsByPriority.Count == 0)
            throw new InvalidOperationException("No polygons remain after cleaning; the input geometry is degenerate.");

        // Containment index per region: identical decisions to ContainsPoint, but lattice/
        // refinement/classification query it millions of times on a large net.
        var regionIndexes = regionsByPriority
            .Select(r => (Region: r, Index: new PolygonSetIndex(r.Polygons)))
            .ToList();
        bool InsideAny(Point2 p) => regionIndexes.Any(r => r.Index.Contains(p));

        // 1. Constraint points and edges from every ring, deduplicated on the 1 nm grid
        //    the polygon engine snap-rounds to (shared boundary vertices merge exactly) —
        //    and WELDED within 10 nm: boolean arrangements can emit two vertices a
        //    sub-nanometre apart that straddle a 1 nm cell boundary, and a constraint
        //    edge between them is unrecoverable (its jittered endpoints are closer than
        //    any predicate can separate). 10 nm is still far below any copper feature.
        const double weldRadius = 10e-9;
        var points = new List<Point2>();
        var pointIndex = new Dictionary<(long, long), int>();
        var weldGrid = new Dictionary<(long, long), List<int>>();
        var constraints = new HashSet<(int, int)>();
        var segments = new List<(Point2 A, Point2 B)>();

        int IndexOf(Point2 p)
        {
            var key = ((long)Math.Round(p.X * 1e9), (long)Math.Round(p.Y * 1e9));
            if (pointIndex.TryGetValue(key, out int i)) return i;
            var cell = ((long)Math.Floor(p.X / weldRadius), (long)Math.Floor(p.Y / weldRadius));
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (weldGrid.TryGetValue((cell.Item1 + dx, cell.Item2 + dy), out var bucket))
                        foreach (int j in bucket)
                            if ((points[j] - p).Length <= weldRadius)
                            {
                                pointIndex[key] = j;         // alias the new nm-key onto the weld target
                                return j;
                            }
            pointIndex[key] = points.Count;
            if (!weldGrid.TryGetValue(cell, out var own)) weldGrid[cell] = own = new List<int>();
            own.Add(points.Count);
            points.Add(p);
            return points.Count - 1;
        }

        // Inward-offset "boundary layer" points collected here, added after all
        // constraints so they can be filtered against the full segment set.
        var boundaryLayer = new List<Point2>();

        foreach (var region in regionsByPriority)
            foreach (var polygon in region.Polygons)
                foreach (var ring in AllRings(polygon))
                {
                    // Interior lies to the left of a CCW ring, right of a CW hole.
                    double inwardSign = Polygon2.RingArea(ring) >= 0 ? 1.0 : -1.0;
                    int first = -1, prev = -1;
                    for (int i = 0; i < ring.Count; i++)
                    {
                        var a = ring[i];
                        var b = ring[(i + 1) % ring.Count];
                        int ia = IndexOf(a);
                        if (i == 0) first = ia;
                        if (prev >= 0) AddConstraintChain(prev, ia);
                        prev = ia;

                        // Split long edges so constraint spacing matches h.
                        double len = (b - a).Length;
                        int pieces = Math.Max(1, (int)Math.Ceiling(len / h));
                        for (int s = 1; s < pieces; s++)
                        {
                            int im = IndexOf(a + (b - a) * ((double)s / pieces));
                            AddConstraintChain(prev, im);
                            prev = im;
                        }
                        segments.Add((a, b));

                        // A row of points just inside the edge guarantees every boundary
                        // vertex pairs with an interior point rather than forming a
                        // zero-area sliver with its collinear neighbours.
                        var dir = b - a;
                        double dl = dir.Length;
                        if (dl > 0)
                        {
                            var inward = new Point2(-dir.Y, dir.X) * (inwardSign / dl);
                            for (int s = 0; s < pieces; s++)
                            {
                                var mid = a + dir * ((s + 0.5) / pieces);
                                boundaryLayer.Add(mid + inward * (0.6 * h));
                            }
                        }
                    }
                    AddConstraintChain(prev, first);
                }

        void AddConstraintChain(int u, int v)
        {
            if (u != v)
                constraints.Add(u < v ? (u, v) : (v, u));
        }

        // 2a. Boundary-layer points, kept only where they fall inside the domain and
        //     don't crowd another constraint (e.g. a nearby copper edge).
        var segmentGrid = new SegmentGrid(segments, h);
        foreach (var p in boundaryLayer)
        {
            if (!InsideAny(p)) continue;
            if (segmentGrid.AnyWithin(p, 0.35 * h)) continue;
            IndexOf(p);
        }

        // 2b. Interior lattice with deterministic jitter, clear of all constraints.
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        int nx = (int)((maxX - minX) / h);
        int ny = (int)((maxY - minY) / h);
        for (int ix = 1; ix <= nx; ix++)
            for (int iy = 1; iy <= ny; iy++)
            {
                var p = new Point2(minX + ix * h + Jitter(ix, iy, 17) * h,
                                   minY + iy * h + Jitter(ix, iy, 71) * h);
                if (p.X >= maxX || p.Y >= maxY) continue;
                if (!InsideAny(p)) continue;
                if (segmentGrid.AnyWithin(p, ConstraintClearance * h)) continue;
                IndexOf(p);
            }

        // 3. Constrained Delaunay triangulation.
        var cdt = new Cdt2D();
        cdt.Triangulate(points, constraints);

        // 4. Bounded quality refinement: circumcenters of skinny in-domain triangles.
        Refine(cdt, InsideAny, segmentGrid, h);

        // 5. Classification and vertex compaction.
        return Classify(cdt, regionIndexes.Select(r => (r.Region.RegionId, r.Index)).ToList());
    }

    /// <summary>Deterministic pseudo-random offset in (−0.05, 0.05) — reproducible meshes.</summary>
    private static double Jitter(int i, int j, int salt)
    {
        uint x = (uint)(i * 2654435761 + j * 40503 + salt * 97911);
        x ^= x >> 13; x *= 2246822519u; x ^= x >> 16;
        return ((x % 10000) / 10000.0 - 0.5) * 0.1;
    }

    private void Refine(Cdt2D cdt, Func<Point2, bool> insideAny, SegmentGrid segmentGrid, double h)
    {
        double minAngleRad = MinAngleDegrees * Math.PI / 180;
        int budget = Math.Max(256, cdt.Points.Count);           // hard cap keeps this terminating
        for (int pass = 0; pass < 12 && budget > 0; pass++)
        {
            var centers = new List<Point2>();
            foreach (var (a, b, c) in cdt.Triangles())
            {
                var pa = cdt.Points[a]; var pb = cdt.Points[b]; var pc = cdt.Points[c];
                var centroid = (pa + pb + pc) * (1.0 / 3.0);
                if (!insideAny(centroid)) continue;
                if (MinAngle(pa, pb, pc) >= minAngleRad && Circumradius(pa, pb, pc) <= 1.3 * h) continue;

                var cc = Circumcenter(pa, pb, pc);
                if (!insideAny(cc)) continue;                   // would encroach the boundary
                if (segmentGrid.AnyWithin(cc, ConstraintClearance * h)) continue;
                centers.Add(cc);
            }
            if (centers.Count == 0) break;
            foreach (var cc in centers)
            {
                if (budget-- <= 0) break;
                cdt.InsertPoint(cc);
            }
        }
    }

    private static PlanarMesh Classify(Cdt2D cdt, IReadOnlyList<(int RegionId, PolygonSetIndex Index)> regions)
    {
        // Nearly-collinear boundary points can form near-zero-area slivers whose
        // circumcircle lies outside the domain (so no interior point invalidates them).
        // They carry no meaningful area and would extrude to degenerate tets, so drop
        // anything below half a degree; the vertices stay covered by their neighbours.
        const double degenerateAngleRad = 0.5 * Math.PI / 180;

        var kept = new List<Tri2>();
        foreach (var (a, b, c) in cdt.Triangles())
        {
            if (MinAngle(cdt.Points[a], cdt.Points[b], cdt.Points[c]) < degenerateAngleRad) continue;
            var centroid = (cdt.Points[a] + cdt.Points[b] + cdt.Points[c]) * (1.0 / 3.0);
            foreach (var (regionId, index) in regions)
            {
                if (!index.Contains(centroid)) continue;
                kept.Add(new Tri2(a, b, c, regionId));
                break;                                          // priority order: first hit wins
            }
        }
        if (kept.Count == 0)
            throw new InvalidOperationException(
                "No triangles fall inside any region — check the input polygons and units.");

        // Compact to used vertices.
        var remap = new Dictionary<int, int>();
        var usedPoints = new List<Point2>();
        int Map(int v)
        {
            if (remap.TryGetValue(v, out int m)) return m;
            remap[v] = usedPoints.Count;
            usedPoints.Add(cdt.Points[v]);
            return usedPoints.Count - 1;
        }
        var tris = kept.Select(t => new Tri2(Map(t.A), Map(t.B), Map(t.C), t.RegionId)).ToList();
        return new PlanarMesh(usedPoints, tris);
    }

    // ---------------- Geometry helpers ----------------

    private static IEnumerable<IReadOnlyList<Point2>> AllRings(Polygon2 polygon)
    {
        yield return polygon.Outer;
        foreach (var hole in polygon.Holes)
            yield return hole;
    }

    /// <summary>Even-odd containment against a polygon set with holes.</summary>
    public static bool ContainsPoint(IReadOnlyList<Polygon2> polygons, Point2 p) =>
        polygons.Any(polygon => RingContains(polygon.Outer, p)
                                && !polygon.Holes.Any(hole => RingContains(hole, p)));

    private static bool RingContains(IReadOnlyList<Point2> ring, Point2 p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if (a.Y > p.Y != b.Y > p.Y
                && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static double MinAngle(Point2 a, Point2 b, Point2 c)
    {
        double la = (c - b).Length, lb = (a - c).Length, lc = (b - a).Length;
        double angleA = Math.Acos(Math.Clamp((lb * lb + lc * lc - la * la) / (2 * lb * lc), -1, 1));
        double angleB = Math.Acos(Math.Clamp((la * la + lc * lc - lb * lb) / (2 * la * lc), -1, 1));
        return Math.Min(angleA, Math.Min(angleB, Math.PI - angleA - angleB));
    }

    private static Point2 Circumcenter(Point2 a, Point2 b, Point2 c)
    {
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-300) return (a + b + c) * (1.0 / 3.0);
        double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
        return new Point2(
            (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
    }

    private static double Circumradius(Point2 a, Point2 b, Point2 c)
    {
        double la = (c - b).Length, lb = (a - c).Length, lc = (b - a).Length;
        double area = Math.Abs(Point2.Cross(b - a, c - a)) / 2;
        return area < 1e-300 ? double.MaxValue : la * lb * lc / (4 * area);
    }
}
