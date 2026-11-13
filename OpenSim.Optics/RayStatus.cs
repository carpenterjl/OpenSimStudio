namespace OpenSim.Optics;

/// <summary>
/// Why a ray stopped, or <see cref="Ok"/> while it is still being traced. A failed ray is kept
/// and reported with its status and the surface it failed at; it is never dropped silently.
/// </summary>
public enum RayStatus
{
    /// <summary>The ray is valid so far.</summary>
    Ok = 0,

    /// <summary>The ray's line does not meet the surface at all.</summary>
    MissedSurface = 1,

    /// <summary>Total internal reflection at a surface that should have refracted the ray.</summary>
    TotalInternalReflection = 2,

    /// <summary>The ray met the surface outside its clear aperture, or inside an obscuration.</summary>
    Vignetted = 3,

    /// <summary>An iterative intersection or solve did not converge to its tolerance.</summary>
    NoConvergence = 4,

    /// <summary>The requested diffraction order does not propagate (it is evanescent).</summary>
    EvanescentOrder = 5,
}

/// <summary>Names for <see cref="RayStatus"/> values.</summary>
public static class RayStatuses
{
    /// <summary>Every status, in declaration order.</summary>
    public static IReadOnlyList<RayStatus> All { get; } = Enum.GetValues<RayStatus>();

    /// <summary>The text shown to the user. Throws for a status with no arm, so a new status
    /// cannot ship without one.</summary>
    public static string Label(RayStatus status) => status switch
    {
        RayStatus.Ok => "Traced",
        RayStatus.MissedSurface => "Missed the surface",
        RayStatus.TotalInternalReflection => "Total internal reflection",
        RayStatus.Vignetted => "Stopped by an aperture",
        RayStatus.NoConvergence => "Intersection did not converge",
        RayStatus.EvanescentOrder => "Diffraction order is evanescent",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No label for this ray status.")
    };
}
