using OpenSim.Core.Geometry2D;

namespace OpenSim.Optics.Layout;

/// <summary>
/// The map from the drawing plane (meters, y up) to pixels (y down) for the cross-section:
/// one scale on both axes, so a sphere stays round. pixel x = OffsetX + Scale·X,
/// pixel y = OffsetY − Scale·Y.
/// </summary>
public readonly record struct LayoutViewport2D(double Scale, double OffsetX, double OffsetY)
{
    /// <summary>The largest equal-axis scale that fits the bounds into a width × height pixel
    /// area less a margin on every side, the drawing centred in it.</summary>
    public static LayoutViewport2D Fit(LayoutBounds2D bounds, double widthPx, double heightPx, double marginPx = 8)
    {
        if (!(widthPx > 2 * marginPx) || !(heightPx > 2 * marginPx))
            throw new ArgumentOutOfRangeException(nameof(widthPx), "The pixel area must be larger than twice the margin.");
        if (bounds.IsEmpty) return new LayoutViewport2D(1, widthPx / 2, heightPx / 2);
        double w = Math.Max(bounds.Width, 1e-12), h = Math.Max(bounds.Height, 1e-12);
        double scale = Math.Min((widthPx - 2 * marginPx) / w, (heightPx - 2 * marginPx) / h);
        double cx = 0.5 * (bounds.MinX + bounds.MaxX), cy = 0.5 * (bounds.MinY + bounds.MaxY);
        return new LayoutViewport2D(scale, widthPx / 2 - scale * cx, heightPx / 2 + scale * cy);
    }

    public Point2 ToPixel(Point2 p) => new(OffsetX + Scale * p.X, OffsetY - Scale * p.Y);

    public Point2 FromPixel(Point2 px) => new((px.X - OffsetX) / Scale, (OffsetY - px.Y) / Scale);
}
