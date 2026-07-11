using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>
/// A discretized PEC surface ready for the moment-method solve: vertices, consistently
/// oriented triangles, and one RWG (Rao–Wilton–Glisson) current basis per INTERIOR
/// edge — an edge shared by exactly two triangles with opposite traversal direction
/// (the <c>TriangleMesh.IsWatertight</c> directed-edge idiom on an open mesh).
/// Boundary edges carry no basis, so the current component normal to the rim vanishes
/// by construction — the surface analog of open wire ends. On its plus triangle the
/// basis is f = (l/2A)(r − p⁺) flowing TOWARD the edge (div = +l/A); on the minus
/// triangle f = (l/2A)(p⁻ − r) (div = −l/A): current crosses the edge from T⁺ to T⁻.
///
/// EXCEPTION, mirroring grounded wire ends: a boundary edge whose both vertices sit
/// exactly ON the ground plane is a GROUNDED edge and gets a half-RWG basis with only
/// its plus triangle real (<see cref="RwgEdge.MinusTriangle"/> = −1); the minus half
/// lives on the image current and enters through the solver's image pass, so current
/// flows continuously into the ground — the strip-monopole base.
/// </summary>
public sealed class SurfaceStructure
{
    /// <summary>One RWG edge: vertex pair (V1 &lt; V2), the two supporting triangles
    /// (Plus = the one traversing V1→V2 in its CCW boundary; Minus = −1 for a grounded
    /// rim edge whose other half is the image), the opposite vertex on each side, and
    /// the edge length.</summary>
    public readonly record struct RwgEdge(
        int V1, int V2, int PlusTriangle, int MinusTriangle,
        int PlusOpposite, int MinusOpposite, double Length);

    internal SurfaceStructure(IReadOnlyList<Vector3D> vertices,
        IReadOnlyList<(int A, int B, int C)> triangles, GroundPlane? ground)
    {
        Vertices = vertices;
        Triangles = triangles;
        Ground = ground;

        // Directed-edge map: interior edge = seen once in each direction.
        var directed = new Dictionary<(int, int), (int Triangle, int Opposite)>();
        foreach (var (t, (a, b, c)) in triangles.Select((tri, i) => (i, tri)))
        {
            foreach (var (u, v, opp) in new[] { (a, b, c), (b, c, a), (c, a, b) })
            {
                if (!directed.TryAdd((u, v), (t, opp)))
                    throw new InvalidOperationException(
                        $"Edge {u}→{v} appears twice in the same direction — the mesh is " +
                        "non-manifold or inconsistently oriented.");
            }
        }

        if (ground is { } plane)
            foreach (var vertex in vertices)
                if (vertex.Z < plane.SurfaceZ)
                    throw new InvalidOperationException(
                        $"a vertex at z = {vertex.Z:g4} lies below the ground plane at " +
                        $"z = {plane.SurfaceZ:g4} — image theory needs the metal strictly above it " +
                        "(only a rim edge may touch)");

        var edges = new List<RwgEdge>();
        foreach (var ((u, v), (plusTri, plusOpp)) in directed)
        {
            double length = (vertices[v] - vertices[u]).Length;
            if (directed.TryGetValue((v, u), out var minus))
            {
                if (u > v) continue;   // canonical: enumerate each undirected edge once
                edges.Add(new RwgEdge(u, v, plusTri, minus.Triangle, plusOpp, minus.Opposite, length));
                continue;
            }
            // Boundary edge: grounded (half-RWG, image supplies the minus half) when
            // both vertices sit exactly on the plane — the builder snaps them bitwise.
            if (ground is { } g && vertices[u].Z == g.SurfaceZ && vertices[v].Z == g.SurfaceZ)
                edges.Add(new RwgEdge(Math.Min(u, v), Math.Max(u, v), plusTri, -1, plusOpp, -1, length));
        }
        // Deterministic basis ordering regardless of dictionary iteration order.
        edges.Sort((x, y) => x.V1 != y.V1 ? x.V1.CompareTo(y.V1) : x.V2.CompareTo(y.V2));
        Edges = edges;

        var areas = new double[triangles.Count];
        var centroids = new Vector3D[triangles.Count];
        for (int t = 0; t < triangles.Count; t++)
        {
            var (a, b, c) = triangles[t];
            areas[t] = Vector3D.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).Length / 2;
            if (areas[t] <= 0)
                throw new InvalidOperationException($"Triangle {t} is degenerate (zero area).");
            if (ground is { } gp && vertices[a].Z == gp.SurfaceZ && vertices[b].Z == gp.SurfaceZ
                && vertices[c].Z == gp.SurfaceZ)
                throw new InvalidOperationException(
                    $"triangle {t} lies IN the ground plane — image theory needs the metal " +
                    "strictly above it (only a rim edge may touch)");
            centroids[t] = (vertices[a] + vertices[b] + vertices[c]) / 3;
        }
        TriangleAreas = areas;
        TriangleCentroids = centroids;

        // Per-triangle supported bases: (basis, sign σ = ±1, opposite vertex).
        var supports = new List<(int Basis, double Sign, int Opposite)>[triangles.Count];
        for (int t = 0; t < triangles.Count; t++) supports[t] = new(3);
        for (int e = 0; e < edges.Count; e++)
        {
            supports[edges[e].PlusTriangle].Add((e, +1.0, edges[e].PlusOpposite));
            if (edges[e].MinusTriangle >= 0)
                supports[edges[e].MinusTriangle].Add((e, -1.0, edges[e].MinusOpposite));
        }
        TriangleSupports = supports;
    }

    public IReadOnlyList<Vector3D> Vertices { get; }

    /// <summary>CCW vertex triples (consistent orientation enforced at construction).</summary>
    public IReadOnlyList<(int A, int B, int C)> Triangles { get; }

    /// <summary>The infinite PEC plane the solve images against, or null for free space.</summary>
    public GroundPlane? Ground { get; }

    /// <summary>Interior edges in deterministic (V1, V2) order — one RWG basis each.</summary>
    public IReadOnlyList<RwgEdge> Edges { get; }

    public IReadOnlyList<double> TriangleAreas { get; }
    public IReadOnlyList<Vector3D> TriangleCentroids { get; }

    /// <summary>Per triangle: the (≤3) bases it supports, each with its RWG sign
    /// (+1 on the plus side, −1 on the minus side) and the opposite vertex.</summary>
    public IReadOnlyList<IReadOnlyList<(int Basis, double Sign, int Opposite)>> TriangleSupports { get; }

    /// <summary>Number of current unknowns (interior edges).</summary>
    public int BasisCount => Edges.Count;

    /// <summary>The basis whose edge midpoint lies nearest <paramref name="point"/> —
    /// how a feed location request resolves to a port edge.</summary>
    public int NearestEdge(Vector3D point)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int e = 0; e < Edges.Count; e++)
        {
            var mid = (Vertices[Edges[e].V1] + Vertices[Edges[e].V2]) / 2;
            double distance = (mid - point).Length;
            if (distance < bestDistance) { bestDistance = distance; best = e; }
        }
        return best;
    }

    /// <summary>Total metal area [m²].</summary>
    public double TotalArea() => TriangleAreas.Sum();
}

/// <summary>A delta-gap voltage port across a group of interior edges (one edge for
/// unstructured meshes; a colinear vertex-line group on wizard grids). The reference
/// <see cref="Direction"/> orients the port: an RWG basis whose T⁺→T⁻ crossing agrees
/// with it is driven at +V·l, the opposite at −V·l.</summary>
public sealed record SurfacePort(IReadOnlyList<int> EdgeBases, Vector3D Direction);

/// <summary>Either a solvable surface or the specific reason none could be built,
/// plus accuracy warnings that must reach the user (never silently degraded).</summary>
public sealed record SurfaceGridResult(SurfaceStructure? Structure, SurfacePort? Port, string? FailureReason)
{
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static SurfaceGridResult Success(SurfaceStructure structure, SurfacePort port) =>
        new(structure, port, null);

    public static SurfaceGridResult Failure(string reason) => new(null, null, reason);
}
