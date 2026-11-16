using System.ComponentModel;
using System.Windows;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Non-Sequential Component Editor window (OP-71). View glue only: it keeps the
/// view model's current row and the grid's current cell in step, passes the grid's messages to
/// the status line, opens the properties flyout and the single-object viewer.</summary>
public partial class NonSequentialEditorWindow : Window
{
    private readonly OpticsNonSequentialEditorViewModel _editor;
    private bool _syncing;

    public NonSequentialEditorWindow(OpticsNonSequentialEditorViewModel editor)
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
        if (e.PropertyName != nameof(OpticsNonSequentialEditorViewModel.CurrentRow) || _syncing) return;
        int row = _editor.CurrentRow;
        if (row < 0 || row >= _editor.Document.RowCount) return;
        _syncing = true;
        Grid.Select(SheetSelection.Single(row, Grid.CurrentCell?.Column ?? 1));
        _syncing = false;
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (!Grid.OpenRowProperties(_editor.CurrentRow)) _editor.Status = "There is no object to show the properties of.";
    }

    private void ViewObject_Click(object sender, RoutedEventArgs e)
    {
        var view = _editor.ViewCurrentObject(out string? refusal);
        if (view is null)
        {
            _editor.Status = refusal ?? "";
            return;
        }
        new NscObjectViewerWindow(view) { Owner = this }.Show();
    }
}
