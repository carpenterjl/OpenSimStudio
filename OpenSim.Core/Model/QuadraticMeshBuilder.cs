using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>
/// Upgrades a linear TET4 mesh to quadratic TET10 by inserting one midpoint node per
/// unique element edge (straight/subparametric edges — no surface snapping, so element
/// volumes and the boundary skin are unchanged). Lives in Core because both the mesher
/// and the solvers need it and Solvers does not reference Meshing.
/// </summary>
public static class QuadraticMeshBuilder
{
    /// <summary>Returns a new quadratic mesh sharing the corner geometry of <paramref name="linear"/>.</summary>
    public static FeMesh Upgrade(FeMesh linear)
    {
        if (linear.IsQuadratic)
            throw new InvalidOperationException("The mesh is already quadratic.");

        var nodes = new List<Vector3D>(linear.Nodes);
        var edgeMid = new Dictionary<(int, int), int>();

        int MidOf(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!edgeMid.TryGetValue(key, out int mid))
            {
                mid = nodes.Count;
                nodes.Add((linear.Nodes[a] + linear.Nodes[b]) / 2.0);
                edgeMid[key] = mid;
            }
            return mid;
        }

        var mids = new List<Tet10Mid>(linear.ElementCount);
        foreach (var e in linear.Elements)
            mids.Add(new Tet10Mid(
                MidOf(e.N0, e.N1), MidOf(e.N0, e.N2), MidOf(e.N0, e.N3),
                MidOf(e.N1, e.N2), MidOf(e.N1, e.N3), MidOf(e.N2, e.N3)));

        return new FeMesh(nodes, linear.Elements, linear.BoundaryTriangles,
            linear.ElementRegionIds, mids);
    }

    /// <summary>
    /// Reconstructs the (cornerA, cornerB) → mid-node lookup from a quadratic mesh.
    /// Recomputed on demand (O(elements)) rather than serialized. Every boundary
    /// triangle edge is also a tet edge, so boundary-condition application never
    /// misses: fixed supports must pin the mid-edge nodes of constrained faces and
    /// surface loads must address them.
    /// </summary>
    public static Dictionary<(int, int), int> BuildEdgeMidMap(FeMesh mesh)
    {
        if (mesh.MidEdgeNodes is null)
            throw new InvalidOperationException("The mesh is linear; there are no mid-edge nodes.");

        var map = new Dictionary<(int, int), int>();
        for (int i = 0; i < mesh.ElementCount; i++)
        {
            var e = mesh.Elements[i];
            var m = mesh.MidEdgeNodes[i];
            void Set(int a, int b, int mid) => map[a < b ? (a, b) : (b, a)] = mid;
            Set(e.N0, e.N1, m.M01); Set(e.N0, e.N2, m.M02); Set(e.N0, e.N3, m.M03);
            Set(e.N1, e.N2, m.M12); Set(e.N1, e.N3, m.M13); Set(e.N2, e.N3, m.M23);
        }
        return map;
    }
}
