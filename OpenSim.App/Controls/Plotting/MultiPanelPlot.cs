using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenSim.Core.Plotting;

namespace OpenSim.App.Controls.Plotting;

/// <summary>The cell layout of a multi-panel figure: a uniform grid filled row by row.</summary>
public static class PanelGrid
{
    /// <summary>Columns for <paramref name="count"/> panels: <paramref name="requested"/> when
    /// positive (never more than the panels), otherwise the smallest square-ish grid,
    /// ceil(sqrt(count)).</summary>
    public static int Columns(int count, int requested)
    {
        if (count <= 0) return 1;
        if (requested > 0) return Math.Min(requested, count);
        return (int)Math.Ceiling(Math.Sqrt(count) - 1e-9);
    }

    /// <summary>The cell rectangles for <paramref name="count"/> panels in <paramref name="size"/>,
    /// row-major.</summary>
    public static Rect[] Cells(int count, int columns, Size size)
    {
        var cells = new Rect[Math.Max(0, count)];
        if (count <= 0) return cells;
        columns = Math.Max(1, columns);
        int rows = (count + columns - 1) / columns;
        double w = size.Width / columns, h = size.Height / rows;
        for (int i = 0; i < count; i++) cells[i] = new Rect(i % columns * w, i / columns * h, w, h);
        return cells;
    }

    /// <summary>
    /// Views that give every panel one scale, each centred on its own data: the widest and tallest
    /// of the panels' automatic ranges, placed about each panel's centre. Spot diagrams of
    /// different fields sit at different image heights, so a shared absolute range would show
    /// most of them empty; a shared scale compares their sizes honestly.
    /// </summary>
    public static PlotRange?[] UniformViews(IReadOnlyList<PlotRange?> ranges)
    {
        double width = 0, height = 0;
        foreach (var r in ranges)
            if (r is { } v) { width = Math.Max(width, v.Width); height = Math.Max(height, v.Height); }
        var views = new PlotRange?[ranges.Count];
        for (int i = 0; i < ranges.Count; i++)
            if (ranges[i] is { } v && width > 0 && height > 0)
            {
                double cx = 0.5 * (v.X0 + v.X1), cy = 0.5 * (v.Y0 + v.Y1);
                views[i] = new PlotRange(cx - width / 2, cx + width / 2, cy - height / 2, cy + height / 2);
            }
        return views;
    }

    /// <summary>The view for a linked panel when another panel's view changes: the source's view
    /// size, offset from the panel's own data centre as the source's view is from its own.</summary>
    public static PlotRange Follow(PlotRange sourceView, PlotRange sourceData, PlotRange targetData)
    {
        double dx = 0.5 * (targetData.X0 + targetData.X1) - 0.5 * (sourceData.X0 + sourceData.X1);
        double dy = 0.5 * (targetData.Y0 + targetData.Y1) - 0.5 * (sourceData.Y0 + sourceData.Y1);
        return new PlotRange(sourceView.X0 + dx, sourceView.X1 + dx, sourceView.Y0 + dy, sourceView.Y1 + dy);
    }
}

/// <summary>
/// A figure of several plots in a uniform grid, one panel per field (or configuration, or
/// wavelength). Each child is an ordinary plot with its own title, readout and menu. With
/// <see cref="LinkViews"/> the panels share one scale: they start at the uniform scale of
/// <see cref="PanelGrid.UniformViews"/>, and a zoom or pan in one is followed by the others about
/// their own centres. The figure's menu (on the gaps between panels) and <see cref="ExportCsv"/>
/// export every panel's tables in one file, a '#' title line before each.
/// </summary>
public sealed class MultiPanelPlot : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int),
        typeof(MultiPanelPlot), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty LinkViewsProperty = DependencyProperty.Register(nameof(LinkViews), typeof(bool),
        typeof(MultiPanelPlot), new FrameworkPropertyMetadata(false, (d, _) => ((MultiPanelPlot)d).ApplyUniform()));

    /// <summary>Columns of the grid; 0 for ceil(sqrt(panels)).</summary>
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public bool LinkViews { get => (bool)GetValue(LinkViewsProperty); set => SetValue(LinkViewsProperty, value); }

    private bool _syncing;

    public MultiPanelPlot()
    {
        Background = Brushes.Transparent;
        ContextMenu = new ContextMenu();
    }

    public IEnumerable<InteractivePlot> Plots => InternalChildren.OfType<InteractivePlot>();

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = InternalChildren.Count;
        int columns = PanelGrid.Columns(count, Columns);
        int rows = Math.Max(1, (count + columns - 1) / columns);
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 360 * columns : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 280 * rows : availableSize.Height);
        var cells = PanelGrid.Cells(count, columns, size);
        for (int i = 0; i < count; i++) InternalChildren[i].Measure(cells[i].Size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = InternalChildren.Count;
        var cells = PanelGrid.Cells(count, PanelGrid.Columns(count, Columns), finalSize);
        for (int i = 0; i < count; i++) InternalChildren[i].Arrange(cells[i]);
        return finalSize;
    }

    protected override void OnVisualChildrenChanged(DependencyObject? visualAdded, DependencyObject? visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is InteractivePlot removed) removed.ViewChanged -= OnChildViewChanged;
        if (visualAdded is InteractivePlot added) added.ViewChanged += OnChildViewChanged;
        ApplyUniform();
    }

    private void OnChildViewChanged(object? sender, EventArgs e)
    {
        if (_syncing || !LinkViews || sender is not InteractivePlot source) return;
        if (source.View is not { } view || source.DataRange is not { } sourceData) { ApplyUniform(); return; }
        _syncing = true;
        try
        {
            foreach (var plot in Plots)
                if (plot != source && plot.DataRange is { } data)
                    plot.SetView(PanelGrid.Follow(view, sourceData, data));
        }
        finally { _syncing = false; }
    }

    /// <summary>Puts every panel at the shared scale (when linked); a reset in any panel lands here.</summary>
    public void ApplyUniform()
    {
        if (!LinkViews || _syncing) return;
        var plots = Plots.ToArray();
        var views = PanelGrid.UniformViews(plots.Select(p => p.DataRange).ToArray());
        _syncing = true;
        try
        {
            for (int i = 0; i < plots.Length; i++) plots[i].SetView(views[i]);
        }
        finally { _syncing = false; }
    }

    /// <summary>Every panel's tables as one CSV text, a "# title: table" line before each.</summary>
    public string ExportCsv() => PlotCsv.Sections(Plots.SelectMany((plot, k) =>
    {
        string title = string.IsNullOrWhiteSpace(plot.Title) ? $"panel {k + 1}" : plot.Title;
        return plot.Tables.Select(t => ($"{title}: {t.Name}", t.Csv()));
    }));

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        base.OnContextMenuOpening(e);
        if (ContextMenu is null) return;
        ContextMenu.Items.Clear();
        ContextMenu.Items.Add(InteractivePlot.Item("Copy all panels as CSV", () => PlotExport.CopyText(ExportCsv())));
        ContextMenu.Items.Add(InteractivePlot.Item("Export all panels as CSV…", () => PlotExport.SaveCsv("figure", ExportCsv)));
        ContextMenu.Items.Add(InteractivePlot.Item("Reset all zoom", () =>
        {
            if (LinkViews) ApplyUniform();
            else foreach (var plot in Plots) plot.ResetZoom();
        }));
        ContextMenu.Items.Add(new Separator());
        ContextMenu.Items.Add(InteractivePlot.Item("Copy image", () => PlotExport.CopyImage(this)));
    }
}
