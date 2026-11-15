using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Design Conversion Tools dialog (OP-61). View glue only.</summary>
public partial class DesignToolsWindow : Window
{
    public DesignToolsWindow(OpticsDesignToolsViewModel tools)
    {
        InitializeComponent();
        DataContext = tools;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
