using OpenSim.Core.Interfaces;

namespace OpenSim.Core.Model;

/// <summary>
/// A lumped thermal network solved together with the mesh: nodes that hold heat and
/// dissipate power, conductances between them, and attachments that exchange heat with
/// faces of the mesh. The nodes become unknowns of the thermal solve beside the mesh's own,
/// so a part's junction, its case, a heatsink or an enclosure's air are solved with the
/// board rather than reduced onto it. A DELPHI-style compact model is such a network; so is
/// the datasheet's two-resistor model.
/// <para>
/// An attachment of conductance G [W/K] to a set of faces of area A acts as a film h = G/A
/// between the faces and the node: the heat the node gives the faces is G·(T_node − T̄),
/// T̄ the faces' area-mean temperature, spread as the local difference says. An attachment
/// of INFINITE conductance ties the node to the faces: the node is the faces' mean
/// temperature, its power is injected over them and its capacity stored in them. A link of
/// infinite conductance makes two nodes one.
/// </para>
/// </summary>
public sealed record ThermalNetwork
{
    public required string Name { get; init; }
    public required IReadOnlyList<ThermalNetworkNode> Nodes { get; init; }
    public IReadOnlyList<ThermalNetworkLink> Links { get; init; } = Array.Empty<ThermalNetworkLink>();
    public IReadOnlyList<ThermalNetworkAttachment> Attachments { get; init; } = Array.Empty<ThermalNetworkAttachment>();

    /// <summary>
    /// The node the <see cref="SolveInput.Environment"/> drives the surface exchange toward
    /// instead of the environment's own ambient — an enclosure's air, warmed by what the board
    /// and the parts give it. Its convection correlations are evaluated against that node's
    /// temperature and the radiation sees it as the surroundings too (the enclosure's walls
    /// taken at its air temperature). The node must reach a fixed node through links, or
    /// nothing fixes its level. Null: the environment's ambient, as it always was.
    /// </summary>
    public string? EnvironmentAmbientNode { get; init; }

    /// <summary>Index of a node by name; −1 when the network has none of that name.</summary>
    public int IndexOf(string node)
    {
        for (int i = 0; i < Nodes.Count; i++)
            if (string.Equals(Nodes[i].Name, node, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Every face id some attachment names, over the given networks.</summary>
    public static IEnumerable<int> AttachedFaceIds(IEnumerable<ThermalNetwork>? networks) =>
        networks is null
            ? Enumerable.Empty<int>()
            : networks.SelectMany(n => n.Attachments).SelectMany(a => a.FaceIds).Distinct();

    /// <summary>Throws with an actionable message when the network cannot be solved on the mesh.</summary>
    public void Validate(FeMesh mesh)
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("A thermal network needs a name.");
        if (Nodes.Count == 0)
            throw new InvalidOperationException($"Thermal network '{Name}' has no nodes.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Name))
                throw new InvalidOperationException($"Thermal network '{Name}': every node needs a name.");
            if (!names.Add(node.Name))
                throw new InvalidOperationException($"Thermal network '{Name}': two nodes are named '{node.Name}'.");
            if (double.IsNaN(node.PowerWatts) || double.IsInfinity(node.PowerWatts))
                throw new InvalidOperationException($"Thermal network '{Name}', node '{node.Name}': the power is not a number.");
            if (!(node.CapacityJoulesPerKelvin >= 0) || double.IsInfinity(node.CapacityJoulesPerKelvin))
                throw new InvalidOperationException($"Thermal network '{Name}', node '{node.Name}': the heat capacity cannot be negative.");
            if (node.FixedKelvin is { } fixedKelvin)
            {
                if (!(fixedKelvin > 0))
                    throw new InvalidOperationException($"Thermal network '{Name}', node '{node.Name}': a fixed temperature must be positive (absolute kelvin).");
                if (node.PowerWatts != 0)
                    throw new InvalidOperationException($"Thermal network '{Name}', node '{node.Name}': a node held at a fixed temperature cannot also dissipate power.");
            }
            node.PowerProfile?.Validate();
        }
        foreach (var link in Links)
        {
            if (IndexOf(link.From) < 0 || IndexOf(link.To) < 0)
                throw new InvalidOperationException($"Thermal network '{Name}': link '{link.From}'–'{link.To}' names a node the network does not have.");
            if (string.Equals(link.From, link.To, StringComparison.Ordinal))
                throw new InvalidOperationException($"Thermal network '{Name}': link '{link.From}'–'{link.To}' joins a node to itself.");
            if (!(link.Conductance > 0))
                throw new InvalidOperationException($"Thermal network '{Name}': link '{link.From}'–'{link.To}' has no conductance; leave it out.");
        }
        foreach (var attachment in Attachments)
        {
            if (IndexOf(attachment.Node) < 0)
                throw new InvalidOperationException($"Thermal network '{Name}': an attachment names node '{attachment.Node}', which the network does not have.");
            if (!(attachment.Conductance > 0))
                throw new InvalidOperationException($"Thermal network '{Name}': the attachment of '{attachment.Node}' has no conductance.");
            if (attachment.FaceIds.Count == 0)
                throw new InvalidOperationException($"Thermal network '{Name}': the attachment of '{attachment.Node}' names no faces.");
            if (mesh.GetFaceTriangles(attachment.FaceIds).Count == 0)
                throw new InvalidOperationException(
                    $"Thermal network '{Name}': the attachment of '{attachment.Node}' names faces ({string.Join(", ", attachment.FaceIds)}) the mesh does not have.");
        }
        if (EnvironmentAmbientNode is { } ambientNode && IndexOf(ambientNode) < 0)
            throw new InvalidOperationException($"Thermal network '{Name}': the environment's ambient node '{ambientNode}' is not one of its nodes.");
    }
}

/// <summary>One node of a <see cref="ThermalNetwork"/>.</summary>
public sealed record ThermalNetworkNode
{
    public required string Name { get; init; }

    /// <summary>Power dissipated at the node [W].</summary>
    public double PowerWatts { get; init; }

    /// <summary>How the node's power varies in a transient. Null: the solve's own profile
    /// (<see cref="TransientThermalSettings.PowerProfile"/>), constant without one.</summary>
    public PowerProfile? PowerProfile { get; init; }

    /// <summary>Heat capacity [J/K]; 0 for a node without thermal mass.</summary>
    public double CapacityJoulesPerKelvin { get; init; }

    /// <summary>Held at this temperature [K] (an ambient, a cold plate); null for a free node.</summary>
    public double? FixedKelvin { get; init; }
}

/// <summary>A conductance [W/K] between two nodes; <see cref="double.PositiveInfinity"/>
/// makes them one node.</summary>
public sealed record ThermalNetworkLink(string From, string To, double Conductance);

/// <summary>A node's exchange with mesh faces: conductance [W/K] spread over the faces'
/// area as a film; <see cref="double.PositiveInfinity"/> ties the node to the faces.</summary>
public sealed record ThermalNetworkAttachment(string Node, IReadOnlyList<int> FaceIds, double Conductance);
