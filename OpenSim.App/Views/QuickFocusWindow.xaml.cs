using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Quick Focus dialog (OP-60). View glue only.</summary>
public partial class QuickFocusWindow : Window
{
    public QuickFocusWindow(OpticsQuickFocusViewModel quickFocus)
    {
        InitializeComponent();
        DataContext = quickFocus;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
