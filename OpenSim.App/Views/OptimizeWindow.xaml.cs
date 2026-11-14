using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Optimize tool window (OP-57). View glue only. Closing it while a run is going
/// stops the run (the best design so far is applied).</summary>
public partial class OptimizeWindow : Window
{
    public OptimizeWindow(OpticsOptimizeViewModel optimize)
    {
        InitializeComponent();
        DataContext = optimize;
        Closing += (_, _) => { if (optimize.StopCommand.CanExecute(null)) optimize.StopCommand.Execute(null); };
    }
}
