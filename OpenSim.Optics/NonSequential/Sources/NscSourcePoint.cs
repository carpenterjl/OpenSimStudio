using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// Source Point (help, "Source Point"): "a point which emits rays into a cone. The cone angle
/// may be any value between 0 and 180 degrees (which would radiate into a full sphere)";
/// parameter 6 is the semi-cone angle in degrees, about the local +z axis. The help does not
/// state the distribution inside the cone; it is taken uniform in solid angle (constant
/// intensity), a reading (Conventions §10 item 68): cos θ = 1 − a(1 − cos α), φ = 2πb (derived:
/// dΩ = d(cos θ)·dφ).
/// </summary>
public sealed class NscSourcePoint : NscSource
{
    public double ConeAngleDegrees { get; }
    public override string TypeName => "Source Point";
    public override Aabb LocalBounds => new(Vector3D.Zero, Vector3D.Zero);
    public override int Dimensions => 2;
    public override string Distribution => $"point, uniform in solid angle within a {ConeAngleDegrees.ToString("g6", CultureInfo.InvariantCulture)}° semi-cone about +z";

    private readonly double _cosAlpha;

    public NscSourcePoint(double coneAngleDegrees, NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(common, properties)
    {
        ConeAngleDegrees = Need(coneAngleDegrees, "the cone angle (degrees)", TypeName, 0, 180);
        _cosAlpha = Math.Cos(coneAngleDegrees * Math.PI / 180);
    }

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        position = Vector3D.Zero;
        direction = FromPolar(1 - u[0] * (1 - _cosAlpha), u[1]);
    }

    public override string Describe(Func<double, string> length) => $"semi-cone angle {ConeAngleDegrees.ToString("g6", CultureInfo.InvariantCulture)}°";
}

/// <summary>
/// Source Ray (help, "Source Ray"): "a point which emits rays along specified direction cosines";
/// parameters 6–8 are the local x, y, z cosines, "automatically normalized"; parameter 9 is the
/// random seed ("If not zero, the random number generator is reseeded with the specified value"),
/// used here as the seed of the source's random choices (its wavelength).
/// </summary>
public sealed class NscSourceRay : NscSource
{
    public Vector3D Cosines { get; }
    public long Seed { get; }
    public override string TypeName => "Source Ray";
    public override Aabb LocalBounds => new(Vector3D.Zero, Vector3D.Zero);
    public override int Dimensions => 0;
    public override long OwnSeed => Seed;
    public override string Distribution => $"one direction {Cosines}";

    public NscSourceRay(double l, double m, double n, long seed = 0, NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(common, properties)
    {
        var d = new Vector3D(l, m, n);
        double length = d.Length;
        if (!(length > 0) || !double.IsFinite(length))
            throw new ArgumentException($"Source Ray: the direction cosines ({l}, {m}, {n}) must be finite and not all zero.");
        Cosines = d / length;
        Seed = seed;
    }

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        position = Vector3D.Zero;
        direction = Cosines;
    }

    public override string Describe(Func<double, string> length) => $"cosines {Cosines}";
}
