using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenSim.App.Controls;

/// <summary>
/// A Smith chart with a reflection-coefficient trace, rendered to a bitmap. Must be called
/// on the UI thread.
/// </summary>
public static class SmithPlot
{
    public static ImageSource Render(IReadOnlyList<System.Numerics.Complex> reflection, int marked, int size = 300)
    {
        var ink = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x8A, 0x90, 0x99)), 1);
        var frame = new Pen(ink, 1.2);
        var trace = new Pen(new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFD)), 1.6);
        double radius = size / 2.0 - 8, cx = size / 2.0, cy = size / 2.0;
        Point Map(double re, double im) => new(cx + re * radius, cy - im * radius);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new EllipseGeometry(new Point(cx, cy), radius, radius));
            // Constant resistance: centre r/(1 + r), radius 1/(1 + r).
            foreach (double r in new[] { 0.2, 0.5, 1, 2, 5 })
                dc.DrawEllipse(null, grid, Map(r / (1 + r), 0), radius / (1 + r), radius / (1 + r));
            // Constant reactance: centre (1, 1/x), radius 1/|x|.
            foreach (double x in new[] { 0.2, 0.5, 1, 2, 5 })
                foreach (double sign in new[] { 1.0, -1.0 })
                    dc.DrawEllipse(null, grid, Map(1, sign / x), radius / x, radius / x);
            dc.DrawLine(grid, Map(-1, 0), Map(1, 0));
            dc.Pop();
            dc.DrawEllipse(null, frame, new Point(cx, cy), radius, radius);

            if (reflection.Count > 0)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(Map(reflection[0].Real, reflection[0].Imaginary), false, false);
                    for (int i = 1; i < reflection.Count; i++)
                        g.LineTo(Map(reflection[i].Real, reflection[i].Imaginary), true, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, trace, geometry);
                // The low-frequency end as a ring, the marked point (best match) as a dot.
                dc.DrawEllipse(null, trace, Map(reflection[0].Real, reflection[0].Imaginary), 3.5, 3.5);
                if (marked >= 0 && marked < reflection.Count)
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B)), null,
                        Map(reflection[marked].Real, reflection[marked].Imaginary), 3.5, 3.5);
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>One curve of a <see cref="CurvePlot"/>.</summary>
public sealed record PlotSeries(string Name, IReadOnlyList<double> X, IReadOnlyList<double> Y, Color Color,
    bool Dashed = false);

/// <summary>
/// A small line plot rendered to a bitmap for a panel's <c>Image</c>: curves over linear or
/// logarithmic axes with a grid at the decades (or at round steps), tick labels and a
/// legend. Must be called on the UI thread.
/// </summary>
public static class CurvePlot
{
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
    private static readonly Pen Grid = new(new SolidColorBrush(Color.FromArgb(0x50, 0x8A, 0x90, 0x99)), 1);
    private static readonly Pen Frame = new(Ink, 1);

    public static ImageSource Render(IReadOnlyList<PlotSeries> series, bool logX, bool logY,
        string xLabel, string yLabel, int width = 560, int height = 300)
    {
        double X(double v) => logX ? Math.Log10(Math.Max(v, 1e-300)) : v;
        double Y(double v) => logY ? Math.Log10(Math.Max(v, 1e-300)) : v;
        double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
        foreach (var s in series)
            for (int i = 0; i < s.X.Count; i++)
            {
                if (double.IsNaN(s.Y[i]) || double.IsInfinity(s.Y[i]) || (logY && s.Y[i] <= 0)) continue;
                x0 = Math.Min(x0, X(s.X[i])); x1 = Math.Max(x1, X(s.X[i]));
                y0 = Math.Min(y0, Y(s.Y[i])); y1 = Math.Max(y1, Y(s.Y[i]));
            }
        if (!(x1 > x0)) { x0 -= 0.5; x1 += 0.5; }
        if (!(y1 > y0)) { y0 -= 0.5; y1 += 0.5; }
        if (logY) { y0 = Math.Floor(y0); y1 = Math.Ceiling(y1); }
        else { double pad = 0.05 * (y1 - y0); y0 -= pad; y1 += pad; }

        const double left = 52, right = 10, top = 8, bottom = 30;
        double w = width - left - right, h = height - top - bottom;
        Point Map(double x, double y) => new(left + (x - x0) / (x1 - x0) * w, top + (y1 - y) / (y1 - y0) * h);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            void Text(string text, double x, double y, bool centre = false, bool rightAlign = false)
            {
                var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    Face, 10, Ink, 1.0);
                double at = centre ? x - formatted.Width / 2 : rightAlign ? x - formatted.Width : x;
                dc.DrawText(formatted, new Point(at, y));
            }

            foreach (double tick in Ticks(x0, x1, logX))
            {
                var p = Map(tick, y0);
                dc.DrawLine(Grid, new Point(p.X, top), new Point(p.X, top + h));
                Text(Label(tick, logX), p.X, top + h + 3, centre: true);
            }
            foreach (double tick in Ticks(y0, y1, logY))
            {
                var p = Map(x0, tick);
                dc.DrawLine(Grid, new Point(left, p.Y), new Point(left + w, p.Y));
                Text(Label(tick, logY), left - 4, p.Y - 7, rightAlign: true);
            }
            dc.DrawRectangle(null, Frame, new Rect(left, top, w, h));
            Text(xLabel, left + w / 2, height - 14, centre: true);
            Text(yLabel, 2, top - 6);

            dc.PushClip(new RectangleGeometry(new Rect(left, top, w, h)));
            foreach (var s in series)
            {
                var pen = new Pen(new SolidColorBrush(s.Color), 1.5);
                if (s.Dashed) pen.DashStyle = DashStyles.Dash;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    bool open = false;
                    for (int i = 0; i < s.X.Count; i++)
                    {
                        bool valid = !double.IsNaN(s.Y[i]) && !double.IsInfinity(s.Y[i]) && (!logY || s.Y[i] > 0);
                        if (!valid) { open = false; continue; }
                        var p = Map(X(s.X[i]), Y(s.Y[i]));
                        if (!open) { g.BeginFigure(p, false, false); open = true; }
                        else g.LineTo(p, true, false);
                    }
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
            dc.Pop();

            double legendY = top + 4;
            foreach (var s in series)
            {
                var pen = new Pen(new SolidColorBrush(s.Color), 1.5);
                if (s.Dashed) pen.DashStyle = DashStyles.Dash;
                dc.DrawLine(pen, new Point(left + w - 118, legendY + 7), new Point(left + w - 98, legendY + 7));
                Text(s.Name, left + w - 94, legendY);
                legendY += 13;
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static IEnumerable<double> Ticks(double from, double to, bool log)
    {
        if (log)
        {
            for (double d = Math.Ceiling(from - 1e-9); d <= to + 1e-9; d++) yield return d;
            yield break;
        }
        double raw = (to - from) / 6;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = (raw / magnitude) switch { < 1.5 => magnitude, < 3.5 => 2 * magnitude, < 7.5 => 5 * magnitude, _ => 10 * magnitude };
        for (double t = Math.Ceiling(from / step) * step; t <= to + 1e-9 * step; t += step) yield return t;
    }

    private static string Label(double tick, bool log)
    {
        if (!log) return tick.ToString("g4", CultureInfo.InvariantCulture);
        double value = Math.Pow(10, tick);
        return value switch
        {
            >= 1e9 => $"{value / 1e9:g3}G",
            >= 1e6 => $"{value / 1e6:g3}M",
            >= 1e3 => $"{value / 1e3:g3}k",
            >= 1 => $"{value:g3}",
            >= 1e-3 => $"{value * 1e3:g3}m",
            _ => $"{value * 1e6:g3}µ"
        };
    }
}
