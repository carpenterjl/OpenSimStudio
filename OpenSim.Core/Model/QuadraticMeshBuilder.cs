using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>
/// Upgrades a linear mesh to quadratic by inserting one midpoint node per unique element
/// edge (straight/subparametric edges — no surface snapping, so element volumes and the
/// boundary skin are unchanged): TET4 becomes TET10, a hexahedral corner mesh becomes
/// HEX20. Lives in Core because both the mesher and the solvers need it and Solvers does
/// not reference Meshing.
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
    /// The twelve edges of a hexahedron as local corner index pairs, in the field order of
    /// <see cref="Hex20Mid"/>: the base ring, the top ring, then the verticals.
    /// </summary>
    private static readonly (int A, int B)[] HexEdges =
    {
        (0, 1), (1, 2), (2, 3), (3, 0),
        (4, 5), (5, 6), (6, 7), (7, 4),
        (0, 4), (1, 5), (2, 6), (3, 7)
    };

    /// <summary>
    /// Returns a HEX20 mesh over the given hexahedral corner mesh, one midpoint node per
    /// unique element edge. Both skins pass through unchanged: their corner indices still
    /// name the same nodes, and their mid-edge nodes are reached through
    /// <see cref="BuildEdgeMidMap"/> exactly as a tetrahedral skin reaches its own.
    /// </summary>
    public static FeMesh UpgradeHex(IReadOnlyList<Vector3D> cornerNodes, IReadOnlyList<Hex8> hexes,
        IReadOnlyList<BoundaryTriangle> boundaryTriangles, IReadOnlyList<BoundaryQuad> boundaryQuads,
        IReadOnlyList<int>? elementRegionIds = null)
    {
        var nodes = new List<Vector3D>(cornerNodes);
        var edgeMid = new Dictionary<(int, int), int>();

        int MidOf(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!edgeMid.TryGetValue(key, out int mid))
            {
                mid = nodes.Count;
                nodes.Add((cornerNodes[a] + cornerNodes[b]) / 2.0);
                edgeMid[key] = mid;
            }
            return mid;
        }

        var mids = new List<Hex20Mid>(hexes.Count);
        var corner = new int[8];
        var m = new int[12];
        foreach (var h in hexes)
        {
            corner[0] = h.N0; corner[1] = h.N1; corner[2] = h.N2; corner[3] = h.N3;
            corner[4] = h.N4; corner[5] = h.N5; corner[6] = h.N6; corner[7] = h.N7;
            for (int e = 0; e < 12; e++)
                m[e] = MidOf(corner[HexEdges[e].A], corner[HexEdges[e].B]);
            mids.Add(new Hex20Mid(m[0], m[1], m[2], m[3], m[4], m[5],
                                  m[6], m[7], m[8], m[9], m[10], m[11]));
        }

        return new FeMesh(nodes, Array.Empty<Tet4>(), boundaryTriangles, elementRegionIds,
            midEdgeNodes: null, hexElements: hexes, hexMidEdgeNodes: mids,
            boundaryQuads: boundaryQuads);
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
        if (mesh.HexElements is not null) return BuildHexEdgeMidMap(mesh);
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

    /// <summary>
    /// The hexahedral counterpart: twelve edges per element. Every edge of every boundary
    /// QUAD is an element edge, so a load or support scoped to a quad face always resolves.
    /// The DIAGONAL of a triangulated quad is deliberately absent — nothing should be looking
    /// one up (see <see cref="BoundaryQuad"/>).
    /// </summary>
    private static Dictionary<(int, int), int> BuildHexEdgeMidMap(FeMesh mesh)
    {
        var map = new Dictionary<(int, int), int>();
        var corner = new int[8];
        for (int i = 0; i < mesh.ElementCount; i++)
        {
            var h = mesh.HexElements![i];
            var hm = mesh.HexMidEdgeNodes![i];
            corner[0] = h.N0; corner[1] = h.N1; corner[2] = h.N2; corner[3] = h.N3;
            corner[4] = h.N4; corner[5] = h.N5; corner[6] = h.N6; corner[7] = h.N7;
            Span<int> mids = stackalloc int[]
            {
                hm.M01, hm.M12, hm.M23, hm.M30,
                hm.M45, hm.M56, hm.M67, hm.M74,
                hm.M04, hm.M15, hm.M26, hm.M37
            };
            for (int e = 0; e < 12; e++)
            {
                int a = corner[HexEdges[e].A], b = corner[HexEdges[e].B];
                map[a < b ? (a, b) : (b, a)] = mids[e];
            }
        }
        return map;
    }
}
