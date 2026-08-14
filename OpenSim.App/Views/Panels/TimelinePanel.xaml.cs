using System.Windows.Controls;

namespace OpenSim.App.Views.Panels;

/// <summary>
/// Result playback timeline, overlaid on the viewport. Everything it shows comes from
/// <see cref="ViewModels.ResultsViewModel"/>; the tick label positions are computed there
/// too (<see cref="ViewModels.TimelineTickMark"/>), so there is no visual behaviour left
/// for code-behind to own.
/// </summary>
public partial class TimelinePanel : UserControl
{
    public TimelinePanel() => InitializeComponent();
}
