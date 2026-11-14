using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Layout;

/// <summary>
/// Radii and derived surfaces the 2-D and 3-D views share, so both draw the same edges, flats,
/// mirror substrates and apertures.
/// </summary>
public static class LayoutGeometry
{
    /// <summary>The aperture annulus around a stop reaches 1.25 times the clear semi-diameter
    /// when no larger mechanical one is given. A drawing choice only.</summary>
    public const double DefaultApertureRatio = 1.25;

    /// <summary>
    /// The radius out to which a surface is drawn: its outer semi-diameter, or the edge of a
    /// lens body it bounds when that is larger (both faces of a body reach the same edge).
    /// </summary>
    public static double DrawRadius(OpticalLayout layout, int surface)
    {
        double r = layout.Surfaces[surface].OuterSemiDiameter;
        foreach (var e in layout.Elements)
            if (e.Front == surface || e.Back == surface)
                r = Math.Max(r, layout.EdgeSemiDiameter(e));
        return r;
    }

    /// <summary>True when the surface bounds a lens body.</summary>
    public static bool InElement(OpticalLayout layout, int surface)
    {
        foreach (var e in layout.Elements)
            if (e.Front == surface || e.Back == surface) return true;
        return false;
    }

    /// <summary>The outer radius of the aperture annulus drawn around a stop.</summary>
    public static double ApertureOuterRadius(LayoutSurface stop) =>
        stop.MechanicalSemiDiameter is double m && m > stop.ClearSemiDiameter
            ? m
            : DefaultApertureRatio * stop.ClearSemiDiameter;

    /// <summary>
    /// The flat back of a mirror's substrate, as a plane surface. Its distance from the vertex
    /// is <see cref="LayoutSurface.MirrorSubstrateThickness"/> beyond the point of the drawn
    /// mirror that lies farthest toward that side, so the substrate is at least that thick
    /// everywhere and a steep mirror never pokes through its own back. Null when the mirror is
    /// drawn as a surface only.
    /// </summary>
    public static LayoutSurface? MirrorBack(LayoutSurface mirror, double drawRadius, int clearSamples)
    {
        double t = mirror.MirrorSubstrateThickness;
        if (t == 0 || !mirror.IsMirror) return null;
        double extreme = 0;
        foreach (double r in SagSampler.Radii(mirror.ClearSemiDiameter, drawRadius, clearSamples))
            for (int q = 0; q < 4; q++)
            {
                var (s, c) = OpticsConventions.SinCosDegrees(90.0 * q);
                double z = mirror.LocalPoint(r * c, r * s).Z;
                extreme = t > 0 ? Math.Max(extreme, z) : Math.Min(extreme, z);
            }
        var shift = RigidTransform.FromTranslation(new Vector3D(0, 0, extreme + t));
        return new LayoutSurface(RigidTransform.Compose(mirror.Placement, shift), ConicSag.Plane, drawRadius)
        {
            IsHidden = mirror.IsHidden
        };
    }
}
