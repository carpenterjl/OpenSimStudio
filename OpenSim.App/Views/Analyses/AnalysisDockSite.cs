using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OpenSim.App.Services;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views.Analyses;

/// <summary>
/// Visual behaviour of the analysis dock: sizing the zones to what they hold, carrying the
/// splitter positions to the view model so they are saved, and opening, hiding and closing the
/// floating windows as the layout's floating list changes. Where windows go is decided by the
/// layout (<see cref="Analyses.DockLayout{T}"/>); nothing here moves one.
/// </summary>
public sealed class AnalysisDockSite : Grid
{
    public static readonly DependencyProperty CenterProperty = DependencyProperty.Register(
        nameof(Center), typeof(UIElement), typeof(AnalysisDockSite),
        new PropertyMetadata(null, (d, e) => ((AnalysisDockSite)d).OnCenterChanged((UIElement?)e.NewValue)));

    private readonly Dictionary<AnalysisWindowViewModel, AnalysisFloatWindow> _floats = new();
    private readonly ColumnDefinition _rightColumn = new() { Width = new GridLength(0) };
    private readonly RowDefinition _bottomRow = new() { Height = new GridLength(0) };
    private readonly Grid _centerCell = new();
    private readonly AnalysisDockZone _rightZone = new() { Vertical = true, Padding = new Thickness(5, 0, 0, 0) };
    private readonly AnalysisDockZone _bottomZone = new() { Padding = new Thickness(0, 5, 0, 0) };
    private readonly GridSplitter _rightSplitter = new()
    {
        Width = 5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch,
        ResizeBehavior = GridResizeBehavior.PreviousAndCurrent, ResizeDirection = GridResizeDirection.Columns,
        Background = Brushes.Transparent,
    };
    private readonly GridSplitter _bottomSplitter = new()
    {
        Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
        ResizeBehavior = GridResizeBehavior.PreviousAndCurrent, ResizeDirection = GridResizeDirection.Rows,
        Background = Brushes.Transparent,
    };
    private MainViewModel? _main;
    private bool _closingFromLayout;

    /// <summary>Built in code, not XAML: a control with its own XAML has its own name scope, and
    /// the viewport placed in <see cref="Center"/> keeps the name MainWindow gives it.</summary>
    public AnalysisDockSite()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        ColumnDefinitions.Add(_rightColumn);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        RowDefinitions.Add(_bottomRow);

        Children.Add(_centerCell);
        Place(_rightZone, row: 0, column: 1, rowSpan: 2);
        Place(_rightSplitter, row: 0, column: 1, rowSpan: 2);
        Place(_bottomZone, row: 1, column: 0, rowSpan: 1);
        Place(_bottomSplitter, row: 1, column: 0, rowSpan: 1);
        _rightZone.SetResourceReference(Control.BackgroundProperty, "AppBgBrush");
        _bottomZone.SetResourceReference(Control.BackgroundProperty, "AppBgBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_rightZone, "AnalysisDockRight");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_bottomZone, "AnalysisDockBottom");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_rightSplitter, "AnalysisDockRightSplitter");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_bottomSplitter, "AnalysisDockBottomSplitter");
        foreach (UIElement element in new UIElement[] { _rightZone, _rightSplitter, _bottomZone, _bottomSplitter })
            element.Visibility = Visibility.Collapsed;
        _rightSplitter.DragCompleted += RightSplitter_DragCompleted;
        _bottomSplitter.DragCompleted += BottomSplitter_DragCompleted;

        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Refresh();
    }

    private void Place(UIElement element, int row, int column, int rowSpan)
    {
        SetRow(element, row);
        SetColumn(element, column);
        SetRowSpan(element, rowSpan);
        Children.Add(element);
    }

    /// <summary>What the docks surround: the viewport.</summary>
    public UIElement? Center
    {
        get => (UIElement?)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    private void OnCenterChanged(UIElement? element)
    {
        _centerCell.Children.Clear();
        if (element is not null) _centerCell.Children.Add(element);
    }

    private void Attach()
    {
        if (_main is not null || DataContext is not MainViewModel main) return;
        _main = main;
        var host = main.OpticsAnalyses;
        _rightZone.Panes = host.Layout.Right;
        _bottomZone.Panes = host.Layout.Bottom;
        host.Layout.Changed += (_, _) => Refresh();
        host.Layout.Floating.CollectionChanged += OnFloatingChanged;
        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OpticsAnalysesViewModel.RightDockWidth) or nameof(OpticsAnalysesViewModel.BottomDockHeight))
                Refresh();
        };
        main.Session.PropertyChanged += OnSessionChanged;
        Refresh();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectSession.ActiveWorkspace) or nameof(ProjectSession.IsHomeActive))
            Refresh();
    }

    /// <summary>The docks show in the Optics workspace only.</summary>
    private bool DocksShown => _main is { Session: { IsHomeActive: false, ActiveWorkspace: WorkspaceKind.Optics } };

    private void Refresh()
    {
        if (_main is null) return;
        var host = _main.OpticsAnalyses;
        bool shown = DocksShown;
        bool right = shown && host.Layout.Right.Count > 0;
        bool bottom = shown && host.Layout.Bottom.Count > 0;
        _rightColumn.Width = right ? new GridLength(host.RightDockWidth) : new GridLength(0);
        _rightColumn.MinWidth = right ? 180 : 0;
        _bottomRow.Height = bottom ? new GridLength(host.BottomDockHeight) : new GridLength(0);
        _bottomRow.MinHeight = bottom ? 100 : 0;
        _rightZone.Visibility = _rightSplitter.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        _bottomZone.Visibility = _bottomSplitter.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;
        SyncFloats();
    }

    private void RightSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_main is not null && _rightColumn.ActualWidth > 0) _main.OpticsAnalyses.RightDockWidth = _rightColumn.ActualWidth;
    }

    private void BottomSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_main is not null && _bottomRow.ActualHeight > 0) _main.OpticsAnalyses.BottomDockHeight = _bottomRow.ActualHeight;
    }

    // ---------------- Floating windows ----------------

    private void OnFloatingChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncFloats();

    /// <summary>One window per floating analysis: made when it floats, closed when it docks or
    /// closes, hidden while another workspace is on show.</summary>
    private void SyncFloats()
    {
        if (_main is null) return;
        var floating = _main.OpticsAnalyses.Layout.Floating;
        foreach (var (vm, window) in _floats.Where(p => !floating.Contains(p.Key)).ToList())
        {
            _floats.Remove(vm);
            _closingFromLayout = true;
            try { window.Close(); }
            finally { _closingFromLayout = false; }
        }
        if (!IsLoaded) return;
        bool shown = DocksShown;
        foreach (var vm in floating)
        {
            if (!_floats.TryGetValue(vm, out var window))
            {
                if (!shown) continue;
                window = Create(vm);
                _floats.Add(vm, window);
            }
            if (shown && !window.IsVisible) window.Show();
            else if (!shown && window.IsVisible) window.Hide();
        }
    }

    private AnalysisFloatWindow Create(AnalysisWindowViewModel vm)
    {
        var owner = Window.GetWindow(this);
        var window = new AnalysisFloatWindow { DataContext = vm, Owner = owner };
        if (vm.FloatWidth is > 0 and var w) window.Width = w;
        if (vm.FloatHeight is > 0 and var h) window.Height = h;
        if (vm.FloatLeft is { } left && vm.FloatTop is { } top && OnScreen(left, top, window.Width, window.Height))
        {
            window.Left = left;
            window.Top = top;
        }
        else if (owner is not null)
        {
            // Not on show before: cascade from the owner's centre.
            int offset = 24 * (_floats.Count % 8);
            window.Left = owner.Left + Math.Max(0, (owner.ActualWidth - window.Width) / 2) + offset;
            window.Top = owner.Top + Math.Max(0, (owner.ActualHeight - window.Height) / 2) + offset;
        }
        window.LocationChanged += (_, _) => Remember(vm, window);
        window.SizeChanged += (_, _) => Remember(vm, window);
        window.Closing += (_, _) =>
        {
            // The user closed the window: that closes the analysis. A close made because the
            // analysis docked again or closed elsewhere is not one.
            if (_closingFromLayout) return;
            _floats.Remove(vm);
            Dispatcher.BeginInvoke(() => vm.CloseCommand.Execute(null));
        };
        return window;
    }

    private static void Remember(AnalysisWindowViewModel vm, Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        vm.FloatLeft = window.Left;
        vm.FloatTop = window.Top;
        vm.FloatWidth = window.Width;
        vm.FloatHeight = window.Height;
    }

    /// <summary>Whether a saved position is still on the desktop (a monitor may have gone).</summary>
    private static bool OnScreen(double left, double top, double width, double height) =>
        left + width > SystemParameters.VirtualScreenLeft + 40
        && top + 20 > SystemParameters.VirtualScreenTop
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40
        && double.IsFinite(width) && double.IsFinite(height);
}
