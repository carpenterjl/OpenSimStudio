using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>
/// Consistent surface loads and support pinning for the structural solvers — the part that
/// depends on what the boundary faces ARE, shared so the static and modal solvers cannot
/// drift apart on it.
/// </summary>
internal static class StructuralSurfaceLoads
{
    /// <summary>3-point Gauss on [-1, 1], enough for the QUAD8 surface integrals below.</summary>
    private static readonly double[] GaussNodes = { -0.7745966692414834, 0.0, 0.7745966692414834 };
    private static readonly double[] GaussWeights = { 5.0 / 9.0, 8.0 / 9.0, 5.0 / 9.0 };

    /// <summary>QUAD8 serendipity shape functions and their reference derivatives at (s, t).
    /// Node order: the four corners, then the mid-side nodes of edges AB, BC, CD, DA.</summary>
    private static void QuadShape(double s, double t, double[] n, double[] ds, double[] dt)
    {
        double[] sc = { -1, 1, 1, -1 };
        double[] tc = { -1, -1, 1, 1 };

        for (int i = 0; i < 4; i++)
        {
            double a = 1 + sc[i] * s, b = 1 + tc[i] * t;
            n[i] = 0.25 * a * b * (sc[i] * s + tc[i] * t - 1);
            ds[i] = 0.25 * sc[i] * b * (2 * sc[i] * s + tc[i] * t);
            dt[i] = 0.25 * tc[i] * a * (sc[i] * s + 2 * tc[i] * t);
        }

        // Mid-side nodes, in edge order AB (t = -1), BC (s = +1), CD (t = +1), DA (s = -1).
        n[4] = 0.5 * (1 - s * s) * (1 - t); ds[4] = -s * (1 - t); dt[4] = -0.5 * (1 - s * s);
        n[5] = 0.5 * (1 + s) * (1 - t * t); ds[5] = 0.5 * (1 - t * t); dt[5] = -t * (1 + s);
        n[6] = 0.5 * (1 - s * s) * (1 + t); ds[6] = -s * (1 + t); dt[6] = 0.5 * (1 - s * s);
        n[7] = 0.5 * (1 - s) * (1 - t * t); ds[7] = -0.5 * (1 - t * t); dt[7] = -t * (1 - s);
    }

    /// <summary>
    /// The consistent nodal load weights of one boundary quad: for each of its 8 nodes, the
    /// scalar integral of that node's shape function over the face, and the same integral
    /// against the OUTWARD area vector (which is what a pressure needs).
    /// <para>
    /// On a flat rectangle these come out as the classic QUAD8 result — A/3 to each mid-side
    /// node and MINUS A/12 to each corner. The negative corner share is why a hexahedral face
    /// cannot be loaded as two triangles: the T6 rule would put zero on the corners and A/3 on
    /// three edge mid-nodes of a triangulation whose diagonal is not even an element edge.
    /// </para>
    /// </summary>
    public static (int[] Nodes, double[] Areas, Vector3D[] AreaVectors) QuadLoadWeights(
        FeMesh mesh, BoundaryQuad quad, Dictionary<(int, int), int> edgeMid)
    {
        int Mid(int a, int b) => edgeMid[a < b ? (a, b) : (b, a)];
        var nodes = new[]
        {
            quad.A, quad.B, quad.C, quad.D,
            Mid(quad.A, quad.B), Mid(quad.B, quad.C), Mid(quad.C, quad.D), Mid(quad.D, quad.A)
        };

        var areas = new double[8];
        var areaVectors = new Vector3D[8];
        var n = new double[8];
        var ds = new double[8];
        var dt = new double[8];

        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                QuadShape(GaussNodes[i], GaussNodes[j], n, ds, dt);

                Vector3D dS = default, dT = default;
                for (int k = 0; k < 8; k++)
                {
                    var p = mesh.Nodes[nodes[k]];
                    dS += p * ds[k];
                    dT += p * dt[k];
                }

                var normal = Vector3D.Cross(dS, dT);       // outward: the quad is wound outward
                double jacobian = normal.Length;
                double w = GaussWeights[i] * GaussWeights[j];

                for (int k = 0; k < 8; k++)
                {
                    areas[k] += w * n[k] * jacobian;
                    areaVectors[k] += normal * (w * n[k]);
                }
            }

        return (nodes, areas, areaVectors);
    }

    /// <summary>
    /// Every DOF a set of fixed supports constrains, including the mid-edge nodes of the
    /// scope on a quadratic mesh.
    /// <para>
    /// Pinning only corners leaves the mid-edge nodes free, which is spurious compliance at a
    /// support. The scope's segments come from the TRIANGULAR skin, and on a hexahedral mesh
    /// two of every quad's six triangle edges are the triangulation DIAGONAL — not an element
    /// edge, so it has no mid-node and there is nothing to pin. Skipping it there loses
    /// nothing: the two triangles of a quad together cover all four of its perimeter edges,
    /// and HEX20 has no face-interior node. On a tetrahedral mesh every skin edge IS an
    /// element edge, so a miss means something is genuinely wrong and still throws.
    /// </para>
    /// </summary>
    public static Dictionary<int, double> BuildPrescribedDofs(FeMesh mesh,
        IReadOnlyList<BoundaryCondition> conditions, Dictionary<(int, int), int>? edgeMid,
        List<string>? log)
    {
        var prescribed = new Dictionary<int, double>();
        foreach (var support in conditions.OfType<FixedSupport>())
        {
            var nodes = new HashSet<int>(mesh.GetScopeNodes(support));
            if (edgeMid is not null)
            {
                foreach (var segment in mesh.GetScopeSegments(support))
                {
                    var key = segment.A < segment.B ? (segment.A, segment.B) : (segment.B, segment.A);
                    if (edgeMid.TryGetValue(key, out int mid)) nodes.Add(mid);
                    else if (!mesh.IsHex)
                        throw new KeyNotFoundException(
                            $"Fixed support '{support.Name}' covers mesh edge ({segment.A}, {segment.B}), " +
                            "which carries no mid-edge node. The quadratic mesh is inconsistent.");
                }
            }
            foreach (int node in nodes)
            {
                prescribed[node * 3] = 0;
                prescribed[node * 3 + 1] = 0;
                prescribed[node * 3 + 2] = 0;
            }
            log?.Add($"Fixed support '{support.Name}': {nodes.Count} nodes fully constrained.");
        }
        return prescribed;
    }

    /// <summary>The assembler for a mesh's element family and order.</summary>
    public static IElasticityAssembler AssemblerFor(FeMesh mesh, Material material) =>
        mesh.IsHex ? new Hex20Assembler(mesh, material)
        : mesh.IsQuadratic ? new Tet10Assembler(mesh, material)
        : new Tet4Assembler(mesh, material);

    /// <summary>The element type, for the solve log.</summary>
    public static string ElementName(FeMesh mesh) =>
        mesh.IsHex ? "HEX20" : mesh.IsQuadratic ? "TET10" : "TET4";
}
