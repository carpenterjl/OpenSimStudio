namespace OpenSim.Pcb.Thermal;

/// <summary>What a node of a <see cref="CompactThermalModel"/> touches.</summary>
public enum CompactNodeRole
{
    /// <summary>The die: it dissipates the part's power. Exactly one per model.</summary>
    Junction,

    /// <summary>A surface in contact with the board (the package bottom, the leads): it sits
    /// at the footprint's mean temperature.</summary>
    Board,

    /// <summary>A surface in the air (the top, the sides): the case film acts on its area, or
    /// the heatsink's resistance takes it, to the air.</summary>
    Exposed,

    /// <summary>An inner node of the model, touching nothing outside the package.</summary>
    Internal
}

/// <summary>One node of a compact package model.</summary>
public sealed record CompactThermalNode(string Name, CompactNodeRole Role)
{
    /// <summary>Heat capacity [J/K]; 0 for a node without thermal mass.</summary>
    public double CapacityJoulesPerKelvin { get; init; }

    /// <summary>Exposed nodes: the area the case film acts on [m²]. Null on the model's only
    /// exposed node: the part's <see cref="ThermalComponent.CaseArea"/>.</summary>
    public double? ExposedAreaSquareMeters { get; init; }
}

/// <summary>A resistance [K/W] between two nodes; 0 makes them one node.</summary>
public sealed record CompactThermalResistor(string From, string To, double KelvinPerWatt);

/// <summary>
/// A package thermal model with more nodes than the datasheet's two resistors: the network
/// a vendor publishes for the package (DELPHI-style — junction, top, bottom, leads, sides,
/// with resistances between them) or one fitted to measurements. The numbers are the
/// vendor's; nothing here supplies them. Board nodes sit at the footprint's mean
/// temperature, exposed nodes lose heat to the air through the part's case film on their
/// own areas (or through its heatsink resistance), and the junction takes the power.
/// </summary>
public sealed record CompactThermalModel
{
    /// <summary>Where the numbers come from (the vendor's model name), for the report.</summary>
    public string? Source { get; init; }

    public required IReadOnlyList<CompactThermalNode> Nodes { get; init; }
    public required IReadOnlyList<CompactThermalResistor> Resistors { get; init; }

    public CompactThermalNode Junction => Nodes.First(n => n.Role == CompactNodeRole.Junction);

    public void Validate(string refDes)
    {
        if (Nodes.Count == 0)
            throw new InvalidOperationException($"{refDes}: the compact model has no nodes.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Name))
                throw new InvalidOperationException($"{refDes}: every node of the compact model needs a name.");
            if (!names.Add(node.Name))
                throw new InvalidOperationException($"{refDes}: the compact model names two nodes '{node.Name}'.");
            if (!(node.CapacityJoulesPerKelvin >= 0) || double.IsInfinity(node.CapacityJoulesPerKelvin))
                throw new InvalidOperationException($"{refDes}: node '{node.Name}' has a negative heat capacity.");
            if (node.ExposedAreaSquareMeters is { } area && !(area > 0))
                throw new InvalidOperationException($"{refDes}: node '{node.Name}' has no exposed area.");
        }
        if (Nodes.Count(n => n.Role == CompactNodeRole.Junction) != 1)
            throw new InvalidOperationException($"{refDes}: the compact model needs exactly one junction node.");
        if (Nodes.All(n => n.Role != CompactNodeRole.Board))
            throw new InvalidOperationException($"{refDes}: the compact model has no node on the board; a part on a board has one.");
        int exposed = Nodes.Count(n => n.Role == CompactNodeRole.Exposed);
        if (exposed > 1 && Nodes.Any(n => n.Role == CompactNodeRole.Exposed && n.ExposedAreaSquareMeters is null))
            throw new InvalidOperationException($"{refDes}: with several exposed nodes, give each its area.");
        foreach (var resistor in Resistors)
        {
            if (!names.Contains(resistor.From) || !names.Contains(resistor.To))
                throw new InvalidOperationException($"{refDes}: resistor '{resistor.From}'–'{resistor.To}' names a node the model does not have.");
            if (string.Equals(resistor.From, resistor.To, StringComparison.Ordinal))
                throw new InvalidOperationException($"{refDes}: resistor '{resistor.From}'–'{resistor.To}' joins a node to itself.");
            if (!(resistor.KelvinPerWatt >= 0) || double.IsInfinity(resistor.KelvinPerWatt))
                throw new InvalidOperationException($"{refDes}: resistor '{resistor.From}'–'{resistor.To}' needs a finite, non-negative resistance (leave an open path out).");
        }
    }
}
