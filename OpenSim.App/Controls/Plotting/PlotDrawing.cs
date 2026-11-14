using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;

namespace OpenSim.App.Controls.Plotting;

/// <summary>How a text anchor relates to the text.</summary>
public enum TextAnchor { Left, Centre, Right }

/// <summary>The marker drawn for each point of a scatter series.</summary>
public enum PlotMarker { Dot, Circle, Plus, Cross, Square, Diamond, Triangle }

/// <summary>Drawing helpers shared by the interactive plots: text, markers, axes and colour bars.</summary>
public static class PlotDrawing
{
    private static readonly Typeface Face = new("Segoe UI");

    public static FormattedText Format(string text, Brush brush, double pixelsPerDip, double size = 10) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, pixelsPerDip);

    /// <summary>Draws text with its top at <paramref name="y"/>, anchored horizontally at <paramref name="x"/>.</summary>
    public static Size Text(DrawingContext dc, string text, double x, double y, Brush brush, double pixelsPerDip,
        TextAnchor anchor = TextAnchor.Left, double size = 10)
    {
        var formatted = Format(text, brush, pixelsPerDip, size);
        double at = anchor switch
        {
            TextAnchor.Centre => x - formatted.Width / 2,
            TextAnchor.Right => x - formatted.Width,
            _ => x
        };
        dc.DrawText(formatted, new Point(at, y));
        return new Size(formatted.Width, formatted.Height);
    }

    /// <summary>Whether a marker is drawn filled (with a brush) rather than stroked.</summary>
    public static bool IsFilled(PlotMarker marker) =>
        marker is PlotMarker.Dot or PlotMarker.Square or PlotMarker.Diamond or PlotMarker.Triangle;

    /// <summary>Adds one marker of half-size <paramref name="r"/> centred on <paramref name="p"/>.
    /// Filled shapes are closed figures; stroked ones are open strokes.</summary>
    public static void AddMarker(StreamGeometryContext g, Point p, PlotMarker marker, double r)
    {
        switch (marker)
        {
            case PlotMarker.Dot:
            case PlotMarker.Circle:
                g.BeginFigure(new Point(p.X + r, p.Y), marker == PlotMarker.Dot, true);
                g.ArcTo(new Point(p.X - r, p.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                g.ArcTo(new Point(p.X + r, p.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                break;
            case PlotMarker.Plus:
                g.BeginFigure(new Point(p.X - r, p.Y), false, false);
                g.LineTo(new Point(p.X + r, p.Y), true, false);
                g.BeginFigure(new Point(p.X, p.Y - r), false, false);
                g.LineTo(new Point(p.X, p.Y + r), true, false);
                break;
            case PlotMarker.Cross:
                double d = r * 0.8;
                g.BeginFigure(new Point(p.X - d, p.Y - d), false, false);
                g.LineTo(new Point(p.X + d, p.Y + d), true, false);
                g.BeginFigure(new Point(p.X - d, p.Y + d), false, false);
                g.LineTo(new Point(p.X + d, p.Y - d), true, false);
                break;
            case PlotMarker.Square:
                double q = r * 0.85;
                g.BeginFigure(new Point(p.X - q, p.Y - q), true, true);
                g.LineTo(new Point(p.X + q, p.Y - q), false, false);
                g.LineTo(new Point(p.X + q, p.Y + q), false, false);
                g.LineTo(new Point(p.X - q, p.Y + q), false, false);
                break;
            case PlotMarker.Diamond:
                g.BeginFigure(new Point(p.X, p.Y - r), true, true);
                g.LineTo(new Point(p.X + r, p.Y), false, false);
                g.LineTo(new Point(p.X, p.Y + r), false, false);
                g.LineTo(new Point(p.X - r, p.Y), false, false);
                break;
            case PlotMarker.Triangle:
                g.BeginFigure(new Point(p.X, p.Y - r), true, true);
                g.LineTo(new Point(p.X + r * 0.866, p.Y + r * 0.5), false, false);
                g.LineTo(new Point(p.X - r * 0.866, p.Y + r * 0.5), false, false);
                break;
        }
    }

    /// <summary>Grid lines and tick labels for a Cartesian frame, ticked as the static plots tick.</summary>
    public static void CartesianAxes(DrawingContext dc, PlotFrame frame, PlotPalette palette, double pixelsPerDip)
    {
        var area = frame.Area;
        var xTicks = frame.X.Log ? AxisTicks.Decades(frame.X.Min, frame.X.Max) : AxisTicks.Linear(frame.X.Min, frame.X.Max, Math.Max(2, (int)(area.Width / 80)));
        double xStep = xTicks.Count > 1 ? xTicks[1] - xTicks[0] : double.NaN;
        foreach (double t in xTicks)
        {
            double px = frame.X.ToPixel(t);
            dc.DrawLine(palette.Grid, new Point(px, area.Top), new Point(px, area.Bottom));
            Text(dc, frame.X.Log ? LogLabel(t) : AxisTicks.Format(t, xStep), px, area.Bottom + 3, palette.Ink, pixelsPerDip, TextAnchor.Centre);
        }
        var yTicks = frame.Y.Log ? AxisTicks.Decades(frame.Y.Min, frame.Y.Max) : AxisTicks.Linear(frame.Y.Min, frame.Y.Max, Math.Max(2, (int)(area.Height / 50)));
        double yStep = yTicks.Count > 1 ? yTicks[1] - yTicks[0] : double.NaN;
        foreach (double t in yTicks)
        {
            double py = frame.Y.ToPixel(t);
            dc.DrawLine(palette.Grid, new Point(area.Left, py), new Point(area.Right, py));
            Text(dc, frame.Y.Log ? LogLabel(t) : AxisTicks.Format(t, yStep), area.Left - 4, py - 7, palette.Ink, pixelsPerDip, TextAnchor.Right);
        }
        dc.DrawRectangle(null, palette.Frame, area);
    }

    /// <summary>A decade label with an SI prefix, as <see cref="CurvePlot"/> labels its log axes.</summary>
    public static string LogLabel(double value) => value switch
    {
        >= 1e9 => string.Create(CultureInfo.InvariantCulture, $"{value / 1e9:g3}G"),
        >= 1e6 => string.Create(CultureInfo.InvariantCulture, $"{value / 1e6:g3}M"),
        >= 1e3 => string.Create(CultureInfo.InvariantCulture, $"{value / 1e3:g3}k"),
        >= 1 => string.Create(CultureInfo.InvariantCulture, $"{value:g3}"),
        >= 1e-3 => string.Create(CultureInfo.InvariantCulture, $"{value * 1e3:g3}m"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value * 1e6:g3}µ")
    };

    /// <summary>
    /// A vertical colour bar over [<paramref name="min"/>, <paramref name="max"/>]: continuous when
    /// <paramref name="edges"/> is null, otherwise one flat block per band coloured at the band's
    /// middle (exactly as the filled contours are coloured), with the edges labelled.
    /// </summary>
    public static void ColorBar(DrawingContext dc, Rect bar, ColormapDefinition map, double min, double max,
        IReadOnlyList<double>? edges, string unit, PlotPalette palette, double pixelsPerDip)
    {
        if (!(max > min) || bar.Height <= 0) return;
        double Y(double v) => bar.Bottom - (v - min) / (max - min) * bar.Height;
        if (edges is null)
        {
            const int steps = 64;
            for (int k = 0; k < steps; k++)
            {
                var brush = Brush(map, (k + 0.5) / steps);
                dc.DrawRectangle(brush, null, new Rect(bar.Left, bar.Bottom - (k + 1) * bar.Height / steps, bar.Width, bar.Height / steps + 0.5));
            }
            var ticks = AxisTicks.Linear(min, max, Math.Max(2, (int)(bar.Height / 40)));
            double step = ticks.Count > 1 ? ticks[1] - ticks[0] : double.NaN;
            foreach (double t in ticks)
                Text(dc, AxisTicks.Format(t, step), bar.Right + 3, Y(t) - 7, palette.Ink, pixelsPerDip);
        }
        else
        {
            for (int k = 0; k + 1 < edges.Count; k++)
            {
                double mid = 0.5 * (edges[k] + edges[k + 1]);
                double top = Y(edges[k + 1]), bottom = Y(edges[k]);
                dc.DrawRectangle(Brush(map, (mid - min) / (max - min)), null, new Rect(bar.Left, top, bar.Width, Math.Max(0, bottom - top) + 0.5));
            }
            // Label every edge that has room (at least 11 px from the last label drawn).
            double lastY = double.PositiveInfinity;
            double step = edges.Count > 2 ? edges[2] - edges[1] : max - min;
            foreach (double e in edges)
            {
                double y = Y(e);
                if (lastY - y < 11 && e != edges[^1]) continue;
                Text(dc, AxisTicks.Format(e, step), bar.Right + 3, y - 7, palette.Ink, pixelsPerDip);
                lastY = y;
            }
        }
        dc.DrawRectangle(null, palette.Frame, bar);
        if (unit.Length > 0) Text(dc, unit, bar.Left, bar.Top - 14, palette.Ink, pixelsPerDip);
    }

    /// <summary>A frozen brush of the colormap at <paramref name="u"/> (0..1).</summary>
    public static SolidColorBrush Brush(ColormapDefinition map, double u)
    {
        var (r, g, b) = map.Sample(u);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
