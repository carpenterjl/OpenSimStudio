using System.ComponentModel;
using System.Windows;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Tolerance Data Editor window (OP-63). View glue only: it keeps the view model's
/// current row and the grid's current cell in step and passes the grid's messages to the status
/// line, as the Lens Data Editor window does.</summary>
public partial class ToleranceDataEditorWindow : Window
{
    private readonly OpticsToleranceEditorViewModel _editor;
    private bool _syncing;

    public ToleranceDataEditorWindow(OpticsToleranceEditorViewModel editor)
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
        _editor.DataViewerRequested += OnDataViewerRequested;   // OP-66
        Closed += (_, _) =>
        {
            _editor.PropertyChanged -= OnEditorChanged;
            _editor.DataViewerRequested -= OnDataViewerRequested;   // OP-66
        };
    }

    // OP-66: Open data viewer shows a new viewer window over this one.
    private void OnDataViewerRequested(object? sender, ToleranceDataViewerViewModel viewer) =>
        new ToleranceDataViewerWindow(viewer) { Owner = this }.Show();

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpticsToleranceEditorViewModel.CurrentRow) || _syncing) return;
        int row = _editor.CurrentRow;
        if (row < 0 || row >= _editor.Document.RowCount) return;
        _syncing = true;
        Grid.Select(SheetSelection.Single(row, Grid.CurrentCell?.Column ?? 1));
        _syncing = false;
    }
}
