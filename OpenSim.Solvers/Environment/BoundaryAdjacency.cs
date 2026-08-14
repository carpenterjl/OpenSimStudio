using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers.Environment;

/// <summary>
/// Per-boundary-triangle geometry the environment needs: area, OUTWARD unit normal, and
/// the mesh region (body) the triangle belongs to.
/// <para>
/// The outward direction is taken from the element behind the triangle — the normal points
/// away from that element's fourth node — rather than from the triangle's winding. Which
/// way a face points decides whether it is a plate the plume rises off or one the plume is
/// trapped under, a factor of two in the film coefficient; deriving it from a winding
/// convention would make the whole environment silently wrong on any mesh that used the
/// other one.
/// </para>
/// </summary>
internal sealed class BoundaryAdjacency
{
    private BoundaryAdjacency(Vector3D[] normals, double[] areas, int[] regions)
    {
        OutwardNormals = normals;
        Areas = areas;
        Regions = regions;
    }

    /// <summary>Unit outward normal per boundary triangle.</summary>
    public IReadOnlyList<Vector3D> OutwardNormals { get; }

    /// <summary>Area per boundary triangle [m²].</summary>
    public IReadOnlyList<double> Areas { get; }

    /// <summary>Mesh region id per boundary triangle (the body index in an assembly).</summary>
    public IReadOnlyList<int> Regions { get; }

    public static BoundaryAdjacency Build(FeMesh mesh)
    {
        int count = mesh.BoundaryTriangles.Count;
        var normals = new Vector3D[count];
        var areas = new double[count];
        var regions = new int[count];
        var matched = new bool[count];

        // Only boundary triangles are indexed, so the map is O(surface), not O(volume).
        var index = new Dictionary<(int, int, int), int>(count);
        for (int t = 0; t < count; t++)
        {
            var bt = mesh.BoundaryTriangles[t];
            index[Sorted(bt.A, bt.B, bt.C)] = t;
            var cross = Vector3D.Cross(mesh.Nodes[bt.B] - mesh.Nodes[bt.A],
                mesh.Nodes[bt.C] - mesh.Nodes[bt.A]);
            areas[t] = 0.5 * cross.Length;
            normals[t] = cross.Length > 0 ? cross / cross.Length : new Vector3D(0, 0, 1);
        }

        // Hoisted: a stackalloc inside the loop is freed only when the method returns.
        Span<int> n = stackalloc int[4];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            n[0] = el.N0; n[1] = el.N1; n[2] = el.N2; n[3] = el.N3;
            for (int opposite = 0; opposite < 4; opposite++)
            {
                int a = n[(opposite + 1) & 3], b = n[(opposite + 2) & 3], c = n[(opposite + 3) & 3];
                if (!index.TryGetValue(Sorted(a, b, c), out int t) || matched[t]) continue;
                matched[t] = true;
                regions[t] = mesh.RegionOf(e);
                // Flip toward the outside: away from the element's remaining node.
                var inward = mesh.Nodes[n[opposite]] - mesh.Nodes[a];
                if (Vector3D.Dot(normals[t], inward) > 0)
                    normals[t] = -normals[t];
            }
        }

        return new BoundaryAdjacency(normals, areas, regions);
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
        return (lo, a + b + c - lo - hi, hi);
    }
}
