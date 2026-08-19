using System;
using System.Globalization;
using System.Windows.Data;

namespace OpenSim.App.Controls;

/// <summary>"2 hours ago" / "Yesterday" / "Last week" for the home screen's recent list.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime utc) return "";
        var age = DateTime.UtcNow - utc.ToUniversalTime();
        return age switch
        {
            { TotalMinutes: < 2 } => "Just now",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes} minutes ago",
            { TotalHours: < 2 } => "1 hour ago",
            { TotalDays: < 1 } => $"{(int)age.TotalHours} hours ago",
            { TotalDays: < 2 } => "Yesterday",
            { TotalDays: < 7 } => $"{(int)age.TotalDays} days ago",
            { TotalDays: < 14 } => "Last week",
            { TotalDays: < 60 } => $"{(int)(age.TotalDays / 7)} weeks ago",
            _ => utc.ToLocalTime().ToString("d", culture)
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
