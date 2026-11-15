using OpenSim.Core.Numerics;

namespace OpenSim.Core.Geometry;

/// <summary>
/// Where a ray leaving a surface starts so that it cannot meet that same surface again through
/// rounding: Pharr, Jakob and Humphreys, Physically Based Rendering, 4th edition (pbr-book.org),
/// section 6.8.6 (Robust Spawned Ray Origins), function <c>OffsetRayOrigin</c>.
/// <para>
/// The computed intersection point p lies within a box of half-widths δ = (δx, δy, δz) around
/// the exact one (section 6.8.5). Moving p along the surface normal n by
/// d = |nx|δx + |ny|δy + |nz|δz, the largest value of the plane function n·x over the box's
/// corners, puts the origin on a plane that does not cross the box, on the side the new ray
/// leaves toward (the sign of w·n for its direction w). Each coordinate is then moved one more
/// representable value away from p, because rounding the sum p + offset may land back inside
/// the box (PBR figure 6.46).
/// </para>
/// </summary>
public static class RobustRayOrigin
{
    /// <summary>The offset origin for a ray of direction <paramref name="direction"/> leaving
    /// the point <paramref name="point"/> (error half-widths <paramref name="error"/>, all ≥ 0)
    /// on a surface of normal <paramref name="normal"/> (either sense).</summary>
    public static Vector3D Offset(Vector3D point, Vector3D error, Vector3D normal, Vector3D direction)
    {
        double d = Math.Abs(normal.X) * error.X + Math.Abs(normal.Y) * error.Y + Math.Abs(normal.Z) * error.Z;
        var offset = normal * d;
        if (Vector3D.Dot(direction, normal) < 0) offset = -offset;
        var po = point + offset;
        return new Vector3D(Away(po.X, offset.X), Away(po.Y, offset.Y), Away(po.Z, offset.Z));

        static double Away(double value, double offset) =>
            offset > 0 ? Math.BitIncrement(value) : offset < 0 ? Math.BitDecrement(value) : value;
    }

    /// <summary>
    /// A conservative error box for a point found by a few dozen floating-point operations on
    /// quantities no larger than <paramref name="scale"/> (the sum of the magnitudes that entered
    /// it, e.g. |p|₁ plus the ray parameter): γ₃₂·scale on every axis. PBR bounds each shape's
    /// point error by γₙ times the magnitudes involved (section 6.8.5, e.g. γ₅|p| for the
    /// reprojected sphere point); 32 is a deliberately generous n for the analytic objects of the
    /// non-sequential engine, whose points pass through a rigid transform (γ₃ each way,
    /// section 6.8.5's transform bound), a quadratic root and a ray step.
    /// </summary>
    public static Vector3D ErrorBox(double scale)
    {
        double e = BoundingVolumeHierarchy.Gamma(32) * Math.Abs(scale);
        return new Vector3D(e, e, e);
    }
}
