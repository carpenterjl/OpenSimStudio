namespace OpenSim.Core.Model;

/// <summary>One mesh edge of a geometric feature edge: an unordered corner-node pair.</summary>
public readonly record struct BoundaryEdgeSegment(int A, int B)
{
    /// <summary>The canonical (ascending) form, so a mesh edge is one key however it is visited.</summary>
    public static BoundaryEdgeSegment Sorted(int a, int b) => a < b ? new(a, b) : new(b, a);
}

/// <summary>
/// A geometric edge of the meshed body: the curve where two geometric faces meet.
/// <para>
/// Identity is the face pair, because that is the only stable handle the mesh carries —
/// tessellation keeps no topological edge id. One face pair can meet along several
/// DISJOINT curves (a washer both rims lie between the same two faces), so each connected
/// component is its own edge; a single id per pair would silently constrain both rims
/// when the user picked one.
/// </para>
/// </summary>
public sealed record BoundaryEdge(int Id, int FaceA, int FaceB,
    IReadOnlyList<BoundaryEdgeSegment> Segments, IReadOnlyList<int> NodeIds, double Length);

/// <summary>A geometric vertex: one mesh node where three or more geometric faces meet.</summary>
public sealed record BoundaryVertex(int Id, int NodeId, IReadOnlyList<int> FaceIds);

/// <summary>
/// The geometric edges and vertices of a mesh boundary skin, derived from the face ids the
/// skin already carries: a mesh edge whose adjacent boundary triangles belong to DIFFERENT
/// geometric faces lies on a feature edge, and a node touching three or more faces is a
/// vertex.
/// <para>
/// Ids are assigned in ascending (FaceA, FaceB, smallest node index) — ONE deterministic
/// ordering, so the same mesh always yields the same ids and a project file that stores
/// them stays meaningful. Vertices are ordered by node index.
/// </para>
/// </summary>
public sealed class BoundaryEdgeSet
{
    public IReadOnlyList<BoundaryEdge> Edges { get; }
    public IReadOnlyList<BoundaryVertex> Vertices { get; }

    /// <summary>
    /// Mesh edges whose skin triangles span MORE than two geometric faces. The mesher
    /// pinch resolver exists to make these impossible, so a non-zero count is worth
    /// reporting rather than hiding: such an edge is still assigned to its two lowest face
    /// ids (deterministic), but its identity is genuinely ambiguous.
    /// </summary>
    public int NonManifoldEdgeCount { get; }

    private BoundaryEdgeSet(IReadOnlyList<BoundaryEdge> edges, IReadOnlyList<BoundaryVertex> vertices,
        int nonManifoldEdgeCount)
    {
        Edges = edges;
        Vertices = vertices;
        NonManifoldEdgeCount = nonManifoldEdgeCount;
    }

    /// <summary>An empty set — a mesh with no boundary skin has no edges or vertices.</summary>
    public static BoundaryEdgeSet Empty { get; } =
        new(Array.Empty<BoundaryEdge>(), Array.Empty<BoundaryVertex>(), 0);

    /// <summary>Edge by id, or null when the id is not on this mesh.</summary>
    public BoundaryEdge? EdgeById(int id) => id >= 0 && id < Edges.Count ? Edges[id] : null;

    /// <summary>Vertex by id, or null when the id is not on this mesh.</summary>
    public BoundaryVertex? VertexById(int id) => id >= 0 && id < Vertices.Count ? Vertices[id] : null;

    /// <summary>
    /// Ids of every edge lying between two of the given faces — what "the edges shared by
    /// this face selection" means.
    /// </summary>
    public IReadOnlyList<int> EdgesBetween(IEnumerable<int> faceIds)
    {
        var faces = faceIds as ISet<int> ?? new HashSet<int>(faceIds);
        var ids = new List<int>();
        foreach (var e in Edges)
            if (faces.Contains(e.FaceA) && faces.Contains(e.FaceB))
                ids.Add(e.Id);
        return ids;
    }

    public static BoundaryEdgeSet Extract(FeMesh mesh)
    {
        if (mesh.BoundaryTriangles.Count == 0) return Empty;

        // Pass 1: per mesh edge, the two lowest distinct face ids and how many there were.
        var edgeFaces = new Dictionary<BoundaryEdgeSegment, (int F0, int F1, int Distinct)>();
        // Per node, the distinct faces meeting there — the vertex test.
        var nodeFaces = new Dictionary<int, SortedSet<int>>();

        foreach (var t in mesh.BoundaryTriangles)
        {
            Accumulate(edgeFaces, BoundaryEdgeSegment.Sorted(t.A, t.B), t.FaceId);
            Accumulate(edgeFaces, BoundaryEdgeSegment.Sorted(t.B, t.C), t.FaceId);
            Accumulate(edgeFaces, BoundaryEdgeSegment.Sorted(t.C, t.A), t.FaceId);

            AddNodeFace(nodeFaces, t.A, t.FaceId);
            AddNodeFace(nodeFaces, t.B, t.FaceId);
            AddNodeFace(nodeFaces, t.C, t.FaceId);
        }

        // Pass 2: group the feature edges by face pair.
        var groups = new Dictionary<(int, int), List<BoundaryEdgeSegment>>();
        int nonManifold = 0;
        foreach (var (segment, faces) in edgeFaces)
        {
            if (faces.Distinct < 2) continue;
            if (faces.Distinct > 2) nonManifold++;
            var key = (faces.F0, faces.F1);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<BoundaryEdgeSegment>();
            list.Add(segment);
        }

        // Pass 3: split each group into connected components.
        var edges = new List<BoundaryEdge>();
        foreach (var key in groups.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2))
        {
            var segments = groups[key];
            // Deterministic input order — dictionary enumeration order is not a contract.
            segments.Sort(static (x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));

            foreach (var component in ConnectedComponents(segments))
            {
                var nodes = new SortedSet<int>();
                double length = 0;
                foreach (var s in component)
                {
                    nodes.Add(s.A);
                    nodes.Add(s.B);
                    length += (mesh.Nodes[s.B] - mesh.Nodes[s.A]).Length;
                }
                edges.Add(new BoundaryEdge(0, key.Item1, key.Item2, component, nodes.ToArray(), length));
            }
        }

        var ordered = edges
            .OrderBy(e => e.FaceA).ThenBy(e => e.FaceB).ThenBy(e => e.NodeIds[0])
            .Select((e, i) => e with { Id = i })
            .ToArray();

        var vertices = nodeFaces
            .Where(kv => kv.Value.Count >= 3)
            .OrderBy(kv => kv.Key)
            .Select((kv, i) => new BoundaryVertex(i, kv.Key, kv.Value.ToArray()))
            .ToArray();

        return new BoundaryEdgeSet(ordered, vertices, nonManifold);
    }

    private static void Accumulate(Dictionary<BoundaryEdgeSegment, (int F0, int F1, int Distinct)> map,
        BoundaryEdgeSegment segment, int faceId)
    {
        if (!map.TryGetValue(segment, out var e))
        {
            map[segment] = (faceId, -1, 1);
            return;
        }
        if (faceId == e.F0 || faceId == e.F1) return;
        if (e.Distinct == 1)
        {
            map[segment] = faceId < e.F0 ? (faceId, e.F0, 2) : (e.F0, faceId, 2);
            return;
        }
        // Three or more faces on one mesh edge: keep the two lowest so the identity stays
        // deterministic, and let the caller see the count.
        map[segment] = (e.F0, e.F1, e.Distinct + 1);
    }

    private static void AddNodeFace(Dictionary<int, SortedSet<int>> map, int node, int faceId)
    {
        if (!map.TryGetValue(node, out var set)) map[node] = set = new SortedSet<int>();
        set.Add(faceId);
    }

    /// <summary>
    /// Splits one face pair mesh edges into curves, joining segments that share a node.
    /// Union-find keyed on node index, lower root winning, so each component
    /// representative is its own lowest node and the output order is deterministic.
    /// </summary>
    private static List<IReadOnlyList<BoundaryEdgeSegment>> ConnectedComponents(
        List<BoundaryEdgeSegment> segments)
    {
        var parent = new Dictionary<int, int>();

        int Find(int x)
        {
            if (!parent.TryGetValue(x, out int p)) { parent[x] = x; return x; }
            while (p != x) { x = p; p = parent[x]; }
            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }

        foreach (var s in segments) Union(s.A, s.B);

        var buckets = new Dictionary<int, List<BoundaryEdgeSegment>>();
        foreach (var s in segments)
        {
            int root = Find(s.A);
            if (!buckets.TryGetValue(root, out var list)) buckets[root] = list = new List<BoundaryEdgeSegment>();
            list.Add(s);
        }

        return buckets.Keys.OrderBy(k => k)
            .Select(k => (IReadOnlyList<BoundaryEdgeSegment>)buckets[k])
            .ToList();
    }
}
