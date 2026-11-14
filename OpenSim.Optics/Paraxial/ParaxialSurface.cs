namespace OpenSim.Optics.Paraxial;

/// <summary>
/// One surface as the paraxial engine sees it: a vertex curvature, the thickness to the next
/// vertex, the medium after it, and whether it is the stop or a mirror. It is the first-order
/// part of a sequential surface; the surface model of OP-20 feeds it (curvature, thickness,
/// material, stop and mirror flags) and adds what only a real ray needs (conic, aspheric terms,
/// apertures).
/// <para>
/// Signs follow docs/Optics-Conventions.md §3: the curvature is positive when the centre of
/// curvature lies toward +z of the vertex, whatever the number of mirrors before; the thickness
/// is negative after an odd number of mirrors. Lengths are meters, curvature 1/m.
/// </para>
/// </summary>
public sealed record ParaxialSurface
{
    /// <summary>Vertex curvature, 1/m (<see cref="OpticsConventions.CurvatureFromRadius"/>).
    /// Ignored on the object and image surfaces: paraxial heights there are taken in the
    /// vertex plane.</summary>
    public double Curvature { get; init; }

    /// <summary>Distance along z from this vertex to the next, meters. On the object surface,
    /// the object distance, which may be <see cref="double.PositiveInfinity"/> (object at
    /// infinity). Ignored on the image surface.</summary>
    public double Thickness { get; init; }

    /// <summary>The medium after the surface. Ignored for a mirror, after which the medium is
    /// the one before it (docs/Optics-Conventions.md §3), and for the image surface.</summary>
    public ParaxialMedium Medium { get; init; } = ParaxialMedium.Air;

    /// <summary>The aperture stop. Exactly one surface between the object and the image is.</summary>
    public bool IsStop { get; init; }

    /// <summary>A reflecting surface (material MIRROR).</summary>
    public bool IsMirror { get; init; }

    /// <summary>The clear semi-diameter, meters, or null. Read only on the stop, when the
    /// aperture is <see cref="ApertureType.FloatByStopSize"/>.</summary>
    public double? SemiDiameter { get; init; }

    /// <summary>The surface's comment, shown in reports.</summary>
    public string Comment { get; init; } = "";
}
