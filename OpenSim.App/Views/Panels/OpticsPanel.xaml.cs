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

    // OP-60
    private QuickFocusWindow? _quickFocus;
    private QuickAdjustWindow? _quickAdjust;
    private SliderWindow? _slider;
    private VisualOptimizerWindow? _visualOptimizer;

    /// <summary>Opens Quick Focus (OP-60), or brings the open one forward.</summary>
    private void QuickFocus_Click(object sender, RoutedEventArgs e)
    {
        if (_quickFocus is { IsLoaded: true }) { _quickFocus.Activate(); return; }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _quickFocus = new QuickFocusWindow(optics.QuickFocus) { Owner = Window.GetWindow(this) };
        _quickFocus.Closed += (_, _) => _quickFocus = null;
        _quickFocus.Show();
    }

    /// <summary>Opens Quick Adjust (OP-60), or brings the open one forward.</summary>
    private void QuickAdjust_Click(object sender, RoutedEventArgs e)
    {
        if (_quickAdjust is { IsLoaded: true }) { _quickAdjust.Activate(); return; }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _quickAdjust = new QuickAdjustWindow(optics.QuickAdjust) { Owner = Window.GetWindow(this) };
        _quickAdjust.Closed += (_, _) => _quickAdjust = null;
        _quickAdjust.Show();
    }

    /// <summary>Opens the Slider (OP-60), or brings the open one forward.</summary>
    private void Slider_Click(object sender, RoutedEventArgs e)
    {
        if (_slider is { IsLoaded: true }) { _slider.Activate(); return; }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _slider = new SliderWindow(optics.Slider) { Owner = Window.GetWindow(this) };
        _slider.Closed += (_, _) => _slider = null;
        _slider.Show();
    }

    /// <summary>Opens the Visual Optimizer (OP-60), or brings the open one forward.</summary>
    private void VisualOptimizer_Click(object sender, RoutedEventArgs e)
    {
        if (_visualOptimizer is { IsLoaded: true }) { _visualOptimizer.Activate(); return; }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _visualOptimizer = new VisualOptimizerWindow(optics.VisualOptimizer) { Owner = Window.GetWindow(this) };
        _visualOptimizer.Closed += (_, _) => _visualOptimizer = null;
        _visualOptimizer.Show();
    }

    // OP-17
    private TestPlateFittingWindow? _testPlateFitting;

    /// <summary>Opens Test Plate Fitting (OP-17), or brings the open one forward.</summary>
    private void TestPlateFitting_Click(object sender, RoutedEventArgs e)
    {
        if (_testPlateFitting is { IsLoaded: true }) { _testPlateFitting.Activate(); return; }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _testPlateFitting = new TestPlateFittingWindow(optics.TestPlates) { Owner = Window.GetWindow(this) };
        _testPlateFitting.Closed += (_, _) => _testPlateFitting = null;
        _testPlateFitting.Show();
    }

    // OP-61
    private DesignToolsWindow? _designTools;

    /// <summary>Opens the design conversion tools (OP-61), or brings the open window forward.</summary>
    private void DesignTools_Click(object sender, RoutedEventArgs e)
    {
        if (_designTools is { IsLoaded: true }) { _designTools.Activate(); return; }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _designTools = new DesignToolsWindow(optics.DesignTools) { Owner = Window.GetWindow(this) };
        _designTools.Closed += (_, _) => _designTools = null;
        _designTools.Show();
    }

    // OP-16
    private LensCatalogWindow? _lensCatalogs;

    /// <summary>Opens Lens Catalogs (OP-16), or brings the open one forward.</summary>
    private void LensCatalogs_Click(object sender, RoutedEventArgs e)
    {
        if (_lensCatalogs is { IsLoaded: true }) { _lensCatalogs.Activate(); return; }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _lensCatalogs = new LensCatalogWindow(optics.LensCatalog) { Owner = Window.GetWindow(this) };
        _lensCatalogs.Closed += (_, _) => _lensCatalogs = null;
        _lensCatalogs.Show();
    }

    // OP-99
    private MacroEditorWindow? _macroEditor;

    /// <summary>Opens the ZPL macro window (OP-99), or brings the open one forward.</summary>
    private void Macros_Click(object sender, RoutedEventArgs e)
    {
        if (_macroEditor is { IsLoaded: true }) { _macroEditor.Activate(); return; }
        if (DataContext is not MainViewModel { Optics: { } optics }) return;
        _macroEditor = new MacroEditorWindow(optics.Macros) { Owner = Window.GetWindow(this) };
        _macroEditor.Closed += (_, _) => _macroEditor = null;
        _macroEditor.Show();
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
