using System.Windows.Media;
using OpenSim.Core.PostProcessing;

namespace OpenSim.App.Rendering;

/// <summary>
/// Turns a Core <see cref="ColormapDefinition"/> into the horizontal gradient brush the
/// renderer samples through texture coordinate u ∈ [0, 1]. This is the only place a
/// colormap becomes a WPF object.
/// </summary>
public static class ColormapBrushFactory
{
    /// <summary>
    /// The gradient for a colormap. A continuous map becomes one gradient stop per color
    /// stop; a DISCRETE map doubles every interior stop (band color up to the boundary,
    /// next band's color from it) so the gradient renders as flat bands — the same trick
    /// a banded contour legend uses, and the reason no separate discrete renderer exists.
    /// </summary>
    public static LinearGradientBrush CreateBrush(ColormapDefinition colormap)
    {
        ArgumentNullException.ThrowIfNull(colormap);
        var stops = new GradientStopCollection();
        if (colormap.Discrete)
        {
            for (int band = 0; band < colormap.Stops.Count - 1; band++)
            {
                var (r, g, b) = colormap.Sample(
                    0.5 * (colormap.Stops[band].Position + colormap.Stops[band + 1].Position));
                var color = Color.FromRgb(r, g, b);
                stops.Add(new GradientStop(color, colormap.Stops[band].Position));
                stops.Add(new GradientStop(color, colormap.Stops[band + 1].Position));
            }
        }
        else
        {
            foreach (var stop in colormap.Stops)
                stops.Add(new GradientStop(Color.FromRgb(stop.R, stop.G, stop.B), stop.Position));
        }

        var brush = new LinearGradientBrush(stops,
            new System.Windows.Point(0, 0.5), new System.Windows.Point(1, 0.5));
        brush.Freeze();
        return brush;
    }
}
