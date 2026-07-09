using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Laplacian smoothing of interior nodes: each node moves toward the average of its
/// edge-connected neighbours. Boundary nodes stay fixed and any move that would
/// invert or degrade an incident element below its current worst quality is rejected,
/// so smoothing can only improve the mesh.
/// </summary>
public static class MeshSmoother
{
    public static void Smooth(List<Vector3D> nodes, IReadOnlyList<Tet4> elements,
        IReadOnlySet<int> fixedNodes, int iterations = 5)
    {
        int n = nodes.Count;
        var neighbors = new HashSet<int>[n];
        var incident = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            neighbors[i] = new HashSet<int>();
            incident[i] = new List<int>();
        }
        Span<int> v = stackalloc int[4];
        for (int e = 0; e < elements.Count; e++)
        {
            var t = elements[e];
            v[0] = t.N0; v[1] = t.N1; v[2] = t.N2; v[3] = t.N3;
            foreach (int a in v)
            {
                incident[a].Add(e);
                foreach (int b in v)
                    if (a != b)
                        neighbors[a].Add(b);
            }
        }

        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = 0; i < n; i++)
            {
                if (fixedNodes.Contains(i) || neighbors[i].Count == 0) continue;

                var sum = Vector3D.Zero;
                foreach (int j in neighbors[i]) sum += nodes[j];
                var candidate = sum / neighbors[i].Count;

                double worstBefore = double.PositiveInfinity;
                foreach (int e in incident[i])
                    worstBefore = Math.Min(worstBefore, ElementQuality(nodes, elements[e]));

                var old = nodes[i];
                nodes[i] = candidate;
                double worstAfter = double.PositiveInfinity;
                foreach (int e in incident[i])
                {
                    var t = elements[e];
                    if (GeometricPredicates.Orient3D(nodes[t.N0], nodes[t.N1], nodes[t.N2], nodes[t.N3]) <= 0)
                    {
                        worstAfter = double.NegativeInfinity;
                        break;
                    }
                    worstAfter = Math.Min(worstAfter, ElementQuality(nodes, t));
                }
                if (worstAfter < worstBefore)
                    nodes[i] = old; // move rejected
            }
        }
    }

    private static double ElementQuality(IReadOnlyList<Vector3D> nodes, Tet4 t) =>
        MeshQuality.RadiusRatio(nodes[t.N0], nodes[t.N1], nodes[t.N2], nodes[t.N3]);
}
