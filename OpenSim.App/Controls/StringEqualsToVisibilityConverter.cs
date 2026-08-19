using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OpenSim.App.Controls;

/// <summary>
/// Visible when the bound value's string form equals the converter parameter — the
/// properties pane uses it to show the panel group of the active study step
/// (Binding StudyRail.ActiveStepName, ConverterParameter=Setup).
/// </summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
