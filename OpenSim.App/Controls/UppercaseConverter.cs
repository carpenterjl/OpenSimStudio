using System;
using System.Globalization;
using System.Windows.Data;

namespace OpenSim.App.Controls;

/// <summary>
/// Uppercases header text for the design's micro-header sections. WPF TextBlocks have
/// no CharacterCasing, so the GroupBox template routes its Header through this.
/// </summary>
public sealed class UppercaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString()?.ToUpperInvariant();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
