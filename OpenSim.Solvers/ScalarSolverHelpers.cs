using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>Shared load-distribution and field-recovery helpers for the scalar solvers.</summary>
internal static class ScalarSolverHelpers
{
    /// <summary>A per-element conductivity tensor, when given, must cover every element and be
    /// symmetric positive definite (the conduction matrix stays SPD only then).</summary>
    public static void ValidateConductivityTensor(SolveInput input)
    {
        if (input.ElementThermalConductivity is not { } tensors) return;
        if (tensors.Count != input.Mesh.ElementCount)
            throw new InvalidOperationException(
                $"ElementThermalConductivity has {tensors.Count} entries but the mesh has {input.Mesh.ElementCount} elements.");
        for (int e = 0; e < tensors.Count; e++)
            if (!tensors[e].IsPositiveDefinite)
                throw new InvalidOperationException(
                    $"The conductivity tensor of element {e} is not positive definite: heat would flow up the gradient.");
    }

    /// <summary>
    /// The time-constant thermal nodal load vector shared by the steady and transient
    /// heat solvers: surface heat flows, the convection ambient term h·T_amb·A/3 per
    /// triangle node, and consistent q·V/4 nodal loads of the element source.
    /// </summary>
    public static double[] AssembleThermalLoads(SolveInput input, List<string> log)
    {
        var mesh = input.Mesh;
        var loads = new double[mesh.NodeCount];
        var edgeMid = mesh.IsQuadratic ? QuadraticMeshBuilder.BuildEdgeMidMap(mesh) : null;
        foreach (var flux in input.BoundaryConditions.OfType<HeatFlux>())
        {
            DistributeOverFaces(mesh, flux.FaceIds, flux.TotalPower, loads, flux.Name);
            log.Add($"Heat flow '{flux.Name}': {flux.TotalPower:g4} W injected.");
        }
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
        {
            // RHS of the Robin term: h·T_amb·∫Nᵢ dA = h·T_amb·A/3 per triangle node.
            foreach (var (t, h) in ConvectionTerms.Triangles(mesh, convection))
            {
                double total = h * convection.AmbientTemperature * TriangleArea(mesh, t);
                if (edgeMid is not null)
                {
                    AddTriangle6(loads, t, edgeMid, total);
                    continue;
                }
                double share = total / 3.0;
                loads[t.A] += share;
                loads[t.B] += share;
                loads[t.C] += share;
            }
            log.Add($"Convection '{convection.Name}': {ConvectionTerms.Describe(mesh, convection)}, " +
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
                if (edgeMid is not null)
                {
                    // ∫Nᵢ dV of a TET10: −V/20 on a corner, V/5 on a mid-edge node.
                    var all = mesh.GetElementNodes(e);
                    for (int i = 0; i < 10; i++) loads[all[i]] += qv * ScalarElements.Tet10Load[i];
                    continue;
                }
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
    /// The same loads in two parts, for a solve whose sources vary in time: what the heat
    /// flows and the volumetric source put in (to be scaled), and the convection
    /// conditions' ambient terms (which are not sources and stay as they are).
    /// </summary>
    public static (double[] Sources, double[] Ambient) AssembleThermalLoadParts(SolveInput input, List<string> log)
    {
        var sources = AssembleThermalLoads(input with
        {
            BoundaryConditions = input.BoundaryConditions.Where(bc => bc is not Convection).ToList()
        }, log);
        var ambient = AssembleThermalLoads(input with
        {
            BoundaryConditions = input.BoundaryConditions.OfType<Convection>().ToList<BoundaryCondition>(),
            ElementHeatSource = null
        }, log);
        return (sources, ambient);
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
        var edgeMid = mesh.IsQuadratic ? QuadraticMeshBuilder.BuildEdgeMidMap(mesh) : null;
        // Areas once: the total and each triangle's share are the same numbers.
        var areas = new double[triangles.Count];
        double totalArea = 0;
        for (int i = 0; i < triangles.Count; i++)
        {
            areas[i] = TriangleArea(mesh, triangles[i]);
            totalArea += areas[i];
        }
        if (totalArea <= 0)
            throw new InvalidOperationException($"'{bcName}': selected faces have zero area.");
        for (int i = 0; i < triangles.Count; i++)
        {
            var t = triangles[i];
            if (edgeMid is not null)
            {
                AddTriangle6(loads, t, edgeMid, total * (areas[i] / totalArea));
                continue;
            }
            double share = total * (areas[i] / totalArea / 3.0);
            loads[t.A] += share;
            loads[t.B] += share;
            loads[t.C] += share;
        }
    }

    /// <summary>A uniform surface quantity over one T6 triangle: ∫Nᵢ dA is zero on its
    /// corners and a third of the area on each mid-edge node.</summary>
    private static void AddTriangle6(double[] loads, BoundaryTriangle t, Dictionary<(int, int), int> edgeMid,
        double total)
    {
        Span<int> nodes = stackalloc int[6];
        nodes[0] = t.A; nodes[1] = t.B; nodes[2] = t.C;
        nodes[3] = Mid(edgeMid, t.A, t.B); nodes[4] = Mid(edgeMid, t.B, t.C); nodes[5] = Mid(edgeMid, t.C, t.A);
        for (int i = 0; i < 6; i++) loads[nodes[i]] += total * ScalarElements.Tri6Load[i];
    }

    private static int Mid(Dictionary<(int, int), int> edgeMid, int a, int b) => edgeMid[a < b ? (a, b) : (b, a)];

    /// <summary>
    /// Every node a condition's scope holds: its corner nodes and, on a TET10 mesh, the
    /// mid-edge node of every edge it covers. A Dirichlet value on corners only would leave
    /// the mid-edge nodes free and the boundary leaking between them.
    /// </summary>
    public static IReadOnlySet<int> ScopeNodes(FeMesh mesh, BoundaryCondition condition,
        Dictionary<(int, int), int>? edgeMid)
    {
        var corners = mesh.GetScopeNodes(condition);
        if (edgeMid is null) return corners;
        var nodes = new HashSet<int>(corners);
        foreach (var segment in mesh.GetScopeSegments(condition))
            nodes.Add(Mid(edgeMid, segment.A, segment.B));
        return nodes;
    }

    /// <summary>
    /// Volume-weighted nodal average of per-element scalars, for smooth contours.
    /// <para>
    /// With <paramref name="elementGroups"/> (one id per element — the material) the
    /// average never crosses a material interface. Current density, power density and
    /// heat flux are discontinuous there: averaged across, a node on a copper/FR4
    /// interface showed the copper's current density diluted by the volume of the FR4
    /// elements around it — 8 % of the true value with 35 µm copper on 0.4 mm laminate.
    /// A node has one value, so a node shared by several materials shows the average of
    /// the side where the quantity is LARGEST; the element field holds both sides.
    /// </para>
    /// </summary>
    public static double[] NodalAverage(FeMesh mesh, IReadOnlyList<double> elementValues,
        IReadOnlyList<int>? elementGroups = null) =>
        NodalAverageWithSides(mesh, elementValues, elementGroups, null).Nodal;

    /// <summary>Volume-weighted nodal average of per-element vectors; see the scalar
    /// overload for what <paramref name="elementGroups"/> does at a material interface.</summary>
    public static Vector3D[] NodalAverage(FeMesh mesh, IReadOnlyList<Vector3D> elementValues,
        IReadOnlyList<int>? elementGroups = null) =>
        NodalAverageWithSides(mesh, elementValues, elementGroups, null).Nodal;

    /// <summary>
    /// The interface-aware nodal average AND each material's own average at the nodes that
    /// several materials share (FU-27): the nodal field shows the larger side, the sides
    /// carry both. Null sides when the mesh is one material or no node is shared, or when
    /// no <paramref name="groupNames"/> are given.
    /// </summary>
    public static (double[] Nodal, InterfaceSides<double>? Sides) NodalAverageWithSides(FeMesh mesh,
        IReadOnlyList<double> elementValues, IReadOnlyList<int>? elementGroups, IReadOnlyList<string>? groupNames) =>
        Average(mesh, elementValues, elementGroups, groupNames,
            static (v, w) => v * w, static (a, b) => a + b, static (v, w) => v / w, static v => Math.Abs(v));

    public static (Vector3D[] Nodal, InterfaceSides<Vector3D>? Sides) NodalAverageWithSides(FeMesh mesh,
        IReadOnlyList<Vector3D> elementValues, IReadOnlyList<int>? elementGroups, IReadOnlyList<string>? groupNames) =>
        Average(mesh, elementValues, elementGroups, groupNames,
            static (v, w) => v * w, static (a, b) => a + b, static (v, w) => v / w, static v => v.LengthSquared);

    private static (T[] Nodal, InterfaceSides<T>? Sides) Average<T>(FeMesh mesh, IReadOnlyList<T> elementValues,
        IReadOnlyList<int>? elementGroups, IReadOnlyList<string>? groupNames,
        Func<T, double, T> scale, Func<T, T, T> add, Func<T, double, T> divide, Func<T, double> size)
        where T : struct
    {
        var nodal = new T[mesh.NodeCount];
        var weight = new double[mesh.NodeCount];
        var shared = SharedNodes(mesh, elementGroups);
        var sides = shared is null ? null : new Dictionary<(int Node, int Group), (T Sum, double Weight)>();
        Accumulate(mesh, (e, w) =>
        {
            foreach (int n in ElementNodes(mesh, e))
            {
                if (shared is not null && shared[n])
                {
                    var key = (n, elementGroups![e]);
                    var (sum, total) = sides!.GetValueOrDefault(key);
                    sides![key] = (add(sum, scale(elementValues[e], w)), total + w);
                    continue;
                }
                nodal[n] = add(nodal[n], scale(elementValues[e], w));
                weight[n] += w;
            }
        });
        for (int i = 0; i < nodal.Length; i++)
            if (weight[i] > 0)
                nodal[i] = divide(nodal[i], weight[i]);
        if (sides is null) return (nodal, null);

        var best = new Dictionary<int, T>();
        var perNode = new Dictionary<int, List<(int Group, T Value)>>();
        foreach (var ((node, group), (sum, total)) in sides)
        {
            if (!(total > 0)) continue;
            var value = divide(sum, total);
            if (!best.TryGetValue(node, out var current) || size(value) > size(current))
                best[node] = value;
            if (!perNode.TryGetValue(node, out var list)) perNode[node] = list = new List<(int, T)>();
            list.Add((group, value));
        }
        foreach (var (node, value) in best) nodal[node] = value;
        if (groupNames is null) return (nodal, null);

        var ordered = new Dictionary<int, IReadOnlyList<(int Group, T Value)>>(perNode.Count);
        foreach (var (node, list) in perNode)
        {
            list.Sort((a, b) => a.Group.CompareTo(b.Group));
            ordered[node] = list;
        }
        return (nodal, new InterfaceSides<T>(groupNames, ordered));
    }

    /// <summary>Which nodes belong to elements of more than one group; null when there
    /// are no groups or no such node (the plain average then applies everywhere).</summary>
    private static bool[]? SharedNodes(FeMesh mesh, IReadOnlyList<int>? elementGroups)
    {
        if (elementGroups is null) return null;
        var first = new int[mesh.NodeCount];
        Array.Fill(first, int.MinValue);
        var shared = new bool[mesh.NodeCount];
        bool any = false;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            int group = elementGroups[e];
            foreach (int n in ElementNodes(mesh, e))
            {
                if (first[n] == int.MinValue) first[n] = group;
                else if (first[n] != group && !shared[n])
                {
                    shared[n] = true;
                    any = true;
                }
            }
        }
        return any ? shared : null;
    }

    /// <summary>One id per element naming its material, for the interface-aware nodal
    /// averages; null when the whole mesh is one material.</summary>
    public static int[]? MaterialGroups(SolveInput input) => MaterialGroups(input, out _);

    /// <summary>The material groups with the material name of each group index, for the
    /// per-side values of an interface-aware field.</summary>
    public static int[]? MaterialGroups(SolveInput input, out IReadOnlyList<string>? names)
    {
        names = null;
        if (input.RegionMaterials is null || input.Mesh.ElementRegionIds is null) return null;
        var ids = new Dictionary<Material, int>();
        var groups = new int[input.Mesh.ElementCount];
        var ordered = new List<string>();
        for (int e = 0; e < groups.Length; e++)
        {
            var material = input.MaterialOf(e);
            if (!ids.TryGetValue(material, out int id))
            {
                ids[material] = id = ids.Count;
                ordered.Add(material.Name);
            }
            groups[e] = id;
        }
        if (ids.Count <= 1) return null;
        names = ordered;
        return groups;
    }

    /// <summary>The log line that goes with an interface-aware nodal field.</summary>
    public static string InterfaceNote(string quantities) =>
        $"The nodal {quantities} are averaged within each material only. At a node shared by " +
        "two materials the value shown is that of the side where it is larger (the field is " +
        "discontinuous there); the field carries each material's own value at those nodes.";

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
