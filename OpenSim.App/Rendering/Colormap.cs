using System.Windows.Media;
using OpenSim.Core.PostProcessing;

namespace OpenSim.App.Rendering;

/// <summary>The two fixed color maps offered outside the colormap editor (RF/SI overlays
/// and the Mechanical/Electrical result views).</summary>
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
    /// <para>
    /// The two kinds resolve to the Core presets of the same name, whose stop arrays are
    /// byte-identical to the ones that used to live here — every existing overlay, legend
    /// and result screenshot keeps its exact colors now that colormaps are editable.
    /// </para>
    /// </summary>
    public static LinearGradientBrush CreateBrush(ColormapKind kind) =>
        ColormapBrushFactory.CreateBrush(Definition(kind));

    /// <summary>The Core colormap a fixed kind stands for.</summary>
    public static ColormapDefinition Definition(ColormapKind kind) => kind == ColormapKind.Viridis
        ? ColormapDefinition.Viridis
        : ColormapDefinition.Rainbow;
}
