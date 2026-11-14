using System.Windows.Media;
using OpenSim.Optics.Layout;

namespace OpenSim.App.Rendering;

/// <summary>
/// How the optical layout views look: which parts are shown, colours, the lens opacity, and
/// line widths as fractions of the layout's size (so a 5 mm eyepiece and a 2 m telescope
/// draw with the same visual weight).
/// </summary>
public sealed record OpticalSceneStyle
{
    public bool ShowShaded { get; init; } = true;
    public bool ShowWireframe { get; init; }
    public bool ShowRays { get; init; } = true;

    public RayColoring Coloring { get; init; } = RayColoring.ByField;

    /// <summary>Leave out rays that failed instead of drawing them to where they stopped.</summary>
    public bool DeleteVignetted { get; init; }

    public Color LensColor { get; init; } = Color.FromRgb(0x8C, 0xC8, 0xF0);
    public Color MirrorColor { get; init; } = Color.FromRgb(0xC8, 0xCC, 0xD4);
    public Color ApertureColor { get; init; } = Color.FromRgb(0x40, 0x40, 0x48);
    public Color WireColor { get; init; } = Color.FromRgb(0x30, 0x30, 0x30);

    /// <summary>Opacity of lens bodies (0 to 1). Below 1 the lenses are transparent and are
    /// re-sorted back to front on every camera move.</summary>
    public double LensOpacity { get; init; } = 0.45;

    /// <summary>Ray ribbon width, as a fraction of the layout's bounding-box diagonal.</summary>
    public double RayWidthFraction { get; init; } = 0.0015;

    /// <summary>Wireframe ribbon width, as a fraction of the layout's bounding-box diagonal.</summary>
    public double WireWidthFraction { get; init; } = 0.001;

    public LatheResolution Resolution { get; init; } = LatheResolution.Default;

    public static OpticalSceneStyle Default { get; } = new();

    /// <summary>The colour of ray colour index <paramref name="key"/> (field or wavelength
    /// index). Cycles past the palette's end.</summary>
    public Color RayColor(int key) =>
        RayPalette[((key % RayPalette.Length) + RayPalette.Length) % RayPalette.Length];

    // A display palette, not a reference value: distinct hues in a fixed order.
    private static readonly Color[] RayPalette =
    {
        Color.FromRgb(0x1F, 0x5F, 0xD0),   // blue
        Color.FromRgb(0x2E, 0xA0, 0x3A),   // green
        Color.FromRgb(0xD0, 0x30, 0x30),   // red
        Color.FromRgb(0xE0, 0x9A, 0x10),   // amber
        Color.FromRgb(0x8E, 0x3C, 0xC8),   // violet
        Color.FromRgb(0x10, 0xA8, 0xB0),   // teal
        Color.FromRgb(0xC8, 0x48, 0x9C),   // magenta
        Color.FromRgb(0x70, 0x70, 0x70),   // grey
    };
}
