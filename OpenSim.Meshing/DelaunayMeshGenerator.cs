using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Generates a tetrahedral FE mesh from closed surface geometry:
/// 1. refine the surface to the target edge length and sample its vertices,
/// 2. seed interior points on a regular grid (kept clear of the surface),
/// 3. Delaunay-triangulate all points (Bowyer–Watson),
/// 4. keep tetrahedra whose centroid lies inside the solid,
/// 5. extract the boundary skin and tag it with geometric face ids.
/// A deterministic sub-element jitter is applied to all points to break the
/// cospherical degeneracies that regular grids and flat faces would otherwise
/// feed the floating-point Delaunay predicates.
/// </summary>
public sealed class DelaunayMeshGenerator : IMeshGenerator
{
    public string Name => "Delaunay tetrahedral mesher";

    /// <summary>Fraction of the bounding-box diagonal used when TargetEdgeLength is 0 (auto).</summary>
    public double AutoEdgeFraction { get; init; } = 1.0 / 15.0;

    public FeMesh Generate(TriangleMesh geometry, MeshSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!geometry.IsWatertight())
            throw new InvalidOperationException(
                "Geometry is not watertight; repair the surface before meshing.");

        var bounds = geometry.Bounds;
        double h = settings.TargetEdgeLength > 0
            ? settings.TargetEdgeLength
            : bounds.Diagonal * AutoEdgeFraction;

        // 1. Surface sample points from the refined surface, thinned to a roughly
        // uniform spacing so anisotropic triangulations (long thin cap fans, dense
        // facet rings) cannot flood the Delaunay stage with badly spaced points.
        // Original geometry vertices go first so feature corners always survive.
        var refined = SurfaceRefiner.Refine(geometry, h);
        var points = ThinPoints(geometry.Vertices.Concat(refined.Vertices), 0.45 * h);
        int surfacePointCount = points.Count;

        // 2. Interior grid points, kept at least 0.45·h away from surface samples.
        var classifier = new SolidClassifier(geometry);
        var surfaceTree = new KdTree(refined.Vertices);
        var min = bounds.Min;
        var size = bounds.Size;
        int nx = Math.Max(1, (int)Math.Floor(size.X / h));
        int ny = Math.Max(1, (int)Math.Floor(size.Y / h));
        int nz = Math.Max(1, (int)Math.Floor(size.Z / h));
        for (int i = 0; i < nx; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j < ny; j++)
            {
                for (int k = 0; k < nz; k++)
                {
                    var p = new Vector3D(
                        min.X + (i + 0.5) * size.X / nx,
                        min.Y + (j + 0.5) * size.Y / ny,
                        min.Z + (k + 0.5) * size.Z / nz);
                    int nearest = surfaceTree.NearestNeighbor(p);
                    if (nearest >= 0 && Vector3D.Distance(refined.Vertices[nearest], p) < 0.45 * h)
                        continue;
                    if (classifier.IsInside(p))
                        points.Add(p);
                }
            }
        }

        // Deterministic jitter — small on surface points (breaks the exact-cocircular
        // grids that defeat floating-point Delaunay predicates; the geometric error is
        // 0.2% of an element edge), larger on interior grid points.
        var rng = new Random(987654321);
        for (int i = 0; i < points.Count; i++)
        {
            double amplitude = (i < surfacePointCount ? 2e-3 : 5e-2) * h;
            points[i] += new Vector3D(
                (rng.NextDouble() - 0.5) * amplitude,
                (rng.NextDouble() - 0.5) * amplitude,
                (rng.NextDouble() - 0.5) * amplitude);
        }

        // 3. Delaunay triangulation, then quality-driven refinement: Steiner points at
        // the circumcenters of bad tets (longest-edge midpoints for boundary slivers,
        // whose circumcenters escape the solid), mirroring the 2D PlanarMesher.Refine.
        var distanceField = new SurfaceDistanceField(refined);
        var triangulation = new BowyerWatson();
        triangulation.Triangulate(points, cancellationToken);
        if (settings.TargetMinQuality > 0)
        {
            int budget = settings.MaxRefinementPoints > 0
                ? settings.MaxRefinementPoints
                : Math.Max(1024, points.Count);
            TetRefiner.Refine(triangulation, points, classifier, distanceField, h,
                settings.TargetMinQuality, budget, cancellationToken);
        }
        var tets = triangulation.FiniteTets();

        // 4. Keep interior, non-degenerate tetrahedra. Because surface points are
        // jittered off the exact geometry, centroids of thin boundary tets can land
        // marginally outside; a tolerance of a few jitter amplitudes keeps them.
        // Conversely, sliver "pancakes" living entirely within the surface skin
        // (4 nearly coplanar surface points) are rejected by a quality gate.
        double surfaceTolerance = 5e-2 * h;
        double volEps = 1e-9 * h * h * h;
        const double sliverQuality = 0.02;
        var kept = new List<(int, int, int, int)>();
        foreach (var (a, b, c, d) in tets)
        {
            double vol6 = GeometricPredicates.Orient3D(points[a], points[b], points[c], points[d]);
            if (Math.Abs(vol6) / 6.0 < volEps)
                continue;
            var centroid = (points[a] + points[b] + points[c] + points[d]) / 4.0;
            double surfaceDistance = distanceField.Distance(centroid);
            bool nearSurface = surfaceDistance <= surfaceTolerance;
            if (!nearSurface && !classifier.IsInside(centroid))
                continue;
            // Sub-sliverQuality tets must never reach the FE system — they wreck the
            // CG conditioning far beyond their ~zero physical volume. The cull band is
            // 0.25·h (wider than the keep band): boundary "pancakes" whose four
            // vertices sit on the skin can have centroids 0.1–0.2·h deep, out of reach
            // of both refinement (circumcenters escape) and smoothing (boundary nodes
            // fixed). Wider bands cull sound-volume needles too and dent the surface.
            if (surfaceDistance <= 0.25 * h &&
                MeshQuality.RadiusRatio(points[a], points[b], points[c], points[d]) < sliverQuality)
                continue;
            kept.Add(vol6 > 0 ? (a, b, c, d) : (a, b, d, c));
        }
        if (kept.Count == 0)
            throw new InvalidOperationException(
                "Meshing produced no interior tetrahedra; try a smaller target edge length.");

        // The unconditional cull can pinch the skin (a boundary edge shared by four
        // faces). Resolve by removing further tets — never by re-admitting a sliver,
        // which would put a poison element back into the system.
        kept = BoundaryPinchResolver.Resolve(kept, points);

        // 5. Compact node numbering to used nodes.
        var nodeMap = new Dictionary<int, int>();
        var nodes = new List<Vector3D>();
        int Map(int old)
        {
            if (!nodeMap.TryGetValue(old, out int idx))
            {
                idx = nodes.Count;
                nodes.Add(points[old]);
                nodeMap[old] = idx;
            }
            return idx;
        }
        var elements = new List<Tet4>(kept.Count);
        foreach (var (a, b, c, d) in kept)
            elements.Add(new Tet4(Map(a), Map(b), Map(c), Map(d)));

        // 6. Smooth interior nodes (boundary skin stays fixed), then extract the skin.
        var boundaryNodes = new HashSet<int>();
        var faceUseForFixing = CountFaceUse(elements);
        foreach (var (face, use) in faceUseForFixing)
        {
            if (use != 1) continue;
            boundaryNodes.Add(face.Item1);
            boundaryNodes.Add(face.Item2);
            boundaryNodes.Add(face.Item3);
        }
        MeshSmoother.Smooth(nodes, elements, boundaryNodes);

        var boundary = ExtractBoundary(nodes, elements, refined);
        var mesh = new FeMesh(nodes, elements, boundary);

        // The quadratic upgrade is the last step so mid-edge nodes are generated on
        // the final refined, smoothed linear geometry.
        return settings.ElementOrder == ElementOrder.Quadratic
            ? QuadraticMeshBuilder.Upgrade(mesh)
            : mesh;
    }

    /// <summary>
    /// Faces used by exactly one element form the boundary skin. Each is wound outward
    /// and tagged with the face id of the nearest refined-surface triangle.
    /// </summary>
    private static List<BoundaryTriangle> ExtractBoundary(
        IReadOnlyList<Vector3D> nodes, IReadOnlyList<Tet4> elements, TriangleMesh refinedSurface)
    {
        var faceUse = new Dictionary<(int, int, int), (int Count, int A, int B, int C, int Opp)>();
        void Touch(int a, int b, int c, int opp)
        {
            var key = SortedFace(a, b, c);
            if (faceUse.TryGetValue(key, out var entry))
                faceUse[key] = (entry.Count + 1, entry.A, entry.B, entry.C, entry.Opp);
            else
                faceUse[key] = (1, a, b, c, opp);
        }
        foreach (var e in elements)
        {
            Touch(e.N1, e.N2, e.N3, e.N0);
            Touch(e.N0, e.N2, e.N3, e.N1);
            Touch(e.N0, e.N1, e.N3, e.N2);
            Touch(e.N0, e.N1, e.N2, e.N3);
        }

        // Face-id lookup: nearest refined-surface triangle centroid.
        var centroids = new List<Vector3D>(refinedSurface.Triangles.Count);
        foreach (var t in refinedSurface.Triangles)
            centroids.Add((refinedSurface.Vertices[t.A] + refinedSurface.Vertices[t.B] + refinedSurface.Vertices[t.C]) / 3.0);
        var centroidTree = new KdTree(centroids);

        var boundary = new List<BoundaryTriangle>();
        foreach (var entry in faceUse.Values)
        {
            if (entry.Count != 1) continue;
            int a = entry.A, b = entry.B, c = entry.C;
            // Outward winding: the opposite vertex must lie on the negative side.
            if (GeometricPredicates.Orient3D(nodes[a], nodes[b], nodes[c], nodes[entry.Opp]) > 0)
                (b, c) = (c, b);
            var centroid = (nodes[a] + nodes[b] + nodes[c]) / 3.0;
            int nearest = centroidTree.NearestNeighbor(centroid);
            int faceId = nearest >= 0 ? refinedSurface.TriangleFaceIds[nearest] : 0;
            boundary.Add(new BoundaryTriangle(a, b, c, faceId));
        }
        return boundary;
    }

    private static Dictionary<(int, int, int), int> CountFaceUse(IReadOnlyList<Tet4> elements)
    {
        var faceUse = new Dictionary<(int, int, int), int>();
        void Touch(int a, int b, int c)
        {
            var key = SortedFace(a, b, c);
            faceUse[key] = faceUse.GetValueOrDefault(key) + 1;
        }
        foreach (var e in elements)
        {
            Touch(e.N1, e.N2, e.N3);
            Touch(e.N0, e.N2, e.N3);
            Touch(e.N0, e.N1, e.N3);
            Touch(e.N0, e.N1, e.N2);
        }
        return faceUse;
    }

    /// <summary>
    /// Greedy Poisson-disk-style thinning: accepts points in order, rejecting any
    /// closer than <paramref name="minSpacing"/> to an already accepted point.
    /// </summary>
    private static List<Vector3D> ThinPoints(IEnumerable<Vector3D> candidates, double minSpacing)
    {
        var accepted = new List<Vector3D>();
        var grid = new Dictionary<(long, long, long), List<int>>();
        double cell = minSpacing;

        foreach (var p in candidates)
        {
            long cx = (long)Math.Floor(p.X / cell);
            long cy = (long)Math.Floor(p.Y / cell);
            long cz = (long)Math.Floor(p.Z / cell);
            bool tooClose = false;
            for (long dx = -1; dx <= 1 && !tooClose; dx++)
                for (long dy = -1; dy <= 1 && !tooClose; dy++)
                    for (long dz = -1; dz <= 1 && !tooClose; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket)) continue;
                        foreach (int i in bucket)
                        {
                            if (Vector3D.Distance(accepted[i], p) < minSpacing)
                            {
                                tooClose = true;
                                break;
                            }
                        }
                    }
            if (tooClose) continue;

            int index = accepted.Count;
            accepted.Add(p);
            if (!grid.TryGetValue((cx, cy, cz), out var own))
                grid[(cx, cy, cz)] = own = new List<int>();
            own.Add(index);
        }
        return accepted;
    }

    private static (int, int, int) SortedFace(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
