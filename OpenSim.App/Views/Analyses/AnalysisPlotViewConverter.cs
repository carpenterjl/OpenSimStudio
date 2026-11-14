using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OpenSim.App.Controls.Plotting;
using OpenSim.Core.Geometry2D;
using OpenSim.Optics.Analysis;

namespace OpenSim.App.Views.Analyses;

/// <summary>
/// The element an analysis window shows for one plot: a line plot as the
/// <see cref="AnalysisPlotImageConverter"/>'s image, a scatter plot (the glass maps, OP-14) as
/// the interactive <see cref="ScatterPlot"/> of OP-7, whose readout names the point, a map as
/// OP-7's <see cref="ImagePlot"/> or <see cref="ContourPlot"/>. Other kinds
/// show nothing until they get an arm here.
/// </summary>
public sealed class AnalysisPlotViewConverter : IValueConverter
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x3D, 0x8B, 0xFD), Color.FromRgb(0xE5, 0x53, 0x4B), Color.FromRgb(0x2A, 0xC3, 0xA2),
        Color.FromRgb(0xE0, 0xAF, 0x68), Color.FromRgb(0xC3, 0x9A, 0xF0), Color.FromRgb(0x7D, 0xCF, 0xFF),
    };

    private static readonly PlotMarker[] Markers =
        { PlotMarker.Dot, PlotMarker.Square, PlotMarker.Triangle, PlotMarker.Diamond, PlotMarker.Circle, PlotMarker.Plus, PlotMarker.Cross };

    private readonly AnalysisPlotImageConverter _images = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AnalysisScatterPlot scatter => Scatter(scatter),
        AnalysisMapPlot map => Map(map),
        AnalysisPlot plot when _images.Convert(plot, typeof(ImageSource), parameter, culture) is ImageSource image =>
            new Image { Source = image, Stretch = Stretch.Uniform, MaxHeight = 320, ToolTip = plot.Title },
        _ => null
    };

    /// <summary>The scatter control for <paramref name="plot"/>: one series per catalog, its
    /// colour and marker cycling, each point labelled for the readout.</summary>
    internal static ScatterPlot Scatter(AnalysisScatterPlot plot) => new()
    {
        Title = plot.Title,
        XLabel = plot.XLabel,
        YLabel = plot.YLabel,
        EqualAspect = false,
        ShowScaleBar = false,
        Width = 560,
        Height = 360,
        Series = plot.Series.Select((s, i) => new ScatterSeries(s.Name,
            s.Points.Select(p => new Point2(p.X, p.Y)).ToArray(), Palette[i % Palette.Length],
            Markers[(i / Palette.Length) % Markers.Length], 3, s.Points.Select(p => p.Label).ToArray())).ToArray(),
    };

    /// <summary>The image or contour control for <paramref name="plot"/>.</summary>
    internal static FrameworkElement Map(AnalysisMapPlot plot) => plot.Style == AnalysisMapStyle.Contour
        ? new ContourPlot
        {
            Title = plot.Title, XLabel = plot.XLabel, YLabel = plot.YLabel, Field = plot.Field,
            ValueUnit = plot.ValueUnit, Levels = plot.Levels, Width = 480, Height = 400,
        }
        : new ImagePlot
        {
            Title = plot.Title, XLabel = plot.XLabel, YLabel = plot.YLabel, Field = plot.Field,
            ValueUnit = plot.ValueUnit, Minimum = plot.Minimum, Maximum = plot.Maximum, Width = 480, Height = 480,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
