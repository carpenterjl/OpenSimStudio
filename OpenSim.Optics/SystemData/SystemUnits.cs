namespace OpenSim.Optics;

/// <summary>
/// The units group of the system data. These are display and file units only: every length
/// inside the program is meters (docs/Optics-Conventions.md §1). Source-unit prefixes (milli-,
/// kilo- and so on) are not offered yet; they arrive with the sources that use them.
/// </summary>
public sealed record SystemUnits
{
    /// <summary>The unit the editors and lens files show lengths in.</summary>
    public LensUnit LensUnit { get; init; } = LensUnit.Millimeter;

    public SourceUnit SourceUnit { get; init; } = SourceUnit.Watts;

    /// <summary>The length whose square irradiance and illuminance are reported per
    /// (W/mm², lm/cm², …).</summary>
    public LensUnit AnalysisAreaUnit { get; init; } = LensUnit.Millimeter;

    public MtfUnit MtfUnit { get; init; } = MtfUnit.CyclesPerMillimeter;
}
