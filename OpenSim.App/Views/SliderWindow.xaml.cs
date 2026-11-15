using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Slider window (OP-60). View glue only: Animate's timer, and closing the window
/// is Exit (the original value is put back) unless Keep and Exit was pressed.</summary>
public partial class SliderWindow : Window
{
    /// <summary>Animate's step interval (this program's choice): longer than the analysis
    /// windows' debounce, so each step gets its own update instead of coalescing with the next.</summary>
    private static readonly TimeSpan AnimateInterval = TimeSpan.FromMilliseconds(400);

    private readonly OpticsSliderViewModel _slider;
    private readonly DispatcherTimer _timer;

    public SliderWindow(OpticsSliderViewModel slider)
    {
        InitializeComponent();
        _slider = slider;
        DataContext = slider;
        _timer = new DispatcherTimer { Interval = AnimateInterval };
        _timer.Tick += (_, _) => _slider.Step();
        slider.PropertyChanged += OnSliderChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            slider.PropertyChanged -= OnSliderChanged;
            slider.Exit();
        };
        slider.Load();
    }

    private void OnSliderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpticsSliderViewModel.IsAnimating)) return;
        if (_slider.IsAnimating) _timer.Start();
        else _timer.Stop();
    }

    private void KeepAndExit_Click(object sender, RoutedEventArgs e)
    {
        _slider.Keep();
        if (!_slider.IsModified) Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
