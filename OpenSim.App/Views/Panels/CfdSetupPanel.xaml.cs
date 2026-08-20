using System.Windows.Controls;

namespace OpenSim.App.Views.Panels;

/// <summary>Code-behind exists solely to call InitializeComponent: a UserControl XAML
/// without one compiles and then renders EMPTY.</summary>
public partial class CfdSetupPanel : UserControl
{
    public CfdSetupPanel() => InitializeComponent();
}
