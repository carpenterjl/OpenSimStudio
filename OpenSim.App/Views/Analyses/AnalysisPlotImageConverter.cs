using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using OpenSim.App.Controls;
using OpenSim.Optics.Analysis;

namespace OpenSim.App.Views.Analyses;

/// <summary>
/// Draws an analysis's plot data for the window. The analyses produce plots as data (they run
/// in the command line too); drawing is the view's business. Line plots go to
/// <see cref="CurvePlot"/>; the kinds OP-7 adds get their arms here.
/// </summary>
public sealed class AnalysisPlotImageConverter : IValueConverter
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x3D, 0x8B, 0xFD), Color.FromRgb(0xE5, 0x53, 0x4B), Color.FromRgb(0x2A, 0xC3, 0xA2),
        Color.FromRgb(0xE0, 0xAF, 0x68), Color.FromRgb(0xC3, 0x9A, 0xF0), Color.FromRgb(0x7D, 0xCF, 0xFF),
    };

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LinePlot line => CurvePlot.Render(
            line.Curves.Select((c, i) => new PlotSeries(c.Name, c.X, c.Y, Palette[i % Palette.Length], c.Dashed)).ToList(),
            line.LogX, line.LogY, line.XLabel, line.YLabel),
        _ => null
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
