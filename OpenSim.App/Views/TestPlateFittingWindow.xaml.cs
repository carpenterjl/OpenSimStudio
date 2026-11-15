using System.Windows;
using Microsoft.Win32;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Test Plate Fitting dialog (OP-17). View glue only: the file dialog sets the
/// view model's file name.</summary>
public partial class TestPlateFittingWindow : Window
{
    private readonly OpticsTestPlateFittingViewModel _fitting;

    public TestPlateFittingWindow(OpticsTestPlateFittingViewModel fitting)
    {
        InitializeComponent();
        _fitting = fitting;
        DataContext = fitting;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Test plate list",
            Filter = "Test plate lists (*.tpd)|*.tpd|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _fitting.FileName = dialog.FileName;
            _fitting.ShowListCommand.Execute(null);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
