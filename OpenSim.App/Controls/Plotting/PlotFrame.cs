using System.Windows;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;

namespace OpenSim.App.Controls.Plotting;

/// <summary>A data-space rectangle, x from <see cref="X0"/> to <see cref="X1"/>, y from
/// <see cref="Y0"/> to <see cref="Y1"/> (WPF's <see cref="Rect"/> cannot hold the empty and
/// log-axis cases cleanly).</summary>
public readonly record struct PlotRange(double X0, double X1, double Y0, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;

    /// <summary>The range grown by <paramref name="fraction"/> of its size on every side; a
    /// zero-width side is widened to <paramref name="minimumHalfWidth"/> about its centre first so
    /// a single point or a flat line still gets a visible axis.</summary>
    public PlotRange Padded(double fraction, double minimumHalfWidth = 0.5)
    {
        double x0 = X0, x1 = X1, y0 = Y0, y1 = Y1;
        if (!(x1 > x0)) { double c = double.IsFinite(x0) ? x0 : 0; x0 = c - minimumHalfWidth; x1 = c + minimumHalfWidth; }
        if (!(y1 > y0)) { double c = double.IsFinite(y0) ? y0 : 0; y0 = c - minimumHalfWidth; y1 = c + minimumHalfWidth; }
        double px = fraction * (x1 - x0), py = fraction * (y1 - y0);
        return new PlotRange(x0 - px, x1 + px, y0 - py, y1 + py);
    }

    /// <summary>The bounding range of points (non-finite skipped); null when none is finite.</summary>
    public static PlotRange? Bounds(IEnumerable<Point2> points, bool positiveX = false, bool positiveY = false)
    {
        double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y0 = double.PositiveInfinity, y1 = double.NegativeInfinity;
        foreach (var p in points)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
            if ((positiveX && p.X <= 0) || (positiveY && p.Y <= 0)) continue;
            x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
            y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
        }
        return x0 <= x1 ? new PlotRange(x0, x1, y0, y1) : null;
    }

    public PlotRange Union(PlotRange other) =>
        new(Math.Min(X0, other.X0), Math.Max(X1, other.X1), Math.Min(Y0, other.Y0), Math.Max(Y1, other.Y1));
}

/// <summary>
/// The pixel rectangle a plot draws its data in and the two axes that map data onto it. With
/// equal aspect the shorter data side is widened about its centre until one data unit is the
/// same number of pixels in x and y (a spot diagram or a pupil map must not be squashed).
/// </summary>
public sealed record PlotFrame(Rect Area, AxisMap X, AxisMap Y)
{
    public static PlotFrame Create(Rect area, PlotRange range, bool equalAspect, bool logX = false, bool logY = false)
    {
        double x0 = range.X0, x1 = range.X1, y0 = range.Y0, y1 = range.Y1;
        if (equalAspect && !logX && !logY && area.Width > 0 && area.Height > 0)
        {
            double perPixel = Math.Max((x1 - x0) / area.Width, (y1 - y0) / area.Height);
            double cx = 0.5 * (x0 + x1), cy = 0.5 * (y0 + y1);
            x0 = cx - 0.5 * perPixel * area.Width; x1 = cx + 0.5 * perPixel * area.Width;
            y0 = cy - 0.5 * perPixel * area.Height; y1 = cy + 0.5 * perPixel * area.Height;
        }
        return new PlotFrame(area,
            new AxisMap(x0, x1, area.Left, area.Right, logX),
            new AxisMap(y0, y1, area.Bottom, area.Top, logY));
    }

    /// <summary>The data range the frame shows (after any equal-aspect widening).</summary>
    public PlotRange Range => new(X.Min, X.Max, Y.Min, Y.Max);

    public Point ToPixel(double x, double y) => new(X.ToPixel(x), Y.ToPixel(y));
    public Point ToPixel(Point2 p) => ToPixel(p.X, p.Y);
    public Point2 ToData(Point pixel) => new(X.ToData(pixel.X), Y.ToData(pixel.Y));
    public bool Contains(Point pixel) => Area.Contains(pixel);
}
