using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// The lumped thermal networks of a solve (<see cref="SolveInput.ThermalNetworks"/>) as extra
/// unknowns after the mesh's nodes, and the terms they add to the thermal matrices and loads
/// — shared by the steady, transient and impedance solvers so the three agree on what a
/// network means.
/// <para>
/// Nodes joined by an infinite link form one group; a group tied to faces (an infinite
/// attachment) is not an unknown — it is the faces' area mean, its power lands on them as
/// a uniform flux and its capacity is stored in them, and its finite links become
/// attachments of the nodes at their other ends. Every other group is one unknown. An
/// attachment of conductance G over faces of area A is the film h = G/A between the faces
/// and the unknown: the same stamps a convection condition makes, with the ambient a column
/// of the matrix instead of a number on the right-hand side. The matrices stay symmetric
/// and positive (semi-)definite, so the shared Jacobi-CG applies unchanged.
/// </para>
/// </summary>
internal sealed class ThermalNetworkTerms
{
    /// <summary>One attachment's faces: their triangles, areas and the area-mean weights of their nodes.</summary>
    private sealed class FaceSet
    {
        public required IReadOnlyList<BoundaryTriangle> Triangles { get; init; }
        public required double[] Areas { get; init; }
        public required double Area { get; init; }
        /// <summary>Σ weight = 1: the area mean of a nodal field, and the spread of a uniform flux.</summary>
        public required (int Node, double Weight)[] NodeWeights { get; init; }

        public double Mean(IReadOnlyList<double> values)
        {
            double sum = 0;
            foreach (var (node, weight) in NodeWeights) sum += weight * values[node];
            return sum;
        }
    }

    /// <summary>Nodes made one by infinite links.</summary>
    private sealed class Group
    {
        public List<(int Network, int Node)> Members { get; } = new();
        public int Dof { get; set; } = -1;
        public double? Fixed { get; set; }
        public double Capacity { get; set; }
        public FaceSet? Tied { get; set; }
        public string TiedBy { get; set; } = "";
        public bool ReachesFixed { get; set; }
        public bool TouchesMesh { get; set; }
        public string Describe(IReadOnlyList<ThermalNetwork> networks) =>
            string.Join(" = ", Members.Select(m => $"'{networks[m.Network].Nodes[m.Node].Name}'"));
    }

    /// <summary>A finite exchange between an unknown and a set of faces.</summary>
    private readonly record struct Coupling(int Dof, FaceSet Faces, double Conductance);

    /// <summary>A finite link between two unknowns.</summary>
    private readonly record struct Spring(int DofA, int DofB, double Conductance);

    private readonly FeMesh _mesh;
    private readonly IReadOnlyList<ThermalNetwork> _networks;
    private readonly int[][] _groupOf;            // [network][node] → group
    private readonly List<Group> _groups;
    private readonly List<Coupling> _couplings;
    private readonly List<Spring> _springs;
    /// <summary>Per tied group: the links from its members to unknowns (the heat they bring to its faces).</summary>
    private readonly Dictionary<int, List<(int Dof, double Conductance)>> _tiedLinks;
    private readonly FaceSet[][] _attachmentFaces;   // [network][attachment]

    private ThermalNetworkTerms(FeMesh mesh, IReadOnlyList<ThermalNetwork> networks, int[][] groupOf,
        List<Group> groups, List<Coupling> couplings, List<Spring> springs,
        Dictionary<int, List<(int, double)>> tiedLinks, FaceSet[][] attachmentFaces, int extraDofs, int? airDof)
    {
        _mesh = mesh;
        _networks = networks;
        _groupOf = groupOf;
        _groups = groups;
        _couplings = couplings;
        _springs = springs;
        _tiedLinks = tiedLinks;
        _attachmentFaces = attachmentFaces;
        MeshDofs = mesh.NodeCount;
        ExtraDofs = extraDofs;
        AirDof = airDof;
    }

    public int MeshDofs { get; }
    public int ExtraDofs { get; }
    public int TotalDofs => MeshDofs + ExtraDofs;

    /// <summary>The unknown the environment's surface exchange drives toward
    /// (<see cref="ThermalNetwork.EnvironmentAmbientNode"/>); null for the plain ambient.</summary>
    public int? AirDof { get; }

    /// <summary>Whether any node's power has a profile of its own.</summary>
    public bool HasProfiles => _networks.Any(n => n.Nodes.Any(node => node.PowerProfile is not null));

    /// <summary>Whether some node is held at a fixed temperature.</summary>
    public bool HasFixed => _groups.Any(g => g.Fixed is not null);

    /// <summary>Whether the networks fix the level of some part of the mesh: faces attached
    /// to a node that reaches a fixed temperature.</summary>
    public bool AnchorsMesh => AnchoredMeshNodes().Count > 0;

    /// <summary>
    /// Checks the networks of a solve beyond each one's own validation: unique names, one
    /// environment ambient node at most and only with an environment, and the structural
    /// refusals (a node tied to two face sets, a fixed node tied to faces, two tied nodes
    /// linked, an unknown nothing fixes). Throws with the node named; returns the terms
    /// (null without networks) for the caller's own checks.
    /// </summary>
    public static ThermalNetworkTerms? Validate(SolveInput input)
    {
        if (input.ThermalNetworks is not { Count: > 0 } networks) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        string? ambientNode = null;
        foreach (var network in networks)
        {
            network.Validate(input.Mesh);
            if (!names.Add(network.Name))
                throw new InvalidOperationException($"Two thermal networks are named '{network.Name}'.");
            if (network.EnvironmentAmbientNode is null) continue;
            if (ambientNode is not null)
                throw new InvalidOperationException(
                    "Only one node can be the environment's ambient; two networks name one.");
            ambientNode = network.EnvironmentAmbientNode;
            if (input.Environment is null)
                throw new InvalidOperationException(
                    $"Thermal network '{network.Name}' names '{ambientNode}' as the environment's ambient node, but the solve has no environment.");
        }
        return Build(input, new List<string>());
    }

    /// <summary>The networks of a solve as terms, or null when it has none.</summary>
    public static ThermalNetworkTerms? Build(SolveInput input, List<string> log)
    {
        if (input.ThermalNetworks is not { Count: > 0 } networks) return null;
        var mesh = input.Mesh;

        // Global node numbering, and the union of nodes an infinite link makes one.
        var offset = new int[networks.Count + 1];
        for (int k = 0; k < networks.Count; k++) offset[k + 1] = offset[k] + networks[k].Nodes.Count;
        var parent = Enumerable.Range(0, offset[^1]).ToArray();
        for (int k = 0; k < networks.Count; k++)
            foreach (var link in networks[k].Links)
                if (double.IsPositiveInfinity(link.Conductance))
                    Union(parent, offset[k] + networks[k].IndexOf(link.From), offset[k] + networks[k].IndexOf(link.To));

        var groups = new List<Group>();
        var groupOfRoot = new Dictionary<int, int>();
        var groupOf = new int[networks.Count][];
        for (int k = 0; k < networks.Count; k++)
        {
            groupOf[k] = new int[networks[k].Nodes.Count];
            for (int i = 0; i < networks[k].Nodes.Count; i++)
            {
                int root = Find(parent, offset[k] + i);
                if (!groupOfRoot.TryGetValue(root, out int g))
                {
                    groupOfRoot[root] = g = groups.Count;
                    groups.Add(new Group());
                }
                groupOf[k][i] = g;
                var group = groups[g];
                var node = networks[k].Nodes[i];
                group.Members.Add((k, i));
                group.Capacity += node.CapacityJoulesPerKelvin;
                if (node.FixedKelvin is { } fixedKelvin)
                {
                    if (group.Fixed is { } other && Math.Abs(other - fixedKelvin) > 1e-9)
                        throw new InvalidOperationException(
                            $"Thermal network '{networks[k].Name}': {group.Describe(networks)} are one node (infinite link) but are held at different temperatures.");
                    group.Fixed = fixedKelvin;
                }
            }
        }

        // Ties first, so a finite attachment or link can tell whether its other end is a face.
        var finiteAttachments = new List<(int Group, FaceSet Faces, double Conductance, string Node, string Network)>();
        var attachmentFaces = new FaceSet[networks.Count][];
        for (int k = 0; k < networks.Count; k++)
        {
            attachmentFaces[k] = new FaceSet[networks[k].Attachments.Count];
            for (int a = 0; a < networks[k].Attachments.Count; a++)
            {
                var attachment = networks[k].Attachments[a];
                var group = groups[groupOf[k][networks[k].IndexOf(attachment.Node)]];
                var faces = attachmentFaces[k][a] = Faces(mesh, attachment.FaceIds, networks[k].Name, attachment.Node);
                if (!double.IsPositiveInfinity(attachment.Conductance))
                {
                    finiteAttachments.Add((groupOf[k][networks[k].IndexOf(attachment.Node)], faces, attachment.Conductance, attachment.Node, networks[k].Name));
                    continue;
                }
                if (group.Fixed is not null)
                    throw new InvalidOperationException(
                        $"Thermal network '{networks[k].Name}': '{attachment.Node}' is held at a fixed temperature and tied to faces; use a fixed temperature condition on those faces instead.");
                if (group.Tied is not null)
                    throw new InvalidOperationException(
                        $"Thermal network '{networks[k].Name}': {group.Describe(networks)} is tied to two sets of faces.");
                group.Tied = faces;
                group.TiedBy = attachment.Node;
            }
        }
        foreach (var (g, _, _, node, network) in finiteAttachments)
            if (groups[g].Tied is not null)
                throw new InvalidOperationException(
                    $"Thermal network '{network}': '{node}' is tied to faces and attached to others as well; a tied node is its faces.");

        int extra = 0;
        foreach (var group in groups)
            if (group.Tied is null) group.Dof = mesh.NodeCount + extra++;

        var couplings = new List<Coupling>();
        var springs = new List<Spring>();
        var tiedLinks = new Dictionary<int, List<(int, double)>>();
        foreach (var (g, faces, conductance, _, _) in finiteAttachments)
        {
            couplings.Add(new Coupling(groups[g].Dof, faces, conductance));
            groups[g].TouchesMesh = true;
        }
        for (int k = 0; k < networks.Count; k++)
            foreach (var link in networks[k].Links)
            {
                if (double.IsPositiveInfinity(link.Conductance)) continue;
                int a = groupOf[k][networks[k].IndexOf(link.From)], b = groupOf[k][networks[k].IndexOf(link.To)];
                if (a == b)
                    throw new InvalidOperationException(
                        $"Thermal network '{networks[k].Name}': link '{link.From}'–'{link.To}' joins two nodes an infinite link already made one.");
                var ga = groups[a];
                var gb = groups[b];
                if (ga.Tied is not null && gb.Tied is not null)
                    throw new InvalidOperationException(
                        $"Thermal network '{networks[k].Name}': link '{link.From}'–'{link.To}' joins two nodes tied to faces — a face-to-face exchange the mesh itself must carry.");
                if (ga.Tied is not null || gb.Tied is not null)
                {
                    var (tied, free, tiedIndex) = ga.Tied is not null ? (ga, gb, a) : (gb, ga, b);
                    couplings.Add(new Coupling(free.Dof, tied.Tied!, link.Conductance));
                    free.TouchesMesh = true;
                    if (!tiedLinks.TryGetValue(tiedIndex, out var list)) tiedLinks[tiedIndex] = list = new List<(int, double)>();
                    list.Add((free.Dof, link.Conductance));
                    continue;
                }
                springs.Add(new Spring(ga.Dof, gb.Dof, link.Conductance));
            }

        // Which unknowns reach a fixed temperature through links, and which reach the mesh.
        var reach = new Dictionary<int, List<int>>();
        foreach (var spring in springs)
        {
            (reach.TryGetValue(spring.DofA, out var la) ? la : reach[spring.DofA] = new List<int>()).Add(spring.DofB);
            (reach.TryGetValue(spring.DofB, out var lb) ? lb : reach[spring.DofB] = new List<int>()).Add(spring.DofA);
        }
        var byDof = groups.Where(g => g.Dof >= 0).ToDictionary(g => g.Dof);
        void Spread(Func<Group, bool> has, Action<Group> mark)
        {
            var queue = new Queue<int>(groups.Where(g => g.Dof >= 0 && has(g)).Select(g => g.Dof));
            while (queue.Count > 0)
            {
                int dof = queue.Dequeue();
                if (!reach.TryGetValue(dof, out var next)) continue;
                foreach (int other in next)
                {
                    var group = byDof[other];
                    if (has(group)) continue;
                    mark(group);
                    queue.Enqueue(other);
                }
            }
        }
        foreach (var group in groups) group.ReachesFixed = group.Fixed is not null;
        Spread(g => g.ReachesFixed, g => g.ReachesFixed = true);
        Spread(g => g.TouchesMesh, g => g.TouchesMesh = true);
        int? airDof = null;
        for (int k = 0; k < networks.Count; k++)
        {
            if (networks[k].EnvironmentAmbientNode is not { } name) continue;
            var group = groups[groupOf[k][networks[k].IndexOf(name)]];
            if (group.Tied is not null)
                throw new InvalidOperationException(
                    $"Thermal network '{networks[k].Name}': the environment's ambient node '{name}' is tied to faces; the surroundings cannot be a face of the mesh.");
            if (!group.ReachesFixed)
                throw new InvalidOperationException(
                    $"Thermal network '{networks[k].Name}': the environment's ambient node '{name}' reaches no fixed temperature through the network's links; " +
                    "give the enclosure's air its conductance to the outside (a flow's ṁ·c_p or the walls' conductance) to a fixed node.");
            airDof = group.Dof;
        }
        foreach (var group in groups)
            if (group.Dof >= 0 && !group.ReachesFixed && !group.TouchesMesh)
            {
                var (k, _) = group.Members[0];
                throw new InvalidOperationException(
                    $"Thermal network '{networks[k].Name}': {group.Describe(networks)} reaches neither a fixed temperature nor the mesh, so nothing fixes its temperature.");
            }

        for (int k = 0; k < networks.Count; k++)
        {
            var network = networks[k];
            int fixedCount = network.Nodes.Count(n => n.FixedKelvin is not null);
            int tiedCount = network.Nodes.Count((n) => groups[groupOf[k][network.IndexOf(n.Name)]].Tied is not null);
            int unknowns = network.Nodes.Select(n => groupOf[k][network.IndexOf(n.Name)]).Distinct().Count(g => groups[g].Tied is null && groups[g].Fixed is null);
            log.Add($"Thermal network '{network.Name}': {network.Nodes.Count} node(s)" +
                    (fixedCount > 0 ? $", {fixedCount} fixed" : "") + (tiedCount > 0 ? $", {tiedCount} tied to faces" : "") +
                    $"; {network.Links.Count} link(s), {network.Attachments.Count} attachment(s); {unknowns} unknown(s) added" +
                    (network.EnvironmentAmbientNode is { } ambient ? $"; the environment drives toward '{ambient}'." : "."));
        }

        return new ThermalNetworkTerms(mesh, networks, groupOf, groups, couplings, springs, tiedLinks, attachmentFaces, extra, airDof);
    }

    private static FaceSet Faces(FeMesh mesh, IReadOnlyList<int> faceIds, string network, string node)
    {
        var triangles = mesh.GetFaceTriangles(faceIds);
        var areas = new double[triangles.Count];
        double area = 0;
        var weights = new Dictionary<int, double>();
        for (int t = 0; t < triangles.Count; t++)
        {
            areas[t] = ScalarSolverHelpers.TriangleArea(mesh, triangles[t]);
            area += areas[t];
            foreach (int n in new[] { triangles[t].A, triangles[t].B, triangles[t].C })
                weights[n] = weights.GetValueOrDefault(n) + areas[t] / 3;
        }
        if (!(area > 0))
            throw new InvalidOperationException($"Thermal network '{network}': the faces '{node}' is attached to have no area.");
        return new FaceSet
        {
            Triangles = triangles, Areas = areas, Area = area,
            NodeWeights = weights.Select(w => (w.Key, w.Value / area)).ToArray()
        };
    }

    // ---------------- Matrices ----------------

    /// <summary>The mesh matrix with the networks' unknowns and conductances added.</summary>
    public CsrMatrix Augment(CsrMatrix meshMatrix)
    {
        var builder = Copy(meshMatrix);
        foreach (var spring in _springs)
        {
            builder.Add(spring.DofA, spring.DofA, spring.Conductance);
            builder.Add(spring.DofB, spring.DofB, spring.Conductance);
            builder.Add(spring.DofA, spring.DofB, -spring.Conductance);
            builder.Add(spring.DofB, spring.DofA, -spring.Conductance);
        }
        foreach (var coupling in _couplings)
            StampCoupling(builder, coupling.Dof, coupling.Faces.Triangles, coupling.Faces.Areas, coupling.Conductance / coupling.Faces.Area);
        return builder.Build();
    }

    /// <summary>The film h over the triangles between their nodes and an unknown: the Robin
    /// stamps on the mesh nodes, −h·A/3 between each node and the unknown, h·A on the unknown.</summary>
    private void StampCoupling(SparseMatrixBuilder builder, int dof, IReadOnlyList<BoundaryTriangle> triangles,
        double[] areas, double h)
    {
        for (int t = 0; t < triangles.Count; t++)
        {
            var tri = triangles[t];
            ScalarDiffusionAssembler.AddRobinSurface(builder, _mesh, tri, h);
            double third = h * areas[t] / 3;
            foreach (int n in new[] { tri.A, tri.B, tri.C })
            {
                builder.Add(n, dof, -third);
                builder.Add(dof, n, -third);
            }
            builder.Add(dof, dof, h * areas[t]);
        }
    }

    /// <summary>The mesh capacity matrix with the networks' capacities: each unknown's on
    /// its diagonal, a tied group's spread over its faces' nodes by area.</summary>
    public CsrMatrix AugmentMass(CsrMatrix meshMass)
    {
        var builder = Copy(meshMass);
        foreach (var group in _groups)
        {
            if (group.Capacity <= 0) continue;
            if (group.Tied is { } faces)
                foreach (var (node, weight) in faces.NodeWeights) builder.Add(node, node, group.Capacity * weight);
            else
                builder.Add(group.Dof, group.Dof, group.Capacity);
        }
        return builder.Build();
    }

    /// <summary>The same for a lumped (diagonal) capacity.</summary>
    public double[] AugmentDiagonal(IReadOnlyList<double> meshDiagonal)
    {
        var diagonal = new double[TotalDofs];
        for (int i = 0; i < MeshDofs; i++) diagonal[i] = meshDiagonal[i];
        foreach (var group in _groups)
        {
            if (group.Tied is { } faces)
                foreach (var (node, weight) in faces.NodeWeights) diagonal[node] += group.Capacity * weight;
            else diagonal[group.Dof] += group.Capacity;
        }
        return diagonal;
    }

    private SparseMatrixBuilder Copy(CsrMatrix meshMatrix)
    {
        var builder = new SparseMatrixBuilder(TotalDofs, TotalDofs);
        for (int row = 0; row < meshMatrix.RowCount; row++)
            for (int k = meshMatrix.RowPointers[row]; k < meshMatrix.RowPointers[row + 1]; k++)
                builder.Add(row, meshMatrix.ColumnIndices[k], meshMatrix.Values[k]);
        // Every unknown has a diagonal entry even before its conductances are added, so
        // Jacobi preconditioning and the step matrices never meet an empty one.
        for (int dof = MeshDofs; dof < TotalDofs; dof++) builder.Add(dof, dof, 0.0);
        return builder;
    }

    /// <summary>
    /// A surface film stamped onto the augmented matrix: the Robin terms on the mesh nodes
    /// as always and, when the environment drives toward a network node, the exchange
    /// toward that unknown instead of toward a fixed ambient.
    /// </summary>
    public CsrMatrix WithFilm(CsrMatrix augmented, SurfaceFilmModel film)
    {
        var builder = Copy(augmented);
        for (int t = 0; t < _mesh.BoundaryTriangles.Count; t++)
        {
            double h = film.TriangleFilmCoefficient[t];
            if (double.IsNaN(h) || h == 0) continue;
            var tri = _mesh.BoundaryTriangles[t];
            if (AirDof is not { } air)
            {
                ScalarDiffusionAssembler.AddRobinSurface(builder, _mesh, tri, h);
                continue;
            }
            double area = ScalarDiffusionAssembler.SurfaceArea(_mesh, tri);
            StampCoupling(builder, air, new[] { tri }, new[] { area }, h);
        }
        return builder.Build();
    }

    /// <summary>
    /// The film's load terms. Toward a fixed ambient: h·T_ref·A/3 per triangle node, as
    /// <see cref="Environment.EnvironmentThermalTerms.WithFilmLoads"/>. Toward the air
    /// unknown: only the part of the reference that is not the air itself (radiation's
    /// Newton offset), lagged at the air's current value — h·(T_ref − T_air)·A/3 on the
    /// nodes and the opposite on the air, so the heat the surface loses is the heat the air
    /// gains and, at convergence, both equal εσ(T⁴ − T_air⁴) plus the convection.
    /// </summary>
    public double[] WithFilmLoads(IReadOnlyList<double> loads, SurfaceFilmModel film, double airTemperature)
    {
        var result = loads.ToArray();
        for (int t = 0; t < _mesh.BoundaryTriangles.Count; t++)
        {
            double h = film.TriangleFilmCoefficient[t];
            if (double.IsNaN(h) || h == 0) continue;
            var tri = _mesh.BoundaryTriangles[t];
            double area = ScalarDiffusionAssembler.SurfaceArea(_mesh, tri);
            double reference = film.ReferenceTemperatureOf(t);
            if (AirDof is not { } air)
            {
                double share = h * reference * area / 3;
                result[tri.A] += share; result[tri.B] += share; result[tri.C] += share;
                continue;
            }
            double offset = h * (reference - airTemperature) * area / 3;
            result[tri.A] += offset; result[tri.B] += offset; result[tri.C] += offset;
            result[air] -= 3 * offset;
        }
        return result;
    }

    // ---------------- Loads, prescribed values, anchors ----------------

    /// <summary>The mesh loads extended to every unknown, with each node's power (scaled by
    /// <paramref name="powerScale"/>, or left out when null) at its unknown or spread over
    /// its faces.</summary>
    public double[] AugmentLoads(IReadOnlyList<double> meshLoads, Func<ThermalNetworkNode, double>? powerScale)
    {
        var loads = new double[TotalDofs];
        for (int i = 0; i < MeshDofs; i++) loads[i] = meshLoads[i];
        if (powerScale is null) return loads;
        AddPowers(loads, powerScale);
        return loads;
    }

    /// <summary>Adds every node's scaled power to a full-length load vector.</summary>
    public void AddPowers(double[] loads, Func<ThermalNetworkNode, double> powerScale)
    {
        for (int k = 0; k < _networks.Count; k++)
            for (int i = 0; i < _networks[k].Nodes.Count; i++)
            {
                var node = _networks[k].Nodes[i];
                if (node.PowerWatts == 0) continue;
                double power = node.PowerWatts * powerScale(node);
                var group = _groups[_groupOf[k][i]];
                if (group.Tied is { } faces)
                    foreach (var (meshNode, weight) in faces.NodeWeights) loads[meshNode] += power * weight;
                else loads[group.Dof] += power;
            }
    }

    /// <summary>Σ of the nodes' scaled powers [W].</summary>
    public double TotalPower(Func<ThermalNetworkNode, double> powerScale) =>
        _networks.Sum(n => n.Nodes.Sum(node => node.PowerWatts * powerScale(node)));

    /// <summary>The fixed nodes as prescribed unknowns — at their temperatures, or at zero
    /// for a rise formulation.</summary>
    public void AddPrescribed(Dictionary<int, double> prescribed, bool zero = false)
    {
        foreach (var group in _groups)
            if (group.Fixed is { } fixedKelvin && group.Dof >= 0)
                prescribed[group.Dof] = zero ? 0 : fixedKelvin;
    }

    /// <summary>Mesh nodes whose temperature level the networks fix: the nodes of faces
    /// attached to (or tied to by a link from) an unknown that reaches a fixed temperature.
    /// (Reaching the mesh spreads through links too, so a case hanging on an attached
    /// junction is anchored by the board; what this returns is the other direction.)</summary>
    public IReadOnlySet<int> AnchoredMeshNodes()
    {
        var anchored = new HashSet<int>();
        var byDof = _groups.Where(g => g.Dof >= 0).ToDictionary(g => g.Dof);
        foreach (var coupling in _couplings)
        {
            if (!byDof[coupling.Dof].ReachesFixed) continue;
            foreach (var tri in coupling.Faces.Triangles)
            {
                anchored.Add(tri.A); anchored.Add(tri.B); anchored.Add(tri.C);
            }
        }
        return anchored;
    }

    /// <summary>The air unknown's value in a full-length vector.</summary>
    public double AirTemperature(IReadOnlyList<double> full) =>
        AirDof is { } air ? full[air] : throw new InvalidOperationException("No network node is the environment's ambient.");

    /// <summary>The mesh's part of a full-length vector.</summary>
    public double[] MeshPart(IReadOnlyList<double> full)
    {
        var mesh = new double[MeshDofs];
        for (int i = 0; i < MeshDofs; i++) mesh[i] = full[i];
        return mesh;
    }

    /// <summary>A full-length vector from the mesh's values, with the unknowns at the given start.</summary>
    public double[] Extend(IReadOnlyList<double> meshValues, double start)
    {
        var full = new double[TotalDofs];
        for (int i = 0; i < MeshDofs; i++) full[i] = meshValues[i];
        for (int i = MeshDofs; i < TotalDofs; i++) full[i] = start;
        return full;
    }

    /// <summary>The weights that read a node's temperature from a full-length vector: its
    /// unknown, or its faces' area mean when tied.</summary>
    public IReadOnlyList<(int Dof, double Weight)> ProbeWeights(string network, string node)
    {
        int k = IndexOfNetwork(network);
        int i = _networks[k].IndexOf(node);
        if (i < 0) throw new InvalidOperationException($"Thermal network '{network}' has no node '{node}'.");
        var group = _groups[_groupOf[k][i]];
        return group.Tied is { } faces ? faces.NodeWeights : new[] { (group.Dof, 1.0) };
    }

    /// <summary>Nodes that dissipate, as (network, node) names.</summary>
    public IEnumerable<(string Network, string Node)> PoweredNodes() =>
        _networks.SelectMany(n => n.Nodes.Where(node => node.PowerWatts != 0).Select(node => (n.Name, node.Name)));

    private int IndexOfNetwork(string name)
    {
        for (int k = 0; k < _networks.Count; k++)
            if (string.Equals(_networks[k].Name, name, StringComparison.Ordinal)) return k;
        throw new InvalidOperationException($"The solve has no thermal network '{name}'.");
    }

    // ---------------- Results ----------------

    /// <summary>Every network's node temperatures and heat flows at a full-length solution;
    /// <paramref name="powerScale"/> is the scale the nodes' powers were applied with (a tied
    /// node's injected power is part of what its faces received).</summary>
    public IReadOnlyList<ThermalNetworkState> States(IReadOnlyList<double> full, Func<ThermalNetworkNode, double> powerScale)
    {
        var states = new List<ThermalNetworkState>(_networks.Count);
        for (int k = 0; k < _networks.Count; k++)
        {
            var network = _networks[k];
            var kelvin = new double[network.Nodes.Count];
            for (int i = 0; i < kelvin.Length; i++) kelvin[i] = TemperatureOf(_groups[_groupOf[k][i]], full);

            var linkWatts = new double[network.Links.Count];
            for (int l = 0; l < linkWatts.Length; l++)
            {
                var link = network.Links[l];
                linkWatts[l] = double.IsPositiveInfinity(link.Conductance)
                    ? double.NaN
                    : link.Conductance * (kelvin[network.IndexOf(link.From)] - kelvin[network.IndexOf(link.To)]);
            }

            var attachmentWatts = new double[network.Attachments.Count];
            for (int a = 0; a < attachmentWatts.Length; a++)
            {
                var attachment = network.Attachments[a];
                int i = network.IndexOf(attachment.Node);
                int g = _groupOf[k][i];
                var group = _groups[g];
                if (group.Tied is { } faces)
                {
                    // What the faces received through this node: its group's injected power and
                    // the heat its links bring from the unknowns at their other ends.
                    double watts = 0;
                    foreach (var (nk, ni) in group.Members)
                        watts += _networks[nk].Nodes[ni].PowerWatts * powerScale(_networks[nk].Nodes[ni]);
                    double faceMean = faces.Mean(full);
                    if (_tiedLinks.TryGetValue(g, out var links))
                        foreach (var (dof, conductance) in links) watts += conductance * (full[dof] - faceMean);
                    attachmentWatts[a] = watts;
                    continue;
                }
                var set = _attachmentFaces[k][a];
                double h = attachment.Conductance / set.Area, node = full[group.Dof], heat = 0;
                for (int t = 0; t < set.Triangles.Count; t++)
                {
                    var tri = set.Triangles[t];
                    heat += h * set.Areas[t] * (node - (full[tri.A] + full[tri.B] + full[tri.C]) / 3);
                }
                attachmentWatts[a] = heat;
            }
            states.Add(new ThermalNetworkState(network.Name, network.Nodes.Select(n => n.Name).ToList(), kelvin, linkWatts, attachmentWatts));
        }
        return states;
    }

    private static double TemperatureOf(Group group, IReadOnlyList<double> full) =>
        group.Tied is { } faces ? faces.Mean(full) : full[group.Dof];

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra == rb) return;
        if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
    }
}
