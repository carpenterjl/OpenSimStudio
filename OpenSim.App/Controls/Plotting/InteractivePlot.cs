using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Plotting;

namespace OpenSim.App.Controls.Plotting;

/// <summary>What the cursor readout shows: a marker at <see cref="Anchor"/> (pixels; NaN for
/// none, when the readout is just the cursor position) and the text beside the cursor.</summary>
public sealed record PlotReadout(Point Anchor, string Text)
{
    public bool HasAnchor => double.IsFinite(Anchor.X) && double.IsFinite(Anchor.Y);
}

/// <summary>
/// The base of the interactive plots. Unlike <see cref="CurvePlot"/> and
/// <see cref="FieldMapPlot"/>, which render a frozen bitmap once, a plot here is a live element:
/// it redraws at its own size, and on every plot
/// <list type="bullet">
/// <item>the cursor readout is drawn on a separate overlay visual, so moving the mouse redraws a
/// marker and a text box, never the data;</item>
/// <item>the wheel zooms about the cursor, a left drag zooms to a box, a middle (or Shift+left)
/// drag pans, a double click or Home restores the automatic range;</item>
/// <item>the context menu copies or saves each of the plot's tables as CSV
/// (<see cref="PlotCsv"/>) and copies the image; Ctrl+C copies the first table.</item>
/// </list>
/// Subclasses supply the data range, the drawing, the readout lookup and the tables; the
/// geometry behind them (ticks, mapping, contouring, lookup, CSV) is in
/// <c>OpenSim.Core.Plotting</c> where it is tested without WPF.
/// </summary>
public abstract class InteractivePlot : FrameworkElement
{
    public static readonly DependencyProperty TitleProperty = Register<InteractivePlot>(nameof(Title), typeof(string), "");
    public static readonly DependencyProperty XLabelProperty = Register<InteractivePlot>(nameof(XLabel), typeof(string), "");
    public static readonly DependencyProperty YLabelProperty = Register<InteractivePlot>(nameof(YLabel), typeof(string), "");

    /// <summary>Heading above the plot; also the default export file name and multi-panel section name.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string XLabel { get => (string)GetValue(XLabelProperty); set => SetValue(XLabelProperty, value); }
    public string YLabel { get => (string)GetValue(YLabelProperty); set => SetValue(YLabelProperty, value); }

    private enum DragMode { None, Zoom, Pan, Custom }

    private readonly DrawingVisual _overlay = new();
    private PlotRange? _view;
    private DragMode _drag;
    private Point _dragStart, _dragLast, _cursor;

    protected InteractivePlot()
    {
        AddVisualChild(_overlay);
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        ContextMenu = new ContextMenu();
    }

    /// <summary>Registers a property that redraws the plot and resets its zoom when it changes
    /// (new data has its own range).</summary>
    protected static DependencyProperty Register<TOwner>(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(TOwner),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender, OnPlotPropertyChanged));

    private static void OnPlotPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var plot = (InteractivePlot)d;
        plot.OnPlotDataChanged(e.Property);
        plot.Readout = null;
        if (!plot.ResetsViewOnChange(e.Property)) return;
        // Raised even when the view was already automatic: the automatic range itself moved, and
        // a linked multi-panel figure must re-derive its shared scale.
        plot._view = null;
        plot.ViewChanged?.Invoke(plot, EventArgs.Empty);
    }

    /// <summary>Called when a registered property changes, before the redraw: drop caches here.</summary>
    protected virtual void OnPlotDataChanged(DependencyProperty property) { }

    /// <summary>Whether a change of <paramref name="property"/> restores the automatic range.
    /// Labels and styling do not; data does.</summary>
    protected virtual bool ResetsViewOnChange(DependencyProperty property) =>
        property != TitleProperty && property != XLabelProperty && property != YLabelProperty;

    // ---------------------------------------------------------------- the view

    /// <summary>The zoomed data range, or null for the automatic one.</summary>
    public PlotRange? View => _view;

    /// <summary>Raised when <see cref="View"/> changes (zoom, pan, reset, new data).</summary>
    public event EventHandler? ViewChanged;

    /// <summary>The automatic data range (what a reset shows), or null with no data.</summary>
    public PlotRange? DataRange => AutoRange();

    public void SetView(PlotRange? view)
    {
        if (Equals(_view, view)) return;
        if (view is { } v && !(v.Width > 0 && v.Height > 0 && double.IsFinite(v.Width) && double.IsFinite(v.Height))) return;
        _view = view;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetZoom() => SetView(null);

    /// <summary>Magnifies by <paramref name="factor"/> about the data under <paramref name="pixel"/>.</summary>
    public void ZoomAt(Point pixel, double factor)
    {
        if (CurrentFrame() is not { } f) return;
        var anchor = f.ToData(pixel);
        var x = f.X.ZoomedAbout(anchor.X, factor);
        var y = f.Y.ZoomedAbout(anchor.Y, factor);
        SetView(new PlotRange(x.Min, x.Max, y.Min, y.Max));
    }

    /// <summary>Shows the data inside a pixel rectangle.</summary>
    public void ZoomToPixels(Rect pixels)
    {
        if (CurrentFrame() is not { } f) return;
        var a = f.ToData(pixels.TopLeft);
        var b = f.ToData(pixels.BottomRight);
        SetView(new PlotRange(Math.Min(a.X, b.X), Math.Max(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
    }

    /// <summary>Moves the content by <paramref name="pixels"/> on screen.</summary>
    public void Pan(Vector pixels)
    {
        if (CurrentFrame() is not { } f) return;
        var x = f.X.Panned(pixels.X);
        var y = f.Y.Panned(pixels.Y);
        SetView(new PlotRange(x.Min, x.Max, y.Min, y.Max));
    }

    /// <summary>The frame at the current size and view; null before layout or when too small.</summary>
    public PlotFrame? CurrentFrame() => ComputeFrame(RenderSize);

    protected PlotFrame? ComputeFrame(Size size)
    {
        var area = PlotArea(size);
        if (area.Width < 8 || area.Height < 8) return null;
        var range = _view ?? AutoRange() ?? new PlotRange(0, 1, 0, 1);
        return PlotFrame.Create(area, range, UseEqualAspect, LogX, LogY);
    }

    // ---------------------------------------------------------------- what subclasses supply

    /// <summary>The automatic range: the data's extent, padded as the plot kind wants.</summary>
    protected abstract PlotRange? AutoRange();
    protected virtual bool UseEqualAspect => false;
    protected virtual bool LogX => false;
    protected virtual bool LogY => false;
    /// <summary>False for plots that draw their own grid (polar).</summary>
    protected virtual bool DrawsCartesianAxes => true;

    /// <summary>Space around the data area: tick labels left and below, the y label and title above.</summary>
    protected virtual Thickness Margins => new(54, string.IsNullOrEmpty(Title) ? 18 : 34, 12, 34);

    protected virtual Rect PlotArea(Size size)
    {
        var m = Margins;
        return new Rect(m.Left, m.Top, Math.Max(0, size.Width - m.Left - m.Right), Math.Max(0, size.Height - m.Top - m.Bottom));
    }

    /// <summary>Draws the data, clipped to the frame's area.</summary>
    protected abstract void RenderData(DrawingContext dc, PlotFrame frame, PlotPalette palette);

    /// <summary>Draws outside the clip: legends, colour bars, side panels.</summary>
    protected virtual void RenderDecorations(DrawingContext dc, PlotFrame frame, PlotPalette palette, Size size) { }

    /// <summary>The readout for the cursor at <paramref name="pixel"/>, or null for none.</summary>
    protected abstract PlotReadout? FindReadout(Point pixel, PlotFrame frame);

    /// <summary>The plot's exportable tables; the first is what Ctrl+C copies.</summary>
    public abstract IReadOnlyList<PlotTable> Tables { get; }

    /// <summary>Lets a subclass claim a left drag (a cross-section handle) before it becomes a zoom box.</summary>
    protected virtual bool BeginCustomDrag(Point pixel, PlotFrame frame) => false;
    protected virtual void ContinueCustomDrag(Point pixel, PlotFrame frame) { }
    protected virtual void EndCustomDrag() { }

    // ---------------------------------------------------------------- readout

    /// <summary>The readout currently shown, null when the cursor is away.</summary>
    public PlotReadout? Readout { get; private set; }

    /// <summary>The readout the cursor would get at <paramref name="pixel"/> (what a mouse move shows).</summary>
    public PlotReadout? ReadoutAt(Point pixel) => CurrentFrame() is { } f ? FindReadout(pixel, f) : null;

    /// <summary>The CSV of the first table (what Ctrl+C copies); empty with no tables.</summary>
    public string ExportCsv() => Tables.Count > 0 ? Tables[0].Csv() : "";

    /// <summary>A plain cursor-position readout, for when nothing plotted is near.</summary>
    protected PlotReadout? CursorReadout(Point pixel, PlotFrame frame)
    {
        if (!frame.Contains(pixel)) return null;
        var d = frame.ToData(pixel);
        return new PlotReadout(new Point(double.NaN, double.NaN),
            $"{AxisName(XLabel, "x")} = {AxisTicks.Readout(d.X)}\n{AxisName(YLabel, "y")} = {AxisTicks.Readout(d.Y)}");
    }

    /// <summary>A label for readouts and CSV headers: the axis label, or a fallback when it is empty.</summary>
    protected static string AxisName(string label, string fallback) => string.IsNullOrWhiteSpace(label) ? fallback : label;

    protected double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    // ---------------------------------------------------------------- layout and rendering

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _overlay : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>A plot fills what it is given; unconstrained, it asks for a modest default.</summary>
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 420 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var palette = PlotPalette.Resolve(this);
        var size = RenderSize;
        // A transparent fill makes the whole element hit-testable for the readout and zoom.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        if (ComputeFrame(size) is not { } frame) return;
        double dip = PixelsPerDip;

        if (!string.IsNullOrEmpty(Title))
            PlotDrawing.Text(dc, Title, frame.Area.Left + frame.Area.Width / 2, 2, palette.Text, dip, TextAnchor.Centre, 11);
        if (DrawsCartesianAxes)
        {
            PlotDrawing.CartesianAxes(dc, frame, palette, dip);
            if (XLabel.Length > 0)
                PlotDrawing.Text(dc, XLabel, frame.Area.Left + frame.Area.Width / 2, frame.Area.Bottom + 17, palette.Ink, dip, TextAnchor.Centre);
            if (YLabel.Length > 0)
                PlotDrawing.Text(dc, YLabel, 2, frame.Area.Top - 15, palette.Ink, dip);
        }

        dc.PushClip(new RectangleGeometry(frame.Area));
        RenderData(dc, frame, palette);
        dc.Pop();
        if (DrawsCartesianAxes) dc.DrawRectangle(null, palette.Frame, frame.Area);
        RenderDecorations(dc, frame, palette, size);
        DrawOverlay();
    }

    private void DrawOverlay()
    {
        var palette = PlotPalette.Resolve(this);
        using var dc = _overlay.RenderOpen();
        if (_drag == DragMode.Zoom)
        {
            var box = new Rect(_dragStart, _dragLast);
            var pen = new Pen(palette.Accent, 1) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x22, 0x3D, 0x8B, 0xFD)), pen, box);
        }
        if (Readout is not { } readout) return;
        if (readout.HasAnchor)
        {
            dc.DrawEllipse(null, new Pen(palette.Accent, 1.5), readout.Anchor, 4.5, 4.5);
        }
        var text = PlotDrawing.Format(readout.Text, palette.Text, PixelsPerDip);
        double width = text.Width + 10, height = text.Height + 6;
        double x = _cursor.X + 14, y = _cursor.Y + 14;
        if (x + width > RenderSize.Width) x = _cursor.X - 14 - width;
        if (y + height > RenderSize.Height) y = _cursor.Y - 14 - height;
        x = Math.Max(0, x); y = Math.Max(0, y);
        dc.DrawRoundedRectangle(palette.ReadoutBackground, palette.ReadoutBorder, new Rect(x, y, width, height), 3, 3);
        dc.DrawText(text, new Point(x + 5, y + 3));
    }

    private void UpdateReadout(Point pixel)
    {
        _cursor = pixel;
        Readout = CurrentFrame() is { } f ? FindReadout(pixel, f) : null;
        DrawOverlay();
    }

    // ---------------------------------------------------------------- mouse and keyboard

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        switch (_drag)
        {
            case DragMode.Zoom: _dragLast = p; break;
            case DragMode.Pan: Pan(p - _dragLast); _dragLast = p; break;
            case DragMode.Custom:
                if (CurrentFrame() is { } f) ContinueCustomDrag(p, f);
                break;
        }
        UpdateReadout(p);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag != DragMode.None) return;
        Readout = null;
        DrawOverlay();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.25 : 1 / 1.25);
        UpdateReadout(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled) return;
        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle) { StartDrag(DragMode.Pan, p); e.Handled = true; return; }
        if (e.ChangedButton != MouseButton.Left) return;
        Focus();
        if (e.ClickCount == 2) { ResetZoom(); e.Handled = true; return; }
        var frame = CurrentFrame();
        if (frame is null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) StartDrag(DragMode.Pan, p);
        else if (BeginCustomDrag(p, frame)) StartDrag(DragMode.Custom, p);
        else if (frame.Contains(p)) StartDrag(DragMode.Zoom, p);
        e.Handled = true;
    }

    private void StartDrag(DragMode mode, Point p)
    {
        _drag = mode;
        _dragStart = _dragLast = p;
        CaptureMouse();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragMode.None) return;
        var mode = _drag;
        var box = new Rect(_dragStart, e.GetPosition(this));
        _drag = DragMode.None;
        ReleaseMouseCapture();
        if (mode == DragMode.Zoom && box.Width > 4 && box.Height > 4) ZoomToPixels(box);
        if (mode == DragMode.Custom) EndCustomDrag();
        UpdateReadout(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == DragMode.None) return;
        if (_drag == DragMode.Custom) EndCustomDrag();
        _drag = DragMode.None;
        DrawOverlay();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var centre = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Tables.Count > 0)
            PlotExport.CopyText(Tables[0].Csv());
        else if (e.Key == Key.Home) ResetZoom();
        else if (e.Key is Key.Add or Key.OemPlus) ZoomAt(centre, 1.25);
        else if (e.Key is Key.Subtract or Key.OemMinus) ZoomAt(centre, 1 / 1.25);
        else if (e.Key == Key.Escape && _drag != DragMode.None) ReleaseMouseCapture();
        else return;
        e.Handled = true;
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        base.OnContextMenuOpening(e);
        if (ContextMenu is null) return;
        ContextMenu.Items.Clear();
        foreach (var item in MenuItems()) ContextMenu.Items.Add(item);
    }

    /// <summary>The context menu: reset zoom, copy and export each table, copy the image.</summary>
    protected virtual IEnumerable<object> MenuItems()
    {
        yield return Item("Reset zoom", ResetZoom, _view is not null);
        yield return new Separator();
        string baseName = string.IsNullOrWhiteSpace(Title) ? "plot" : Title;
        foreach (var table in Tables)
        {
            var t = table;
            yield return Item($"Copy {t.Name} as CSV", () => PlotExport.CopyText(t.Csv()));
            yield return Item($"Export {t.Name} as CSV…", () => PlotExport.SaveCsv($"{baseName} {t.Name}", t.Csv));
        }
        yield return new Separator();
        yield return Item("Copy image", () =>
        {
            Readout = null;
            DrawOverlay();
            PlotExport.CopyImage(this);
        });
    }

    internal static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Converts a data point to a pixel, for subclasses' readout lookups.</summary>
    protected static Point2 ToPixel2(PlotFrame frame, Point2 p)
    {
        var q = frame.ToPixel(p);
        return new Point2(q.X, q.Y);
    }
}
