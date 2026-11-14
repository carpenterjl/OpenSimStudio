using System.ComponentModel;
using System.Windows;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Lens Data Editor window (OP-31). View glue only: it keeps the view model's
/// current row and the grid's current cell in step, passes the grid's messages to the status
/// line, and asks for the file to compare.</summary>
public partial class LensDataEditorWindow : Window
{
    private readonly OpticsLensEditorViewModel _editor;
    private bool _syncing;

    public LensDataEditorWindow(OpticsLensEditorViewModel editor)
    {
        InitializeComponent();
        _editor = editor;
        DataContext = editor;
        Grid.DataGrid.CurrentCellChanged += (_, _) =>
        {
            if (_syncing || Grid.CurrentCell is not { } cell) return;
            _syncing = true;
            _editor.CurrentRow = cell.Row;
            _syncing = false;
        };
        Grid.Message += (_, e) => _editor.Status = e.Text;
        _editor.PropertyChanged += OnEditorChanged;
        Closed += (_, _) => _editor.PropertyChanged -= OnEditorChanged;
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpticsLensEditorViewModel.CurrentRow) || _syncing) return;
        int row = _editor.CurrentRow;
        if (row < 0 || row >= _editor.Document.RowCount) return;
        _syncing = true;
        Grid.Select(SheetSelection.Single(row, Grid.CurrentCell?.Column ?? 1));
        _syncing = false;
    }

    private void Properties_Click(object sender, RoutedEventArgs e) => Grid.OpenRowProperties(_editor.CurrentRow);

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Compare with a lens or project file",
            Filter = "Lens files (*.osslens)|*.osslens|Projects (*.ossproj)|*.ossproj|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true) _editor.CompareWithFile(dialog.FileName);
    }
}
