using System.Windows.Input;
using OpenSim.App.Controls.Spreadsheet;

namespace OpenSim.App.Tests.Controls.Spreadsheet;

/// <summary>The spreadsheet's plain pieces: cell text, the undo stack, selections and the key map.</summary>
public sealed class SheetModelTests
{
    private static readonly SheetColumn Number = new("x", "X") { Minimum = -1, Maximum = 1 };
    private static readonly SheetColumn Whole = new("n", "N") { Kind = CellKind.Integer };

    // ------------------------------------------------------------------ cell text

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("-1", -1.0)]
    [InlineData("1e-3", 0.001)]
    [InlineData(" +0.25 ", 0.25)]
    public void Numbers_ReadInTheInvariantCulture(string text, double expected)
    {
        Assert.True(CellText.TryParse(Number, text, out var value, out _));
        Assert.Equal(expected, value.Number);
    }

    [Theory]
    [InlineData("0,5")]     // a decimal comma
    [InlineData("1,000")]   // a thousands separator
    [InlineData("NaN")]
    [InlineData("2")]       // above the maximum
    [InlineData("Infinity")]
    public void BadNumbers_AreRefused(string text)
    {
        Assert.False(CellText.TryParse(Number, text, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("inf", double.PositiveInfinity)]
    [InlineData("-Infinity", double.NegativeInfinity)]
    [InlineData("+INF", double.PositiveInfinity)]
    [InlineData("∞", double.PositiveInfinity)]
    public void Infinity_HasSeveralSpellings(string text, double expected)
    {
        Assert.True(CellText.TryParseNumber(text, out double x));
        Assert.Equal(expected, x);
        Assert.False(CellText.TryParseNumber("+-inf", out _));
    }

    [Fact]
    public void Integers_RefuseFractions_AndFormatWithoutAPoint()
    {
        Assert.True(CellText.TryParse(Whole, "12", out var value, out _));
        Assert.Equal("12", CellText.Format(Whole, value));
        Assert.False(CellText.TryParse(Whole, "1.5", out _, out _));
        Assert.NotNull(CellText.Validate(Whole, CellValue.Of(2.5)));
    }

    [Fact]
    public void Format_RoundTripsEveryDouble()
    {
        var any = new SheetColumn("y", "Y") { AllowsInfinity = true };
        foreach (double x in new[] { 0.1, 1.0 / 3.0, -2.718281828459045, 1e-300, double.MaxValue, double.Epsilon, double.PositiveInfinity })
        {
            string text = CellText.Format(any, CellValue.Of(x));
            Assert.True(CellText.TryParse(any, text, out var back, out _));
            Assert.Equal(x, back.Number);
        }
    }

    // ------------------------------------------------------------------ undo stack

    private sealed class Counter
    {
        public int Value;
    }

    private sealed class Add : IUndoableEdit
    {
        private readonly Counter _counter;
        private readonly int _amount;

        public Add(Counter counter, int amount)
        {
            _counter = counter;
            _amount = amount;
            _counter.Value += amount;
        }

        public string Description => $"Add {_amount}";

        public void Undo() => _counter.Value -= _amount;

        public void Redo() => _counter.Value += _amount;
    }

    [Fact]
    public void UndoAndRedo_WalkTheHistory_AndANewEditClearsRedo()
    {
        var counter = new Counter();
        var stack = new UndoStack();
        stack.Push(new Add(counter, 1));
        stack.Push(new Add(counter, 10));

        Assert.Equal("Add 10", stack.UndoDescription);
        Assert.True(stack.Undo());
        Assert.Equal(1, counter.Value);
        Assert.Equal("Add 10", stack.RedoDescription);
        Assert.True(stack.Redo());
        Assert.Equal(11, counter.Value);

        stack.Undo();
        stack.Push(new Add(counter, 100));
        Assert.False(stack.CanRedo);
        Assert.Equal(101, counter.Value);
        stack.Undo();
        stack.Undo();
        Assert.False(stack.Undo());
        Assert.Equal(0, counter.Value);
    }

    [Fact]
    public void AGroup_IsOneStep_AndNestedGroupsFoldIn()
    {
        var counter = new Counter();
        var stack = new UndoStack();
        using (stack.BeginGroup("Outer"))
        {
            stack.Push(new Add(counter, 1));
            using (stack.BeginGroup("Inner"))
            {
                stack.Push(new Add(counter, 2));
                stack.Push(new Add(counter, 4));
            }
            Assert.False(stack.CanUndo);   // not while the group is open
        }

        Assert.Equal(1, stack.UndoCount);
        Assert.Equal("Outer", stack.UndoDescription);
        stack.Undo();
        Assert.Equal(0, counter.Value);
        stack.Redo();
        Assert.Equal(7, counter.Value);
    }

    [Fact]
    public void ARolledBackGroup_UndoesItsEdits_AndRecordsNothing()
    {
        var counter = new Counter();
        var stack = new UndoStack();
        stack.Push(new Add(counter, 1));
        using (var group = stack.BeginGroup("Paste"))
        {
            stack.Push(new Add(counter, 2));
            stack.Push(new Add(counter, 4));
            group.Rollback();
        }

        Assert.Equal(1, counter.Value);
        Assert.Equal(1, stack.UndoCount);
        Assert.Equal("Add 1", stack.UndoDescription);
    }

    [Fact]
    public void TheOldestStepsGo_WhenTheCapacityIsReached()
    {
        var counter = new Counter();
        var stack = new UndoStack(capacity: 3);
        for (int i = 1; i <= 5; i++) stack.Push(new Add(counter, i));

        Assert.Equal(3, stack.UndoCount);
        while (stack.Undo()) { }
        Assert.Equal(1 + 2, counter.Value);   // the first two can no longer be undone
    }

    [Fact]
    public void GroupsMustCloseInOrder()
    {
        var stack = new UndoStack();
        var outer = stack.BeginGroup("a");
        stack.BeginGroup("b");
        Assert.Throws<InvalidOperationException>(() => outer.Dispose());
    }

    // ------------------------------------------------------------------ selection

    [Fact]
    public void ASelection_KnowsItsBoundsRowsAndShape()
    {
        var block = SheetSelection.Block(2, 1, 3, 2);
        Assert.True(block.IsRectangular);
        Assert.Equal((2, 4, 1, 2), (block.Top, block.Bottom, block.Left, block.Right));
        Assert.Equal(new[] { 2, 3, 4 }, block.RowIndices);

        var scattered = new SheetSelection(new[] { new SheetCellAddress(5, 3), new SheetCellAddress(1, 1), new SheetCellAddress(5, 3) });
        Assert.Equal(2, scattered.Cells.Count);
        Assert.False(scattered.IsRectangular);
        Assert.Equal(new SheetCellAddress(1, 1), scattered.Cells[0]);
        Assert.Equal(5, scattered.Height);
        Assert.Equal(3, scattered.Width);
        Assert.Equal(new[] { 0, 4 }, scattered.Offset(-1).RowIndices);

        Assert.True(SheetSelection.Empty.IsEmpty);
        Assert.Equal(-1, SheetSelection.Empty.Top);
        Assert.Equal(6, SheetSelection.WholeRows(new[] { 0, 2 }, 3).Cells.Count);
    }

    // ------------------------------------------------------------------ keys

    [Fact]
    public void TheKeyMap_CoversBothClipboardKeySets_AndLeavesNavigationToTheDataGrid()
    {
        Assert.Equal(SheetCommand.Copy, SheetKeyMap.Resolve(Key.C, ModifierKeys.Control));
        Assert.Equal(SheetCommand.Copy, SheetKeyMap.Resolve(Key.Insert, ModifierKeys.Control));
        Assert.Equal(SheetCommand.Paste, SheetKeyMap.Resolve(Key.Insert, ModifierKeys.Shift));
        Assert.Equal(SheetCommand.Cut, SheetKeyMap.Resolve(Key.Delete, ModifierKeys.Shift));
        Assert.Equal(SheetCommand.Redo, SheetKeyMap.Resolve(Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal(SheetCommand.InsertRowAbove, SheetKeyMap.Resolve(Key.Insert, ModifierKeys.None));
        Assert.Equal(SheetCommand.DeleteRows, SheetKeyMap.Resolve(Key.Delete, ModifierKeys.Control));
        Assert.Equal(SheetCommand.ClearCells, SheetKeyMap.Resolve(Key.Delete, ModifierKeys.None));
        Assert.Equal(SheetCommand.MoveRowsUp, SheetKeyMap.Resolve(Key.Up, ModifierKeys.Alt));
        Assert.Equal(SheetCommand.RowProperties, SheetKeyMap.Resolve(Key.Enter, ModifierKeys.Alt));

        foreach (var key in new[] { Key.Up, Key.Down, Key.Left, Key.Right, Key.Tab, Key.Enter, Key.F2, Key.Home, Key.PageDown })
            Assert.Null(SheetKeyMap.Resolve(key, ModifierKeys.None));
        Assert.Null(SheetKeyMap.Resolve(Key.A, ModifierKeys.Control));

        Assert.All(Enum.GetValues<SheetCommand>(), c => Assert.True(SheetKeyMap.GestureText.ContainsKey(c), c.ToString()));
    }
}
