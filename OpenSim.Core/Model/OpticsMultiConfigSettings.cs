namespace OpenSim.Core.Model;

/// <summary>
/// The multi-configuration table (plan item OP-30; OpticStudio's Multi-Configuration Editor) as
/// project and lens files store it: operand rows, one value per configuration in each, and the
/// configuration the analyses use. OpenSim.Optics' <c>MultiConfiguration</c> applies a
/// configuration to the rest of the optics record. Values are in the units the saved surfaces
/// and system data use (lens units, degrees, micrometers), so a typed value reads back digit
/// for digit. An operand code this build does not know is kept and written back unchanged;
/// applying a configuration that holds one fails with its name.
/// </summary>
public sealed record OpticsMultiConfigSettings
{
    /// <summary>The number of configurations (columns), 1 or more.</summary>
    public int ConfigurationCount { get; init; } = 1;

    /// <summary>The configuration the analyses use, 1-based (OpticStudio's active configuration).</summary>
    public int CurrentConfiguration { get; init; } = 1;

    /// <summary>The operand rows in editor order (row 1 first); null is no rows.</summary>
    public List<OpticsConfigOperandSettings>? Operands { get; init; }
}

/// <summary>
/// One operand row: the operand's four-letter code (OpticStudio's "Type" column; null is MOFF,
/// an unused row), its up to three integer arguments (surface, field, wavelength, parameter …
/// as the operand defines them) and its value in each configuration.
/// </summary>
public sealed record OpticsConfigOperandSettings
{
    /// <summary>The operand code ("THIC", "GLSS", …); null or empty is "MOFF".</summary>
    public string? Type { get; init; }

    public int Number1 { get; init; }
    public int Number2 { get; init; }
    public int Number3 { get; init; }

    /// <summary>The value in configuration 1, 2, …; a configuration past the end of the list
    /// holds zero (or empty text).</summary>
    public List<OpticsConfigValueSettings>? Values { get; init; }
}

/// <summary>
/// One cell of the table: a number or a text (by the operand), and how it is decided — fixed
/// (null), "Variable", "Pickup" (from another cell: <see cref="PickupConfiguration"/>,
/// <see cref="PickupOperand"/>, value × scale + offset) or "ThermalPickup" (from the reference
/// configuration <see cref="PickupConfiguration"/>; stored, evaluated by OP-91).
/// </summary>
public sealed record OpticsConfigValueSettings
{
    /// <summary>The number; ignored when <see cref="IsInfinite"/> or for a text operand.</summary>
    public double Value { get; init; }

    /// <summary>True for +infinity (an object at infinity's THIC); JSON has no infinity.</summary>
    public bool IsInfinite { get; init; }

    /// <summary>The text of a text operand (GLSS, COTN, MCOM, LTTL).</summary>
    public string? Text { get; init; }

    /// <summary>Null (fixed), "Variable", "Pickup" or "ThermalPickup".</summary>
    public string? Solve { get; init; }

    /// <summary>A pickup's source configuration, 1-based; a thermal pickup's reference configuration.</summary>
    public int PickupConfiguration { get; init; }

    /// <summary>A pickup's source operand row, 1-based.</summary>
    public int PickupOperand { get; init; }

    public double PickupScale { get; init; } = 1;
    public double PickupOffset { get; init; }
}
