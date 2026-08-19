using System.ComponentModel;
using System.Windows;
using OpenSim.App.Services;
using OpenSim.App.ViewModels;

namespace OpenSim.App;

/// <summary>
/// Code-behind limited to visual behaviour: custom window chrome (min/max/close), the
/// ribbon's zoom-to-fit relay into the viewport, log auto-scroll + collapse, and
/// progress-bar visibility. Viewport interaction lives in <see cref="Views.Viewport3DView"/>.
/// The docking manager is gone: the design's shell is a fixed rail/steps/properties
/// layout, so there is no arrangement to persist (layout.v2.xml is simply ignored).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.Log.Entries.CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0)
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    LogList.ScrollIntoView(LogList.Items[^1]);
                }), System.Windows.Threading.DispatcherPriority.Background);
        };
        viewModel.Session.PropertyChanged += OnSessionPropertyChanged;
        SolveProgress.Visibility = Visibility.Collapsed;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectSession.IsBusy))
            SolveProgress.Visibility = _viewModel.Session.IsBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Custom chrome ----------------

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------- Ribbon visual relays ----------------

    /// <summary>The ribbon's Fit button: pure camera behaviour, so it goes straight to
    /// the viewport (the hit-testing / zoom-to-fit code-behind exemption).</summary>
    private void Fit_Click(object sender, RoutedEventArgs e) => ViewportView.ZoomToFit();

    private bool _logCollapsed;

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        _logCollapsed = !_logCollapsed;
        LogList.Visibility = _logCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LogChevron.LayoutTransform = _logCollapsed
            ? new System.Windows.Media.RotateTransform(180)
            : null;
    }
}
