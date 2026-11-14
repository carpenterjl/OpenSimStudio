using System.Windows;
using System.Windows.Controls;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views.Panels;

public partial class OpticsPanel : UserControl
{
    private LensDataEditorWindow? _lensEditor;

    public OpticsPanel() => InitializeComponent();

    /// <summary>Opens the Lens Data Editor (OP-31), or brings the open one forward.</summary>
    private void LensDataEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_lensEditor is { IsLoaded: true })
        {
            _lensEditor.Activate();
            return;
        }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _lensEditor = new LensDataEditorWindow(optics.LensEditor) { Owner = Window.GetWindow(this) };
        _lensEditor.Closed += (_, _) => _lensEditor = null;
        _lensEditor.Show();
    }
}
