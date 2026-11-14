using OpenSim.Core.Geometry2D;

namespace OpenSim.Optics.Layout;

/// <summary>An axis-aligned rectangle of the drawing plane, meters. <see cref="Empty"/> holds
/// nothing; including a point into it gives that point.</summary>
public readonly record struct LayoutBounds2D(double MinX, double MinY, double MaxX, double MaxY)
{
    public static LayoutBounds2D Empty { get; } =
        new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => !(MaxX >= MinX && MaxY >= MinY);
    public double Width => IsEmpty ? 0 : MaxX - MinX;
    public double Height => IsEmpty ? 0 : MaxY - MinY;

    public LayoutBounds2D Including(Point2 p) =>
        new(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));
}
