using System.Windows;
using System.Windows.Controls;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views.Panels;

public partial class OpticsPanel : UserControl
{
    private LensDataEditorWindow? _lensEditor;
    private MultiConfigEditorWindow? _multiConfigEditor;

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

    // OP-54
    private MeritFunctionEditorWindow? _meritEditor;

    /// <summary>Opens the Merit Function Editor (OP-54), or brings the open one forward.</summary>
    private void MeritFunctionEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_meritEditor is { IsLoaded: true })
        {
            _meritEditor.Activate();
            return;
        }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _meritEditor = new MeritFunctionEditorWindow(optics.MeritEditor) { Owner = Window.GetWindow(this) };
        _meritEditor.Closed += (_, _) => _meritEditor = null;
        _meritEditor.Show();
    }

    // OP-63
    private ToleranceDataEditorWindow? _toleranceEditor;

    /// <summary>Opens the Tolerance Data Editor (OP-63), or brings the open one forward.</summary>
    private void ToleranceDataEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_toleranceEditor is { IsLoaded: true })
        {
            _toleranceEditor.Activate();
            return;
        }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _toleranceEditor = new ToleranceDataEditorWindow(optics.ToleranceEditor) { Owner = Window.GetWindow(this) };
        _toleranceEditor.Closed += (_, _) => _toleranceEditor = null;
        _toleranceEditor.Show();
    }

    // OP-57
    private OptimizeWindow? _optimize;

    /// <summary>Opens the Optimize tool (OP-57), or brings the open one forward.</summary>
    private void Optimize_Click(object sender, RoutedEventArgs e)
    {
        if (_optimize is { IsLoaded: true })
        {
            _optimize.Activate();
            return;
        }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _optimize = new OptimizeWindow(optics.Optimize) { Owner = Window.GetWindow(this) };
        _optimize.Closed += (_, _) => _optimize = null;
        _optimize.Show();
    }

    /// <summary>Opens the Multi-Configuration Editor (OP-30), or brings the open one forward.</summary>
    private void MultiConfigEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_multiConfigEditor is { IsLoaded: true })
        {
            _multiConfigEditor.Activate();
            return;
        }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _multiConfigEditor = new MultiConfigEditorWindow(optics.MultiConfig) { Owner = Window.GetWindow(this) };
        _multiConfigEditor.Closed += (_, _) => _multiConfigEditor = null;
        _multiConfigEditor.Show();
    }
}
