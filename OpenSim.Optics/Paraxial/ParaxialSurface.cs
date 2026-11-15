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

    // OP-27: idealised surfaces (docs/Optics-Conventions.md §18).

    /// <summary>A power φ added to the surface's own c·(n′ − n), 1/m: the ideal thin lens of
    /// OpticStudio's Paraxial surface (φ = 1/f, f measured in air). It enters the signed-index
    /// formalism as it is, like the power of a real thin lens, which is the same whichever way
    /// light crosses it. Zero for an ordinary surface.</summary>
    public double ThinLensPower { get; init; }

    /// <summary>
    /// A first-order matrix that replaces the surface's refraction: it acts on (height, slope
    /// u = dy/dz) at the vertex plane, as OpticStudio's ABCD surface does, and the engine turns
    /// it into the reduced form [[A, B/n], [n′·C, n′·D/n]] with the signed indices before and
    /// after (section 11). Null for an ordinary surface. A matrix with B ≠ 0 moves the ray on the
    /// vertex plane: <see cref="ParaxialRay.Heights"/> keeps the height arriving there, and the
    /// next transfer starts from the moved one. Such a surface cannot be surface 1, the stop or
    /// the last surface (<see cref="ParaxialLens.Validate"/>), where the engine reads heights
    /// and slopes of one surface together.
    /// </summary>
    public ParaxialMatrix? SlopeMatrix { get; init; }

    // OP-26: diffractive surfaces (docs/Optics-Conventions.md §57).

    /// <summary>
    /// The power of a diffractive phase per unit wavelength, 1/m²: the engine adds
    /// φ = <see cref="DiffractivePowerPerWavelength"/>·λ to the surface's power, λ its wavelength
    /// in the system's air in meters (the medium the indices are relative to). A phase
    /// Φ(y) = ½·Φ″·y² (radians, order included) turns a ray by Δ(n·sin θ) = (λ/2π)·Φ′(y), so
    /// n′u′ = n·u − y·φ with φ = −λ·Φ″/(2π) whatever the indices, like a thin lens; this is
    /// −Φ″/(2π). Zero for an ordinary surface.
    /// </summary>
    public double DiffractivePowerPerWavelength { get; init; }
}
