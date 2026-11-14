namespace OpenSim.Optics;

/// <summary>
/// What a field point's (x, y) means (OpticStudio's five field types). Angles are degrees,
/// heights meters. Launching the chief ray for each type needs the paraxial engine (OP-19)
/// and, for a real image height, ray aiming (OP-22).
/// </summary>
public enum FieldType
{
    /// <summary>The angle of the chief ray in object space, x and y measured from the axis in
    /// the xz and yz planes.</summary>
    Angle = 0,

    /// <summary>The height of the object point. Needs an object at a finite distance.</summary>
    ObjectHeight = 1,

    /// <summary>The height at which the paraxial chief ray meets the image surface.</summary>
    ParaxialImageHeight = 2,

    /// <summary>The height at which the real chief ray meets the image surface.</summary>
    RealImageHeight = 3,

    /// <summary>The chief ray's direction as a theodolite gives it: x is the azimuth and y the
    /// elevation, in degrees.</summary>
    TheodoliteAngle = 4,
}

/// <summary>Saved names and labels for <see cref="FieldType"/>, and the unit each uses.</summary>
public static class FieldTypes
{
    public static SavedNameTable<FieldType> Names { get; } = new(
        (FieldType.Angle, "Angle", "Angle"),
        (FieldType.ObjectHeight, "ObjectHeight", "Object height"),
        (FieldType.ParaxialImageHeight, "ParaxialImageHeight", "Paraxial image height"),
        (FieldType.RealImageHeight, "RealImageHeight", "Real image height"),
        (FieldType.TheodoliteAngle, "TheodoliteAngle", "Theodolite angle"));

    /// <summary>True for a type whose x and y are angles in degrees; false for heights, which
    /// are lengths (meters inside, lens units in the editor).</summary>
    public static bool IsAngle(FieldType type) => type is FieldType.Angle or FieldType.TheodoliteAngle;
}
