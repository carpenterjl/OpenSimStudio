using System.ComponentModel;
using System.Windows;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Merit Function Editor window (OP-54). View glue only: it keeps the view model's
/// current row and the grid's current cell in step and passes the grid's messages to the status
/// line, as the Lens Data Editor window does.</summary>
public partial class MeritFunctionEditorWindow : Window
{
    private readonly OpticsMeritEditorViewModel _editor;
    private bool _syncing;

    public MeritFunctionEditorWindow(OpticsMeritEditorViewModel editor)
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

    /// <summary>Opens the Optimization Wizard (OP-56) on this editor.</summary>
    private void OnWizard(object sender, RoutedEventArgs e) =>
        new MeritWizardWindow(_editor.CreateWizard()) { Owner = this }.ShowDialog();

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpticsMeritEditorViewModel.CurrentRow) || _syncing) return;
        int row = _editor.CurrentRow;
        if (row < 0 || row >= _editor.Document.RowCount) return;
        _syncing = true;
        Grid.Select(SheetSelection.Single(row, Grid.CurrentCell?.Column ?? 1));
        _syncing = false;
    }
}
