using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OpenSim.App.Controls;

/// <summary>
/// A lucide-style stroke icon. Renders a 24x24 <see cref="Geometry"/> from
/// Themes/Icons.xaml as a round-capped 2px stroke in the control's Foreground,
/// scaled uniformly to the control size. Stroke rendering (not fill) is what keeps
/// the generated icon geometries faithful to the design's SVG masks.
/// </summary>
public sealed class IconGlyph : Control
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry), typeof(System.Windows.Media.Geometry), typeof(IconGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Windows.Media.Geometry? Geometry
    {
        get => (System.Windows.Media.Geometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    static IconGlyph()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(IconGlyph),
            new FrameworkPropertyMetadata(typeof(IconGlyph)));
        // Icons are decorative chrome; hit testing belongs to the parent button/row.
        IsHitTestVisibleProperty.OverrideMetadata(typeof(IconGlyph),
            new FrameworkPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(IconGlyph),
            new FrameworkPropertyMetadata(false));
    }
}
