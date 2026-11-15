using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Export CAD Files dialog (OP-96). View glue only: the save dialog, then the view
/// model's export.</summary>
public partial class CadExportWindow : Window
{
    private readonly OpticsCadExportViewModel _export;

    public CadExportWindow(OpticsCadExportViewModel export)
    {
        InitializeComponent();
        _export = export;
        DataContext = export;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = _export.FileFilter,
            FileName = _export.DefaultFileName,
            Title = "Export CAD file",
        };
        if (dialog.ShowDialog(this) != true) return;
        await _export.ExportToAsync(dialog.FileName);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
