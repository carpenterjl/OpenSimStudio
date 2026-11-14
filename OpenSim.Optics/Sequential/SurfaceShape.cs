using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential;

/// <summary>
/// The shape of a sequential surface other than OpticStudio's Standard surface, and optionally
/// its own way of turning a ray (docs/Optics-Conventions.md §13). A surface whose
/// <see cref="SequentialSurface.Shape"/> is null is the Standard conic of
/// <see cref="SequentialSurface.Curvature"/> and <see cref="SequentialSurface.Conic"/>, traced by
/// <see cref="ConicIntersection"/>. A shape replaces the intersection and the normal; the trace
/// still transfers the ray to the vertex plane, tests the aperture, refracts or reflects with
/// the normal, and accumulates the optical path, unless <see cref="TryRedirect"/> takes over.
/// <para>
/// The aspheres (OP-23) and the idealised and special surfaces (OP-27) derive from this class.
/// Every member works in the surface's local frame (vertex at the origin, +z along the local
/// axis), in meters, and must be thread-safe: one trace is shared between threads.
/// </para>
/// </summary>
public abstract class SurfaceShape
{
    /// <summary>The surface type's name as the lens data editor shows it (e.g. "Even Asphere").</summary>
    public abstract string TypeName { get; }

    /// <summary>The sag z(x, y) of the surface, meters, or NaN where it is not defined.</summary>
    public abstract double Sag(double x, double y, SequentialSurface surface);

    /// <summary>
    /// The intersection of the ray through (<paramref name="x"/>, <paramref name="y"/>, 0) with
    /// unit direction <paramref name="direction"/> and the surface, with the signed distance
    /// from (x, y, 0) to it (the same contract as <see cref="ConicIntersection.Intersect"/>).
    /// False when the ray misses or the iteration fails; the trace then flags the ray
    /// <see cref="RayStatus.MissedSurface"/>.
    /// </summary>
    public abstract bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance);

    /// <summary>The unit normal at a point on the surface, the sense that is +z at the vertex
    /// (as <see cref="ConicIntersection.Normal"/>).</summary>
    public abstract Vector3D Normal(Vector3D point, SequentialSurface surface);

    /// <summary>
    /// A surface that turns rays by its own rule (an ideal lens, an ABCD block, a retroreflector,
    /// a Fresnel facet) returns true with the direction after the surface, the optical path it
    /// adds beyond the geometric segment (meters, already multiplied by the index), and
    /// <paramref name="failure"/> = <see cref="RayStatus.Ok"/> or the reason the ray stops there.
    /// The default returns false, and the trace refracts or reflects with <see cref="Normal"/>.
    /// </summary>
    public virtual bool TryRedirect(in SurfaceRedirectInput input, out Vector3D direction,
        out double extraOpticalPath, out RayStatus failure)
    {
        direction = input.Incoming;
        extraOpticalPath = 0;
        failure = RayStatus.Ok;
        return false;
    }

    // OP-27: what the paraxial engine and the OPD analyses need from a shape.

    /// <summary>
    /// The surface as the paraxial engine sees it (<see cref="SequentialLens.ToParaxialLens"/>):
    /// by default the vertex curvature of <paramref name="surface"/> and nothing else, which is
    /// right for a shape whose sag is the Standard conic's to second order. A shape whose
    /// first-order action differs (an ideal thin lens, an ABCD block, a Fresnel facet, a
    /// polynomial whose r² term adds curvature) overrides it. The paraxial engine is
    /// rotationally symmetric and traces the y meridian; an anamorphic shape gives its y-z
    /// behaviour.
    /// </summary>
    public virtual ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { Curvature = surface.Curvature };

    /// <summary>
    /// Null when the optical path the trace accumulates through this shape is the true one
    /// (so OPD, MTF and Zernike fits are meaningful); otherwise one sentence saying why not,
    /// which an analysis that needs OPD shows instead of a result (OpticStudio refuses OPD
    /// through an ABCD surface and through a Fresnel surface on a curved substrate).
    /// </summary>
    public virtual string? OpdLimitation(SequentialSurface surface) => null;
}

/// <summary>
/// The first-order model of one surface (OP-27): a vertex curvature that refracts with
/// c·(n′ − n), a thin-lens power added to it, or an ABCD matrix on (height, slope u = dy/dz)
/// that replaces both (<see cref="Paraxial.ParaxialSurface.SlopeMatrix"/>).
/// </summary>
public readonly record struct ParaxialShapeEquivalent
{
    public double Curvature { get; init; }
    public double ThinLensPower { get; init; }
    public Paraxial.ParaxialMatrix? SlopeMatrix { get; init; }
}

/// <summary>What <see cref="SurfaceShape.TryRedirect"/> is given at one surface.</summary>
public readonly record struct SurfaceRedirectInput
{
    public required SequentialSurface Surface { get; init; }
    /// <summary>0-based surface number.</summary>
    public required int SurfaceIndex { get; init; }
    /// <summary>The intersection point, local frame, meters.</summary>
    public required Vector3D Point { get; init; }
    /// <summary>The unit direction before the surface.</summary>
    public required Vector3D Incoming { get; init; }
    /// <summary>The unit normal from <see cref="SurfaceShape.Normal"/>.</summary>
    public required Vector3D Normal { get; init; }
    public required double IndexBefore { get; init; }
    public required double IndexAfter { get; init; }
    /// <summary>The vacuum wavelength of the ray, meters.</summary>
    public required double VacuumWavelength { get; init; }
}
