using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Core.PostProcessing;

namespace OpenSim.App.ViewModels;

/// <summary>
/// One row of the colormap editor's stop list: a color anchor's position, its swatch, and
/// where its marker sits on the gradient strip.
/// <para>
/// A row is a READ-ONLY projection of a <see cref="ColormapStop"/> — edits go back through
/// <see cref="ColormapViewModel"/>, which rebuilds the whole definition, because the stop
/// list is an ordered validated structure and a row mutating its own position would let it
/// fall out of order. The row OBJECT is nevertheless reused when a definition of the same
/// shape replaces it: dragging a marker rebuilds the definition on every mouse move, and a
/// fresh row would destroy the very <c>Thumb</c> the mouse is captured on, ending the drag
/// after one pixel.
/// </para>
/// </summary>
public partial class ColormapStopViewModel : ObservableObject
{
    /// <summary>Pixel width of the gradient strip the marker positions are measured in;
    /// the editor's preview and its marker canvas share it.</summary>
    public const double StripWidth = 200;

    /// <summary>Half the marker glyph's width, so a marker centers on its position.</summary>
    private const double MarkerHalfWidth = 6;

    private SolidColorBrush _swatch;

    public ColormapStopViewModel(int index, ColormapStop stop, bool isEndpoint)
    {
        Index = index;
        IsEndpoint = isEndpoint;
        _stop = stop;
        _swatch = Freeze(stop);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Swatch), nameof(Position), nameof(MarkerLeft),
        nameof(Hex), nameof(Label))]
    private ColormapStop _stop;

    public int Index { get; }

    /// <summary>The first and last stops are pinned at 0 and 1 (the map must span the whole
    /// axis), so they can be recolored but never moved or removed.</summary>
    public bool IsEndpoint { get; }

    public Brush Swatch => _swatch;

    public double Position => Stop.Position;

    /// <summary>Left edge of this stop's marker on the strip, in device-independent pixels.</summary>
    public double MarkerLeft => Stop.Position * StripWidth - MarkerHalfWidth;

    public string Hex => $"#{Stop.R:X2}{Stop.G:X2}{Stop.B:X2}";

    /// <summary>List label: "0.50  #21918C".</summary>
    public string Label => $"{Stop.Position:0.00}  {Hex}";

    partial void OnStopChanged(ColormapStop value) => _swatch = Freeze(value);

    private static SolidColorBrush Freeze(ColormapStop stop)
    {
        var brush = new SolidColorBrush(Color.FromRgb(stop.R, stop.G, stop.B));
        brush.Freeze();
        return brush;
    }
}
