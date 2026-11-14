namespace OpenSim.Optics.Paraxial;

/// <summary>
/// A paraxial ray traced through a <see cref="ParaxialLens"/>, one entry per surface (object
/// surface first, image surface last). Lengths are meters.
/// </summary>
public sealed record ParaxialRay
{
    /// <summary>The height at each surface's vertex plane. Entry 0 is the height at the object
    /// plane, which is <see cref="double.NaN"/> when the object is at infinity
    /// (<see cref="ObjectAtInfinity"/>).</summary>
    public required IReadOnlyList<double> Heights { get; init; }

    /// <summary>u = dy/dz after each surface, in the system's frame (z does not turn round at a
    /// mirror). Entry 0 is the slope in object space; the image surface's entry repeats the
    /// slope arriving at it.</summary>
    public required IReadOnlyList<double> Slopes { get; init; }

    /// <summary>ν = n·u after each surface, n the signed index (negative after an odd number
    /// of mirrors, docs/Optics-Conventions.md §11).</summary>
    public required IReadOnlyList<double> ReducedSlopes { get; init; }

    public bool ObjectAtInfinity { get; init; }

    /// <summary>This ray with every height and slope multiplied by <paramref name="factor"/>:
    /// paraxial rays through one point of the axis, or one point of the pupil, are linear in
    /// their scale.</summary>
    public ParaxialRay Scaled(double factor) => this with
    {
        Heights = Heights.Select(h => h * factor).ToArray(),
        Slopes = Slopes.Select(u => u * factor).ToArray(),
        ReducedSlopes = ReducedSlopes.Select(v => v * factor).ToArray(),
    };
}
