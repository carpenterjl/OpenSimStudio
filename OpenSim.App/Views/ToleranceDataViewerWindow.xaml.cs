using System.Data;
using System.Windows;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Views;

/// <summary>The Tolerance Data Viewer window (OP-66). View glue only: the trial table's columns take their
/// captions as headers (their names are plain so a binding path can hold them).</summary>
public partial class ToleranceDataViewerWindow : Window
{
    public ToleranceDataViewerWindow(ToleranceDataViewerViewModel viewer)
    {
        InitializeComponent();
        DataContext = viewer;
        TrialsGrid.AutoGeneratingColumn += (_, e) =>
        {
            if (viewer.Trials?.Table is DataTable t && t.Columns[e.PropertyName] is { } c) e.Column.Header = c.Caption;
        };
    }
}
