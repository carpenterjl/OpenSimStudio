using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// One surface of a sequential system as the real ray tracer takes it (OpticStudio's Standard
/// surface): a rotationally symmetric conic with vertex curvature <see cref="Curvature"/> and
/// conic constant <see cref="Conic"/>, the thickness to the next vertex, the medium after it,
/// stop and mirror flags, an optional aperture, and its semi-diameters.
/// <para>
/// Signs follow docs/Optics-Conventions.md §3 and §13: curvature positive when the centre of
/// curvature lies toward +z of the vertex, whatever the number of mirrors before; thickness
/// negative after an odd number of mirrors. k = 0 is a sphere, −1 a paraboloid, below −1 a
/// hyperboloid, between −1 and 0 a prolate and above 0 an oblate ellipsoid. Lengths are meters,
/// curvature 1/m.
/// </para>
/// <para>
/// Tilts, decenters and coordinate breaks are OP-21: the tracer places every surface through
/// one transform per surface (<see cref="SequentialTrace"/>), which is a translation along z
/// until they arrive.
/// </para>
/// </summary>
public sealed record SequentialSurface
{
    /// <summary>Vertex curvature, 1/m (<see cref="OpticsConventions.CurvatureFromRadius"/>).
    /// Ignored on the object surface (taken as a plane).</summary>
    public double Curvature { get; init; }

    /// <summary>Conic constant k (dimensionless).</summary>
    public double Conic { get; init; }

    /// <summary>Distance along the local z to the next vertex, meters. On the object surface the
    /// object distance, possibly <see cref="double.PositiveInfinity"/>. Ignored on the image
    /// surface.</summary>
    public double Thickness { get; init; }

    /// <summary>The medium after the surface: a constant index or a catalog material, resolved
    /// at each wavelength through <see cref="OpticsEnvironment.GlassIndex"/> (OP-11, OP-13).
    /// Ignored for a mirror (the medium after it is the one before) and for an ignored
    /// surface.</summary>
    public ParaxialMedium Medium { get; init; } = ParaxialMedium.Air;

    /// <summary>The aperture stop. Exactly one surface between the object and the image is.</summary>
    public bool IsStop { get; init; }

    /// <summary>A reflecting surface (material MIRROR).</summary>
    public bool IsMirror { get; init; }

    /// <summary>OpticStudio's "ignore this surface": the ray crosses the surface's vertex plane
    /// and goes on unchanged (no shape, no aperture, no change of medium); its thickness still
    /// counts. Not allowed on the object, the image, the stop or a mirror.</summary>
    public bool IsIgnored { get; init; }

    /// <summary>OpticStudio's "skip rays to this surface", a drawing option: layouts draw ray
    /// segments past this surface without a vertex on it (<see cref="RayPath.DrawnHits"/>). The
    /// trace itself is unchanged.</summary>
    public bool SkipRaysTo { get; init; }

    /// <summary>The aperture, or null for none. A surface without one never vignettes,
    /// whatever its semi-diameter (as in OpticStudio).</summary>
    public SurfaceAperture? Aperture { get; init; }

    /// <summary>The clear semi-diameter, meters, or null for automatic
    /// (<see cref="SemiDiameterSizing"/>). On the stop it is the stop size the aperture type
    /// "float by stop size" reads.</summary>
    public double? SemiDiameter { get; init; }

    /// <summary>The mechanical semi-diameter, meters, or null for the clear one plus the chip zone.</summary>
    public double? MechanicalSemiDiameter { get; init; }

    /// <summary>The chip zone, meters: an annulus beyond the clear semi-diameter that the
    /// mechanical semi-diameter always includes.</summary>
    public double ChipZone { get; init; }

    /// <summary>The surface's shape when it is not the Standard conic (aspheres OP-23, special
    /// surfaces OP-27), or null for the Standard surface.</summary>
    public SurfaceShape? Shape { get; init; }

    /// <summary>The surface's comment, shown in reports.</summary>
    public string Comment { get; init; } = "";

    /// <summary>The vertex radius, meters; +infinity for a plane.</summary>
    public double Radius => OpticsConventions.RadiusFromCurvature(Curvature);
}
