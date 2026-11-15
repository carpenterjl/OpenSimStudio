using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Visual Optimizer window (OP-60). View glue only: closing it is Exit (the
/// original values are put back) unless Keep and Exit was pressed.</summary>
public partial class VisualOptimizerWindow : Window
{
    private readonly OpticsVisualOptimizerViewModel _optimizer;

    public VisualOptimizerWindow(OpticsVisualOptimizerViewModel optimizer)
    {
        InitializeComponent();
        _optimizer = optimizer;
        DataContext = optimizer;
        Closed += (_, _) => optimizer.Exit();
        optimizer.Load();
    }

    private void KeepAndExit_Click(object sender, RoutedEventArgs e)
    {
        _optimizer.Keep();
        if (!_optimizer.IsModified) Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
