using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>Tolerances and the default joint quality used when detecting contacts.</summary>
public sealed record ContactDetectionSettings
{
    /// <summary>
    /// How far apart two surfaces may be and still count as touching [m]. 0 means auto:
    /// a quarter of the mean chord length of the assembly's skin. Surfaces that are coincident in
    /// CAD are separated in the mesh only by the mesher's deterministic jitter and by the
    /// sagitta of a tessellated curve — both well under a quarter element — while a real
    /// air gap is far wider. (A tolerance of an element or more would silently weld parts
    /// across visible gaps.)
    /// </summary>
    public double GapTolerance { get; init; }

    /// <summary>
    /// Minimum −(n̂_A·n̂_B) for two surfaces to be considered facing each other; 0.99 is
    /// about 8°. Anti-parallelism is the test rather than any absolute orientation, so it
    /// holds under either winding convention as long as the mesh is consistent.
    /// </summary>
    public double NormalOpposition { get; init; } = 0.99;

    /// <summary>
    /// Interfacial conductance applied to every detected joint [W/(m²·K)]. 5e3 is the usual
    /// order for dry machined metal-to-metal contact at moderate pressure; it is a stated
    /// placeholder for a measured value, not a prediction — real joints span 1e2 (rough,
    /// unloaded) to 1e5 (greased or bolted hard).
    /// </summary>
    public double DefaultConductance { get; init; } = 5e3;
}

/// <summary>
/// Finds where the bodies of a merged assembly mesh touch, and turns each interface into
/// positive-weight jump stamps the scalar assembler can scatter.
///
/// The rule is a vertex quadrature applied from BOTH sides at half weight: every boundary
/// triangle of body A contributes its three vertices (each worth ½·A/3), which project onto
/// the opposing surface of body B, and body B's triangles do the same back. Summing both
/// passes reproduces the interface area exactly, and every stamp is a positive multiple of
/// s·sᵀ, so the coupling is SPD by construction with no mesh-matching requirement.
///
/// Rejected alternatives: centroid-to-centroid lumping (leaves the tangential variation of
/// the jump energetically free, so a tilted interface exchanges the wrong heat) and a full
/// mortar projection (a polygon-clipping kernel for accuracy the vertex rule already reaches
/// at first order — and exactly, when the two surface meshes match).
/// </summary>
public static class ContactDetector
{
    /// <summary>
    /// Detects contacts on a mesh merged by <see cref="FeMeshAssembler"/>.
    /// </summary>
    /// <param name="mesh">The merged assembly mesh.</param>
    /// <param name="nodeBases">Per-body first node index, from the merge.</param>
    /// <param name="settings">Tolerances; null uses the defaults.</param>
    /// <param name="log">Optional per-interface assumption log.</param>
    public static IReadOnlyList<ContactInterface> Find(FeMesh mesh, IReadOnlyList<int> nodeBases,
        ContactDetectionSettings? settings = null, Action<string>? log = null)
    {
        settings ??= new ContactDetectionSettings();
        if (nodeBases.Count < 2) return Array.Empty<ContactInterface>();
        if (settings.DefaultConductance <= 0)
            throw new InvalidOperationException("The contact conductance must be positive.");
        if (settings.NormalOpposition is <= 0 or > 1)
            throw new InvalidOperationException("NormalOpposition must lie in (0, 1].");
        if (settings.GapTolerance < 0)
            throw new InvalidOperationException("The gap tolerance cannot be negative.");

        var triangles = mesh.BoundaryTriangles;
        int count = triangles.Count;
        if (count == 0) return Array.Empty<ContactInterface>();

        // Per-triangle body, area and unit normal — computed once, in index order.
        var body = new int[count];
        var area = new double[count];
        var normal = new Vector3D[count];
        double chordSum = 0;
        int chordCount = 0;
        for (int t = 0; t < count; t++)
        {
            var tri = triangles[t];
            body[t] = BodyOfNode(nodeBases, tri.A);
            var cross = Vector3D.Cross(mesh.Nodes[tri.B] - mesh.Nodes[tri.A],
                                       mesh.Nodes[tri.C] - mesh.Nodes[tri.A]);
            double len = cross.Length;
            area[t] = 0.5 * len;
            normal[t] = len > 0 ? cross / len : new Vector3D(0, 0, 0);
            if (area[t] > 0) { chordSum += Math.Sqrt(2 * area[t]); chordCount++; }
        }
        double meanChord = chordCount > 0 ? chordSum / chordCount : 0;
        double tolerance = settings.GapTolerance > 0 ? settings.GapTolerance : 0.25 * meanChord;
        if (tolerance <= 0) return Array.Empty<ContactInterface>();

        var grid = new TriangleGrid(mesh, triangles, Math.Max(meanChord, 2 * tolerance), tolerance);
        var pairs = new SortedDictionary<(int A, int B), Accumulator>();

        // Hoisted out of the triangle loop on purpose: a stackalloc inside a loop is only
        // freed when the method returns, which overflows the stack on a real assembly skin.
        Span<int> corners = stackalloc int[3];
        var found = new List<(int Vertex, int Target, double L0, double L1, double L2)>(3);
        for (int t = 0; t < count; t++)
        {
            if (area[t] <= 0) continue;
            var tri = triangles[t];
            corners[0] = tri.A; corners[1] = tri.B; corners[2] = tri.C;
            double weight = 0.5 * area[t] / 3.0;

            // A triangle belongs to at most one interface: the first partner body one of its
            // vertices finds. Later vertices that pair with a different body would be a
            // three-way corner, which the pairwise interface model does not represent.
            int partner = -1;
            int unpaired = 0;
            found.Clear();
            for (int c = 0; c < 3; c++)
            {
                int target = Project(mesh, triangles, grid, body, area, normal, corners[c], t,
                    tolerance, settings.NormalOpposition, out double l0, out double l1, out double l2);
                if (target < 0) { unpaired++; continue; }
                if (partner < 0) partner = body[target];
                if (body[target] != partner) { unpaired++; continue; }
                found.Add((corners[c], target, l0, l1, l2));
            }
            if (partner < 0 || found.Count == 0) continue;

            var key = body[t] < partner ? (body[t], partner) : (partner, body[t]);
            if (!pairs.TryGetValue(key, out var acc)) pairs[key] = acc = new Accumulator();
            foreach (var (vertex, target, l0, l1, l2) in found)
            {
                var u = triangles[target];
                acc.Stamps.Add(new ContactStamp(vertex, u.A, u.B, u.C, 1.0, -l0, -l1, -l2, weight));
                if (body[t] == key.Item1) acc.AreaA += weight * 2; else acc.AreaB += weight * 2;
            }
            acc.Unpaired += unpaired;
        }

        var result = new List<ContactInterface>();
        foreach (var ((a, b), acc) in pairs)
        {
            if (acc.Stamps.Count == 0) continue;
            double coupled = acc.Stamps.Sum(s => s.Weight);
            var contact = new ContactInterface
            {
                BodyA = a,
                BodyB = b,
                Stamps = acc.Stamps,
                Conductance = settings.DefaultConductance,
                CoupledArea = coupled,
                AreaA = acc.AreaA,
                AreaB = acc.AreaB,
                UnpairedPoints = acc.Unpaired
            };
            result.Add(contact);
            log?.Invoke($"Contact bodies {a}–{b}: {coupled:g4} m² coupled " +
                        $"({acc.Stamps.Count} quadrature points, {acc.Unpaired} unpaired), " +
                        $"h_c = {settings.DefaultConductance:g4} W/(m²·K), " +
                        $"gap tolerance {tolerance:g3} m.");
        }
        if (result.Count == 0)
            log?.Invoke($"No body-to-body contact found within {tolerance:g3} m — the bodies " +
                        "exchange heat only through the environment.");
        return result;
    }

    private sealed class Accumulator
    {
        public List<ContactStamp> Stamps { get; } = new();
        public double AreaA;
        public double AreaB;
        public int Unpaired;
    }

    /// <summary>
    /// Finds the opposing triangle a vertex projects onto: nearest by |signed distance to the
    /// triangle plane| among triangles of a DIFFERENT body whose normal opposes the source
    /// triangle's and which contain the projected point. Ties break to the lower triangle
    /// index, so the result never depends on traversal order.
    /// </summary>
    private static int Project(FeMesh mesh, IReadOnlyList<BoundaryTriangle> triangles, TriangleGrid grid,
        int[] body, double[] area, Vector3D[] normal, int node, int sourceTriangle,
        double tolerance, double opposition, out double l0, out double l1, out double l2)
    {
        l0 = l1 = l2 = 0;
        var p = mesh.Nodes[node];
        var ns = normal[sourceTriangle];
        int sourceBody = body[sourceTriangle];
        int best = -1;
        double bestDistance = double.MaxValue;

        foreach (int u in grid.Candidates(p))
        {
            if (body[u] == sourceBody || area[u] <= 0) continue;
            if (Vector3D.Dot(ns, normal[u]) > -opposition) continue;

            var tri = triangles[u];
            var a = mesh.Nodes[tri.A];
            double distance = Vector3D.Dot(p - a, normal[u]);
            double magnitude = Math.Abs(distance);
            if (magnitude > tolerance) continue;
            if (magnitude > bestDistance) continue;

            var projected = p - normal[u] * distance;
            if (!Barycentric(a, mesh.Nodes[tri.B], mesh.Nodes[tri.C], projected, area[u],
                    out double b0, out double b1, out double b2))
                continue;

            // Strictly-nearer wins; an exact tie (a vertex sitting on a shared edge) takes
            // the lower index.
            if (magnitude < bestDistance || u < best)
            {
                best = u;
                bestDistance = magnitude;
                l0 = b0; l1 = b1; l2 = b2;
            }
        }
        return best;
    }

    /// <summary>
    /// Barycentric coordinates of an in-plane point, from sub-triangle areas. The tolerance is
    /// relative to the triangle: a vertex of a matching mesh lands exactly on an edge (λ = 0)
    /// up to rounding, and a vertex on the rim of a partial overlap must not be admitted.
    /// </summary>
    private static bool Barycentric(Vector3D a, Vector3D b, Vector3D c, Vector3D p, double area,
        out double l0, out double l1, out double l2)
    {
        var n = Vector3D.Cross(b - a, c - a);
        double twice = 2 * area;
        l0 = Vector3D.Dot(Vector3D.Cross(b - p, c - p), n) / (twice * twice);
        l1 = Vector3D.Dot(Vector3D.Cross(c - p, a - p), n) / (twice * twice);
        l2 = 1.0 - l0 - l1;
        const double edge = -1e-9;
        return l0 >= edge && l1 >= edge && l2 >= edge;
    }

    private static int BodyOfNode(IReadOnlyList<int> nodeBases, int node)
    {
        for (int b = nodeBases.Count - 1; b >= 0; b--)
            if (node >= nodeBases[b]) return b;
        return 0;
    }

    /// <summary>
    /// Uniform bucket grid over the boundary triangles (the SegmentGrid recipe one dimension
    /// up). Triangles are inserted into every cell their tolerance-expanded bounding box
    /// covers, so a single-cell lookup finds every triangle a query point could project onto.
    /// </summary>
    private sealed class TriangleGrid
    {
        private readonly Dictionary<(int X, int Y, int Z), List<int>> _cells = new();
        private readonly double _cell;

        public TriangleGrid(FeMesh mesh, IReadOnlyList<BoundaryTriangle> triangles, double cell,
            double tolerance)
        {
            _cell = cell;
            for (int t = 0; t < triangles.Count; t++)
            {
                var tri = triangles[t];
                var a = mesh.Nodes[tri.A];
                var b = mesh.Nodes[tri.B];
                var c = mesh.Nodes[tri.C];
                var (x0, y0, z0) = Index(new Vector3D(
                    Math.Min(a.X, Math.Min(b.X, c.X)) - tolerance,
                    Math.Min(a.Y, Math.Min(b.Y, c.Y)) - tolerance,
                    Math.Min(a.Z, Math.Min(b.Z, c.Z)) - tolerance));
                var (x1, y1, z1) = Index(new Vector3D(
                    Math.Max(a.X, Math.Max(b.X, c.X)) + tolerance,
                    Math.Max(a.Y, Math.Max(b.Y, c.Y)) + tolerance,
                    Math.Max(a.Z, Math.Max(b.Z, c.Z)) + tolerance));
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!_cells.TryGetValue((x, y, z), out var list))
                                _cells[(x, y, z)] = list = new List<int>();
                            list.Add(t);
                        }
            }
        }

        public IReadOnlyList<int> Candidates(Vector3D point) =>
            _cells.TryGetValue(Index(point), out var list) ? list : Array.Empty<int>();

        private (int X, int Y, int Z) Index(Vector3D p) =>
            ((int)Math.Floor(p.X / _cell), (int)Math.Floor(p.Y / _cell), (int)Math.Floor(p.Z / _cell));
    }
}
