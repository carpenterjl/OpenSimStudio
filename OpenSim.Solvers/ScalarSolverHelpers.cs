using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Solvers;

/// <summary>Shared load-distribution and field-recovery helpers for the scalar solvers.</summary>
internal static class ScalarSolverHelpers
{
    /// <summary>
    /// The time-constant thermal nodal load vector shared by the steady and transient
    /// heat solvers: surface heat flows, the convection ambient term h·T_amb·A/3 per
    /// triangle node, and consistent q·V/4 nodal loads of the element source.
    /// </summary>
    public static double[] AssembleThermalLoads(SolveInput input, List<string> log)
    {
        var mesh = input.Mesh;
        var loads = new double[mesh.NodeCount];
        foreach (var flux in input.BoundaryConditions.OfType<HeatFlux>())
        {
            DistributeOverFaces(mesh, flux.FaceIds, flux.TotalPower, loads, flux.Name);
            log.Add($"Heat flow '{flux.Name}': {flux.TotalPower:g4} W injected.");
        }
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
        {
            // RHS of the Robin term: h·T_amb·∫Nᵢ dA = h·T_amb·A/3 per triangle node.
            foreach (var t in mesh.GetFaceTriangles(convection.FaceIds))
            {
                double share = convection.Coefficient * convection.AmbientTemperature
                               * TriangleArea(mesh, t) / 3.0;
                loads[t.A] += share;
                loads[t.B] += share;
                loads[t.C] += share;
            }
            log.Add($"Convection '{convection.Name}': h = {convection.Coefficient:g4} W/(m²·K), " +
                    $"ambient {convection.AmbientTemperature:g4} K.");
        }
        if (input.ElementHeatSource is not null)
        {
            // Consistent nodal loads of a constant element source: ∫N q dV = q·V/4 per node.
            double totalSource = 0;
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                double qv = input.ElementHeatSource[e] * mesh.ElementVolume(e);
                totalSource += qv;
                var el = mesh.Elements[e];
                loads[el.N0] += qv / 4;
                loads[el.N1] += qv / 4;
                loads[el.N2] += qv / 4;
                loads[el.N3] += qv / 4;
            }
            log.Add($"Volumetric heat source: {totalSource:g4} W total.");
        }
        return loads;
    }

    /// <summary>
    /// Distributes a total surface quantity (current [A], heat flow [W]) area-weighted
    /// over the nodes of the given faces so the resultant is exact: each triangle carries
    /// its area share, split evenly over its three nodes.
    /// </summary>
    public static void DistributeOverFaces(FeMesh mesh, IReadOnlyList<int> faceIds, double total,
        double[] loads, string bcName)
    {
        var triangles = mesh.GetFaceTriangles(faceIds);
        double totalArea = triangles.Sum(t => TriangleArea(mesh, t));
        if (totalArea <= 0)
            throw new InvalidOperationException($"'{bcName}': selected faces have zero area.");
        foreach (var t in triangles)
        {
            double share = total * (TriangleArea(mesh, t) / totalArea / 3.0);
            loads[t.A] += share;
            loads[t.B] += share;
            loads[t.C] += share;
        }
    }

    /// <summary>Volume-weighted nodal average of per-element scalars, for smooth contours.</summary>
    public static double[] NodalAverage(FeMesh mesh, IReadOnlyList<double> elementValues)
    {
        var nodal = new double[mesh.NodeCount];
        var weight = new double[mesh.NodeCount];
        Accumulate(mesh, (e, w) =>
        {
            foreach (int n in ElementNodes(mesh, e))
            {
                nodal[n] += elementValues[e] * w;
                weight[n] += w;
            }
        });
        for (int i = 0; i < nodal.Length; i++)
            if (weight[i] > 0)
                nodal[i] /= weight[i];
        return nodal;
    }

    /// <summary>Volume-weighted nodal average of per-element vectors.</summary>
    public static Vector3D[] NodalAverage(FeMesh mesh, IReadOnlyList<Vector3D> elementValues)
    {
        var nodal = new Vector3D[mesh.NodeCount];
        var weight = new double[mesh.NodeCount];
        Accumulate(mesh, (e, w) =>
        {
            foreach (int n in ElementNodes(mesh, e))
            {
                nodal[n] += elementValues[e] * w;
                weight[n] += w;
            }
        });
        for (int i = 0; i < nodal.Length; i++)
            if (weight[i] > 0)
                nodal[i] /= weight[i];
        return nodal;
    }

    /// <summary>
    /// Mesh region ids that no anchor can reach — the steady-state well-posedness test for an
    /// assembly. Conduction connects the nodes of an element and a thermal contact connects
    /// the nodes it couples, so a body joined to the rest only through contact is correctly
    /// counted as anchored. A component with neither a prescribed temperature nor a convective
    /// surface has an undetermined temperature level (a singular block), which the caller
    /// turns into a message naming the bodies rather than a CG that wanders.
    /// </summary>
    public static IReadOnlyList<int> FindUnanchoredRegions(FeMesh mesh,
        IReadOnlyList<ContactInterface>? contacts, IReadOnlySet<int> anchoredNodes)
    {
        var parent = new int[mesh.NodeCount];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            Union(parent, el.N0, el.N1);
            Union(parent, el.N0, el.N2);
            Union(parent, el.N0, el.N3);
        }
        if (contacts is not null)
            foreach (var contact in contacts)
                foreach (var s in contact.Stamps)
                {
                    Union(parent, s.Node0, s.Node1);
                    Union(parent, s.Node0, s.Node2);
                    Union(parent, s.Node0, s.Node3);
                }

        var anchoredRoots = new HashSet<int>();
        foreach (int node in anchoredNodes)
            if (node >= 0 && node < parent.Length)
                anchoredRoots.Add(Find(parent, node));

        var floating = new SortedSet<int>();
        for (int e = 0; e < mesh.ElementCount; e++)
            if (!anchoredRoots.Contains(Find(parent, mesh.Elements[e].N0)))
                floating.Add(mesh.RegionOf(e));
        return floating.ToList();
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];   // path halving
            i = parent[i];
        }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra == rb) return;
        // Attach the higher root to the lower one: the result never depends on visit order.
        if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
    }

    public static double TriangleArea(FeMesh mesh, BoundaryTriangle t) =>
        0.5 * Vector3D.Cross(mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length;

    private static void Accumulate(FeMesh mesh, Action<int, double> visit)
    {
        for (int e = 0; e < mesh.ElementCount; e++)
            visit(e, mesh.ElementVolume(e));
    }

    private static int[] ElementNodes(FeMesh mesh, int element) => mesh.GetElementNodes(element);
}
