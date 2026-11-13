using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics;

/// <summary>
/// The electric field carried along a ray: three complex components in the same frame as the
/// ray's direction, transverse to it in an isotropic medium. Units are arbitrary but common to
/// all rays of one trace.
/// </summary>
public readonly record struct RayField(Complex X, Complex Y, Complex Z)
{
    /// <summary>|E|², the quantity the ray's intensity scales with.</summary>
    public double NormSquared =>
        X.Real * X.Real + X.Imaginary * X.Imaginary
        + Y.Real * Y.Real + Y.Imaginary * Y.Imaginary
        + Z.Real * Z.Real + Z.Imaginary * Z.Imaginary;

    /// <summary>E·d for a real direction d; zero when the field is transverse to d.</summary>
    public Complex Along(Vector3D direction) => X * direction.X + Y * direction.Y + Z * direction.Z;

    /// <summary>The field rotated by a transform's rotation (translation does not act on it).</summary>
    public RayField Rotated(RigidTransform t) => new(
        X * t.Col0.X + Y * t.Col1.X + Z * t.Col2.X,
        X * t.Col0.Y + Y * t.Col1.Y + Z * t.Col2.Y,
        X * t.Col0.Z + Y * t.Col1.Z + Z * t.Col2.Z);

    /// <summary>The field rotated back by a transform's rotation.</summary>
    public RayField RotatedBack(RigidTransform t) => new(
        X * t.Col0.X + Y * t.Col0.Y + Z * t.Col0.Z,
        X * t.Col1.X + Y * t.Col1.Y + Z * t.Col1.Z,
        X * t.Col2.X + Y * t.Col2.Y + Z * t.Col2.Z);
}

/// <summary>
/// One geometric ray. All lengths are meters (docs/Optics-Conventions.md):
/// <list type="bullet">
/// <item><see cref="Position"/> and <see cref="Direction"/> are in one frame, which the holder
/// of the ray knows (a surface's local frame during a trace, the global frame in results).</item>
/// <item><see cref="Direction"/> is a unit vector: its components are the direction cosines
/// (L, M, N). It points the way the light travels, so after a mirror N is negative.</item>
/// <item><see cref="Wavelength"/> is the vacuum wavelength.</item>
/// <item><see cref="OpticalPath"/> is Σ n·d over the segments so far, with n the (positive)
/// index and d the geometric length along the direction of travel (negative only for a
/// virtual step back to a reference). The index is never negated after a mirror.</item>
/// <item><see cref="Intensity"/> is relative power, 1 at launch.</item>
/// <item><see cref="Field"/> is null unless polarization is being traced.</item>
/// </list>
/// </summary>
public readonly record struct Ray
{
    /// <summary>The surface index recorded on a ray that has not failed.</summary>
    public const int NoSurface = -1;

    public Vector3D Position { get; init; }
    public Vector3D Direction { get; init; }
    public double Wavelength { get; init; }
    public double OpticalPath { get; init; }
    public double Intensity { get; init; }
    public RayField? Field { get; init; }
    public RayStatus Status { get; init; }

    /// <summary>The index of the surface the ray failed at, or <see cref="NoSurface"/>.</summary>
    public int FailedAtSurface { get; init; }

    /// <summary>A new ray of unit intensity and zero optical path. The direction is normalised
    /// here; a zero direction or a wavelength that is not positive and finite is refused.</summary>
    public Ray(Vector3D position, Vector3D direction, double wavelength)
    {
        if (!(wavelength > 0) || double.IsInfinity(wavelength))
            throw new ArgumentOutOfRangeException(nameof(wavelength), wavelength,
                "A ray's vacuum wavelength must be positive and finite (meters).");
        double length = direction.Length;
        if (!(length > 0) || double.IsInfinity(length))
            throw new ArgumentException("A ray's direction must be a finite, non-zero vector.", nameof(direction));
        Position = position;
        // An already-unit direction is kept bit for bit, so exact axis directions stay exact.
        Direction = length == 1.0 ? direction : direction / length;
        Wavelength = wavelength;
        OpticalPath = 0;
        Intensity = 1;
        Field = null;
        Status = RayStatus.Ok;
        FailedAtSurface = NoSurface;
    }

    /// <summary>True while the ray has not failed.</summary>
    public bool IsOk => Status == RayStatus.Ok;

    /// <summary>The point a signed geometric distance along the ray.</summary>
    public Vector3D PointAt(double distance) => Position + Direction * distance;

    /// <summary>The ray moved a geometric distance through a medium of the given index.
    /// The optical path changes by index·distance: a negative distance is a virtual
    /// propagation (back to a vertex plane or a reference sphere) and takes the path back.</summary>
    public Ray Advanced(double distance, double index)
    {
        if (!(index > 0))
            throw new ArgumentOutOfRangeException(nameof(index), index, "The refractive index must be positive.");
        return this with { Position = PointAt(distance), OpticalPath = OpticalPath + index * distance };
    }

    /// <summary>The ray stopped with a failure at a surface. Position, direction and path are
    /// kept as they were when it failed.</summary>
    public Ray Failed(RayStatus status, int surface)
    {
        if (status == RayStatus.Ok)
            throw new ArgumentException("A failure needs a failure status.", nameof(status));
        return this with { Status = status, FailedAtSurface = surface };
    }

    /// <summary>The same ray written in the parent frame of <paramref name="localToParent"/>.</summary>
    public Ray ToParent(RigidTransform localToParent) => localToParent.IsIdentity ? this : this with
    {
        Position = localToParent.Apply(Position),
        Direction = localToParent.ApplyDirection(Direction),
        Field = Field?.Rotated(localToParent)
    };

    /// <summary>The same ray written in the local frame of <paramref name="localToParent"/>.</summary>
    public Ray ToLocal(RigidTransform localToParent) => localToParent.IsIdentity ? this : this with
    {
        Position = localToParent.InverseApply(Position),
        Direction = localToParent.InverseApplyDirection(Direction),
        Field = Field?.RotatedBack(localToParent)
    };
}
