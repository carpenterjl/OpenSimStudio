using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Meshing2D;

namespace OpenSim.Pcb.Extrude;

/// <summary>
/// Extrudes one region of a planar triangulation into a watertight surface
/// <see cref="TriangleMesh"/> for display and general meshing. Face ids follow the
/// primitive-factory convention: 0 = top, 1 = bottom, 2+i = the side wall of the
/// i-th boundary loop (loops ≈ copper islands/holes, so pads are selectable).
/// </summary>
public static class PolygonExtruder
{
    public static TriangleMesh Extrude(PlanarMesh mesh, int regionId, double z0, double z1)
    {
        if (z1 <= z0)
            throw new ArgumentException("z1 must be above z0.");
        var tris = mesh.Triangles.Where(t => t.RegionId == regionId).ToList();
        if (tris.Count == 0)
            throw new InvalidOperationException($"Region {regionId} has no triangles to extrude.");

        // Compact 2D vertices used by this region; bottom sheet then top sheet.
        var remap = new Dictionary<int, int>();
        var points = new List<Point2Ref>();
        int Map(int v)
        {
            if (remap.TryGetValue(v, out int m)) return m;
            remap[v] = points.Count;
            points.Add(new Point2Ref(mesh.Points[v].X, mesh.Points[v].Y));
            return points.Count - 1;
        }
        foreach (var t in tris) { Map(t.A); Map(t.B); Map(t.C); }

        int n = points.Count;
        var vertices = new List<Vector3D>(2 * n);
        vertices.AddRange(points.Select(p => new Vector3D(p.X, p.Y, z0)));
        vertices.AddRange(points.Select(p => new Vector3D(p.X, p.Y, z1)));

        var triangles = new List<Triangle>();
        var faceIds = new List<int>();

        foreach (var t in tris)
        {
            int a = remap[t.A], b = remap[t.B], c = remap[t.C];
            triangles.Add(new Triangle(n + a, n + b, n + c));   // top: CCW from above → +z normal
            faceIds.Add(0);
            triangles.Add(new Triangle(a, c, b));               // bottom: reversed → −z normal
            faceIds.Add(1);
        }

        // Boundary edges of the region (used exactly once, kept in CCW direction) form
        // the side walls; group them into loops for face ids.
        var boundary = BoundaryEdges(tris, remap);
        var loopOf = GroupIntoLoops(boundary);
        foreach (var (edge, loop) in boundary.Zip(loopOf))
        {
            // Interior is left of (u,v); the outward wall normal points right of it.
            int u0 = edge.U, v0 = edge.V, u1 = n + edge.U, v1 = n + edge.V;
            triangles.Add(new Triangle(u0, v0, v1));
            faceIds.Add(2 + loop);
            triangles.Add(new Triangle(u0, v1, u1));
            faceIds.Add(2 + loop);
        }

        return new TriangleMesh(vertices, triangles, faceIds);
    }

    private readonly record struct Point2Ref(double X, double Y);
    internal readonly record struct Edge(int U, int V);

    /// <summary>
    /// Edges used by exactly one triangle, in the direction the (CCW) triangle uses
    /// them — so the region interior lies to their left.
    /// </summary>
    internal static List<Edge> BoundaryEdges(List<Tri2> tris, Dictionary<int, int> remap)
    {
        var count = new Dictionary<(int, int), int>();
        void Bump(int u, int v)
        {
            var key = u < v ? (u, v) : (v, u);
            count[key] = count.GetValueOrDefault(key) + 1;
        }
        foreach (var t in tris)
        {
            int a = remap[t.A], b = remap[t.B], c = remap[t.C];
            Bump(a, b); Bump(b, c); Bump(c, a);
        }
        var edges = new List<Edge>();
        foreach (var t in tris)
        {
            int a = remap[t.A], b = remap[t.B], c = remap[t.C];
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                if (count[u < v ? (u, v) : (v, u)] == 1)
                    edges.Add(new Edge(u, v));
        }
        return edges;
    }

    /// <summary>Union-find grouping of boundary edges into connected loops.</summary>
    internal static int[] GroupIntoLoops(List<Edge> edges)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            parent.TryAdd(x, x);
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (var e in edges)
            parent[Find(e.U)] = Find(e.V);

        var loopIndex = new Dictionary<int, int>();
        var result = new int[edges.Count];
        for (int i = 0; i < edges.Count; i++)
        {
            int root = Find(edges[i].U);
            if (!loopIndex.TryGetValue(root, out int loop))
                loopIndex[root] = loop = loopIndex.Count;
            result[i] = loop;
        }
        return result;
    }
}
