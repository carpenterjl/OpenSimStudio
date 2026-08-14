using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views.Panels;

public partial class ColormapEditorPanel : UserControl
{
    public ColormapEditorPanel() => InitializeComponent();

    /// <summary>
    /// Dragging a stop marker. Pure visual behavior — pixels are turned into a colormap
    /// position here and the view model decides what is legal (endpoints are pinned,
    /// neighbours clamp). The drag is measured against the stop's CURRENT position each
    /// tick rather than a captured start, because the view model moves the stop as we go.
    /// </summary>
    private void StopThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { DataContext: ColormapStopViewModel stop }) return;
        if (DataContext is not MainViewModel main) return;
        main.Colormap.MoveStop(stop.Index,
            stop.Position + e.HorizontalChange / ColormapStopViewModel.StripWidth);
    }
}
