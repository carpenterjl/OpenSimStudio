using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// Source Gaussian (help, "Source Gaussian"): "a Gaussian distribution of rays which appear to
/// emanate from a point source"; parameter 6 Beam Size, "the beam radius at the 1 over e^2 point
/// in intensity", parameter 7 Position, "the distance from the apparent point of divergence of the
/// rays to the source plane location. If zero, the rays are collimated. A positive Position value
/// means that the apparent point of divergence is located in the negative Z direction".
/// <para>
/// Start points in the local XY plane with density ∝ exp(−2r²/w²), a normal distribution of
/// standard deviation σ = w/2 in x and y (derived), untruncated: r = σ√(−2 ln(1 − a)), φ = 2πb
/// (the radial CDF is 1 − exp(−r²/2σ²)). Each ray points away from (0, 0, −Position); for a
/// negative Position the rays converge toward (0, 0, |Position|) (a reading).
/// </para>
/// </summary>
public sealed class NscSourceGaussian : NscSource
{
    public double BeamSize { get; }
    public double Position { get; }
    public override string TypeName => "Source Gaussian";
    public override Aabb LocalBounds => new(new Vector3D(-2 * BeamSize, -2 * BeamSize, 0), new Vector3D(2 * BeamSize, 2 * BeamSize, 0));
    public override int Dimensions => 2;
    public override string Distribution => Position == 0 ? "Gaussian beam, collimated" : "Gaussian beam from an apparent point";

    /// <param name="beamSize">The 1/e² intensity radius, meters, ≥ 0.</param>
    /// <param name="position">Meters.</param>
    public NscSourceGaussian(double beamSize, double position, NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(common, properties)
    {
        BeamSize = Need(beamSize, "the beam size (meters)", TypeName, 0);
        Position = Need(position, "the position (meters)", TypeName);
    }

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        double sigma = BeamSize / 2;
        double r = sigma * Math.Sqrt(-2 * Math.Log(1 - u[0])), phi = 2 * Math.PI * u[1];
        double x = r * Math.Cos(phi), y = r * Math.Sin(phi);
        position = new Vector3D(x, y, 0);
        if (Position == 0) direction = Vector3D.UnitZ;
        else
        {
            double s = Math.Sign(Position);
            direction = new Vector3D(s * x, s * y, s * Position);
        }
    }

    public override string Describe(Func<double, string> length) =>
        $"beam size {length(BeamSize)}, position {length(Position)}";
}

/// <summary>
/// Source Two Angle (help, "Source Two Angle"): a flat rectangle or ellipse in the local XY plane
/// whose start points are uniform over it, emitting into a cone with "distinct maximum half angles
/// for each direction". Parameters 6 X Half Width, 7 Y Half Width, 8 X Half Angle, 9 Y Half Angle
/// (degrees, "Maximum 89 degrees"), 10 Spatial Shape (0 rectangular, 1 elliptical), 11 Angular
/// Shape (0 rectangular, 1 elliptical), 12 Uniform Angle ("0 for uniform irradiance on a distant
/// projected plane, or 1 for uniform in angle space").
/// <para>
/// The help does not define the two angles further. They are read as the projected angles of the
/// Source Diode (θx = atan(l/n), θy = atan(m/n); help, "Source Diode"), so the direction is
/// (tan θx, tan θy, 1) normalised; "uniform in angle space" is uniform in (θx, θy) over
/// |θx| ≤ αx, |θy| ≤ αy (or the ellipse (θx/αx)² + (θy/αy)² ≤ 1); "uniform irradiance on a distant
/// projected plane" is uniform in (tan θx, tan θy) over the same shape with tan αx, tan αy, which
/// is uniform on any plane z = const (readings, Conventions §10 item 68).
/// </para>
/// </summary>
public sealed class NscSourceTwoAngle : NscSource
{
    public double HalfWidthX { get; }
    public double HalfWidthY { get; }
    public double HalfAngleXDegrees { get; }
    public double HalfAngleYDegrees { get; }
    public bool EllipticalShape { get; }
    public bool EllipticalAngles { get; }
    public bool UniformInAngle { get; }

    public override string TypeName => "Source Two Angle";
    public override Aabb LocalBounds => new(new Vector3D(-HalfWidthX, -HalfWidthY, 0), new Vector3D(HalfWidthX, HalfWidthY, 0));
    public override int Dimensions => 4;
    public override string Distribution =>
        $"{(EllipticalShape ? "ellipse" : "rectangle")}, {(EllipticalAngles ? "elliptical" : "rectangular")} cone uniform {(UniformInAngle ? "in angle" : "on a distant plane")}";

    public NscSourceTwoAngle(double halfWidthX, double halfWidthY, double halfAngleXDegrees, double halfAngleYDegrees,
        bool ellipticalShape, bool ellipticalAngles, bool uniformInAngle, NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(common, properties)
    {
        HalfWidthX = Need(halfWidthX, "the X half width (meters)", TypeName, 0);
        HalfWidthY = Need(halfWidthY, "the Y half width (meters)", TypeName, 0);
        HalfAngleXDegrees = Need(halfAngleXDegrees, "the X half angle (degrees)", TypeName, 0, 89);
        HalfAngleYDegrees = Need(halfAngleYDegrees, "the Y half angle (degrees)", TypeName, 0, 89);
        EllipticalShape = ellipticalShape;
        EllipticalAngles = ellipticalAngles;
        UniformInAngle = uniformInAngle;
    }

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        var (x, y) = EllipticalShape ? InEllipse(HalfWidthX, HalfWidthY, u[0], u[1]) : InRectangle(HalfWidthX, HalfWidthY, u[0], u[1]);
        position = new Vector3D(x, y, 0);
        double ax = HalfAngleXDegrees * Math.PI / 180, ay = HalfAngleYDegrees * Math.PI / 180;
        double sx = UniformInAngle ? ax : Math.Tan(ax), sy = UniformInAngle ? ay : Math.Tan(ay);
        var (p, q) = EllipticalAngles ? InEllipse(sx, sy, u[2], u[3]) : InRectangle(sx, sy, u[2], u[3]);
        direction = UniformInAngle ? new Vector3D(Math.Tan(p), Math.Tan(q), 1) : new Vector3D(p, q, 1);
    }

    public override string Describe(Func<double, string> length) =>
        $"half widths {length(HalfWidthX)} × {length(HalfWidthY)}, half angles {HalfAngleXDegrees.ToString("g6", CultureInfo.InvariantCulture)}° × {HalfAngleYDegrees.ToString("g6", CultureInfo.InvariantCulture)}°";
}
