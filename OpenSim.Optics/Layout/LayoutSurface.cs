using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Layout;

/// <summary>
/// One surface as the layout views draw it. This is the renderer's input, not the lens data
/// model: the sequential system (OP-18, OP-20, OP-21) reduces each of its surfaces to one of
/// these once its global placement is known.
/// <list type="bullet">
/// <item><see cref="Placement"/> takes the surface's local frame (vertex at the origin, local z
/// the axis the sag is measured along) to the global frame: global = R·local + t
/// (docs/Optics-Conventions.md §4).</item>
/// <item><see cref="ClearSemiDiameter"/> is the radius the light uses; the sag is drawn out to
/// it. <see cref="MechanicalSemiDiameter"/>, when larger, adds a flat: the annulus from the
/// clear to the mechanical radius is drawn perpendicular to the local z at the sag of the clear
/// edge, as OpticStudio draws a lens whose mechanical semi-diameter exceeds the clear one.</item>
/// <item><see cref="MaterialAfter"/> names the medium after the surface; null or blank is air
/// (or vacuum). A surface with a material after it and the next surface bound a lens body.</item>
/// </list>
/// All lengths are meters.
/// </summary>
public sealed record LayoutSurface(RigidTransform Placement, ISurfaceSag Sag, double ClearSemiDiameter)
{
    /// <summary>The mechanical semi-diameter, or null for the same as the clear one.</summary>
    public double? MechanicalSemiDiameter { get; init; }

    /// <summary>The medium after this surface; null or blank is air.</summary>
    public string? MaterialAfter { get; init; }

    /// <summary>True for a reflecting surface (OpticStudio's MIRROR material).</summary>
    public bool IsMirror { get; init; }

    /// <summary>The substrate drawn behind a mirror that is not part of a lens body: the signed
    /// distance along the local z from the vertex to a flat back. Zero draws the mirror as a
    /// surface only.</summary>
    public double MirrorSubstrateThickness { get; init; }

    /// <summary>True for the aperture stop; the views draw an aperture annulus around it.</summary>
    public bool IsStop { get; init; }

    /// <summary>True for a surface the views leave out (OpticStudio's per-surface "do not
    /// draw"). A hidden surface still bounds a lens body.</summary>
    public bool IsHidden { get; init; }

    /// <summary>The radius out to which the surface is drawn: the larger of the clear and
    /// mechanical semi-diameters.</summary>
    public double OuterSemiDiameter => Math.Max(ClearSemiDiameter, MechanicalSemiDiameter ?? ClearSemiDiameter);

    /// <summary>True when a medium other than air follows the surface.</summary>
    public bool HasMaterialAfter => !string.IsNullOrWhiteSpace(MaterialAfter);

    /// <summary>
    /// The local point at (x, y) on the drawn surface: the sag inside the clear aperture, and
    /// on the flat beyond it the sag where the same radial line crosses the clear edge.
    /// </summary>
    public Vector3D LocalPoint(double x, double y)
    {
        double r = Math.Sqrt(x * x + y * y);
        if (r <= ClearSemiDiameter || r == 0) return new Vector3D(x, y, Sag.Sag(x, y));
        double s = ClearSemiDiameter / r;
        return new Vector3D(x, y, Sag.Sag(x * s, y * s));
    }

    /// <summary><see cref="LocalPoint"/> in the global frame.</summary>
    public Vector3D GlobalPoint(double x, double y) => Placement.Apply(LocalPoint(x, y));
}
