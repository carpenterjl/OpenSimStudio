using System.Windows.Media;

namespace OpenSim.App.Rendering;

/// <summary>Color maps for scalar result visualization.</summary>
public enum ColormapKind
{
    Rainbow,
    Viridis
}

public static class Colormap
{
    /// <summary>
    /// A horizontal gradient brush spanning texture coordinate 0..1; meshes map a
    /// normalized scalar to the U texture coordinate to get per-vertex coloring.
    /// </summary>
    public static LinearGradientBrush CreateBrush(ColormapKind kind)
    {
        var stops = new GradientStopCollection();
        var samples = kind == ColormapKind.Viridis ? ViridisStops : RainbowStops;
        foreach (var (offset, color) in samples)
            stops.Add(new GradientStop(color, offset));
        var brush = new LinearGradientBrush(stops, new System.Windows.Point(0, 0.5), new System.Windows.Point(1, 0.5));
        brush.Freeze();
        return brush;
    }

    private static readonly (double, Color)[] RainbowStops =
    {
        (0.00, Color.FromRgb(0, 0, 255)),
        (0.25, Color.FromRgb(0, 255, 255)),
        (0.50, Color.FromRgb(0, 255, 0)),
        (0.75, Color.FromRgb(255, 255, 0)),
        (1.00, Color.FromRgb(255, 0, 0))
    };

    private static readonly (double, Color)[] ViridisStops =
    {
        (0.00, Color.FromRgb(68, 1, 84)),
        (0.25, Color.FromRgb(59, 82, 139)),
        (0.50, Color.FromRgb(33, 145, 140)),
        (0.75, Color.FromRgb(94, 201, 98)),
        (1.00, Color.FromRgb(253, 231, 37))
    };
}
