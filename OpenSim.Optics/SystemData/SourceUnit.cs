namespace OpenSim.Optics;

/// <summary>The unit source power is stated and reported in (illumination and non-sequential
/// work): radiometric, photometric, or energy for a pulse.</summary>
public enum SourceUnit
{
    Watts = 0,
    Lumens = 1,
    Joules = 2,
}

/// <summary>Saved names and labels for <see cref="SourceUnit"/>.</summary>
public static class SourceUnits
{
    public static SavedNameTable<SourceUnit> Names { get; } = new(
        (SourceUnit.Watts, "Watts", "Watts"),
        (SourceUnit.Lumens, "Lumens", "Lumens"),
        (SourceUnit.Joules, "Joules", "Joules"));
}
