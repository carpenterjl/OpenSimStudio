using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenSim.App.Controls.Spreadsheet;
using static OpenSim.App.Tests.Controls.Spreadsheet.TestSheets;

namespace OpenSim.App.Tests.Controls.Spreadsheet;

/// <summary>
/// The grid control over a document, laid out off-screen on an STA thread: its columns, the
/// headers that follow the current row, selection mapping, and that 500 rows build containers
/// only for the rows in view, before and after scrolling to the end.
/// </summary>
public sealed class SpreadsheetGridTests
{
    private static void OnSta(Action test)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static SpreadsheetGrid Lay(SheetDocument doc, double height = 400)
    {
        var grid = new SpreadsheetGrid { Document = doc };
        var size = new Size(900, height);
        grid.Measure(size);
        grid.Arrange(new Rect(size));
        grid.UpdateLayout();
        return grid;
    }

    private static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T t) found.Add(t);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
        return found;
    }

    [Fact]
    public void TheGrid_HasTheTypeColumnAndOneColumnPerSlot() => OnSta(() =>
    {
        var grid = Lay(Document(3));
        Assert.Equal(9, grid.DataGrid.Columns.Count);
        Assert.Equal("Surface type", grid.DataGrid.Columns[0].Header);
        Assert.Equal("Radius", grid.DataGrid.Columns[RadiusColumn].Header);
    });

    [Fact]
    public void TheHeaders_FollowTheCurrentRowsType() => OnSta(() =>
    {
        var doc = Document(2);
        doc.SetRowType(1, Break);
        var grid = Lay(doc);

        grid.Select(SheetSelection.Single(1, RadiusColumn));
        Assert.Equal("Decenter X", grid.DataGrid.Columns[RadiusColumn].Header);
        Assert.Equal("Order", grid.DataGrid.Columns[Par1Column].Header);

        grid.Select(SheetSelection.Single(0, RadiusColumn));
        Assert.Equal("Radius", grid.DataGrid.Columns[RadiusColumn].Header);
        Assert.Equal("Par 1", grid.DataGrid.Columns[Par1Column].Header);
    });

    [Fact]
    public void Selection_MapsToSheetAddresses_AndCommandsActOnIt() => OnSta(() =>
    {
        var doc = Document(5);
        var grid = Lay(doc);

        grid.Select(SheetSelection.Block(1, RadiusColumn, 2, 2));
        Assert.Equal(SheetSelection.Block(1, RadiusColumn, 2, 2).Cells, grid.Selection.Cells);
        Assert.Equal(new SheetCellAddress(1, RadiusColumn), grid.CurrentCell);

        Assert.True(grid.Execute(SheetCommand.ToggleVariable));
        Assert.Equal(CellFlagKind.Variable, doc.Rows[2].Cells[ThicknessColumn - 1].Flag.Kind);

        var moved = doc.Rows[1];
        Assert.True(grid.Execute(SheetCommand.MoveRowsDown));
        Assert.Same(moved, doc.Rows[2]);
        Assert.Equal(new[] { 2, 3 }, grid.Selection.RowIndices);   // the selection moved with the rows

        Assert.True(grid.Execute(SheetCommand.InsertRowAbove));
        Assert.Equal(7, doc.RowCount);   // two rows selected: two inserted
        Assert.True(grid.Execute(SheetCommand.Undo));
        Assert.Equal(5, doc.RowCount);
    });

    [Fact]
    public void FiveHundredRows_BuildContainersOnlyForTheRowsInView_BeforeAndAfterScrolling() => OnSta(() =>
    {
        var doc = Document(500);
        var grid = Lay(doc, height: 400);

        var before = Descendants<DataGridRow>(grid);
        Assert.InRange(before.Count, 1, 40);   // 400 px of 22 px rows, plus the virtualising panel's cache

        // Off-screen, with no window, the DataGrid defers ScrollIntoView and never completes
        // it, so scroll its own viewer: item scrolling, so the offset counts rows.
        var viewer = Descendants<ScrollViewer>(grid.DataGrid).First();
        Assert.True(viewer.CanContentScroll);
        viewer.ScrollToVerticalOffset(445);
        grid.UpdateLayout();
        Flush();
        grid.UpdateLayout();
        var after = Descendants<DataGridRow>(grid);
        Assert.Contains(after, r => ReferenceEquals(r.Item, doc.Rows[450]));
        Assert.DoesNotContain(after, r => ReferenceEquals(r.Item, doc.Rows[0]));
        Assert.InRange(after.Count, 1, 40);
    });

    /// <summary>Runs what the grid queued on the dispatcher (a scroll it defers until its
    /// containers exist).</summary>
    private static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
