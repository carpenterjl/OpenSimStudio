using System.Globalization;
using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Sources;

/// <summary>
/// Source Radial (help, "Source Radial"): "a flat, rectangular or elliptical shape that emits rays
/// into a hemisphere ... symmetric about the local z axis and ... defined by a cubic spline fit to
/// arbitrary intensity vs. angle data". Parameters 6 X Half Width and 7 Y Half Width ("If less than
/// zero, the emitting region will be elliptical"), 8 Minimum Angle (0–89.9°), 9 Maximum Angle
/// (minimum + 0.1 to 90°), 10 the number of points (5–181), 11+ "the relative intensity measured in
/// the far field of the source at the angle corresponding to that point", the angular range "evenly
/// divided among the specified number of points".
/// <para>
/// Here the spline is the natural cubic spline through the points (the help does not name the end
/// conditions; a reading) and the intensity is per unit solid angle, so the polar angle has density
/// I(θ)·sin θ on [θmin, θmax], sampled by <see cref="NscInverseCdf"/>; φ = 2πb. Where the spline
/// dips below zero it is taken as zero, and <see cref="Notes"/> says so. Start points are uniform
/// over the rectangle or ellipse.
/// </para>
/// </summary>
public sealed class NscSourceRadial : NscSource
{
    private readonly NscInverseCdf _theta;

    public double HalfWidthX { get; }
    public double HalfWidthY { get; }
    public bool Elliptical { get; }
    public double MinAngleDegrees { get; }
    public double MaxAngleDegrees { get; }
    public IReadOnlyList<double> Intensities { get; }

    /// <summary>The fitted relative intensity at θ (degrees).</summary>
    public NaturalCubicSpline Spline { get; }

    public override string TypeName => "Source Radial";
    public override Aabb LocalBounds => new(new Vector3D(-HalfWidthX, -HalfWidthY, 0), new Vector3D(HalfWidthX, HalfWidthY, 0));
    public override int Dimensions => 4;
    public override IReadOnlyList<string> Notes => _theta.Notes;
    public override string Distribution => $"{(Elliptical ? "ellipse" : "rectangle")}, axisymmetric spline intensity over {F(MinAngleDegrees)}°–{F(MaxAngleDegrees)}°";

    /// <param name="halfWidthX">Meters; negative for an elliptical region (its magnitude is the half width).</param>
    /// <param name="halfWidthY">As <paramref name="halfWidthX"/>.</param>
    public NscSourceRadial(double halfWidthX, double halfWidthY, double minAngleDegrees, double maxAngleDegrees,
        IReadOnlyList<double> intensities, NscSourceCommon? common = null, NscSourceProperties? properties = null)
        : base(common, properties)
    {
        ArgumentNullException.ThrowIfNull(intensities);
        Need(halfWidthX, "the X half width (meters)", TypeName);
        Need(halfWidthY, "the Y half width (meters)", TypeName);
        Elliptical = halfWidthX < 0 || halfWidthY < 0;
        HalfWidthX = Math.Abs(halfWidthX);
        HalfWidthY = Math.Abs(halfWidthY);
        MinAngleDegrees = Need(minAngleDegrees, "the minimum angle (degrees)", TypeName, 0, 89.9);
        MaxAngleDegrees = Need(maxAngleDegrees, "the maximum angle (degrees)", TypeName, minAngleDegrees + 0.1, 90);
        int n = intensities.Count;
        if (n < 5 || n > 181)
            throw new ArgumentException($"Source Radial: the number of points must be between 5 and 181 (help, \"Source Radial\"); it is {n}.");
        for (int i = 0; i < n; i++)
            if (!(intensities[i] >= 0) || !double.IsFinite(intensities[i]))
                throw new ArgumentException($"Source Radial: the relative intensity at point {i + 1} is {intensities[i]}; it must be zero or positive.");
        Intensities = intensities.ToArray();
        var angles = new double[n];
        for (int i = 0; i < n; i++) angles[i] = minAngleDegrees + (maxAngleDegrees - minAngleDegrees) * i / (n - 1);
        Spline = new NaturalCubicSpline(angles, Intensities.ToArray());
        _theta = new NscInverseCdf(t => Spline.Evaluate(t) * Math.Sin(t * Math.PI / 180), minAngleDegrees, maxAngleDegrees,
            "Source Radial, intensity × sin θ against θ (degrees)", angles);
    }

    public override void Sample(ReadOnlySpan<double> u, SplitMix64 extra, out Vector3D position, out Vector3D direction)
    {
        var (x, y) = Elliptical ? InEllipse(HalfWidthX, HalfWidthY, u[0], u[1]) : InRectangle(HalfWidthX, HalfWidthY, u[0], u[1]);
        position = new Vector3D(x, y, 0);
        double theta = _theta.Sample(u[2]) * Math.PI / 180;
        direction = FromPolar(Math.Cos(theta), u[3]);
    }

    public override string Describe(Func<double, string> length) =>
        $"half widths {length(HalfWidthX)} × {length(HalfWidthY)}{(Elliptical ? " (elliptical)" : "")}, {Intensities.Count} points over {F(MinAngleDegrees)}°–{F(MaxAngleDegrees)}°";

    private static string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
