using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;

namespace OpenSim.App.Controls.Plotting;

/// <summary>
/// A false-colour image of a <see cref="GridField"/>, one screen block per sample, with a movable
/// cross-section whose profile is drawn beneath it: PSF, wavefront and detector images. Drag an
/// end handle to move that end, drag the line to move it whole; the ends bind two-way
/// (<see cref="CrossSectionStart"/>, <see cref="CrossSectionEnd"/>, data units) and
/// <see cref="CrossSectionChanged"/> fires on every move. Samples are drawn unsmoothed (nearest
/// neighbour) so what is seen is what was computed; the readout gives the sample under the cursor.
/// </summary>
public sealed class ImagePlot : InteractivePlot
{
    private static readonly Point Unset = new(double.NaN, double.NaN);

    public static readonly DependencyProperty FieldProperty = Register<ImagePlot>(nameof(Field), typeof(GridField), null);
    public static readonly DependencyProperty ColormapProperty =
        Register<ImagePlot>(nameof(Colormap), typeof(ColormapDefinition), ColormapDefinition.Viridis);
    public static readonly DependencyProperty MinimumProperty = Register<ImagePlot>(nameof(Minimum), typeof(double), double.NaN);
    public static readonly DependencyProperty MaximumProperty = Register<ImagePlot>(nameof(Maximum), typeof(double), double.NaN);
    public static readonly DependencyProperty ValueUnitProperty = Register<ImagePlot>(nameof(ValueUnit), typeof(string), "");
    public static readonly DependencyProperty ShowCrossSectionProperty = Register<ImagePlot>(nameof(ShowCrossSection), typeof(bool), true);
    public static readonly DependencyProperty CrossSectionStartProperty = DependencyProperty.Register(nameof(CrossSectionStart), typeof(Point),
        typeof(ImagePlot), new FrameworkPropertyMetadata(Unset, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnCrossSectionMoved));
    public static readonly DependencyProperty CrossSectionEndProperty = DependencyProperty.Register(nameof(CrossSectionEnd), typeof(Point),
        typeof(ImagePlot), new FrameworkPropertyMetadata(Unset, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnCrossSectionMoved));

    public GridField? Field { get => (GridField?)GetValue(FieldProperty); set => SetValue(FieldProperty, value); }
    public ColormapDefinition Colormap { get => (ColormapDefinition)GetValue(ColormapProperty); set => SetValue(ColormapProperty, value); }
    /// <summary>The value at the bottom of the colour scale; NaN for the smallest sample.</summary>
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    /// <summary>The value at the top of the colour scale; NaN for the largest sample.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string ValueUnit { get => (string)GetValue(ValueUnitProperty); set => SetValue(ValueUnitProperty, value); }
    public bool ShowCrossSection { get => (bool)GetValue(ShowCrossSectionProperty); set => SetValue(ShowCrossSectionProperty, value); }
    /// <summary>Start of the cross-section in data units; unset (NaN) for the left end of the
    /// horizontal line through the middle row.</summary>
    public Point CrossSectionStart { get => (Point)GetValue(CrossSectionStartProperty); set => SetValue(CrossSectionStartProperty, value); }
    public Point CrossSectionEnd { get => (Point)GetValue(CrossSectionEndProperty); set => SetValue(CrossSectionEndProperty, value); }

    /// <summary>Raised whenever the cross-section moves (drag or property).</summary>
    public event EventHandler? CrossSectionChanged;

    private static void OnCrossSectionMoved(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ImagePlot)d).CrossSectionChanged?.Invoke(d, EventArgs.Empty);

    private BitmapSource? _bitmap;
    private enum Handle { None, Start, End, Body }
    private Handle _dragging;
    private Point2 _dragFrom;

    public ImagePlot() => RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

    protected override bool UseEqualAspect => true;
    protected override Thickness Margins => new(54, string.IsNullOrEmpty(Title) ? 18 : 34, 84, 34);

    protected override bool ResetsViewOnChange(DependencyProperty property) => property == FieldProperty;

    protected override void OnPlotDataChanged(DependencyProperty property)
    {
        if (property == FieldProperty || property == ColormapProperty || property == MinimumProperty || property == MaximumProperty)
            _bitmap = null;
    }

    /// <summary>The colour scale in use: <see cref="Minimum"/> and <see cref="Maximum"/> where set.</summary>
    public (double Min, double Max) ColorRange
    {
        get
        {
            var (min, max) = Field?.FiniteRange() ?? (double.NaN, double.NaN);
            if (double.IsFinite(Minimum)) min = Minimum;
            if (double.IsFinite(Maximum)) max = Maximum;
            if (!(max > min)) max = min + 1;
            return (min, max);
        }
    }

    /// <summary>The cross-section ends in use (the defaults resolved).</summary>
    public (Point2 Start, Point2 End)? Section
    {
        get
        {
            if (Field is not { } f) return null;
            double midY = f.YAt(f.Rows / 2);
            var a = CrossSectionStart; var b = CrossSectionEnd;
            var start = double.IsFinite(a.X) && double.IsFinite(a.Y) ? new Point2(a.X, a.Y) : new Point2(f.X0, midY);
            var end = double.IsFinite(b.X) && double.IsFinite(b.Y) ? new Point2(b.X, b.Y) : new Point2(f.X1, midY);
            return (start, end);
        }
    }

    /// <summary>The profile along the cross-section, two samples per grid spacing.</summary>
    public IReadOnlyList<CrossSectionSample> Profile()
    {
        if (Field is not { } f || Section is not { } s) return Array.Empty<CrossSectionSample>();
        return CrossSection.Sample(f, s.Start, s.End, CrossSection.DefaultCount(f, s.Start, s.End));
    }

    private double ImageHeightShare => 0.62;

    protected override Rect PlotArea(Size size)
    {
        var area = base.PlotArea(size);
        if (!ShowCrossSection) return area;
        return new Rect(area.Left, area.Top, area.Width, Math.Max(0, (area.Height - 40) * ImageHeightShare));
    }

    /// <summary>Where the profile is drawn: below the image, the same width.</summary>
    public Rect ProfileArea(Size size)
    {
        var image = PlotArea(size);
        double top = image.Bottom + 40;
        return new Rect(image.Left, top, image.Width, Math.Max(0, size.Height - Margins.Bottom - top));
    }

    protected override PlotRange? AutoRange() => Field is { } f
        ? new PlotRange(f.X0 - f.DeltaX / 2, f.X1 + f.DeltaX / 2, f.Y0 - f.DeltaY / 2, f.Y1 + f.DeltaY / 2)
        : null;

    private BitmapSource? Bitmap()
    {
        if (_bitmap is not null || Field is not { } f) return _bitmap;
        var (min, max) = ColorRange;
        int rows = f.Rows, columns = f.Columns;
        var pixels = new byte[rows * columns * 4];
        var map = Colormap;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                double v = f.Values[r, c];
                int at = ((rows - 1 - r) * columns + c) * 4;   // top row of the bitmap = largest y
                if (!double.IsFinite(v)) continue;            // no data: transparent
                var (red, green, blue) = map.Sample((v - min) / (max - min));
                pixels[at] = blue; pixels[at + 1] = green; pixels[at + 2] = red; pixels[at + 3] = 255;
            }
        var bitmap = BitmapSource.Create(columns, rows, 96, 96, PixelFormats.Bgra32, null, pixels, columns * 4);
        bitmap.Freeze();
        return _bitmap = bitmap;
    }

    protected override void RenderData(DrawingContext dc, PlotFrame frame, PlotPalette palette)
    {
        if (Field is not { } f || Bitmap() is not { } bitmap) return;
        var topLeft = frame.ToPixel(f.X0 - f.DeltaX / 2, f.Y1 + f.DeltaY / 2);
        var bottomRight = frame.ToPixel(f.X1 + f.DeltaX / 2, f.Y0 - f.DeltaY / 2);
        dc.DrawImage(bitmap, new Rect(topLeft, bottomRight));

        if (!ShowCrossSection || Section is not { } s) return;
        var a = frame.ToPixel(s.Start);
        var b = frame.ToPixel(s.End);
        var halo = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), 3);
        var line = new Pen(Brushes.White, 1.2);
        dc.DrawLine(halo, a, b);
        dc.DrawLine(line, a, b);
        foreach (var p in new[] { a, b })
        {
            dc.DrawRectangle(Brushes.White, halo, new Rect(p.X - 4, p.Y - 4, 8, 8));
        }
        dc.DrawEllipse(palette.Accent, null, a, 2, 2);   // the start end, where distance is zero
    }

    protected override void RenderDecorations(DrawingContext dc, PlotFrame frame, PlotPalette palette, Size size)
    {
        if (Field is null) return;
        var (min, max) = ColorRange;
        var bar = new Rect(frame.Area.Right + 10, frame.Area.Top, 14, frame.Area.Height);
        PlotDrawing.ColorBar(dc, bar, Colormap, min, max, null, ValueUnit, palette, PixelsPerDip);
        if (!ShowCrossSection || ProfileFrame(size) is not { } profileFrame) return;

        PlotDrawing.CartesianAxes(dc, profileFrame, palette, PixelsPerDip);
        PlotDrawing.Text(dc, "distance along section", profileFrame.Area.Left + profileFrame.Area.Width / 2,
            profileFrame.Area.Bottom + 17, palette.Ink, PixelsPerDip, TextAnchor.Centre);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            bool open = false;
            foreach (var sample in Profile())
            {
                if (!double.IsFinite(sample.Value)) { open = false; continue; }
                var q = profileFrame.ToPixel(sample.Distance, sample.Value);
                if (!open) { g.BeginFigure(q, false, false); open = true; }
                else g.LineTo(q, true, false);
            }
        }
        geometry.Freeze();
        dc.PushClip(new RectangleGeometry(profileFrame.Area));
        dc.DrawGeometry(null, new Pen(palette.Accent, 1.5), geometry);
        dc.Pop();
    }

    /// <summary>The profile's own frame: distance along the section against value.</summary>
    private PlotFrame? ProfileFrame(Size size)
    {
        var area = ProfileArea(size);
        if (area.Width < 8 || area.Height < 8) return null;
        var profile = Profile();
        if (profile.Count < 2) return null;
        double length = profile[^1].Distance;
        var values = profile.Select(p => p.Value).Where(double.IsFinite).ToArray();
        double y0 = values.Length > 0 ? values.Min() : 0, y1 = values.Length > 0 ? values.Max() : 1;
        var range = new PlotRange(0, length > 0 ? length : 1, y0, y1).Padded(0.06, Math.Max(Math.Abs(y0) * 0.05, 0.5));
        return PlotFrame.Create(area, range with { X0 = 0, X1 = length > 0 ? length : 1 }, false);
    }

    protected override PlotReadout? FindReadout(Point pixel, PlotFrame frame)
    {
        if (Field is not { } f) return null;
        string unit = ValueUnit.Length > 0 ? " " + ValueUnit : "";
        if (frame.Contains(pixel))
        {
            var d = frame.ToData(pixel);
            if (f.NearestSample(d.X, d.Y) is not { } rc) return CursorReadout(pixel, frame);
            double x = f.XAt(rc.Column), y = f.YAt(rc.Row), v = f.Values[rc.Row, rc.Column];
            string value = double.IsFinite(v) ? AxisTicks.Readout(v) + unit : "no data";
            return new PlotReadout(frame.ToPixel(x, y),
                $"{AxisName(XLabel, "x")} = {AxisTicks.Readout(x)}\n{AxisName(YLabel, "y")} = {AxisTicks.Readout(y)}\n" +
                $"value = {value}\nsample [{rc.Row}, {rc.Column}]");
        }
        if (ShowCrossSection && ProfileFrame(RenderSize) is { } pf && pf.Contains(pixel))
        {
            var profile = Profile();
            double length = profile[^1].Distance;
            double s = pf.X.ToData(pixel.X);
            int i = length > 0 ? (int)Math.Clamp(Math.Round(s / length * (profile.Count - 1)), 0, profile.Count - 1) : 0;
            var sample = profile[i];
            var anchor = double.IsFinite(sample.Value) ? pf.ToPixel(sample.Distance, sample.Value) : new Point(double.NaN, double.NaN);
            string value = double.IsFinite(sample.Value) ? AxisTicks.Readout(sample.Value) + unit : "off the image";
            return new PlotReadout(anchor,
                $"distance = {AxisTicks.Readout(sample.Distance)}\n{AxisName(XLabel, "x")} = {AxisTicks.Readout(sample.X)}, " +
                $"{AxisName(YLabel, "y")} = {AxisTicks.Readout(sample.Y)}\nvalue = {value}");
        }
        return null;
    }

    protected override bool BeginCustomDrag(Point pixel, PlotFrame frame)
    {
        if (!ShowCrossSection || Section is not { } s) return false;
        var p = new Point2(pixel.X, pixel.Y);
        var a = ToPixel2(frame, s.Start);
        var b = ToPixel2(frame, s.End);
        if ((p - a).Length <= 8) _dragging = Handle.Start;
        else if ((p - b).Length <= 8) _dragging = Handle.End;
        else if (ReadoutLookup.DistanceToSegment(p, a, b) <= 5) _dragging = Handle.Body;
        else return false;
        _dragFrom = frame.ToData(pixel);
        return true;
    }

    protected override void ContinueCustomDrag(Point pixel, PlotFrame frame)
    {
        if (Field is not { } f || Section is not { } s) return;
        Point2 Clamp(Point2 q) => new(Math.Clamp(q.X, f.X0, f.X1), Math.Clamp(q.Y, f.Y0, f.Y1));
        var d = frame.ToData(pixel);
        switch (_dragging)
        {
            case Handle.Start: MoveSection(Clamp(d), s.End); break;
            case Handle.End: MoveSection(s.Start, Clamp(d)); break;
            case Handle.Body:
                var shift = d - _dragFrom;
                // Move whole only as far as both ends stay on the image.
                shift = new Point2(
                    Math.Clamp(shift.X, f.X0 - Math.Min(s.Start.X, s.End.X), f.X1 - Math.Max(s.Start.X, s.End.X)),
                    Math.Clamp(shift.Y, f.Y0 - Math.Min(s.Start.Y, s.End.Y), f.Y1 - Math.Max(s.Start.Y, s.End.Y)));
                MoveSection(s.Start + shift, s.End + shift);
                _dragFrom += shift;
                break;
        }
    }

    protected override void EndCustomDrag() => _dragging = Handle.None;

    /// <summary>Sets both ends (data units), keeping any binding.</summary>
    public void MoveSection(Point2 start, Point2 end)
    {
        SetCurrentValue(CrossSectionStartProperty, new Point(start.X, start.Y));
        SetCurrentValue(CrossSectionEndProperty, new Point(end.X, end.Y));
    }

    public override IReadOnlyList<PlotTable> Tables => new[]
    {
        new PlotTable("image", () => Field is { } f
            ? PlotCsv.Grid(f, comments: ValueUnit.Length > 0 ? new[] { "unit: " + ValueUnit } : null)
            : ""),
        new PlotTable("cross-section", () => PlotCsv.Table(
            new[] { "distance", AxisName(XLabel, "x"), AxisName(YLabel, "y"), "value" },
            Profile().Select(p => (IReadOnlyList<string>)new[] { PlotCsv.Number(p.Distance), PlotCsv.Number(p.X), PlotCsv.Number(p.Y), PlotCsv.Number(p.Value) }),
            Section is { } s ? new[] { $"from {PlotCsv.Number(s.Start.X)} {PlotCsv.Number(s.Start.Y)} to {PlotCsv.Number(s.End.X)} {PlotCsv.Number(s.End.Y)}" } : null))
    };
}
