namespace OpenSim.Core.Model;

/// <summary>
/// Merges the independently meshed bodies of an assembly into the ONE <see cref="FeMesh"/>
/// the existing solvers consume — nodes and elements concatenated, per-element region ids
/// set to the body index, geometric face ids offset into disjoint per-body ranges, and
/// every body's boundary conditions cloned onto those offset ids.
///
/// Bodies are meshed one at a time (each with its own auto edge length) and merged after,
/// never triangulated together: one Delaunay pass over concatenated parts bridges bodies
/// closer than the mesher's near-surface keep band and mistags skin faces by nearest
/// centroid.
///
/// Nodes are NEVER re-welded across bodies. Merging coincident nodes would fake perfect
/// thermal contact wherever two tessellations happened to land on the same point and leave
/// the rest of the interface disconnected — contact is a modeled interface with a finite
/// conductance (<see cref="ContactDetector"/>), not an accident of vertex coordinates.
///
/// Body order is the single deterministic ordering that runs through the whole assembly
/// path: STEP resolver tree order = <see cref="SimProject.Bodies"/> order = the region ids
/// produced here. Merged face ids are solve-transient (never persisted, never shown), so
/// only their non-collision matters.
/// </summary>
public static class FeMeshAssembler
{
    /// <summary>The merged mesh plus the offsets needed to map results back to bodies.</summary>
    /// <param name="Mesh">The merged mesh; <c>ElementRegionIds[e]</c> is the body index.</param>
    /// <param name="NodeBases">First merged node index of each body.</param>
    /// <param name="ElementBases">First merged element index of each body.</param>
    /// <param name="FaceIdBases">First merged face id of each body.</param>
    /// <param name="BoundaryConditions">Every body's conditions, scope ids offset and names prefixed.</param>
    /// <param name="RegionMaterials">Body index → that body's material (by reference).</param>
    /// <param name="EdgeIdBases">First merged geometric-edge id of each body.</param>
    /// <param name="VertexIdBases">First merged geometric-vertex id of each body.</param>
    public sealed record AssembledMesh(
        FeMesh Mesh,
        IReadOnlyList<int> NodeBases,
        IReadOnlyList<int> ElementBases,
        IReadOnlyList<int> FaceIdBases,
        IReadOnlyList<BoundaryCondition> BoundaryConditions,
        IReadOnlyDictionary<int, Material> RegionMaterials,
        IReadOnlyList<int> EdgeIdBases,
        IReadOnlyList<int> VertexIdBases)
    {
        public int BodyCount => NodeBases.Count;

        /// <summary>The body a merged node belongs to.</summary>
        public int BodyOfNode(int node)
        {
            if (node < 0 || node >= Mesh.NodeCount)
                throw new ArgumentOutOfRangeException(nameof(node), node,
                    $"The merged mesh has {Mesh.NodeCount} nodes.");
            return LastBaseAtOrBelow(NodeBases, node);
        }

        /// <summary>The body a merged element belongs to (identical to its region id).</summary>
        public int BodyOfElement(int element)
        {
            if (element < 0 || element >= Mesh.ElementCount)
                throw new ArgumentOutOfRangeException(nameof(element), element,
                    $"The merged mesh has {Mesh.ElementCount} elements.");
            return Mesh.RegionOf(element);
        }

        /// <summary>
        /// The body a merged face id belongs to — the mapping a viewport pick needs to turn
        /// a clicked face into the part that owns it.
        /// </summary>
        public int BodyOfFace(int faceId)
        {
            if (faceId < 0)
                throw new ArgumentOutOfRangeException(nameof(faceId), faceId, "Face ids are non-negative.");
            return LastBaseAtOrBelow(FaceIdBases, faceId);
        }

        private static int LastBaseAtOrBelow(IReadOnlyList<int> bases, int value)
        {
            for (int b = bases.Count - 1; b >= 0; b--)
                if (value >= bases[b]) return b;
            return 0;
        }
    }

    /// <summary>
    /// The purely GEOMETRIC part of a merge: the combined mesh and the offsets that map each
    /// body's local ids into it.
    /// <para>
    /// Separated from the boundary conditions because the two have different lifetimes. This
    /// depends on nothing but the bodies' meshes, which are immutable and replaced wholesale by
    /// a remesh — so it can be cached on their identity. The conditions depend on what the user
    /// edited a moment ago and must never be.
    /// </para>
    /// </summary>
    public sealed record MergedGeometry(
        FeMesh Mesh,
        int[] NodeBases,
        int[] ElementBases,
        int[] FaceIdBases,
        int[] EdgeIdBases,
        int[] VertexIdBases);

    /// <summary>
    /// Merges the bodies in the given order. Every body must carry a linear (TET4) mesh
    /// and a material; anything else is a typed failure naming the body.
    /// </summary>
    public static AssembledMesh Assemble(IReadOnlyList<Body> bodies)
    {
        var geometry = MergeMeshes(bodies);
        var (conditions, materials) = ResolveConditions(bodies, geometry);
        return new AssembledMesh(geometry.Mesh, geometry.NodeBases, geometry.ElementBases,
            geometry.FaceIdBases, conditions, materials,
            geometry.EdgeIdBases, geometry.VertexIdBases);
    }

    /// <summary>
    /// The geometric merge alone — see <see cref="MergedGeometry"/> for why it is separable.
    /// </summary>
    public static MergedGeometry MergeMeshes(IReadOnlyList<Body> bodies)
    {
        if (bodies is null || bodies.Count == 0)
            throw new InvalidOperationException("The assembly has no bodies to merge.");

        foreach (var body in bodies)
        {
            var mesh = body.Mesh ?? throw new InvalidOperationException(
                $"Body '{body.Name}' has no mesh. Generate a mesh for every body before solving.");
            if (mesh.ElementCount == 0)
                throw new InvalidOperationException($"Body '{body.Name}' has an empty mesh (no elements).");
            if (mesh.IsQuadratic)
                throw new InvalidOperationException(
                    $"Body '{body.Name}' carries a quadratic (TET10 or HEX20) mesh; assembly solves are " +
                    "linear tetrahedral (TET4) only. Re-generate that mesh with linear tetrahedral elements.");
            if (mesh.ElementRegionIds is not null)
                throw new InvalidOperationException(
                    $"Body '{body.Name}' already carries per-element regions (a multi-material PCB " +
                    "mesh). Assemblies of multi-region bodies are out of scope — the merged region " +
                    "ids identify bodies.");
            if (body.Material is null)
                throw new InvalidOperationException(
                    $"Body '{body.Name}' has no material assigned. Every body of an assembly needs one.");
        }

        var nodeBases = new int[bodies.Count];
        var elementBases = new int[bodies.Count];
        var faceIdBases = FaceIdBases(bodies);
        int nodeTotal = 0, elementTotal = 0;
        for (int b = 0; b < bodies.Count; b++)
        {
            var mesh = bodies[b].Mesh!;
            nodeBases[b] = nodeTotal;
            elementBases[b] = elementTotal;
            nodeTotal += mesh.NodeCount;
            elementTotal += mesh.ElementCount;
        }

        // Geometric edge and vertex ids are DERIVED from the merged skin, so they need
        // their own offsets — a record `with` would copy a body-local id straight into the
        // merged space unchanged. A plain cumulative count is the right offset because the
        // merge preserves the ordering the ids are assigned in: bodies are never re-welded,
        // so each body's skin keeps its own node range and its own contiguous, ascending
        // face range, and both keys of the ordering (FaceA, FaceB, then lowest node) only
        // shift by a constant per body. The identity is gated, not assumed.
        var edgeIdBases = new int[bodies.Count];
        var vertexIdBases = new int[bodies.Count];
        int edgeTotal = 0, vertexTotal = 0;
        for (int b = 0; b < bodies.Count; b++)
        {
            var localEdges = bodies[b].Mesh!.Edges;
            edgeIdBases[b] = edgeTotal;
            vertexIdBases[b] = vertexTotal;
            edgeTotal += localEdges.Edges.Count;
            vertexTotal += localEdges.Vertices.Count;
        }

        var nodes = new Numerics.Vector3D[nodeTotal];
        var elements = new Tet4[elementTotal];
        var regions = new int[elementTotal];
        var triangles = new List<BoundaryTriangle>();

        for (int b = 0; b < bodies.Count; b++)
        {
            var body = bodies[b];
            var mesh = body.Mesh!;
            int nb = nodeBases[b], eb = elementBases[b], fb = faceIdBases[b];

            for (int i = 0; i < mesh.NodeCount; i++)
                nodes[nb + i] = mesh.Nodes[i];   // rigid copy: coordinates are bitwise unchanged

            for (int e = 0; e < mesh.ElementCount; e++)
            {
                var t = mesh.Elements[e];
                elements[eb + e] = new Tet4(t.N0 + nb, t.N1 + nb, t.N2 + nb, t.N3 + nb);
                regions[eb + e] = b;
            }

            foreach (var t in mesh.BoundaryTriangles)
                triangles.Add(new BoundaryTriangle(t.A + nb, t.B + nb, t.C + nb, t.FaceId + fb));
        }

        var merged = new FeMesh(nodes, elements, triangles, regions);
        return new MergedGeometry(merged, nodeBases, elementBases, faceIdBases,
            edgeIdBases, vertexIdBases);
    }

    /// <summary>
    /// The boundary conditions and materials of a merge, rebased onto an already-merged
    /// geometry. Always recomputed rather than cached alongside it: a condition edit, a
    /// material swap or a change of geometry scope moves these and moves nothing else.
    /// </summary>
    public static (List<BoundaryCondition> Conditions, Dictionary<int, Material> Materials)
        ResolveConditions(IReadOnlyList<Body> bodies, MergedGeometry geometry)
    {
        var conditions = new List<BoundaryCondition>();
        var materials = new Dictionary<int, Material>();

        for (int b = 0; b < bodies.Count; b++)
        {
            var body = bodies[b];
            // Geometry-scoped conditions are resolved against THIS body's own geometry and
            // mesh before anything is rebased: the resolver speaks body-local ids, and the
            // offsets here turn those into merged ones. Resolving after the merge would
            // mean matching against a skin that carries five other bodies.
            int fb = geometry.FaceIdBases[b];
            int gb = geometry.EdgeIdBases[b], vb = geometry.VertexIdBases[b];
            foreach (var bc in GeometryScopeResolver.ResolveForBody(body))
                conditions.Add(bc with
                {
                    Name = $"{body.Name}: {bc.Name}",
                    FaceIds = bc.FaceIds.Select(f => f + fb).ToArray(),
                    EdgeIds = bc.EdgeIds?.Select(e => e + gb).ToArray(),
                    VertexIds = bc.VertexIds?.Select(v => v + vb).ToArray()
                });

            materials[b] = body.Material!;
        }

        return (conditions, materials);
    }

    /// <summary>
    /// First global face id of each body — the offset that turns a body-local face id into
    /// the assembly-wide one, and back. The viewport needs this BEFORE anything is meshed
    /// (to show and pick the parts of an assembly), so it is computed from whatever the
    /// body carries — geometry, mesh, or conditions — and never from the merge alone.
    /// </summary>
    public static int[] FaceIdBases(IReadOnlyList<Body> bodies)
    {
        var bases = new int[bodies.Count];
        int total = 0;
        for (int b = 0; b < bodies.Count; b++)
        {
            bases[b] = total;
            total += FaceIdStride(bodies[b]);
        }
        return bases;
    }

    /// <summary>
    /// Width of one body's face-id range. Computed from the body's own ids rather than a
    /// fixed base: a real CAD part can carry thousands of faces, so any constant stride
    /// eventually collides. (The PCB pad-face base is a persisted contract; these ids are
    /// not — they exist only for the duration of a solve.)
    /// <para>
    /// Geometry, mesh and conditions all widen it. The geometry's ids matter because the
    /// pre-mesh scene partitions face ids with the same function the merge later uses, and
    /// the two must agree; a face too small to survive meshing would otherwise shrink the
    /// stride and move every later body's range.
    /// </para>
    /// </summary>
    private static int FaceIdStride(Body body)
    {
        int max = -1;
        if (body.Mesh is { } mesh)
            foreach (var t in mesh.BoundaryTriangles)
                if (t.FaceId > max) max = t.FaceId;
        if (body.Geometry is { } geometry)
            foreach (int f in geometry.TriangleFaceIds)
                if (f > max) max = f;
        // A condition may name a face the skin does not carry; the solver reports that, but
        // the stride must still cover it so the offset cannot land in the next body's range.
        foreach (var bc in body.BoundaryConditions)
            foreach (int f in bc.FaceIds)
                if (f > max) max = f;
        return max + 1;
    }

    /// <summary>
    /// Turns each body's <see cref="Body.HeatSourcePower"/> [W] into the per-element
    /// volumetric source [W/m³] the thermal solvers consume: q = P / V, with V the body's
    /// own MESHED volume, so the integral of q over the body reproduces P exactly whatever
    /// the mesher did to the geometry.
    /// <para>
    /// Returns null when no body dissipates anything, which keeps a source-free assembly
    /// solve bitwise identical to one that never knew about heat sources.
    /// </para>
    /// </summary>
    public static double[]? BuildElementHeatSource(AssembledMesh assembled,
        IReadOnlyList<Body> bodies, Action<string>? log = null)
    {
        if (!bodies.Any(b => b.HeatSourcePower is not null and not 0)) return null;

        var mesh = assembled.Mesh;
        var source = new double[mesh.ElementCount];
        for (int b = 0; b < bodies.Count; b++)
        {
            if (bodies[b].HeatSourcePower is not { } power || power == 0) continue;
            int first = assembled.ElementBases[b];
            int last = b + 1 < bodies.Count ? assembled.ElementBases[b + 1] : mesh.ElementCount;

            double volume = 0;
            for (int e = first; e < last; e++) volume += Math.Abs(mesh.ElementVolume(e));
            if (volume <= 0)
                throw new InvalidOperationException(
                    $"Body '{bodies[b].Name}' dissipates {power:g4} W but its mesh has zero volume, " +
                    "so the source has nowhere to go.");

            double q = power / volume;
            for (int e = first; e < last; e++) source[e] = q;
            log?.Invoke($"Heat source '{bodies[b].Name}': {power:g4} W over {volume:g4} m³ " +
                        $"= {q:g4} W/m³.");
        }
        return source;
    }
}
