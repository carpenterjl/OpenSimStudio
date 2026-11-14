using System.Windows;
using System.Windows.Media;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;

namespace OpenSim.App.Controls.Plotting;

/// <summary>
/// Filled and/or line contours of a <see cref="GridField"/> (<see cref="GridContour"/>), with a
/// banded colour bar: wavefront and OPD maps, MTF maps. The levels are
/// <see cref="Levels"/> when given, otherwise about <see cref="LevelCount"/> round values. Each
/// band is coloured at its middle value on <see cref="Colormap"/>, so the colour bar and the fill
/// agree block for block. The geometry is built once per field in data space and drawn through a
/// transform, so zooming and panning do not re-contour.
/// </summary>
public sealed class ContourPlot : InteractivePlot
{
    public static readonly DependencyProperty FieldProperty = Register<ContourPlot>(nameof(Field), typeof(GridField), null);
    public static readonly DependencyProperty LevelCountProperty = Register<ContourPlot>(nameof(LevelCount), typeof(int), 10);
    public static readonly DependencyProperty LevelsProperty = Register<ContourPlot>(nameof(Levels), typeof(IReadOnlyList<double>), null);
    public static readonly DependencyProperty FilledProperty = Register<ContourPlot>(nameof(Filled), typeof(bool), true);
    public static readonly DependencyProperty ShowLinesProperty = Register<ContourPlot>(nameof(ShowLines), typeof(bool), true);
    public static readonly DependencyProperty ColormapProperty =
        Register<ContourPlot>(nameof(Colormap), typeof(ColormapDefinition), ColormapDefinition.Viridis);
    public static readonly DependencyProperty EqualAspectProperty = Register<ContourPlot>(nameof(EqualAspect), typeof(bool), true);
    public static readonly DependencyProperty ValueUnitProperty = Register<ContourPlot>(nameof(ValueUnit), typeof(string), "");

    public GridField? Field { get => (GridField?)GetValue(FieldProperty); set => SetValue(FieldProperty, value); }
    public int LevelCount { get => (int)GetValue(LevelCountProperty); set => SetValue(LevelCountProperty, value); }
    /// <summary>Explicit contour levels; null for round levels chosen from <see cref="LevelCount"/>.</summary>
    public IReadOnlyList<double>? Levels { get => (IReadOnlyList<double>?)GetValue(LevelsProperty); set => SetValue(LevelsProperty, value); }
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public bool ShowLines { get => (bool)GetValue(ShowLinesProperty); set => SetValue(ShowLinesProperty, value); }
    public ColormapDefinition Colormap { get => (ColormapDefinition)GetValue(ColormapProperty); set => SetValue(ColormapProperty, value); }
    public bool EqualAspect { get => (bool)GetValue(EqualAspectProperty); set => SetValue(EqualAspectProperty, value); }
    public string ValueUnit { get => (string)GetValue(ValueUnitProperty); set => SetValue(ValueUnitProperty, value); }

    private sealed record Contours(double Min, double Max, IReadOnlyList<double> Levels, IReadOnlyList<double> Edges,
        IReadOnlyList<ContourLine> Lines, IReadOnlyList<ContourBand> Bands, System.Windows.Media.Geometry[] BandGeometry, System.Windows.Media.Geometry LineGeometry);

    private Contours? _contours;

    protected override bool UseEqualAspect => EqualAspect;
    protected override Thickness Margins => new(54, string.IsNullOrEmpty(Title) ? 18 : 34, 84, 34);

    protected override bool ResetsViewOnChange(DependencyProperty property) =>
        property == FieldProperty || property == EqualAspectProperty;

    protected override void OnPlotDataChanged(DependencyProperty property)
    {
        if (property == FieldProperty || property == LevelCountProperty || property == LevelsProperty) _contours = null;
    }

    /// <summary>The levels in use (explicit or chosen), the band edges and both kinds of contour.</summary>
    public (IReadOnlyList<double> Levels, IReadOnlyList<double> Edges, IReadOnlyList<ContourLine> Lines, IReadOnlyList<ContourBand> Bands)? Contouring =>
        Build() is { } c ? (c.Levels, c.Edges, c.Lines, c.Bands) : null;

    private Contours? Build()
    {
        if (_contours is not null) return _contours;
        if (Field is not { } field) return null;
        var (min, max) = field.FiniteRange();
        if (!double.IsFinite(min)) return null;
        var levels = Levels is { } explicitLevels
            ? explicitLevels.Where(double.IsFinite).OrderBy(l => l).ToArray()
            : GridContour.NiceLevels(min, max, Math.Max(1, LevelCount));
        var edges = GridContour.BandEdges(min, max, levels);
        var lines = GridContour.Lines(field, levels);
        var bands = GridContour.Bands(field, edges);

        var bandGeometry = new System.Windows.Media.Geometry[bands.Count];
        for (int k = 0; k < bands.Count; k++)
        {
            var g = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var ctx = g.Open())
                foreach (var polygon in bands[k].Polygons)
                {
                    ctx.BeginFigure(new Point(polygon[0].X, polygon[0].Y), true, true);
                    for (int i = 1; i < polygon.Length; i++) ctx.LineTo(new Point(polygon[i].X, polygon[i].Y), false, false);
                }
            g.Freeze();
            bandGeometry[k] = g;
        }
        var lineGeometry = new StreamGeometry();
        using (var ctx = lineGeometry.Open())
            foreach (var line in lines)
                foreach (var (a, b) in line.Segments)
                {
                    ctx.BeginFigure(new Point(a.X, a.Y), false, false);
                    ctx.LineTo(new Point(b.X, b.Y), true, false);
                }
        lineGeometry.Freeze();
        return _contours = new Contours(min, max, levels, edges, lines, bands, bandGeometry, lineGeometry);
    }

    protected override PlotRange? AutoRange() =>
        Field is { } f ? new PlotRange(f.X0, f.X1, f.Y0, f.Y1) : null;

    /// <summary>The data-to-pixel transform of a linear frame, applied to data-space geometry.</summary>
    internal static MatrixTransform DataTransform(PlotFrame frame)
    {
        double sx = (frame.X.PixelEnd - frame.X.PixelStart) / (frame.X.Max - frame.X.Min);
        double sy = (frame.Y.PixelEnd - frame.Y.PixelStart) / (frame.Y.Max - frame.Y.Min);
        var t = new MatrixTransform(sx, 0, 0, sy, frame.X.PixelStart - frame.X.Min * sx, frame.Y.PixelStart - frame.Y.Min * sy);
        t.Freeze();
        return t;
    }

    protected override void RenderData(DrawingContext dc, PlotFrame frame, PlotPalette palette)
    {
        if (Build() is not { } c) return;
        var transform = DataTransform(frame);
        double span = c.Max - c.Min;
        if (Filled)
        {
            // Aliased edges: the band polygons share their edges exactly, and anti-aliasing each
            // one separately would leave faint seams along every triangle edge.
            var group = new DrawingGroup();
            RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
            using (var g = group.Open())
                for (int k = 0; k < c.BandGeometry.Length; k++)
                {
                    double mid = 0.5 * (c.Edges[k] + c.Edges[k + 1]);
                    var brush = PlotDrawing.Brush(Colormap, span > 0 ? (mid - c.Min) / span : 0.5);
                    g.DrawGeometry(brush, null, new GeometryGroup { Children = { c.BandGeometry[k] }, Transform = transform });
                }
            dc.DrawDrawing(group);
        }
        if (ShowLines)
        {
            if (Filled)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x10, 0x10)), 0.8);
                dc.DrawGeometry(null, pen, new GeometryGroup { Children = { c.LineGeometry }, Transform = transform });
            }
            else
                // Unfilled: each line in its level's colour, so the colour bar still reads.
                foreach (var line in c.Lines)
                {
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                        foreach (var (a, b) in line.Segments)
                        {
                            ctx.BeginFigure(frame.ToPixel(a), false, false);
                            ctx.LineTo(frame.ToPixel(b), true, false);
                        }
                    geometry.Freeze();
                    dc.DrawGeometry(null, new Pen(PlotDrawing.Brush(Colormap, span > 0 ? (line.Level - c.Min) / span : 0.5), 1.2), geometry);
                }
        }
    }

    protected override void RenderDecorations(DrawingContext dc, PlotFrame frame, PlotPalette palette, Size size)
    {
        if (Build() is not { } c) return;
        var bar = new Rect(frame.Area.Right + 10, frame.Area.Top, 14, frame.Area.Height);
        PlotDrawing.ColorBar(dc, bar, Colormap, c.Min, c.Max, Filled ? c.Edges : null, ValueUnit, palette, PixelsPerDip);
    }

    protected override PlotReadout? FindReadout(Point pixel, PlotFrame frame)
    {
        if (!frame.Contains(pixel) || Field is not { } field || Build() is not { } c) return null;
        var d = frame.ToData(pixel);
        double value = field.Interpolate(d.X, d.Y);
        string unit = ValueUnit.Length > 0 ? " " + ValueUnit : "";
        string position = $"{AxisName(XLabel, "x")} = {AxisTicks.Readout(d.X)}\n{AxisName(YLabel, "y")} = {AxisTicks.Readout(d.Y)}";
        if (!double.IsFinite(value)) return new PlotReadout(new Point(double.NaN, double.NaN), position + "\nno data");
        int band = GridContour.BandIndex(value, c.Edges);
        string between = band >= 0 ? $"\nband {AxisTicks.Readout(c.Edges[band])} to {AxisTicks.Readout(c.Edges[band + 1])}" : "";
        return new PlotReadout(new Point(double.NaN, double.NaN), $"{position}\nvalue = {AxisTicks.Readout(value)}{unit}{between}");
    }

    public override IReadOnlyList<PlotTable> Tables => new[]
    {
        new PlotTable("grid", () => Field is { } f
            ? PlotCsv.Grid(f, comments: Comments())
            : ""),
        new PlotTable("contour lines", () => PlotCsv.Table(new[] { "level", "x1", "y1", "x2", "y2" },
            (Build()?.Lines ?? Array.Empty<ContourLine>()).SelectMany(l => l.Segments.Select(s =>
                (IReadOnlyList<string>)new[] { PlotCsv.Number(l.Level), PlotCsv.Number(s.A.X), PlotCsv.Number(s.A.Y), PlotCsv.Number(s.B.X), PlotCsv.Number(s.B.Y) })),
            Comments()))
    };

    private IEnumerable<string> Comments()
    {
        var list = new List<string>();
        if (ValueUnit.Length > 0) list.Add("unit: " + ValueUnit);
        if (Build() is { } c) list.Add("levels: " + string.Join(" ", c.Levels.Select(PlotCsv.Number)));
        return list;
    }
}
