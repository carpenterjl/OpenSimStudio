using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// One ray at one surface, as the Single Ray Trace lists it. Lengths are meters; positions and
/// directions are in the surface's local frame (vertex at the origin) unless named global, and
/// global means the frame rays are launched in: object space's axes, origin at surface 1's
/// vertex before surface 1's own tilt (docs/Optics-Conventions.md §13, §15).
/// </summary>
public sealed record SurfaceHit
{
    public int Surface { get; init; }

    /// <summary>The intersection with the surface, local frame. On the object surface, the
    /// launch point (in surface 1's frame when the object is at infinity).</summary>
    public Vector3D Position { get; init; }

    /// <summary>The same point in the global frame.</summary>
    public Vector3D GlobalPosition { get; init; }

    /// <summary>The unit direction arriving at the surface, local frame.</summary>
    public Vector3D IncomingDirection { get; init; }

    /// <summary>The unit direction leaving the surface (after refraction or reflection), local frame.</summary>
    public Vector3D Direction { get; init; }

    /// <summary>The unit surface normal at the point, the sense that is +z at the vertex.</summary>
    public Vector3D Normal { get; init; }

    /// <summary>Index of the space before and after the surface, relative to the system's air.</summary>
    public double IndexBefore { get; init; }
    public double IndexAfter { get; init; }

    /// <summary>Angle between the incoming ray and the normal, degrees, 0 to 90.</summary>
    public double AngleOfIncidenceDegrees { get; init; }

    /// <summary>Angle between the leaving ray and the normal, degrees, 0 to 90 (for a mirror,
    /// equal to the angle of incidence).</summary>
    public double AngleOfExitDegrees { get; init; }

    /// <summary>Signed geometric length from the previous point (the launch point for surface 1)
    /// to this one; negative for a virtual step back.</summary>
    public double SegmentLength { get; init; }

    /// <summary>Index before times <see cref="SegmentLength"/>.</summary>
    public double SegmentOpticalPath { get; init; }

    /// <summary>The optical path from the launch point to here.</summary>
    public double OpticalPath { get; init; }

    /// <summary>Ok, or the status the ray failed with at this surface (only
    /// <see cref="RayStatus.Vignetted"/> and <see cref="RayStatus.TotalInternalReflection"/>
    /// leave a hit; a ray that misses the surface has none there).</summary>
    public RayStatus Status { get; init; }

    /// <summary>The angle between a direction and a unit normal, degrees, from atan2(|d × n|, |d·n|).</summary>
    internal static double AngleDegrees(Vector3D d, Vector3D n) =>
        Math.Atan2(Vector3D.Cross(d, n).Length, Math.Abs(Vector3D.Dot(d, n))) * (180 / Math.PI);
}

/// <summary>
/// A ray traced surface by surface: the hits from the object surface on, and the final ray.
/// A failed ray keeps its hits up to the surface it failed at; <see cref="Final"/> carries the
/// status and that surface.
/// </summary>
public sealed record RayPath
{
    public required IReadOnlyList<SurfaceHit> Hits { get; init; }

    /// <summary>The ray at the image surface (local frame), or where it failed.</summary>
    public required Ray Final { get; init; }

    /// <summary>True when the object is at infinity: hit 0 is then the launch point on the
    /// reference plane, not an object point.</summary>
    public bool ObjectAtInfinity { get; init; }

    public RayStatus Status => Final.Status;
    public int FailedAtSurface => Final.FailedAtSurface;

    /// <summary>The hits a layout draws: all but those on surfaces marked
    /// <see cref="SequentialSurface.SkipRaysTo"/>.</summary>
    public IReadOnlyList<SurfaceHit> DrawnHits(SequentialLens lens) =>
        Hits.Where(h => h.Surface == 0 || h.Surface >= lens.Surfaces.Count || !lens.Surfaces[h.Surface].SkipRaysTo).ToArray();
}
