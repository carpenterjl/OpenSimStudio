using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Quick Adjust dialog (OP-60). View glue only.</summary>
public partial class QuickAdjustWindow : Window
{
    public QuickAdjustWindow(OpticsQuickAdjustViewModel quickAdjust)
    {
        InitializeComponent();
        DataContext = quickAdjust;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
