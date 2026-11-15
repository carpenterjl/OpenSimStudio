namespace OpenSim.Optics.Tolerancing;

/// <summary>The groups of OpticStudio's "Tolerance Operands Summary Table" and "Tolerance
/// Control Operands Summary Table" (OpticStudio 2025 R2 help).</summary>
public enum ToleranceCategory
{
    /// <summary>"Surface Tolerances": one surface's radius, thickness, shape, irregularity,
    /// parameters, material and coating.</summary>
    Surface,

    /// <summary>"Element Tolerances": a group of surfaces decentered, tilted or rolled as one.</summary>
    Element,

    /// <summary>"User Defined Tolerances": the values of an existing coordinate break.</summary>
    UserDefined,

    /// <summary>"Non-sequential Component Tolerances".</summary>
    NonSequential,

    /// <summary>TMCO: a value of the multi-configuration table.</summary>
    MultiConfiguration,

    /// <summary>The control operands: compensators, pivot, save, seed, statistics, test
    /// wavelength, comments. They perturb nothing.</summary>
    Control,
}

/// <summary>
/// One tolerance operand of OpticStudio's Tolerance Data Editor: its code, what its three integer
/// columns and its Min and Max mean, the help page they were read from, and whether this build
/// can apply it. Built by <see cref="ToleranceOperandRegistry"/>.
/// </summary>
public sealed class ToleranceOperandDefinition
{
    /// <summary>OpticStudio's four-letter code.</summary>
    public required string Code { get; init; }

    /// <summary>A short descriptive name.</summary>
    public required string Name { get; init; }

    /// <summary>What the operand perturbs or does, after its help page.</summary>
    public required string Description { get; init; }

    public required ToleranceCategory Category { get; init; }

    /// <summary>The OpticStudio 2025 R2 help page the definition was read from (saved under
    /// plans/sources/optics/op-63-help/).</summary>
    public required string Source { get; init; }

    /// <summary>The meaning of Int1, Int2 and Int3 ("Surf", "Adjust", …); null where unused.</summary>
    public string? Int1 { get; init; }
    public string? Int2 { get; init; }
    public string? Int3 { get; init; }

    /// <summary>What Min and Max are ("lens units", "degrees", "fringes at TWAV" …); null for a
    /// row that does not use them.</summary>
    public string? Unit { get; init; }

    /// <summary>True when Min is always the negative of Max (TEXI, TEZI and the ISO operands:
    /// "The min tolerance value is automatically set to the negative of the max value").</summary>
    public bool MinIsNegativeMax { get; init; }

    /// <summary>True when Min and Max must both be zero or more (TSDR, TEDR: "both Min and Max must
    /// be greater than or equal to 0"; TARR, TRLR: "Min must be greater than or equal to 0").</summary>
    public bool NonNegative { get; init; }

    /// <summary>True for the element tilts, decenters and rolls, which wrap a surface group in
    /// coordinate breaks and must obey the nesting rules ("Nesting Rules for Monte Carlo Analysis").</summary>
    public bool IsElement { get; init; }

    /// <summary>True for the radial operands (TSDR, TEDR, TARR, TRLR), whose direction is drawn.</summary>
    public bool IsRadial { get; init; }

    /// <summary>Null when this build applies the operand; otherwise why it cannot, which
    /// <see cref="TolerancePerturber.Apply"/> throws with the code.</summary>
    public string? NotAppliedReason { get; init; }

    /// <summary>True for a control row (<see cref="ToleranceCategory.Control"/>).</summary>
    public bool IsControl => Category == ToleranceCategory.Control;

    /// <summary>"Tolerance on radius (TRAD)".</summary>
    public string DisplayName => $"{Name} ({Code})";

    public override string ToString() => Code;
}
