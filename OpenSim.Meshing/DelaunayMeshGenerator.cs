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
/// <para>
/// The mesh HUGS the geometry's feature edges: they are sampled at the target spacing
/// before anything else, face samples are kept clear of them, and after triangulation
/// every such node is snapped back onto the exact geometry. The snap comes AFTER the
/// predicates have run, on purpose - pinning those points beforehand would hand the
/// predicates exactly the degeneracies the jitter exists to prevent (the eight corners of
/// a box are exactly cospherical, and they are the first points inserted). It moves a node
/// by at most the jitter amplitude, two orders below the sliver-cull threshold, so the
/// clip, cull and pinch decisions already made stay sound.
/// </para>
/// <para>
/// Geometry with no feature edges - a single-face STL, a PCB net - seeds exactly the
/// candidate list, in exactly the order, that it always did.
/// </para>
/// <para>
/// There is NO boundary recovery: step 4 keeps what the triangulation happens to offer.
/// What stands in for it is <see cref="MeshAudit"/>. A geometry with a wall or gap under
/// two elements across is refused before step 1, and the finished mesh is audited against
/// the geometry before it is returned - a mesh that is not the geometry is an exception,
/// not a result.
/// </para>
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

        // A feature thinner than two elements is one this pipeline bridges, fills or drops
        // without a trace in the volume, so it is refused here, on the geometry alone,
        // before any meshing work - and the finished mesh is audited against the same
        // analysis at the end.
        var audit = BeginAudit(geometry, settings, AutoEdgeFraction);
        audit.ThrowIfUnresolved();
        double h = audit.EdgeLength;

        var refined = SurfaceRefiner.Refine(geometry, h);
        var classifier = new SolidClassifier(geometry);
        var surfaceTree = new KdTree(refined.Vertices);
        var distanceField = new SurfaceDistanceField(refined);

        // Seed, triangulate, clip, skin - then audit. Nothing in that pipeline recovers the
        // boundary: the skin is whatever survives the clip, so a mesh that is not the
        // geometry must not leave this method.
        //
        // Two defects are this pipeline's own, are recognisable, and are repaired by
        // rebuilding rather than refused. A mesh that passes first time - every convex
        // part, and most others - is produced exactly as it always was.
        //
        // PITS. A sliver culled where it touches the surface along an edge leaves a slit;
        // the pinch resolver closes the slit by removing the sound tetrahedron beside it,
        // and the result is a pit half an element deep with INTERIOR points on the skin,
        // which a mesh of the geometry never has. Those points are what made the sliver:
        // the rebuild leaves them out. Every other point keeps its jittered position, so
        // the rebuild differs only around the pits.
        //
        // CUT REENTRANT EDGES. Where surface points sit about half an element from a
        // reentrant edge on both of its faces, a Delaunay face joins them across the void
        // and the edge is chamfered by a quarter of an element or so. Seeding the feature
        // edges twice as densely makes the faces beside the edge Delaunay instead. It is
        // done as a rebuild, not by default, because it changes every point's jitter and
        // with it the mesh of every part that has a feature edge at all.
        var droppedCells = new HashSet<(int, int, int)>();
        var blocked = new List<Vector3D>();
        var remarks = new List<string>();
        int creaseDivisions = 1;
        for (int attempt = 0; ; attempt++)
        {
            var seeds = BuildSeeds(geometry, refined, classifier, surfaceTree, h, creaseDivisions, droppedCells,
                cancellationToken);
            var (mesh, pointOfNode) = Triangulate(seeds.Points, seeds.ExactFeaturePositions, classifier,
                distanceField, h, settings, blocked, cancellationToken);
            var report = audit.Evaluate(mesh);
            if (remarks.Count > 0)
                report = report with { Warnings = report.Warnings.Concat(remarks).ToArray() };

            if (report.Passed)
            {
                // The quadratic upgrade is the last step so mid-edge nodes are generated on
                // the final refined, smoothed linear geometry.
                return (settings.ElementOrder == ElementOrder.Quadratic
                    ? QuadraticMeshBuilder.Upgrade(mesh)
                    : mesh).WithAudit(report);
            }

            var exposed = new SortedSet<int>();
            foreach (var t in mesh.BoundaryTriangles)
                foreach (int node in new[] { t.A, t.B, t.C })
                    if (pointOfNode[node] >= seeds.SurfacePointCount) exposed.Add(node);

            if (attempt < MaxRebuilds && exposed.Count > 0)
            {
                foreach (int node in exposed)
                {
                    int point = pointOfNode[node];
                    if (seeds.CellOfPoint.TryGetValue(point, out var cell)) droppedCells.Add(cell);
                    else blocked.Add(mesh.Nodes[node]);     // a refinement point: keep the refiner off this spot
                }
                remarks.Add($"Rebuild {attempt + 1}: the sliver cull left {exposed.Count} interior point(s) on the " +
                            "skin (pits); rebuilt without them.");
                continue;
            }
            if (attempt < MaxRebuilds && creaseDivisions == 1 && geometry.FeatureEdges.Edges.Count > 0)
            {
                creaseDivisions = 2;
                remarks.Add($"Rebuild {attempt + 1}: the skin did not conform (a cut reentrant edge is the usual " +
                            "cause); rebuilt with feature edges seeded twice as densely.");
                continue;
            }

            throw new MeshAuditException(report);
        }
    }

    /// <summary>How many times a triangulation that fails the audit in a way this mesher
    /// knows how to repair is rebuilt before the failure is reported.</summary>
    private const int MaxRebuilds = 4;

    /// <summary>The point set of one attempt: surface samples first, then the interior
    /// grid, all jittered.</summary>
    /// <param name="CellOfPoint">The grid cell of each interior point, by point index.</param>
    private sealed record Seeds(List<Vector3D> Points, int SurfacePointCount,
        Dictionary<int, Vector3D> ExactFeaturePositions, Dictionary<int, (int, int, int)> CellOfPoint);

    /// <summary>Steps 1 and 2, and the jitter.</summary>
    private static Seeds BuildSeeds(TriangleMesh geometry, TriangleMesh refined, SolidClassifier classifier,
        KdTree surfaceTree, double h, int creaseDivisions, HashSet<(int, int, int)> droppedCells,
        CancellationToken cancellationToken)
    {
        // 1. Surface sample points from the refined surface, thinned to a roughly
        // uniform spacing so anisotropic triangulations (long thin cap fans, dense
        // facet rings) cannot flood the Delaunay stage with badly spaced points.
        // Feature corners and edges are seeded first so they always survive the thinning.
        var points = ThinPoints(BuildSurfaceSeeds(geometry, refined, h, creaseDivisions), 0.45 * h,
            out var exactFeaturePositions);
        int surfacePointCount = points.Count;

        // 2. Interior grid points, kept at least 0.45·h away from surface samples.
        var cells = new List<(int, int, int)>();
        var bounds = geometry.Bounds;
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
                    {
                        points.Add(p);
                        cells.Add((i, j, k));
                    }
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

        // Interior points a rebuild leaves out are removed AFTER the jitter, so every
        // point that stays is where it was in the attempt before.
        var kept = new List<Vector3D>(points.Count);
        var cellOfPoint = new Dictionary<int, (int, int, int)>(cells.Count);
        for (int i = 0; i < points.Count; i++)
        {
            if (i >= surfacePointCount)
            {
                var cell = cells[i - surfacePointCount];
                if (droppedCells.Contains(cell)) continue;
                cellOfPoint[kept.Count] = cell;
            }
            kept.Add(points[i]);
        }
        return new Seeds(kept, surfacePointCount, exactFeaturePositions, cellOfPoint);
    }

    /// <summary>
    /// The audit this mesher holds its output to, set up for a geometry and its settings;
    /// its edge length is the one the mesh is built at. Exposed so a mesh made earlier -
    /// loaded from a project file - can be audited the same way.
    /// <para>
    /// An explicit target edge length is taken as given. The AUTOMATIC one starts at a
    /// fraction of the bounding-box diagonal and is then made finer, as far as
    /// <see cref="AutoRefinementLimit"/> times, when that would leave a wall or a gap less
    /// than two elements across: "automatic" has to mean a size the part can be meshed at,
    /// or a plate, a beam or anything else slender has no automatic size at all (the
    /// reference beam, 200 x 60 x 20 mm, is 20 mm thick against an automatic 14 mm). Past
    /// that limit the refusal stands and names the size that would do, because resolving a
    /// very thin feature uniformly is a decision about cost, and not one to take silently.
    /// </para>
    /// </summary>
    public static MeshAudit BeginAudit(TriangleMesh geometry, MeshSettings settings,
        double autoEdgeFraction = 1.0 / 15.0)
    {
        if (settings.TargetEdgeLength > 0) return MeshAudit.Begin(geometry, settings.TargetEdgeLength);

        double coarse = geometry.Bounds.Diagonal * autoEdgeFraction;
        var audit = MeshAudit.Begin(geometry, coarse);
        // A finer sampling can find a thinner feature than the coarse one saw, hence rounds.
        for (int round = 0; round < 3 && audit.Resolution.Outcome == MeshAuditOutcome.Failed; round++)
        {
            double thinnest = audit.Resolution.Observed!.Value;
            double resolved = thinnest / MeshAudit.ResolutionFactor;
            if (resolved < coarse / AutoRefinementLimit) break;
            audit = MeshAudit.Begin(geometry, resolved);
            audit.AddNote($"Automatic edge length reduced from {coarse:g3} m to {resolved:g3} m: the thinnest wall or " +
                          $"gap is {thinnest:g3} m, and the mesher needs two elements across it.");
        }
        return audit;
    }

    /// <summary>How much finer than its default the automatic edge length may be made to
    /// resolve a thin feature. Four keeps the worst case - a blocky part with one thin
    /// feature - to a few hundred thousand elements.</summary>
    public const double AutoRefinementLimit = 4;

    /// <summary>
    /// Steps 3-6: triangulate and refine the point set, keep the interior tetrahedra, and
    /// extract the skin. Returns the mesh and, for each of its nodes, the index of the
    /// point it came from (refinement points follow the seeds).
    /// </summary>
    private static (FeMesh Mesh, int[] PointOfNode) Triangulate(List<Vector3D> points,
        Dictionary<int, Vector3D> exactFeaturePositions, SolidClassifier classifier,
        SurfaceDistanceField distanceField, double h, MeshSettings settings,
        IReadOnlyList<Vector3D> blocked, CancellationToken cancellationToken)
    {
        // 3. Delaunay triangulation, then quality-driven refinement: Steiner points at
        // the circumcenters of bad tets (longest-edge midpoints for boundary slivers,
        // whose circumcenters escape the solid), mirroring the 2D PlanarMesher.Refine.
        var triangulation = new BowyerWatson();
        triangulation.Triangulate(points, cancellationToken);
        if (settings.TargetMinQuality > 0)
        {
            int budget = settings.MaxRefinementPoints > 0
                ? settings.MaxRefinementPoints
                : Math.Max(1024, points.Count);
            TetRefiner.Refine(triangulation, points, classifier, distanceField, h,
                settings.TargetMinQuality, budget, cancellationToken, blocked);
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
        var pointOfNode = new List<int>();
        int Map(int old)
        {
            if (!nodeMap.TryGetValue(old, out int idx))
            {
                idx = nodes.Count;
                nodes.Add(points[old]);
                pointOfNode.Add(old);
                nodeMap[old] = idx;
            }
            return idx;
        }
        var elements = new List<Tet4>(kept.Count);
        foreach (var (a, b, c, d) in kept)
            elements.Add(new Tet4(Map(a), Map(b), Map(c), Map(d)));

        // Snap feature nodes back onto the exact geometry. The predicates have already run
        // on the fully jittered point set, so this restores the exactness a boundary
        // condition scoped to an edge needs without ever showing the triangulation a
        // degenerate configuration. Refinement points are appended after the seeds, so
        // they carry indices past the seed range and are never snapped.
        foreach (var (original, compacted) in nodeMap)
            if (exactFeaturePositions.TryGetValue(original, out var exact))
                nodes[compacted] = exact;

        // 6. Smooth interior nodes (boundary skin stays fixed), then extract the skin.
        // One face-use map serves both: which nodes the smoother must hold, and which faces
        // become the skin. Counting them twice walked all four faces of every element a
        // second time to rediscover exactly the same set.
        var faceUse = BuildFaceUse(elements);
        var boundaryNodes = new HashSet<int>();
        foreach (var (face, entry) in faceUse)
        {
            if (entry.Count != 1) continue;
            boundaryNodes.Add(face.Item1);
            boundaryNodes.Add(face.Item2);
            boundaryNodes.Add(face.Item3);
        }
        MeshSmoother.Smooth(nodes, elements, boundaryNodes);

        var boundary = ExtractBoundary(nodes, faceUse, distanceField);
        return (new FeMesh(nodes, elements, boundary), pointOfNode.ToArray());
    }

    /// <summary>
    /// Faces used by exactly one element form the boundary skin. Each is wound outward
    /// and tagged with the face id of the nearest refined-surface triangle - nearest by
    /// true surface distance, which is what keeps a crease-hugging triangle on the face it
    /// actually lies on (see <see cref="SurfaceDistanceField.NearestFaceId"/>).
    /// </summary>
    private static List<BoundaryTriangle> ExtractBoundary(
        IReadOnlyList<Vector3D> nodes,
        Dictionary<(int, int, int), (int Count, int A, int B, int C, int Opp)> faceUse,
        SurfaceDistanceField surface)
    {
        var boundary = new List<BoundaryTriangle>();
        foreach (var entry in faceUse.Values)
        {
            if (entry.Count != 1) continue;
            int a = entry.A, b = entry.B, c = entry.C;
            // Outward winding: the opposite vertex must lie on the negative side.
            if (GeometricPredicates.Orient3D(nodes[a], nodes[b], nodes[c], nodes[entry.Opp]) > 0)
                (b, c) = (c, b);
            var centroid = (nodes[a] + nodes[b] + nodes[c]) / 3.0;
            boundary.Add(new BoundaryTriangle(a, b, c, surface.NearestFaceId(centroid)));
        }
        return boundary;
    }

    /// <summary>
    /// How many elements use each face, with the first user's winding and opposite vertex.
    /// Faces are visited in element order, four per element — the enumeration order the skin
    /// is then built in, so boundary triangles come out in exactly the order they always have.
    /// </summary>
    private static Dictionary<(int, int, int), (int Count, int A, int B, int C, int Opp)>
        BuildFaceUse(IReadOnlyList<Tet4> elements)
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
        return faceUse;
    }

    /// <summary>A surface sample, and whether it sits on a feature edge or corner - the
    /// points the finished mesh must carry EXACTLY.</summary>
    private readonly record struct SurfaceSeed(Vector3D Position, bool OnFeature);

    /// <summary>
    /// The surface candidates, in priority order: feature corners, then feature edges at
    /// the target spacing, then the face samples. The thinning keeps whatever it sees
    /// first, so this ordering is what makes a crease out of exact points.
    /// <para>
    /// A face sample that ALREADY lies on a feature edge is marked as one rather than
    /// dropped. The surface refiner subdivides each input triangle, so the samples along a
    /// triangle edge sit exactly on the crease the geometry folds along; left unmarked they
    /// would be jittered a fraction of an element off the very line the mesh is supposed to
    /// hug, and they land between the seeded edge samples where the thinning cannot see
    /// them. Classifying is right where deleting is not: the points are wanted, it is only
    /// their treatment under the jitter that differs.
    /// </para>
    /// <para>
    /// Clearing face samples out of a band around each crease was tried instead, and
    /// measured worse: an empty band lets the triangulation bridge straight across the
    /// crease, and a triangle genuinely spanning both faces has to be tagged with one of
    /// them, so the skin splits along a line the geometry does not fold at - inventing a
    /// feature edge in the middle of a flat face. No face-tagging rule can rescue that;
    /// the answer is not to leave the gap for it to bridge.
    /// </para>
    /// </summary>
    private static List<SurfaceSeed> BuildSurfaceSeeds(TriangleMesh geometry, TriangleMesh refined, double h,
        int creaseDivisions)
    {
        var features = geometry.FeatureEdges;
        var seeds = new List<SurfaceSeed>();

        foreach (var vertex in features.Vertices)
            seeds.Add(new SurfaceSeed(geometry.Vertices[vertex.NodeId], true));

        // Grid cell at the thinning radius: coarse enough that a metre-long edge registers
        // in a handful of cells, and every query radius below is far smaller.
        var onFeature = new FeatureEdgeProximity(0.45 * h);
        foreach (var edge in features.Edges)
            foreach (var segment in edge.Segments)
            {
                var a = geometry.Vertices[segment.A];
                var b = geometry.Vertices[segment.B];
                onFeature.Add(a, b);

                seeds.Add(new SurfaceSeed(a, true));
                seeds.Add(new SurfaceSeed(b, true));
                int steps = Math.Max(1, (int)Math.Ceiling((b - a).Length / h)) * creaseDivisions;
                for (int k = 1; k < steps; k++)
                    seeds.Add(new SurfaceSeed(a + (b - a) * ((double)k / steps), true));
            }

        // "Already on the edge" is a geometric identity, not a proximity judgement: these
        // samples are computed by interpolating the endpoints of an input triangle edge, so
        // they are on the line to rounding. The tolerance says exactly that.
        double onEdgeTolerance = 1e-9 * geometry.Bounds.Diagonal;
        foreach (var p in geometry.Vertices.Concat(refined.Vertices))
            seeds.Add(new SurfaceSeed(p, onFeature.IsWithin(p, onEdgeTolerance)));

        return seeds;
    }

    /// <summary>
    /// Greedy Poisson-disk-style thinning: accepts points in order, rejecting any
    /// closer than <paramref name="minSpacing"/> to an already accepted point. Reports the
    /// exact geometric position of every accepted feature sample, keyed by its index, so
    /// the snap pass can restore it after triangulation.
    /// </summary>
    private static List<Vector3D> ThinPoints(IEnumerable<SurfaceSeed> candidates, double minSpacing,
        out Dictionary<int, Vector3D> exactFeaturePositions)
    {
        exactFeaturePositions = new Dictionary<int, Vector3D>();
        var accepted = new List<Vector3D>();
        var grid = new Dictionary<(long, long, long), List<int>>();
        double cell = minSpacing;

        foreach (var seed in candidates)
        {
            var p = seed.Position;
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
            if (seed.OnFeature) exactFeaturePositions[index] = p;
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
