namespace OpenSim.Optics;

/// <summary>Saved names and labels for the enums the conventions define
/// (<see cref="LensUnit"/>, <see cref="FieldNormalization"/>), which the system data saves.</summary>
public static class ConventionNames
{
    public static SavedNameTable<LensUnit> LensUnits { get; } = new(
        (LensUnit.Millimeter, "Millimeter", "Millimeters"),
        (LensUnit.Centimeter, "Centimeter", "Centimeters"),
        (LensUnit.Inch, "Inch", "Inches"),
        (LensUnit.Meter, "Meter", "Meters"));

    /// <summary>The short symbol a column header shows ("mm").</summary>
    public static string Symbol(LensUnit unit) => unit switch
    {
        LensUnit.Millimeter => "mm",
        LensUnit.Centimeter => "cm",
        LensUnit.Inch => "in",
        LensUnit.Meter => "m",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown lens unit.")
    };

    public static SavedNameTable<FieldNormalization> FieldNormalizations { get; } = new(
        (FieldNormalization.Radial, "Radial", "Radial"),
        (FieldNormalization.Rectangular, "Rectangular", "Rectangular"));
}
