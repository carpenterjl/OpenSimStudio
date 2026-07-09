namespace OpenSim.Core.Geometry2D;

/// <summary>A 2D point/vector in meters (all PCB geometry is converted to SI on parse).</summary>
public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 a, double s) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>Z-component of the 2D cross product (signed parallelogram area).</summary>
    public static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;
}
