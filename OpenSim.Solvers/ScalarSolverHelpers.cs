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

    public static double TriangleArea(FeMesh mesh, BoundaryTriangle t) =>
        0.5 * Vector3D.Cross(mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length;

    private static void Accumulate(FeMesh mesh, Action<int, double> visit)
    {
        for (int e = 0; e < mesh.ElementCount; e++)
            visit(e, mesh.ElementVolume(e));
    }

    private static int[] ElementNodes(FeMesh mesh, int element) => mesh.GetElementNodes(element);
}
