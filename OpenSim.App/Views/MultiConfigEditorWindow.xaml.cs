using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Multi-Configuration Editor window (OP-30). View glue only: it passes the grid's
/// cursor (operand row and configuration column) and messages to the view model.</summary>
public partial class MultiConfigEditorWindow : Window
{
    public MultiConfigEditorWindow(OpticsMultiConfigEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
        Grid.DataGrid.CurrentCellChanged += (_, _) =>
        {
            if (Grid.CurrentCell is not { } cell) return;
            editor.CurrentRow = cell.Row;
            int configuration = cell.Column - OpticsMultiConfigEditorViewModel.FirstConfigurationSlot;
            if (configuration >= 1) editor.CursorConfiguration = configuration;
        };
        Grid.Message += (_, e) => editor.Status = e.Text;
    }
}
