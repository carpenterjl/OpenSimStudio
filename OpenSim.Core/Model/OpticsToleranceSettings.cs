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

    // OP-64
    /// <summary>The settings of the last tolerance run (sensitivity and inverse sensitivity,
    /// plan item OP-64); null for the defaults.</summary>
    public OpticsToleranceRunSettings? Run { get; init; }
}

// OP-64
/// <summary>
/// The tolerance run's settings as the project and the lens file store them (OpticStudio's
/// Tolerancing dialog, Set-Up and Criterion tabs, as far as plan item OP-64 builds them). Every
/// member is nullable; null is the default. The enumerations are stored by name; OpenSim.Optics'
/// <c>ToleranceRunSettings</c> converts to and from its options and names any value it does not know.
/// </summary>
public sealed record OpticsToleranceRunSettings
{
    /// <summary>"Sensitivity", "InverseLimit" or "InverseIncrement".</summary>
    public string? Mode { get; init; }

    /// <summary>"RmsSpotRadius", "RmsSpotX", "RmsSpotY", "RmsWavefront", "MeritFunction",
    /// "BoresightError" or "StrehlRatio".</summary>
    public string? Criterion { get; init; }

    /// <summary>"LinearDifference" or "RssDifference".</summary>
    public string? Change { get; init; }

    /// <summary>"None", "ParaxialFocus", "OptimizeDls" or "OptimizeOd".</summary>
    public string? Compensation { get; init; }

    /// <summary>"YSymmetric", "XYSymmetric" or "UserDefined".</summary>
    public string? Fields { get; init; }

    /// <summary>Rays along a radial arm of the Gaussian quadrature (spot and wavefront criteria).</summary>
    public int? Sampling { get; init; }

    /// <summary>The pupil grid of the Strehl ratio criterion.</summary>
    public int? PsfSampling { get; init; }

    /// <summary>The optimizer's cycles for the compensators; null or 0 is automatic.</summary>
    public int? OptimizerCycles { get; init; }

    /// <summary>The 1-based rows of the compensators to optimize; null for all.</summary>
    public List<int>? CompensatorRows { get; init; }

    /// <summary>The Limit of Inverse Limit mode, in the criterion's unit.</summary>
    public double? Limit { get; init; }

    /// <summary>The Increment of Inverse Increment mode, in the criterion's unit.</summary>
    public double? Increment { get; init; }

    /// <summary>How many worst offenders to list.</summary>
    public int? ShowWorst { get; init; }
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
