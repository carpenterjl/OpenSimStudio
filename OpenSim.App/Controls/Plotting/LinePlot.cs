using System.Windows;
using System.Windows.Media;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;

namespace OpenSim.App.Controls.Plotting;

/// <summary>
/// The interactive counterpart of <see cref="CurvePlot"/>: the same <see cref="PlotSeries"/>
/// curves over linear or logarithmic axes, with the readout, zoom and export every interactive
/// plot has. Ray fans, MTF and field-curvature curves use it.
/// </summary>
public sealed class LinePlot : InteractivePlot
{
    public static readonly DependencyProperty SeriesProperty =
        Register<LinePlot>(nameof(Series), typeof(IReadOnlyList<PlotSeries>), Array.Empty<PlotSeries>());
    public static readonly DependencyProperty LogXAxisProperty = Register<LinePlot>(nameof(LogXAxis), typeof(bool), false);
    public static readonly DependencyProperty LogYAxisProperty = Register<LinePlot>(nameof(LogYAxis), typeof(bool), false);

    public IReadOnlyList<PlotSeries> Series { get => (IReadOnlyList<PlotSeries>)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public bool LogXAxis { get => (bool)GetValue(LogXAxisProperty); set => SetValue(LogXAxisProperty, value); }
    public bool LogYAxis { get => (bool)GetValue(LogYAxisProperty); set => SetValue(LogYAxisProperty, value); }

    protected override bool LogX => LogXAxis;
    protected override bool LogY => LogYAxis;

    private IEnumerable<Point2> Points(PlotSeries s)
    {
        int n = Math.Min(s.X.Count, s.Y.Count);
        for (int i = 0; i < n; i++) yield return new Point2(s.X[i], s.Y[i]);
    }

    private bool Plottable(Point2 p) =>
        double.IsFinite(p.X) && double.IsFinite(p.Y) && (!LogXAxis || p.X > 0) && (!LogYAxis || p.Y > 0);

    protected override PlotRange? AutoRange()
    {
        if (PlotRange.Bounds(Series.SelectMany(Points), LogXAxis, LogYAxis) is not { } b) return null;
        double x0 = b.X0, x1 = b.X1, y0 = b.Y0, y1 = b.Y1;
        if (LogXAxis) { if (!(x1 > x0)) { x0 /= 10; x1 *= 10; } }
        else if (!(x1 > x0)) { x0 -= 0.5; x1 += 0.5; }
        if (LogYAxis) { y0 = Math.Pow(10, Math.Floor(Math.Log10(y0))); y1 = Math.Pow(10, Math.Ceiling(Math.Log10(y1))); if (!(y1 > y0)) y1 = y0 * 10; }
        else
        {
            if (!(y1 > y0)) { y0 -= 0.5; y1 += 0.5; }
            double pad = 0.05 * (y1 - y0);
            y0 -= pad; y1 += pad;
        }
        return new PlotRange(x0, x1, y0, y1);
    }

    protected override void RenderData(DrawingContext dc, PlotFrame frame, PlotPalette palette)
    {
        foreach (var s in Series)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                bool open = false;
                foreach (var p in Points(s))
                {
                    if (!Plottable(p)) { open = false; continue; }
                    var q = frame.ToPixel(p);
                    if (!open) { g.BeginFigure(q, false, false); open = true; }
                    else g.LineTo(q, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, SeriesPen(s), geometry);
        }
    }

    private static Pen SeriesPen(PlotSeries s)
    {
        var pen = new Pen(new SolidColorBrush(s.Color), 1.5);
        if (s.Dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }

    protected override void RenderDecorations(DrawingContext dc, PlotFrame frame, PlotPalette palette, Size size)
    {
        if (Series.Count < 2 && (Series.Count == 0 || string.IsNullOrEmpty(Series[0].Name))) return;
        double y = frame.Area.Top + 4, x = frame.Area.Right - 124;
        foreach (var s in Series)
        {
            dc.DrawLine(SeriesPen(s), new Point(x, y + 7), new Point(x + 20, y + 7));
            PlotDrawing.Text(dc, s.Name, x + 24, y, palette.Ink, PixelsPerDip);
            y += 13;
        }
    }

    protected override PlotReadout? FindReadout(Point pixel, PlotFrame frame)
    {
        var pixels = Series.Select(s => (IReadOnlyList<Point2>)Points(s)
            .Select(p => Plottable(p) ? ToPixel2(frame, p) : new Point2(double.NaN, double.NaN)).ToArray()).ToArray();
        if (frame.Contains(pixel) && ReadoutLookup.Nearest(pixels, new Point2(pixel.X, pixel.Y), 24) is { } hit)
        {
            var s = Series[hit.Series];
            var q = pixels[hit.Series][hit.Index];
            string head = string.IsNullOrEmpty(s.Name) ? "" : s.Name + "\n";
            return new PlotReadout(new Point(q.X, q.Y),
                $"{head}{AxisName(XLabel, "x")} = {AxisTicks.Readout(s.X[hit.Index])}\n{AxisName(YLabel, "y")} = {AxisTicks.Readout(s.Y[hit.Index])}");
        }
        return CursorReadout(pixel, frame);
    }

    public override IReadOnlyList<PlotTable> Tables => new[]
    {
        new PlotTable("curves", () => PlotCsv.Points(AxisName(XLabel, "x"), AxisName(YLabel, "y"),
            Series.Select(s => (s.Name, (IReadOnlyList<Point2>)Points(s).ToArray()))))
    };
}
