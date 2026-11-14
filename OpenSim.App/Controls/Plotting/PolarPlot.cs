using System.Windows;
using System.Windows.Media;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;

namespace OpenSim.App.Controls.Plotting;

/// <summary>One curve of a <see cref="PolarPlot"/>: radii against angles in radians.</summary>
public sealed record PolarSeries(string Name, IReadOnlyList<double> Theta, IReadOnlyList<double> R, Color Color,
    bool Dashed = false);

/// <summary>
/// Curves in polar coordinates over rings and spokes (<see cref="PolarMap"/>): detector
/// intensity against angle, radiation patterns. The data space is the unit disc, so the
/// common zoom and pan work unchanged; the readout gives angle in degrees and radius.
/// </summary>
public sealed class PolarPlot : InteractivePlot
{
    public static readonly DependencyProperty SeriesProperty =
        Register<PolarPlot>(nameof(Series), typeof(IReadOnlyList<PolarSeries>), Array.Empty<PolarSeries>());
    public static readonly DependencyProperty RMinProperty = Register<PolarPlot>(nameof(RMin), typeof(double), 0.0);
    public static readonly DependencyProperty RMaxProperty = Register<PolarPlot>(nameof(RMax), typeof(double), double.NaN);
    public static readonly DependencyProperty ZeroProperty = Register<PolarPlot>(nameof(Zero), typeof(PolarZero), PolarZero.Right);
    public static readonly DependencyProperty ClockwiseProperty = Register<PolarPlot>(nameof(Clockwise), typeof(bool), false);
    public static readonly DependencyProperty AngleStepProperty = Register<PolarPlot>(nameof(AngleStep), typeof(double), 30.0);
    public static readonly DependencyProperty RUnitProperty = Register<PolarPlot>(nameof(RUnit), typeof(string), "");

    public IReadOnlyList<PolarSeries> Series { get => (IReadOnlyList<PolarSeries>)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    /// <summary>The radius at the centre; smaller radii clamp to it (a decibel floor).</summary>
    public double RMin { get => (double)GetValue(RMinProperty); set => SetValue(RMinProperty, value); }
    /// <summary>The radius of the outer ring; NaN for the next round value above the data.</summary>
    public double RMax { get => (double)GetValue(RMaxProperty); set => SetValue(RMaxProperty, value); }
    public PolarZero Zero { get => (PolarZero)GetValue(ZeroProperty); set => SetValue(ZeroProperty, value); }
    public bool Clockwise { get => (bool)GetValue(ClockwiseProperty); set => SetValue(ClockwiseProperty, value); }
    /// <summary>Degrees between spokes.</summary>
    public double AngleStep { get => (double)GetValue(AngleStepProperty); set => SetValue(AngleStepProperty, value); }
    public string RUnit { get => (string)GetValue(RUnitProperty); set => SetValue(RUnitProperty, value); }

    protected override bool UseEqualAspect => true;
    protected override bool DrawsCartesianAxes => false;
    protected override Thickness Margins => new(12, string.IsNullOrEmpty(Title) ? 12 : 28, 12, 12);

    protected override bool ResetsViewOnChange(DependencyProperty property) =>
        property == SeriesProperty || property == RMinProperty || property == RMaxProperty;

    /// <summary>The mapping in use: <see cref="RMax"/>, or the next round value at or above the
    /// largest radius plotted.</summary>
    public PolarMap Map
    {
        get
        {
            double rmin = double.IsFinite(RMin) ? RMin : 0;
            double rmax = RMax;
            if (!double.IsFinite(rmax))
            {
                double top = Series.SelectMany(s => s.R).Where(double.IsFinite).DefaultIfEmpty(rmin + 1).Max();
                double step = AxisTicks.NiceStep(Math.Max(top - rmin, 1e-300), 4);
                rmax = rmin + Math.Ceiling((top - rmin) / step - 1e-9) * step;
                if (!(rmax > rmin)) rmax = rmin + 1;
            }
            return new PolarMap(rmin, rmax, Zero, Clockwise);
        }
    }

    private static IEnumerable<(double Theta, double R)> Samples(PolarSeries s)
    {
        int n = Math.Min(s.Theta.Count, s.R.Count);
        for (int i = 0; i < n; i++) yield return (s.Theta[i], s.R[i]);
    }

    protected override PlotRange? AutoRange() => new PlotRange(-1.12, 1.12, -1.12, 1.12);

    protected override void RenderData(DrawingContext dc, PlotFrame frame, PlotPalette palette)
    {
        var map = Map;
        var centre = frame.ToPixel(0, 0);
        double unit = 1 / frame.X.DataPerPixel;
        double dip = PixelsPerDip;

        var rings = AxisTicks.Linear(map.RMin, map.RMax, 4).Where(r => r > map.RMin).ToList();
        if (rings.Count == 0 || rings[^1] < map.RMax) rings.Add(map.RMax);
        double ringStep = rings.Count > 1 ? rings[1] - rings[0] : map.RMax - map.RMin;
        foreach (double r in rings)
            dc.DrawEllipse(null, r == map.RMax ? palette.Frame : palette.Grid, centre, map.Rho(r) * unit, map.Rho(r) * unit);
        double step = AngleStep > 0 ? AngleStep : 30;
        for (double deg = 0; deg < 360 - 1e-9; deg += step)
        {
            double theta = deg * Math.PI / 180;
            var outer = map.ToCartesian(theta, map.RMax);
            dc.DrawLine(palette.Grid, centre, frame.ToPixel(outer));
            var label = frame.ToPixel(outer * 1.07);
            PlotDrawing.Text(dc, AxisTicks.Format(deg, 1) + "°", label.X, label.Y - 7, palette.Ink, dip, TextAnchor.Centre);
        }
        // Ring labels along the spoke half-way between the first two, clear of the curves' usual peak.
        double labelTheta = step / 2 * Math.PI / 180;
        foreach (double r in rings)
        {
            var at = frame.ToPixel(map.ToCartesian(labelTheta, r));
            PlotDrawing.Text(dc, AxisTicks.Format(r, ringStep), at.X + 2, at.Y - 12, palette.Ink, dip);
        }

        foreach (var s in Series)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                bool open = false;
                foreach (var (theta, r) in Samples(s))
                {
                    if (!double.IsFinite(theta) || !double.IsFinite(r)) { open = false; continue; }
                    var q = frame.ToPixel(map.ToCartesian(theta, r));
                    if (!open) { g.BeginFigure(q, false, false); open = true; }
                    else g.LineTo(q, true, false);
                }
            }
            geometry.Freeze();
            var pen = new Pen(new SolidColorBrush(s.Color), 1.5);
            if (s.Dashed) pen.DashStyle = DashStyles.Dash;
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    protected override void RenderDecorations(DrawingContext dc, PlotFrame frame, PlotPalette palette, Size size)
    {
        double y = frame.Area.Top + 2, x = frame.Area.Left + 2;
        foreach (var s in Series.Where(s => s.Name.Length > 0))
        {
            dc.DrawLine(new Pen(new SolidColorBrush(s.Color), 1.5), new Point(x, y + 7), new Point(x + 18, y + 7));
            PlotDrawing.Text(dc, s.Name, x + 22, y, palette.Ink, PixelsPerDip);
            y += 13;
        }
    }

    protected override PlotReadout? FindReadout(Point pixel, PlotFrame frame)
    {
        if (!frame.Contains(pixel)) return null;
        var map = Map;
        string unit = RUnit.Length > 0 ? " " + RUnit : "";
        var pixels = Series.Select(s => (IReadOnlyList<Point2>)Samples(s)
            .Select(p => ToPixel2(frame, map.ToCartesian(p.Theta, p.R))).ToArray()).ToArray();
        if (ReadoutLookup.Nearest(pixels, new Point2(pixel.X, pixel.Y), 16) is { } hit)
        {
            var s = Series[hit.Series];
            var q = pixels[hit.Series][hit.Index];
            string head = s.Name.Length > 0 ? s.Name + "\n" : "";
            return new PlotReadout(new Point(q.X, q.Y),
                $"{head}θ = {AxisTicks.Readout(s.Theta[hit.Index] * 180 / Math.PI)}°\nr = {AxisTicks.Readout(s.R[hit.Index])}{unit}");
        }
        var (theta, r) = map.FromCartesian(frame.ToData(pixel));
        return new PlotReadout(new Point(double.NaN, double.NaN),
            $"θ = {AxisTicks.Readout(theta * 180 / Math.PI)}°\nr = {AxisTicks.Readout(r)}{unit}");
    }

    public override IReadOnlyList<PlotTable> Tables => new[]
    {
        new PlotTable("curves", () => PlotCsv.Points("theta_deg", RUnit.Length > 0 ? $"r [{RUnit}]" : "r",
            Series.Select(s => (s.Name, (IReadOnlyList<Point2>)Samples(s).Select(p => new Point2(p.Theta * 180 / Math.PI, p.R)).ToArray()))))
    };
}
