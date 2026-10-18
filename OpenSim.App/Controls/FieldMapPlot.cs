using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenSim.Core.PostProcessing;

namespace OpenSim.App.Controls;

/// <summary>An outline drawn over a <see cref="FieldMapPlot"/>: an axis-aligned rectangle in data units.</summary>
public sealed record MapOutline(double X0, double Y0, double X1, double Y1);

/// <summary>
/// A scalar field sampled on a regular grid over a rectangle, rendered to a bitmap for a panel's
/// <c>Image</c>: a viridis heatmap at equal scale in x and y, the region outlines over it, axis ticks
/// and a colour bar. Logarithmic over a number of decades below the peak, or linear from zero. Must
/// be called on the UI thread.
/// </summary>
public static class FieldMapPlot
{
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
    private static readonly Pen Frame = new(Ink, 1);
    private static readonly Pen OutlinePen = new(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 1);

    /// <param name="values">Samples, [row, column]: row 0 at <paramref name="y0"/>, column 0 at <paramref name="x0"/>.</param>
    /// <param name="scale">Multiplies the data coordinates for the tick labels (1e3 for mm).</param>
    public static ImageSource Render(double[,] values, double x0, double x1, double y0, double y1,
        IReadOnlyList<MapOutline> outlines, bool logarithmic, int decades, string xLabel, string yLabel, string unit,
        double scale = 1e3, int width = 460, int maxHeight = 520)
    {
        int rows = values.GetLength(0), columns = values.GetLength(1);
        double peak = 0;
        foreach (double v in values) if (v > peak && double.IsFinite(v)) peak = v;
        double floor = logarithmic ? peak / Math.Pow(10, Math.Max(1, decades)) : 0;
        double Unit(double v)
        {
            if (!(peak > 0) || !double.IsFinite(v)) return 0;
            if (!logarithmic) return Math.Clamp(v / peak, 0, 1);
            if (v <= floor) return 0;
            return Math.Clamp(Math.Log10(v / floor) / Math.Log10(peak / floor), 0, 1);
        }

        // The raster, one pixel per sample, top row = largest y.
        var pixels = new byte[rows * columns * 4];
        var map = ColormapDefinition.Viridis;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                var (red, green, blue) = map.Sample(Unit(values[r, c]));
                int at = ((rows - 1 - r) * columns + c) * 4;
                pixels[at] = blue; pixels[at + 1] = green; pixels[at + 2] = red; pixels[at + 3] = 255;
            }
        var raster = BitmapSource.Create(columns, rows, 96, 96, PixelFormats.Bgra32, null, pixels, columns * 4);
        raster.Freeze();

        const double left = 46, top = 8, bottom = 30, bar = 14, barGap = 10, barLabels = 64;
        double w = width - left - bar - barGap - barLabels;
        double h = w * (y1 - y0) / (x1 - x0);
        if (h > maxHeight - top - bottom) { h = maxHeight - top - bottom; w = h * (x1 - x0) / (y1 - y0); }
        if (h < 60) h = 60;
        int height = (int)Math.Ceiling(top + h + bottom);
        Point Map(double x, double y) => new(left + (x - x0) / (x1 - x0) * w, top + (y1 - y) / (y1 - y0) * h);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            void Text(string text, double x, double y, bool centre = false, bool rightAlign = false)
            {
                var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Ink, 1.0);
                double at = centre ? x - formatted.Width / 2 : rightAlign ? x - formatted.Width : x;
                dc.DrawText(formatted, new Point(at, y));
            }
            var plot = new Rect(left, top, w, h);
            dc.DrawImage(raster, plot);
            dc.PushClip(new RectangleGeometry(plot));
            foreach (var o in outlines)
            {
                var a = Map(o.X0, o.Y1);
                var b = Map(o.X1, o.Y0);
                dc.DrawRectangle(null, OutlinePen, new Rect(a, b));
            }
            dc.Pop();
            dc.DrawRectangle(null, Frame, plot);
            foreach (double t in Ticks(x0 * scale, x1 * scale))
            {
                var p = Map(t / scale, y0);
                dc.DrawLine(Frame, p, new Point(p.X, p.Y + 3));
                Text(t.ToString("g4", CultureInfo.InvariantCulture), p.X, top + h + 3, centre: true);
            }
            foreach (double t in Ticks(y0 * scale, y1 * scale))
            {
                var p = Map(x0, t / scale);
                dc.DrawLine(Frame, new Point(p.X - 3, p.Y), p);
                Text(t.ToString("g4", CultureInfo.InvariantCulture), left - 5, p.Y - 7, rightAlign: true);
            }
            Text(xLabel, left + w / 2, top + h + 15, centre: true);
            Text(yLabel, 2, top - 6);

            // The colour bar, bottom = the floor, top = the peak.
            double bx = left + w + barGap;
            int steps = 64;
            for (int k = 0; k < steps; k++)
            {
                var (red, green, blue) = map.Sample((k + 0.5) / steps);
                var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
                brush.Freeze();
                double yk = top + h - (k + 1) * h / steps;
                dc.DrawRectangle(brush, null, new Rect(bx, yk, bar, h / steps + 0.5));
            }
            dc.DrawRectangle(null, Frame, new Rect(bx, top, bar, h));
            Text($"{peak:g3} {unit}", bx + bar + 3, top - 2);
            Text(logarithmic ? $"{floor:g3} {unit}" : $"0 {unit}", bx + bar + 3, top + h - 11);
            if (logarithmic) Text($"log, {Math.Max(1, decades)} decades", bx + bar + 3, top + h / 2 - 6);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static IEnumerable<double> Ticks(double from, double to)
    {
        double raw = (to - from) / 5;
        if (!(raw > 0)) yield break;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = (raw / magnitude) switch { < 1.5 => magnitude, < 3.5 => 2 * magnitude, < 7.5 => 5 * magnitude, _ => 10 * magnitude };
        for (double t = Math.Ceiling(from / step) * step; t <= to + 1e-9 * step; t += step) yield return Math.Abs(t) < 1e-12 * step ? 0 : t;
    }
}
