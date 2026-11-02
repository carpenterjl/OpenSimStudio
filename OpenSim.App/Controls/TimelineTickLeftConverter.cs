using System.Globalization;
using System.Windows.Data;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Controls;

/// <summary>Places a timeline label: the frame's axis fraction and the track's current width
/// give its left edge, so the labels follow the panel when the pane is resized.</summary>
public sealed class TimelineTickLeftConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [double fraction, double width] ? TimelineTickMark.LeftAt(fraction, width) : 0.0;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
