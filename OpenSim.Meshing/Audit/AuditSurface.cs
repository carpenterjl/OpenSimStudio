using OpenSim.Core.Numerics;

namespace OpenSim.Meshing.Audit;

/// <summary>One connected closed surface of a body: an outer skin or the wall of a void.</summary>
/// <param name="Genus">Handles: 0 for a sphere-like shell, 1 for a through-hole, … from
/// χ = V − E + F. Not an integer when the shell is not a closed orientable 2-manifold.</param>
/// <param name="Depth">How many other shells enclose this one.</param>
/// <param name="Parent">The enclosing shell one level out, or −1.</param>
/// <param name="Region">The material region this shell bounds: its own when it is an outer
/// skin (even depth), its parent's when it is a void wall (odd depth).</param>
internal sealed record SurfaceShell(
    int Id,
    IReadOnlyList<int> Triangles,
    double SignedVolume,
    double Area,
    double Genus,
    int Depth,
    int Parent,
    int Region);

/// <summary>
/// A closed triangle surface taken apart the way the audit needs it, independent of face
/// ids: connected shells, how they nest, which of them bound material, and which way is
/// "out of the material" at every triangle.
/// <para>
/// Nesting is by ray parity, the rule <see cref="PointInSolidClassifier"/> classifies by:
/// every even-depth shell is the outer skin of a material region and its odd-depth children
/// are that region's voids, so a solid island inside a cavity (depths 0, 1, 2) is two
/// regions, not one region with a forgotten interior.
/// </para>
/// </summary>
internal sealed class AuditSurface
{
    public IReadOnlyList<Vector3D> Vertices { get; }
    public (int A, int B, int C)[] Triangles { get; }
    public SurfaceBvh Bvh { get; }
    public SurfaceShell[] Shells { get; }
    public int[] ShellOf { get; }

    /// <summary>Unit normal pointing out of the material at each triangle (zero for a
    /// degenerate triangle). Derived from nesting, not trusted from the winding of a void.</summary>
    public Vector3D[] Outward { get; }

    public double[] TriangleAreas { get; }

    /// <summary>Number of material regions (even-depth shells).</summary>
    public int RegionCount { get; }

    /// <summary>Volume of each material region: its outer shell less its voids.</summary>
    public double[] RegionVolumes { get; }

    /// <summary>Wetted area of each material region: its outer shell plus its voids.</summary>
    public double[] RegionAreas { get; }

    /// <summary>Edges not shared by exactly two triangles; the surface is closed and
    /// manifold along its edges exactly when this is zero.</summary>
    public int IrregularEdgeCount { get; }

    /// <summary>The two triangles on each edge, for edges that have exactly two.</summary>
    public Dictionary<(int, int), (int T0, int T1)> EdgeTriangles { get; }

    public AuditSurface(IReadOnlyList<Vector3D> vertices, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        Vertices = vertices;
        Triangles = triangles as (int, int, int)[] ?? triangles.ToArray();
        int n = Triangles.Length;
        Bvh = new SurfaceBvh(vertices, Triangles);

        TriangleAreas = new double[n];
        for (int t = 0; t < n; t++)
        {
            var (a, b, c) = Triangles[t];
            TriangleAreas[t] = 0.5 * Vector3D.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).Length;
        }

        // Edge adjacency.
        var edgeUse = new Dictionary<(int, int), (int T0, int T1, int Count)>(3 * n / 2 + 1);
        for (int t = 0; t < n; t++)
        {
            var (a, b, c) = Triangles[t];
            Touch(edgeUse, a, b, t); Touch(edgeUse, b, c, t); Touch(edgeUse, c, a, t);
        }
        EdgeTriangles = new Dictionary<(int, int), (int, int)>(edgeUse.Count);
        int irregular = 0;
        foreach (var (edge, use) in edgeUse)
        {
            if (use.Count == 2) EdgeTriangles[edge] = (use.T0, use.T1);
            else irregular++;
        }
        IrregularEdgeCount = irregular;

        // Shells: triangles joined across shared edges.
        var parent = new int[n];
        for (int t = 0; t < n; t++) parent[t] = t;
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (var use in edgeUse.Values)
        {
            if (use.Count < 2) continue;
            int ra = Find(use.T0), rb = Find(use.T1);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
        var shellIndex = new Dictionary<int, int>();
        ShellOf = new int[n];
        var members = new List<List<int>>();
        for (int t = 0; t < n; t++)
        {
            int root = Find(t);
            if (!shellIndex.TryGetValue(root, out int s))
            {
                shellIndex[root] = s = members.Count;
                members.Add(new List<int>());
            }
            ShellOf[t] = s;
            members[s].Add(t);
        }
        int shellCount = members.Count;

        // Per-shell measures.
        var signedVolume = new double[shellCount];
        var area = new double[shellCount];
        var genus = new double[shellCount];
        for (int s = 0; s < shellCount; s++)
        {
            var shellVertices = new HashSet<int>();
            var shellEdges = new HashSet<(int, int)>();
            // Volume about a point on the shell, so a small shell far from the origin does
            // not lose its volume to cancellation.
            var reference = vertices[Triangles[members[s][0]].A];
            foreach (int t in members[s])
            {
                var (a, b, c) = Triangles[t];
                signedVolume[s] += Vector3D.Dot(vertices[a] - reference,
                    Vector3D.Cross(vertices[b] - reference, vertices[c] - reference)) / 6.0;
                area[s] += TriangleAreas[t];
                shellVertices.Add(a); shellVertices.Add(b); shellVertices.Add(c);
                shellEdges.Add(Key(a, b)); shellEdges.Add(Key(b, c)); shellEdges.Add(Key(c, a));
            }
            int euler = shellVertices.Count - shellEdges.Count + members[s].Count;
            genus[s] = (2 - euler) / 2.0;
        }

        // Nesting: a point of each shell, classified against every other shell by parity.
        var containing = new List<int>[shellCount];
        var depth = new int[shellCount];
        if (shellCount > 1)
        {
            var hits = new List<SurfaceBvh.RayHit>();
            var votes = new int[shellCount];
            var crossings = new int[shellCount];
            for (int s = 0; s < shellCount; s++)
            {
                int probe = members[s][0];
                foreach (int t in members[s])
                    if (TriangleAreas[t] > TriangleAreas[probe]) probe = t;
                var origin = Bvh.Centroid(probe);

                Array.Clear(votes);
                foreach (var direction in SurfaceBvh.ParityDirections.Take(3))
                {
                    Array.Clear(crossings);
                    hits.Clear();
                    Bvh.AllHits(origin, direction, hits);
                    foreach (var hit in hits)
                        if (ShellOf[hit.Triangle] != s) crossings[ShellOf[hit.Triangle]]++;
                    for (int other = 0; other < shellCount; other++)
                        if (crossings[other] % 2 == 1) votes[other]++;
                }
                containing[s] = new List<int>();
                for (int other = 0; other < shellCount; other++)
                    if (other != s && votes[other] >= 2) containing[s].Add(other);
                depth[s] = containing[s].Count;
            }
        }
        else
        {
            containing[0] = new List<int>();
        }

        var parentShell = new int[shellCount];
        for (int s = 0; s < shellCount; s++)
        {
            parentShell[s] = -1;
            foreach (int other in containing[s])
                if (depth[other] == depth[s] - 1) parentShell[s] = other;
        }

        // Regions: one per even-depth shell.
        var regionOfShell = new int[shellCount];
        int regions = 0;
        for (int s = 0; s < shellCount; s++)
            regionOfShell[s] = depth[s] % 2 == 0 ? regions++ : -1;
        for (int s = 0; s < shellCount; s++)
            if (regionOfShell[s] < 0)
                regionOfShell[s] = parentShell[s] >= 0 ? regionOfShell[parentShell[s]] : -1;
        RegionCount = regions;
        RegionVolumes = new double[regions];
        RegionAreas = new double[regions];
        for (int s = 0; s < shellCount; s++)
        {
            int r = regionOfShell[s];
            if (r < 0) continue;
            RegionVolumes[r] += depth[s] % 2 == 0 ? Math.Abs(signedVolume[s]) : -Math.Abs(signedVolume[s]);
            RegionAreas[r] += area[s];
        }

        Shells = new SurfaceShell[shellCount];
        for (int s = 0; s < shellCount; s++)
            Shells[s] = new SurfaceShell(s, members[s], signedVolume[s], area[s], genus[s], depth[s],
                parentShell[s], regionOfShell[s]);

        // Out of the material: away from an outer skin's interior, into a void's.
        Outward = new Vector3D[n];
        for (int t = 0; t < n; t++)
        {
            int s = ShellOf[t];
            bool woundOutOfShell = signedVolume[s] >= 0;
            bool materialInside = depth[s] % 2 == 0;
            Outward[t] = woundOutOfShell == materialInside ? Bvh.Normals[t] : -Bvh.Normals[t];
        }
    }

    /// <summary>Region of the shell a triangle belongs to (−1 for an orphan void).</summary>
    public int RegionOfTriangle(int triangle) => Shells[ShellOf[triangle]].Region;

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    private static void Touch(Dictionary<(int, int), (int T0, int T1, int Count)> map, int a, int b, int t)
    {
        var key = Key(a, b);
        map[key] = map.TryGetValue(key, out var use)
            ? (use.T0, use.Count == 1 ? t : use.T1, use.Count + 1)
            : (t, -1, 1);
    }
}
