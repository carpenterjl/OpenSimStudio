using System.Windows;
using System.Windows.Media;

namespace OpenSim.App.Controls.Plotting;

/// <summary>
/// The brushes an interactive plot draws its chrome with, taken from the theme tokens
/// (Themes/Tokens.*.xaml) where the plot sits in a themed tree. Outside one (a test, a bitmap
/// export) it falls back to the ink of <see cref="CurvePlot"/> and <see cref="FieldMapPlot"/>,
/// which reads on both themes, so the interactive and static plots look alike.
/// </summary>
public sealed record PlotPalette(Brush Ink, Pen Grid, Pen Frame, Brush Text, Brush Accent,
    Brush ReadoutBackground, Pen ReadoutBorder)
{
    private static readonly Color FallbackInk = Color.FromRgb(0x8A, 0x90, 0x99);

    /// <summary>The series colours of the existing RF plots (AntennaViewModel.Ports), in order.</summary>
    public static IReadOnlyList<Color> SeriesColors { get; } = new[]
    {
        Color.FromRgb(0x3D, 0x8B, 0xFD), Color.FromRgb(0xE0, 0x6C, 0x2B), Color.FromRgb(0x2E, 0xA0, 0x5A),
        Color.FromRgb(0xA0, 0x4C, 0xC8), Color.FromRgb(0xE5, 0x53, 0x4B), Color.FromRgb(0x9A, 0xA0, 0xA6)
    };

    /// <summary>The palette for <paramref name="element"/>'s place in the resource tree.</summary>
    public static PlotPalette Resolve(FrameworkElement element)
    {
        Brush Find(string key, Color fallback) =>
            element.TryFindResource(key) as Brush ?? Frozen(new SolidColorBrush(fallback));
        var ink = Find("TextFaintBrush", FallbackInk);
        var inkColor = (ink as SolidColorBrush)?.Color ?? FallbackInk;
        var grid = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x50, inkColor.R, inkColor.G, inkColor.B))), 1);
        grid.Freeze();
        var frame = new Pen(ink, 1);
        frame.Freeze();
        var border = new Pen(Find("PanelBorderBrush", FallbackInk), 1);
        border.Freeze();
        return new PlotPalette(ink, grid, frame,
            Find("TextBodyBrush", FallbackInk),
            Find("AccentBrush", Color.FromRgb(0x3D, 0x8B, 0xFD)),
            Find("PanelBgRaisedBrush", Color.FromArgb(0xE6, 0x17, 0x1F, 0x33)),
            border);
    }

    internal static Brush Frozen(Brush brush)
    {
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }
}
