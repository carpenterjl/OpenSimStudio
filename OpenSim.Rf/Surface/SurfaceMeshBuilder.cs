using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// Deterministic surface-mesh sources for the RWG solver, each returning a
/// <see cref="SurfaceGridResult"/> with its delta-gap port built in:
///  • Rectangular plate — a structured m×n grid with alternating diagonals; a vertex
///    ROW is placed at the requested port fraction, so the port's colinear interior
///    edges exist BY CONSTRUCTION (current crosses them along +y).
///  • Rectangular patch over ground — the same grid at z = groundZ + height with the
///    Stage-A image machinery; the ground plane itself is never meshed.
///  • Arbitrary polygon (a PCB copper island) — boundary rings resampled to the target
///    edge length, an interior lattice, then <see cref="Cdt2D"/> with boundary
///    constraints; triangle containment is tested against the JITTERED ring (the
///    triangulation's own coordinates — the STEP tessellator lesson), and the port is
///    the single interior edge nearest the feed hint.
/// The unknown cap mirrors the wire builder (dense LU is O(N³)); sliver triangles are
/// WARNED about, never silently accepted.
/// </summary>
public static class SurfaceMeshBuilder
{
    /// <summary>Triangles with a smaller minimum angle [deg] are reported.</summary>
    private const double MinAngleWarningDegrees = 10.0;

    public static SurfaceGridResult BuildRectangularPlate(double width, double length,
        double maxEdgeLength, double z = 0, double portFraction = 0.5,
        GroundPlane? ground = null, int maxUnknowns = 2000)
    {
        if (width <= 0 || length <= 0 || maxEdgeLength <= 0)
            return SurfaceGridResult.Failure("the plate needs positive width, length, and element size");
        if (ground is not null && z <= ground.SurfaceZ)
            return SurfaceGridResult.Failure(
                $"the plate at z = {z:g4} must sit strictly above the ground plane at z = {ground.SurfaceZ:g4}");

        int m = Math.Max(1, (int)Math.Ceiling(width / maxEdgeLength));
        int n = Math.Max(2, (int)Math.Ceiling(length / maxEdgeLength));
        int portRow = Math.Clamp((int)Math.Round(portFraction * n), 1, n - 1);

        var vertices = new List<Vector3D>((m + 1) * (n + 1));
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= m; i++)
                vertices.Add(new Vector3D(
                    -width / 2 + width * i / m, -length / 2 + length * j / n, z));
        int Index(int i, int j) => j * (m + 1) + i;

        var triangles = new List<(int, int, int)>(2 * m * n);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < m; i++)
            {
                int v00 = Index(i, j), v10 = Index(i + 1, j);
                int v11 = Index(i + 1, j + 1), v01 = Index(i, j + 1);
                // Alternating diagonals (deterministic), CCW seen from +z.
                if ((i + j) % 2 == 0)
                {
                    triangles.Add((v00, v10, v11));
                    triangles.Add((v00, v11, v01));
                }
                else
                {
                    triangles.Add((v00, v10, v01));
                    triangles.Add((v10, v11, v01));
                }
            }

        SurfaceStructure structure;
        try { structure = new SurfaceStructure(vertices, triangles, ground); }
        catch (InvalidOperationException ex) { return SurfaceGridResult.Failure(ex.Message); }

        if (structure.BasisCount > maxUnknowns)
            return SurfaceGridResult.Failure(
                $"{structure.BasisCount} RWG unknowns exceed the {maxUnknowns} cap (dense LU is O(N³)) — " +
                "raise the element size or lower the frequency");

        // Port: the interior edges along the port vertex row (they exist by construction).
        var portBases = new List<int>();
        for (int e = 0; e < structure.Edges.Count; e++)
        {
            var edge = structure.Edges[e];
            int j1 = edge.V1 / (m + 1), j2 = edge.V2 / (m + 1);
            if (j1 == portRow && j2 == portRow) portBases.Add(e);
        }
        if (portBases.Count == 0)
            return SurfaceGridResult.Failure("no interior port edges exist — the plate is too coarse");

        return SurfaceGridResult.Success(structure,
            new SurfacePort(portBases, new Vector3D(0, 1, 0)));
    }

    public static SurfaceGridResult BuildPatchOverGround(double width, double length,
        double heightAboveGround, double groundZ, double maxEdgeLength, int maxUnknowns = 2000)
    {
        if (heightAboveGround <= 0)
            return SurfaceGridResult.Failure("the patch height above the ground plane must be positive");
        // Edge-fed patch: the port sits at the first interior vertex row (a boundary
        // edge carries no basis, so the feed cannot sit ON the rim — stated to the user).
        return BuildRectangularPlate(width, length, maxEdgeLength,
            z: groundZ + heightAboveGround, portFraction: 0,
            ground: new GroundPlane(groundZ), maxUnknowns: maxUnknowns);
    }

    /// <summary>Meshes a planar polygon (e.g. a PCB copper island, meters) at height
    /// <paramref name="z"/>. The port is the single interior edge nearest
    /// <paramref name="feedHint"/> (the island centroid when null).</summary>
    public static SurfaceGridResult BuildFromPolygon(Polygon2 shape, double maxEdgeLength,
        double z, Point2? feedHint = null, GroundPlane? ground = null, int maxUnknowns = 2000)
    {
        if (maxEdgeLength <= 0)
            return SurfaceGridResult.Failure("the element size must be positive");
        if (ground is not null && z <= ground.SurfaceZ)
            return SurfaceGridResult.Failure(
                $"the copper at z = {z:g4} must sit strictly above the ground plane at z = {ground.SurfaceZ:g4}");

        var warnings = new List<string>();
        var cleaned = PolygonCleaner.Clean(new[] { shape });
        if (cleaned.Count == 0)
            return SurfaceGridResult.Failure("the polygon collapsed during cleaning (degenerate outline)");
        var polygon = cleaned.OrderByDescending(p => Math.Abs(Polygon2.RingArea(p.Outer))).First();
        if (cleaned.Count > 1)
            warnings.Add($"The outline split into {cleaned.Count} pieces after cleaning; the largest is meshed.");

        // Boundary rings resampled to the element length (all CCW/CW as given — the
        // orientation only matters for containment, handled by PolygonSetIndex).
        var points = new List<Point2>();
        var constraints = new List<(int U, int V)>();
        var ringRanges = new List<(int Start, int Count)>();
        foreach (var ring in Polygon2.OrientedRings(polygon))
        {
            int start = points.Count;
            var resampled = ResampleRing(ring, maxEdgeLength);
            if (resampled.Count < 3)
                return SurfaceGridResult.Failure("a boundary ring degenerated during resampling");
            points.AddRange(resampled);
            for (int i = 0; i < resampled.Count; i++)
                constraints.Add((start + i, start + (i + 1) % resampled.Count));
            ringRanges.Add((start, resampled.Count));
        }

        // Interior lattice, kept clear of the boundary (SegmentGrid proximity test).
        var latticeInside = new PolygonSetIndex(new[] { polygon });
        var segments = new List<(Point2 A, Point2 B)>();
        foreach (var (start, count) in ringRanges)
            for (int i = 0; i < count; i++)
                segments.Add((points[start + i], points[start + (i + 1) % count]));
        double spacing = maxEdgeLength * 0.87;   // ≈ equilateral row spacing
        var grid = new SegmentGrid(segments, spacing);
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        int boundaryCount = points.Count;
        int row = 0;
        for (double y = minY + spacing / 2; y < maxY; y += spacing, row++)
            for (double x = minX + spacing / 2 + (row % 2) * spacing / 2; x < maxX; x += spacing)
            {
                var p = new Point2(x, y);
                if (!latticeInside.Contains(p)) continue;
                if (grid.AnyWithin(p, 0.45 * spacing)) continue;
                points.Add(p);
            }

        var cdt = new Cdt2D();
        try { cdt.Triangulate(points, constraints); }
        catch (Exception ex)
        {
            return SurfaceGridResult.Failure($"boundary constraint recovery failed — {ex.Message}");
        }

        // Containment against the JITTERED rings — the triangulation's own coordinates.
        var jitteredRings = new List<IReadOnlyList<Point2>>();
        foreach (var (start, count) in ringRanges)
        {
            var ring = new List<Point2>(count);
            for (int i = 0; i < count; i++) ring.Add(cdt.Points[start + i]);
            jitteredRings.Add(ring);
        }
        var inside = new PolygonSetIndex(new[]
        {
            new Polygon2(jitteredRings[0], jitteredRings.Skip(1).ToArray())
        });

        var vertices = cdt.Points.Select(p => new Vector3D(p.X, p.Y, z)).ToList();
        var triangles = new List<(int, int, int)>();
        int slivers = 0;
        foreach (var (a, b, c) in cdt.Triangles())
        {
            var pa = cdt.Points[a];
            var pb = cdt.Points[b];
            var pc = cdt.Points[c];
            var centroid = new Point2((pa.X + pb.X + pc.X) / 3, (pa.Y + pb.Y + pc.Y) / 3);
            if (!inside.Contains(centroid)) continue;
            triangles.Add((a, b, c));   // Cdt2D emits CCW → consistent +z normals
            if (MinAngleDegrees(pa, pb, pc) < MinAngleWarningDegrees) slivers++;
        }
        if (triangles.Count == 0)
            return SurfaceGridResult.Failure("no triangles survived containment — the outline may be degenerate");
        if (slivers > 0)
            warnings.Add($"{slivers} triangles have a minimum angle below {MinAngleWarningDegrees}° " +
                         "(boundary slivers); expect reduced local accuracy.");

        SurfaceStructure structure;
        try { structure = new SurfaceStructure(vertices, triangles, ground); }
        catch (InvalidOperationException ex) { return SurfaceGridResult.Failure(ex.Message); }

        if (structure.BasisCount > maxUnknowns)
            return SurfaceGridResult.Failure(
                $"{structure.BasisCount} RWG unknowns exceed the {maxUnknowns} cap (dense LU is O(N³)) — " +
                "raise the element size or lower the frequency");
        if (structure.BasisCount == 0)
            return SurfaceGridResult.Failure(
                "the mesh has no interior edges — the polygon is too small relative to the element size");

        var hint = feedHint ?? Centroid(polygon.Outer);
        int portBasis = structure.NearestEdge(new Vector3D(hint.X, hint.Y, z));
        var portEdge = structure.Edges[portBasis];
        var crossing = structure.TriangleCentroids[portEdge.MinusTriangle]
                     - structure.TriangleCentroids[portEdge.PlusTriangle];
        return SurfaceGridResult.Success(structure,
            new SurfacePort(new[] { portBasis }, crossing.Normalized())) with
        { Warnings = warnings };
    }

    private static List<Point2> ResampleRing(IReadOnlyList<Point2> ring, double maxEdgeLength)
    {
        var result = new List<Point2>();
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            result.Add(a);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            int pieces = (int)Math.Ceiling(length / maxEdgeLength);
            for (int p = 1; p < pieces; p++)
                result.Add(new Point2(a.X + dx * p / pieces, a.Y + dy * p / pieces));
        }
        return result;
    }

    private static Point2 Centroid(IReadOnlyList<Point2> ring)
    {
        double x = 0, y = 0;
        foreach (var p in ring) { x += p.X; y += p.Y; }
        return new Point2(x / ring.Count, y / ring.Count);
    }

    private static double MinAngleDegrees(Point2 a, Point2 b, Point2 c)
    {
        double la = Distance(b, c), lb = Distance(c, a), lc = Distance(a, b);
        double smallest = Math.PI;
        foreach (var (opposite, s1, s2) in new[] { (la, lb, lc), (lb, lc, la), (lc, la, lb) })
        {
            double cos = Math.Clamp((s1 * s1 + s2 * s2 - opposite * opposite) / (2 * s1 * s2), -1, 1);
            smallest = Math.Min(smallest, Math.Acos(cos));
        }
        return smallest * 180 / Math.PI;
    }

    private static double Distance(Point2 a, Point2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
