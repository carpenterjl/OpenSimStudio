namespace OpenSim.Core.Model;

/// <summary>
/// The tolerance data (plan item OP-63; OpticStudio's Tolerance Data Editor) as the project and
/// the lens file store it: the operand rows in order, each by OpticStudio's four-letter code, and
/// the irregularity model the TIRR rows use. Null on <see cref="OpticsSettings.Tolerances"/> —
/// every file written before OP-63 — is an empty table. OpenSim.Optics' <c>ToleranceDataSettings</c>
/// converts to and from its model and names any code it does not know.
/// </summary>
public sealed record OpticsToleranceSettings
{
    /// <summary>The rows in editor order (row 1 first); null is no rows.</summary>
    public List<OpticsToleranceOperandSettings>? Operands { get; init; }

    /// <summary>How a TIRR row's fringes are read (docs/Optics-Conventions.md §36): null or
    /// "PowerRemoved" (the default: the peak-to-valley after removing piston, tilt and power, as
    /// ISO 10110 counts irregularity), or "OpticStudio" (the help page's sag formula as written).</summary>
    public string? IrregularityModel { get; init; }
}

/// <summary>
/// One tolerance row: the operand's four-letter code ("TRAD", "TETX", "COMP" …; null or empty is
/// TOFF, an unused row), its comment, its three integer arguments (OpticStudio's Int1 to Int3:
/// surfaces, codes, parameter numbers, as the operand defines them), and the minimum and maximum
/// change from nominal in the operand's unit (lens units, degrees, fringes, …). Control rows use
/// the same columns as OpticStudio does (TWAV keeps the test wavelength in <see cref="Min"/>).
/// </summary>
public sealed record OpticsToleranceOperandSettings
{
    public string? Type { get; init; }

    public string? Comment { get; init; }

    public int Int1 { get; init; }

    public int Int2 { get; init; }

    public int Int3 { get; init; }

    public double Min { get; init; }

    public double Max { get; init; }

    /// <summary>OpticStudio's operand property "Ignore this Operand During Tolerancing".</summary>
    public bool Ignore { get; init; }

    /// <summary>OpticStudio's operand property "Do Not Adjust During Inverse Tolerancing".</summary>
    public bool DoNotAdjust { get; init; }
}
