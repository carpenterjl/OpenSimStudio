using System.Windows;
using Microsoft.Win32;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Lens Catalogs dialog (OP-16). View glue only: the folder dialogs set the view
/// model's folders.</summary>
public partial class LensCatalogWindow : Window
{
    private readonly OpticsLensCatalogViewModel _catalog;

    public LensCatalogWindow(OpticsLensCatalogViewModel catalog)
    {
        InitializeComponent();
        _catalog = catalog;
        DataContext = catalog;
    }

    private void BrowseLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Stock lens catalog folder", InitialDirectory = _catalog.LibraryFolder };
        if (dialog.ShowDialog(this) == true) _catalog.SetLibraryFolder(dialog.FolderName, save: true);
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Folder of .zmx and .zar lens files" };
        if (dialog.ShowDialog(this) == true) _catalog.PrivateSource = dialog.FolderName;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
