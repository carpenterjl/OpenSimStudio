namespace OpenSim.Core.Results;

/// <summary>
/// A lumped thermal network's part of a solution: every node's temperature, the heat
/// through every link and the heat every attachment put into the mesh, in the network's
/// own order.
/// </summary>
/// <param name="Network">The network's name.</param>
/// <param name="Nodes">Node names, in the network's order.</param>
/// <param name="Kelvin">Node temperatures [K], in the same order.</param>
/// <param name="LinkWatts">Heat through each link, from its first node to its second [W]
/// (NaN for a link that made two nodes one).</param>
/// <param name="AttachmentWatts">Heat each attachment put into the mesh [W], positive
/// toward the mesh.</param>
public sealed record ThermalNetworkState(string Network, IReadOnlyList<string> Nodes, IReadOnlyList<double> Kelvin,
    IReadOnlyList<double> LinkWatts, IReadOnlyList<double> AttachmentWatts)
{
    /// <summary>The temperature of a node by name [K].</summary>
    public double Of(string node)
    {
        for (int i = 0; i < Nodes.Count; i++)
            if (string.Equals(Nodes[i], node, StringComparison.Ordinal)) return Kelvin[i];
        throw new KeyNotFoundException($"Thermal network '{Network}' has no node '{node}'.");
    }
}
