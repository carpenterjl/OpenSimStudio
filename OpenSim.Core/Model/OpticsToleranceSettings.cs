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

    // OP-65
    /// <summary>The settings of the last Monte Carlo run (plan item OP-65); null for the defaults.</summary>
    public OpticsToleranceMonteCarloSettings? MonteCarlo { get; init; }

    // OP-66
    /// <summary>The Tolerance Wizard's settings (plan item OP-66); null for the defaults.</summary>
    public OpticsToleranceWizardSettings? Wizard { get; init; }

    // OP-66
    /// <summary>The Quick Yield settings (plan item OP-66); null for the defaults.</summary>
    public OpticsToleranceQuickYieldSettings? QuickYield { get; init; }
}

// OP-66
/// <summary>
/// The Tolerance Wizard's settings as the project and the lens file store them (OpticStudio's
/// Tolerance Wizard, as far as plan item OP-66 builds it). Every member is nullable; null is the
/// default. OpenSim.Optics' <c>ToleranceWizardSettings</c> converts to and from its options and
/// names any value it does not know.
/// </summary>
public sealed record OpticsToleranceWizardSettings
{
    public bool? Radius { get; init; }

    /// <summary>"LensUnits", "Percent" or "Fringes".</summary>
    public string? RadiusUnit { get; init; }

    public double? RadiusValue { get; init; }
    public bool? Thickness { get; init; }
    public double? ThicknessValue { get; init; }
    public bool? DecenterX { get; init; }
    public double? DecenterXValue { get; init; }
    public bool? DecenterY { get; init; }
    public double? DecenterYValue { get; init; }
    public bool? TiltX { get; init; }

    /// <summary>"LensUnits" (total indicator runout) or "Degrees".</summary>
    public string? TiltXUnit { get; init; }

    public double? TiltXValue { get; init; }
    public bool? TiltY { get; init; }

    /// <summary>"LensUnits" (total indicator runout) or "Degrees".</summary>
    public string? TiltYUnit { get; init; }

    public double? TiltYValue { get; init; }
    public bool? Irregularity { get; init; }
    public double? IrregularityFringes { get; init; }
    public bool? ElementDecenterX { get; init; }
    public double? ElementDecenterXValue { get; init; }
    public bool? ElementDecenterY { get; init; }
    public double? ElementDecenterYValue { get; init; }
    public bool? ElementTiltX { get; init; }
    public double? ElementTiltXDegrees { get; init; }
    public bool? ElementTiltY { get; init; }
    public double? ElementTiltYDegrees { get; init; }
    public bool? Index { get; init; }
    public double? IndexValue { get; init; }
    public bool? Abbe { get; init; }
    public double? AbbePercent { get; init; }

    /// <summary>"Start At Row": 1 replaces every row, n &gt; 1 keeps rows 1 to n − 1, −1 appends.</summary>
    public int? StartAtRow { get; init; }

    /// <summary>The test wavelength, micrometers.</summary>
    public double? TestWavelength { get; init; }

    public int? StartSurface { get; init; }

    /// <summary>The last surface; null for the surface before the image.</summary>
    public int? StopSurface { get; init; }

    public bool? FocusCompensator { get; init; }

    /// <summary>The focus compensator's ± range, lens units; null for none (Min = Max = 0).</summary>
    public double? FocusCompensatorRange { get; init; }
}

// OP-66
/// <summary>The Quick Yield settings as the project and the lens file store them. Null is the default.</summary>
public sealed record OpticsToleranceQuickYieldSettings
{
    /// <summary>The number of samples of the quadratic model.</summary>
    public int? Samples { get; init; }

    /// <summary>False to leave out the cross terms (one evaluation per pair of rows).</summary>
    public bool? CrossTerms { get; init; }
}

// OP-65
/// <summary>
/// The Monte Carlo run's settings as the project and the lens file store them (OpticStudio's
/// Tolerancing dialog, Monte Carlo tab, as far as plan item OP-65 builds it). Every member is
/// nullable; null is the default. OpenSim.Optics' <c>MonteCarloSettings</c> converts to and from
/// its options and names any value it does not know.
/// </summary>
public sealed record OpticsToleranceMonteCarloSettings
{
    /// <summary>The number of trials ("# Monte Carlo Runs").</summary>
    public int? Trials { get; init; }

    /// <summary>The run's seed; 0 draws one from the clock.</summary>
    public int? Seed { get; init; }

    /// <summary>"Normal", "Uniform" or "Parabolic": the distribution of rows no STAT row governs.</summary>
    public string? Statistics { get; init; }

    /// <summary>The normal distribution's standard deviations to the extremes for rows no STAT row governs.</summary>
    public int? StandardDeviations { get; init; }

    /// <summary>The criterion limit the yield is counted against, in the criterion's unit.</summary>
    public double? YieldLimit { get; init; }

    /// <summary>The percentiles reported, in percent.</summary>
    public List<double>? Percentiles { get; init; }

    /// <summary>The folder user-defined distribution (.UDD) files are read from.</summary>
    public string? DistributionFolder { get; init; }

    /// <summary>True to compute the MTF of each trial for the overlay plot.</summary>
    public bool? OverlayMtf { get; init; }

    /// <summary>The overlay's field, 1-based.</summary>
    public int? OverlayField { get; init; }

    /// <summary>The overlay's largest frequency, MTF units (cycles per millimeter).</summary>
    public double? OverlayMaxFrequency { get; init; }

    /// <summary>The overlay's number of frequencies.</summary>
    public int? OverlayPoints { get; init; }

    /// <summary>How many trials the overlay draws (the first ones).</summary>
    public int? OverlayTrials { get; init; }

    /// <summary>The histogram's number of bins.</summary>
    public int? HistogramBins { get; init; }
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
    /// "BoresightError", "StrehlRatio", "GeometricMtfAverage", "GeometricMtfTangential",
    /// "GeometricMtfSagittal", "DiffractionMtfAverage", "DiffractionMtfTangential",
    /// "DiffractionMtfSagittal", "DiffractionEncircledEnergy", "GeometricEncircledEnergy" or
    /// "UserScript" (OP-67).</summary>
    public string? Criterion { get; init; }

    /// <summary>"LinearDifference" or "RssDifference".</summary>
    public string? Change { get; init; }

    /// <summary>"None", "ParaxialFocus", "OptimizeDls" or "OptimizeOd".</summary>
    public string? Compensation { get; init; }

    /// <summary>"YSymmetric", "XYSymmetric" or "UserDefined".</summary>
    public string? Fields { get; init; }

    /// <summary>Rays along a radial arm of the Gaussian quadrature (spot and wavefront criteria).</summary>
    public int? Sampling { get; init; }

    /// <summary>The pupil grid of the Strehl ratio, MTF and encircled-energy criteria.</summary>
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

    /// <summary>The MTF criteria's frequency, MTF units (cycles per millimeter).</summary>
    public double? MtfFrequency { get; init; }

    /// <summary>The encircled-energy criteria's radius, micrometers.</summary>
    public double? EncircledRadiusMicrometers { get; init; }

    // OP-67: the tolerance script of the "UserScript" criterion.
    /// <summary>The script's name (as "ALIGN.TSC"); null is the default name.</summary>
    public string? ScriptName { get; init; }

    /// <summary>The script's text (ZPL with the tolerance-script commands).</summary>
    public string? ScriptText { get; init; }

    /// <summary>The folder LOADMERIT reads merit function files from.</summary>
    public string? ScriptMeritFolder { get; init; }

    /// <summary>The folder of OPENFILE's files and SAVE's lenses.</summary>
    public string? ScriptOutputFolder { get; init; }

    /// <summary>The folder of the macros CALLMACRO runs.</summary>
    public string? ScriptMacroFolder { get; init; }
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
