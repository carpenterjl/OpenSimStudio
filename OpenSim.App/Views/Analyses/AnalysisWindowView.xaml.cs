using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OpenSim.App.Views.Analyses;

public partial class AnalysisWindowView : UserControl
{
    public AnalysisWindowView() => InitializeComponent();

    /// <summary>Opens the dock menu under its button on a left click (visual behaviour only:
    /// the items are bound to the window's commands).</summary>
    private void WindowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = button.DataContext;
        menu.IsOpen = true;
    }
}
