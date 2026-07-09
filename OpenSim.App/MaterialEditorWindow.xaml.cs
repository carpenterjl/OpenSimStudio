using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App;

/// <summary>Code-behind limited to construction and closing; all logic is in the view model.</summary>
public partial class MaterialEditorWindow : Window
{
    public MaterialEditorWindow(MaterialEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
