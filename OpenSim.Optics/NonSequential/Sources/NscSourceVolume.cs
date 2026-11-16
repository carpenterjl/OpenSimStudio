using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>The three volume sources (help, "Source Volume Cylinder", "Source Volume Ellipse",
/// "Source Volume Rectangle").</summary>
public enum NscVolumeShape
{
    /// <summary>(x/Wx)² + (y/Wy)² ≤ 1 and (z/Wz)² ≤ 1.</summary>
    Cylinder = 0,

    /// <summary>(x/Wx)² + (y/Wy)² + (z/Wz)² ≤ 1.</summary>
    Ellipse = 1,

    /// <summary>|x| ≤ Wx, |y| ≤ Wy, |z| ≤ Wz.</summary>
    Rectangle = 2,
}

/// <summary>
/// Source Volume Cylinder, Source Volume Ellipse and Source Volume Rectangle (help, those pages):
/// "Rays are emitted in random directions from anywhere inside the volume, with uniform
/// probability in both position and ray direction"; parameters 6–8 are the half widths Wx, Wy,
/// Wz, the volume centred on the local origin. Directions are uniform over the whole sphere
/// (<see cref="NscSource.Isotropic"/>). Start points by inversion: the cylinder's cross-section as
/// <see cref="NscSource.InEllipse"/> with z = Wz(2c − 1); the ellipsoid as the unit ball's
/// r = a^(1/3), cos θ = 1 − 2b, φ = 2πc (derived: dV = r²dr·d(cos θ)·dφ) scaled by the half widths;
/// the box coordinate by coordinate.
/// </summary>
public sealed class NscSourceVolume : NscSource
{
    public NscVolumeShape Shape { get; }
    public double HalfWidthX { get; }
    public double HalfWidthY { get; }
    public double HalfWidthZ { get; }

    public override string TypeName => Shape switch
    {
        NscVolumeShape.Cylinder => "Source Volume Cylinder",
        NscVolumeShape.Ellipse => "Source Volume Ellipse",
        _ => "Source Volume Rectangle",
    };

    public override Aabb LocalBounds => new(new Vector3D(-HalfWidthX, -HalfWidthY, -HalfWidthZ), new Vector3D(HalfWidthX, HalfWidthY, HalfWidthZ));
    public override int Dimensions => 5;
    public override string Distribution => $"uniform in the {Shape.ToString().ToLowerInvariant()} volume, isotropic";

    public NscSourceVolume(NscVolumeShape shape, double halfWidthX, double halfWidthY, double halfWidthZ,
        NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(common, properties)
    {
        if (!Enum.IsDefined(shape)) throw new ArgumentException($"Unknown volume source shape {(int)shape}.");
        Shape = shape;
        HalfWidthX = Need(halfWidthX, "the X half width (meters)", TypeName, 0);
        HalfWidthY = Need(halfWidthY, "the Y half width (meters)", TypeName, 0);
        HalfWidthZ = Need(halfWidthZ, "the Z half width (meters)", TypeName, 0);
    }

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        switch (Shape)
        {
            case NscVolumeShape.Cylinder:
            {
                var (x, y) = InEllipse(HalfWidthX, HalfWidthY, u[0], u[1]);
                position = new Vector3D(x, y, HalfWidthZ * (2 * u[2] - 1));
                break;
            }
            case NscVolumeShape.Ellipse:
            {
                double r = Math.Cbrt(u[0]);
                var d = Isotropic(u[1], u[2]);
                position = new Vector3D(HalfWidthX * r * d.X, HalfWidthY * r * d.Y, HalfWidthZ * r * d.Z);
                break;
            }
            default:
                position = new Vector3D(HalfWidthX * (2 * u[0] - 1), HalfWidthY * (2 * u[1] - 1), HalfWidthZ * (2 * u[2] - 1));
                break;
        }
        direction = Isotropic(u[3], u[4]);
    }

    public override string Describe(Func<double, string> length) =>
        $"half widths {length(HalfWidthX)} × {length(HalfWidthY)} × {length(HalfWidthZ)}";
}

/// <summary>
/// Source Tube (help, "Source Tube"): "similar to the Source Volume Cylinder, except the rays only
/// emanate from the surface of the tube"; parameter 6 Length L, 7 Radius R, the axis along z and
/// "The local origin ... at the center of the front face", so the tube spans 0 ≤ z ≤ L. Start
/// points uniform in area over the side wall (z = L·a, φ = 2πb); directions uniform over the whole
/// sphere, as the volume sources' (a reading: the help gives the tube no angular distribution of
/// its own).
/// </summary>
public sealed class NscSourceTube : NscSource
{
    public double Length { get; }
    public double Radius { get; }
    public override string TypeName => "Source Tube";
    public override Aabb LocalBounds => new(new Vector3D(-Radius, -Radius, 0), new Vector3D(Radius, Radius, Length));
    public override int Dimensions => 4;
    public override string Distribution => "uniform over the tube wall, isotropic";

    public NscSourceTube(double length, double radius, NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(common, properties)
    {
        Length = Need(length, "the length L (meters)", TypeName, 0);
        Radius = Need(radius, "the radius R (meters)", TypeName, 0);
    }

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        double phi = 2 * Math.PI * u[1];
        position = new Vector3D(Radius * Math.Cos(phi), Radius * Math.Sin(phi), Length * u[0]);
        direction = Isotropic(u[2], u[3]);
    }

    public override string Describe(Func<double, string> length) => $"length {length(Length)}, radius {length(Radius)}";
}

/// <summary>
/// Source Filament (help, "Source Filament"): "a thin wire coiled in a helix shape. The wire turns
/// "N" times along the full Z coordinate length given by "L". The radius of the turns is defined
/// by "R". Rays emanate from a randomly chosen point along the helix in a random direction";
/// parameters 6 L, 7 R, 8 N ("may be fractional or even negative to reverse the rotation").
/// Here the helix is (R cos 2πNt, R sin 2πNt, L·t) for 0 ≤ t ≤ 1, starting on +x at z = 0 like the
/// tube (the start and the z range are readings). A helix has constant speed in t, so t = a is
/// uniform along the wire; directions are uniform over the whole sphere.
/// </summary>
public sealed class NscSourceFilament : NscSource
{
    public double Length { get; }
    public double Radius { get; }
    public double Turns { get; }
    public override string TypeName => "Source Filament";
    public override Aabb LocalBounds => new(new Vector3D(-Radius, -Radius, 0), new Vector3D(Radius, Radius, Length));
    public override int Dimensions => 3;
    public override string Distribution => "uniform along a helix, isotropic";

    public NscSourceFilament(double length, double radius, double turns, NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(common, properties)
    {
        Length = Need(length, "the length L (meters)", TypeName, 0);
        Radius = Need(radius, "the radius R (meters)", TypeName, 0);
        Turns = Need(turns, "the number of turns N", TypeName);
    }

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        double t = u[0], phi = 2 * Math.PI * Turns * t;
        position = new Vector3D(Radius * Math.Cos(phi), Radius * Math.Sin(phi), Length * t);
        direction = Isotropic(u[1], u[2]);
    }

    public override string Describe(Func<double, string> length) =>
        $"length {length(Length)}, radius {length(Radius)}, {Turns.ToString("g6", CultureInfo.InvariantCulture)} turns";
}
