using System.Windows;
using System.Windows.Media;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;

namespace OpenSim.App.Controls.Plotting;

/// <summary>One series of a <see cref="ScatterPlot"/>: points drawn with one marker and colour
/// (a spot diagram draws one series per wavelength).</summary>
/// <param name="Size">Marker half-size in pixels.</param>
/// <param name="Labels">A name per point for the readout (a glass on a glass map), or null.</param>
public sealed record ScatterSeries(string Name, IReadOnlyList<Point2> Points, Color Color,
    PlotMarker Marker = PlotMarker.Dot, double Size = 2.5, IReadOnlyList<string>? Labels = null);

/// <summary>A reference circle drawn over a scatter plot, in data units (the Airy disc of a spot
/// diagram, a field stop).</summary>
public sealed record PlotCircle(double X, double Y, double Radius, string Label, Color Color, bool Dashed = false);

/// <summary>
/// Points with per-series markers, reference circles and a scale bar, at equal scale in x and y
/// by default: the spot diagram plot. The scale bar is the longest round length that fits a
/// quarter of the width (<see cref="ScaleBar.Length"/>), in <see cref="Unit"/>.
/// </summary>
public sealed class ScatterPlot : InteractivePlot
{
    public static readonly DependencyProperty SeriesProperty =
        Register<ScatterPlot>(nameof(Series), typeof(IReadOnlyList<ScatterSeries>), Array.Empty<ScatterSeries>());
    public static readonly DependencyProperty CirclesProperty =
        Register<ScatterPlot>(nameof(Circles), typeof(IReadOnlyList<PlotCircle>), Array.Empty<PlotCircle>());
    public static readonly DependencyProperty EqualAspectProperty = Register<ScatterPlot>(nameof(EqualAspect), typeof(bool), true);
    public static readonly DependencyProperty ShowScaleBarProperty = Register<ScatterPlot>(nameof(ShowScaleBar), typeof(bool), true);
    public static readonly DependencyProperty ShowAxesProperty = Register<ScatterPlot>(nameof(ShowAxes), typeof(bool), true);
    public static readonly DependencyProperty UnitProperty = Register<ScatterPlot>(nameof(Unit), typeof(string), "");

    public IReadOnlyList<ScatterSeries> Series { get => (IReadOnlyList<ScatterSeries>)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<PlotCircle> Circles { get => (IReadOnlyList<PlotCircle>)GetValue(CirclesProperty); set => SetValue(CirclesProperty, value); }
    public bool EqualAspect { get => (bool)GetValue(EqualAspectProperty); set => SetValue(EqualAspectProperty, value); }
    public bool ShowScaleBar { get => (bool)GetValue(ShowScaleBarProperty); set => SetValue(ShowScaleBarProperty, value); }
    /// <summary>Tick labels and grid; a spot diagram often shows only the scale bar.</summary>
    public bool ShowAxes { get => (bool)GetValue(ShowAxesProperty); set => SetValue(ShowAxesProperty, value); }
    /// <summary>The data unit, for the scale bar and readout ("µm").</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    protected override bool UseEqualAspect => EqualAspect;
    protected override bool DrawsCartesianAxes => ShowAxes;
    protected override Thickness Margins => ShowAxes ? base.Margins : new Thickness(8, string.IsNullOrEmpty(Title) ? 8 : 24, 8, 8);

    protected override bool ResetsViewOnChange(DependencyProperty property) =>
        property == SeriesProperty || property == CirclesProperty || property == EqualAspectProperty;

    protected override PlotRange? AutoRange()
    {
        var bounds = PlotRange.Bounds(Series.SelectMany(s => s.Points));
        foreach (var c in Circles)
        {
            var circle = new PlotRange(c.X - c.Radius, c.X + c.Radius, c.Y - c.Radius, c.Y + c.Radius);
            bounds = bounds is { } b ? b.Union(circle) : circle;
        }
        if (bounds is not { } r) return null;
        // A perfect system's spot is one point: give it a visible neighbourhood scaled to where it is.
        double half = Math.Max(Math.Max(Math.Abs(r.X0), Math.Abs(r.Y0)) * 1e-3, 1e-9);
        if (!(r.Width > 0) && r.Height > 0) half = r.Height / 2;
        if (!(r.Height > 0) && r.Width > 0) half = r.Width / 2;
        return r.Padded(0.08, half);
    }

    protected override void RenderData(DrawingContext dc, PlotFrame frame, PlotPalette palette)
    {
        foreach (var c in Circles)
        {
            var pen = new Pen(new SolidColorBrush(c.Color), 1.2);
            if (c.Dashed) pen.DashStyle = DashStyles.Dash;
            var centre = frame.ToPixel(c.X, c.Y);
            double rx = c.Radius / frame.X.DataPerPixel, ry = c.Radius / frame.Y.DataPerPixel;
            dc.DrawEllipse(null, pen, centre, rx, ry);
        }
        foreach (var s in Series)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
                foreach (var p in s.Points)
                {
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
                    var q = frame.ToPixel(p);
                    if (frame.Area.Contains(q)) PlotDrawing.AddMarker(g, q, s.Marker, s.Size);
                }
            geometry.Freeze();
            var brush = new SolidColorBrush(s.Color);
            if (PlotDrawing.IsFilled(s.Marker)) dc.DrawGeometry(brush, null, geometry);
            else dc.DrawGeometry(null, new Pen(brush, 1), geometry);
        }
    }

    protected override void RenderDecorations(DrawingContext dc, PlotFrame frame, PlotPalette palette, Size size)
    {
        var area = frame.Area;
        double dip = PixelsPerDip;
        if (ShowScaleBar && ScaleBar.Length(frame.X.DataPerPixel, area.Width / 4) is var length && double.IsFinite(length))
        {
            double pixels = length / frame.X.DataPerPixel;
            double x0 = area.Left + 10, y = area.Bottom - 10;
            var pen = new Pen(palette.Text, 1.5);
            dc.DrawLine(pen, new Point(x0, y), new Point(x0 + pixels, y));
            dc.DrawLine(pen, new Point(x0, y - 4), new Point(x0, y + 4));
            dc.DrawLine(pen, new Point(x0 + pixels, y - 4), new Point(x0 + pixels, y + 4));
            string label = AxisTicks.Readout(length) + (Unit.Length > 0 ? " " + Unit : "");
            PlotDrawing.Text(dc, label, x0 + pixels / 2, y - 17, palette.Text, dip, TextAnchor.Centre);
        }
        // Legend: each named series with its marker, then each labelled circle.
        double ly = area.Top + 4, lx = area.Right - 110;
        foreach (var s in Series.Where(s => s.Name.Length > 0))
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) PlotDrawing.AddMarker(g, new Point(lx + 6, ly + 7), s.Marker, Math.Max(3, s.Size));
            var brush = new SolidColorBrush(s.Color);
            if (PlotDrawing.IsFilled(s.Marker)) dc.DrawGeometry(brush, null, geometry);
            else dc.DrawGeometry(null, new Pen(brush, 1), geometry);
            PlotDrawing.Text(dc, s.Name, lx + 16, ly, palette.Ink, dip);
            ly += 13;
        }
        foreach (var c in Circles.Where(c => c.Label.Length > 0))
        {
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(c.Color), 1.2), new Point(lx + 6, ly + 7), 4, 4);
            PlotDrawing.Text(dc, c.Label, lx + 16, ly, palette.Ink, dip);
            ly += 13;
        }
    }

    protected override PlotReadout? FindReadout(Point pixel, PlotFrame frame)
    {
        if (!frame.Contains(pixel)) return null;
        var pixels = Series.Select(s => (IReadOnlyList<Point2>)s.Points.Select(p => ToPixel2(frame, p)).ToArray()).ToArray();
        string unit = Unit.Length > 0 ? " " + Unit : "";
        if (ReadoutLookup.Nearest(pixels, new Point2(pixel.X, pixel.Y), 12) is { } hit)
        {
            var s = Series[hit.Series];
            var p = s.Points[hit.Index];
            var q = pixels[hit.Series][hit.Index];
            string point = s.Labels is { } labels && hit.Index < labels.Count ? labels[hit.Index] : $"point {hit.Index + 1}";
            string head = s.Name.Length > 0 ? $"{s.Name}, {point}\n" : $"{point}\n";
            return new PlotReadout(new Point(q.X, q.Y),
                $"{head}{AxisName(XLabel, "x")} = {AxisTicks.Readout(p.X)}{unit}\n{AxisName(YLabel, "y")} = {AxisTicks.Readout(p.Y)}{unit}");
        }
        var d = frame.ToData(pixel);
        return new PlotReadout(new Point(double.NaN, double.NaN),
            $"{AxisName(XLabel, "x")} = {AxisTicks.Readout(d.X)}{unit}\n{AxisName(YLabel, "y")} = {AxisTicks.Readout(d.Y)}{unit}");
    }

    public override IReadOnlyList<PlotTable> Tables => new[]
    {
        new PlotTable("points", () => PlotCsv.Points(AxisName(XLabel, "x"), AxisName(YLabel, "y"),
            Series.Select(s => (s.Name, s.Points)),
            Unit.Length > 0 ? new[] { "unit: " + Unit } : null))
    };
}
