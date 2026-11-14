namespace OpenSim.Optics;

/// <summary>
/// How rays are aimed (OpticStudio's System Explorer, Ray Aiming; Ansys OpticStudio 2025 R2
/// help, "Ray Aiming", ansyshelp.ansys.com, OpticStudio_Help/topics/Ray_Aiming.html).
/// </summary>
public enum RayAimingMode
{
    /// <summary>Rays are launched at the paraxial entrance pupil of the primary wavelength;
    /// pupil aberration is ignored.</summary>
    Off = 0,

    /// <summary>Rays are aimed at the real stop; the stop radius is the paraxial marginal ray's
    /// height there.</summary>
    Paraxial = 1,

    /// <summary>Rays are aimed at the real stop; the stop radius is the real marginal ray's
    /// height there (from the centre of the object at the primary wavelength).</summary>
    Real = 2,
}

/// <summary>Saved names and labels for <see cref="RayAimingMode"/>.</summary>
public static class RayAimingModes
{
    public static SavedNameTable<RayAimingMode> Names { get; } = new(
        (RayAimingMode.Off, "Off", "Off"),
        (RayAimingMode.Paraxial, "Paraxial", "Paraxial"),
        (RayAimingMode.Real, "Real", "Real"));
}

/// <summary>
/// The ray-aiming group of the system data (OP-22): the mode, the robust switch, and the pupil
/// shift and compression that help the first guess (OpticStudio help, "Ray Aiming": "The pupil
/// shift and compress values are just to get the ray aiming started"; they never change the
/// pupil's size). Shifts are meters; compressions are dimensionless fractions (0.1 shrinks the
/// first guess by 10 %). How they enter the first guess is docs/Optics-Conventions.md §16.
/// </summary>
public sealed record RayAiming
{
    public RayAimingMode Mode { get; init; } = RayAimingMode.Off;

    /// <summary>Always reach a ray by continuation from the axis (field, then pupil), and refuse
    /// a ray that reaches the stop by a virtual path; slower, for wide-angle systems where Newton
    /// from the paraxial guess can find the wrong root.</summary>
    public bool Robust { get; init; }

    /// <summary>Shift of the first guess on the paraxial entrance pupil, meters, at full field
    /// when <see cref="ScalePupilShiftByField"/> is set.</summary>
    public double PupilShiftX { get; init; }

    public double PupilShiftY { get; init; }

    /// <summary>Shift of the first guess's plane along z, meters at full field (always scaled
    /// with the radial normalised field). Positive moves it toward +z.</summary>
    public double PupilShiftZ { get; init; }

    /// <summary>Compression of the first guess's pupil coordinates in x (0 none; below 1).</summary>
    public double PupilCompressX { get; init; }

    public double PupilCompressY { get; init; }

    /// <summary>Scale the x and y pupil shifts with the radial normalised field; otherwise the
    /// same shifts apply at every field (the default; OpticStudio's help does not state its own).</summary>
    public bool ScalePupilShiftByField { get; init; }

    public bool IsOn => Mode != RayAimingMode.Off;

    /// <summary>Adds one actionable line to <paramref name="problems"/> per problem.</summary>
    public void Validate(ICollection<string> problems)
    {
        if (!Enum.IsDefined(Mode))
            problems.Add($"Ray aiming: the mode {Mode} is not known.");
        if (!double.IsFinite(PupilShiftX) || !double.IsFinite(PupilShiftY) || !double.IsFinite(PupilShiftZ))
            problems.Add($"Ray aiming: the pupil shifts must be numbers (they are {PupilShiftX}, {PupilShiftY}, {PupilShiftZ}).");
        if (!double.IsFinite(PupilCompressX) || !(PupilCompressX < 1) || !double.IsFinite(PupilCompressY) || !(PupilCompressY < 1))
            problems.Add($"Ray aiming: the pupil compressions must be below 1, which would close the first guess's pupil (they are {PupilCompressX}, {PupilCompressY}).");
    }
}
