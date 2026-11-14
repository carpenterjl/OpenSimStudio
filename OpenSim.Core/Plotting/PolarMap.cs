using OpenSim.Core.Geometry2D;

namespace OpenSim.Core.Plotting;

/// <summary>Where a polar plot puts angle zero.</summary>
public enum PolarZero
{
    /// <summary>Zero on the right, the mathematical convention.</summary>
    Right,
    /// <summary>Zero at the top, the compass and detector-plot convention.</summary>
    Top
}

/// <summary>
/// The mapping of a polar plot: (angle, radius) to a point in the unit disc, radius
/// <see cref="RMin"/> at the centre and <see cref="RMax"/> on the unit circle. Radii below RMin
/// clamp to the centre (a decibel plot with a floor), radii above RMax fall outside the disc and
/// are clipped by the plot. Angles are radians.
/// </summary>
public readonly record struct PolarMap(double RMin, double RMax, PolarZero Zero = PolarZero.Right, bool Clockwise = false)
{
    private double Offset => Zero == PolarZero.Right ? 0 : Math.PI / 2;
    private double Sign => Clockwise ? -1 : 1;

    /// <summary>Distance from the centre, in unit-disc radii, of radius <paramref name="r"/>.</summary>
    public double Rho(double r) => Math.Max(0, (r - RMin) / (RMax - RMin));

    /// <summary>The unit-disc point (y up) of angle <paramref name="theta"/> and radius <paramref name="r"/>.</summary>
    public Point2 ToCartesian(double theta, double r)
    {
        double rho = Rho(r), phi = Offset + Sign * theta;
        return new Point2(rho * Math.Cos(phi), rho * Math.Sin(phi));
    }

    /// <summary>The (angle in (-π, π], radius) of a unit-disc point: the inverse of
    /// <see cref="ToCartesian"/> for radii at or above RMin.</summary>
    public (double Theta, double R) FromCartesian(Point2 p)
    {
        double theta = Sign * (Math.Atan2(p.Y, p.X) - Offset);
        theta = Math.IEEERemainder(theta, 2 * Math.PI);
        if (theta <= -Math.PI) theta += 2 * Math.PI;
        return (theta, RMin + p.Length * (RMax - RMin));
    }
}
