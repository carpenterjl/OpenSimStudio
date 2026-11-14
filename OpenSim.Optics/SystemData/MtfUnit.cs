namespace OpenSim.Optics;

/// <summary>The spatial-frequency unit of MTF results: per length on a focal image, per angle on
/// an afocal one.</summary>
public enum MtfUnit
{
    CyclesPerMillimeter = 0,
    CyclesPerMilliradian = 1,
}

/// <summary>Saved names and labels for <see cref="MtfUnit"/>.</summary>
public static class MtfUnits
{
    public static SavedNameTable<MtfUnit> Names { get; } = new(
        (MtfUnit.CyclesPerMillimeter, "CyclesPerMillimeter", "Cycles per millimeter"),
        (MtfUnit.CyclesPerMilliradian, "CyclesPerMilliradian", "Cycles per milliradian"));
}
