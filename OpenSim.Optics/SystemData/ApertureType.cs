namespace OpenSim.Optics;

/// <summary>
/// How the size of the light beam entering a sequential system is stated (OpticStudio's six
/// aperture types). The value each type takes is described on <see cref="Aperture.Value"/>.
/// Turning any of them into the paraxial entrance pupil needs the system's first-order data,
/// which the paraxial engine (OP-19) computes; ray aiming (OP-22) uses the result.
/// </summary>
public enum ApertureType
{
    /// <summary>The diameter of the paraxial entrance pupil, a length.</summary>
    EntrancePupilDiameter = 0,

    /// <summary>The image-space F-number of the system used at infinite conjugates: the
    /// paraxial effective focal length over the paraxial entrance-pupil diameter.</summary>
    ImageSpaceFNumber = 1,

    /// <summary>The numerical aperture n·sin θ of the marginal ray in object space, with n the
    /// object-space index.</summary>
    ObjectSpaceNumericalAperture = 2,

    /// <summary>No value: the stop surface's semi-diameter sets the beam.</summary>
    FloatByStopSize = 3,

    /// <summary>The paraxial working F-number, 1/(2·n'·u') with u' the paraxial marginal-ray
    /// slope in image space and n' the image-space index, at the conjugates in use.</summary>
    ParaxialWorkingFNumber = 4,

    /// <summary>The half-angle, in degrees, of the cone of light from the on-axis object point
    /// to the rim of the entrance pupil.</summary>
    ObjectConeAngle = 5,
}

/// <summary>Saved names and labels for <see cref="ApertureType"/>.</summary>
public static class ApertureTypes
{
    public static SavedNameTable<ApertureType> Names { get; } = new(
        (ApertureType.EntrancePupilDiameter, "EntrancePupilDiameter", "Entrance pupil diameter"),
        (ApertureType.ImageSpaceFNumber, "ImageSpaceFNumber", "Image space F/#"),
        (ApertureType.ObjectSpaceNumericalAperture, "ObjectSpaceNA", "Object space NA"),
        (ApertureType.FloatByStopSize, "FloatByStopSize", "Float by stop size"),
        (ApertureType.ParaxialWorkingFNumber, "ParaxialWorkingFNumber", "Paraxial working F/#"),
        (ApertureType.ObjectConeAngle, "ObjectConeAngle", "Object cone angle"));

    /// <summary>True when the aperture value is a length (shown in lens units, stored in
    /// meters).</summary>
    public static bool IsLength(ApertureType type) => type == ApertureType.EntrancePupilDiameter;

    /// <summary>True when the type takes no value.</summary>
    public static bool HasNoValue(ApertureType type) => type == ApertureType.FloatByStopSize;
}
