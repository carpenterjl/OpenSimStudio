using OpenSim.Core.Numerics;

namespace OpenSim.Core.PostProcessing;

/// <summary>The axis a section plane is perpendicular to.</summary>
public enum SectionAxis
{
    X,
    Y,
    Z
}

/// <summary>
/// An axis-aligned section/clipping plane. The kept half-space is where
/// <see cref="SignedDistance"/> ≤ 0: by default everything below <see cref="Offset"/>
/// along the axis, or the opposite side when <see cref="FlipKeptSide"/> is set.
/// </summary>
public readonly record struct SectionPlane(SectionAxis Axis, double Offset)
{
    /// <summary>
    /// False (default): keep the negative side (below <see cref="Offset"/> along the axis).
    /// True: keep the positive side. The sign is applied inside <see cref="SignedDistance"/>
    /// so every consumer (skin filter, cut, contour clip) flips consistently.
    /// </summary>
    public bool FlipKeptSide { get; init; }

    public double SignedDistance(Vector3D p)
    {
        double d = Axis switch
        {
            SectionAxis.X => p.X - Offset,
            SectionAxis.Y => p.Y - Offset,
            _ => p.Z - Offset
        };
        return FlipKeptSide ? -d : d;
    }
}
