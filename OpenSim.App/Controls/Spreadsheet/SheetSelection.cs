namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// A set of selected cells, not necessarily rectangular (a control-click selection), in row-major
/// order. The grid turns its own selection into one of these, and every command (copy, paste,
/// delete rows, toggle variable) works from it, so the rules are testable without a window.
/// </summary>
public sealed class SheetSelection
{
    private readonly SheetCellAddress[] _cells;
    private readonly HashSet<SheetCellAddress> _set;

    /// <summary>The selection of <paramref name="cells"/> (duplicates dropped).</summary>
    public SheetSelection(IEnumerable<SheetCellAddress> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _set = new HashSet<SheetCellAddress>(cells);
        _cells = _set.OrderBy(c => c.Row).ThenBy(c => c.Column).ToArray();
        if (_cells.Length > 0)
        {
            Top = _cells.Min(c => c.Row);
            Bottom = _cells.Max(c => c.Row);
            Left = _cells.Min(c => c.Column);
            Right = _cells.Max(c => c.Column);
        }
        else
        {
            Top = Bottom = Left = Right = -1;
        }
    }

    /// <summary>No cells.</summary>
    public static SheetSelection Empty { get; } = new(Array.Empty<SheetCellAddress>());

    /// <summary>One cell.</summary>
    public static SheetSelection Single(int row, int column) => new(new[] { new SheetCellAddress(row, column) });

    /// <summary>The rectangle of <paramref name="rows"/> × <paramref name="columns"/> cells whose
    /// top-left is (<paramref name="top"/>, <paramref name="left"/>).</summary>
    public static SheetSelection Block(int top, int left, int rows, int columns)
    {
        var cells = new List<SheetCellAddress>(Math.Max(0, rows * columns));
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                cells.Add(new SheetCellAddress(top + r, left + c));
        return new SheetSelection(cells);
    }

    /// <summary>Every cell of <paramref name="rows"/>, across all <paramref name="columnCount"/>
    /// grid columns (a row-header selection).</summary>
    public static SheetSelection WholeRows(IEnumerable<int> rows, int columnCount) =>
        new(rows.SelectMany(r => Enumerable.Range(0, columnCount).Select(c => new SheetCellAddress(r, c))));

    /// <summary>The cells, row-major.</summary>
    public IReadOnlyList<SheetCellAddress> Cells => _cells;

    /// <summary>True when nothing is selected.</summary>
    public bool IsEmpty => _cells.Length == 0;

    /// <summary>The first selected row; −1 when empty.</summary>
    public int Top { get; }

    /// <summary>The last selected row; −1 when empty.</summary>
    public int Bottom { get; }

    /// <summary>The first selected column; −1 when empty.</summary>
    public int Left { get; }

    /// <summary>The last selected column; −1 when empty.</summary>
    public int Right { get; }

    /// <summary>The bounding rectangle's height in rows.</summary>
    public int Height => IsEmpty ? 0 : Bottom - Top + 1;

    /// <summary>The bounding rectangle's width in columns.</summary>
    public int Width => IsEmpty ? 0 : Right - Left + 1;

    /// <summary>True when the cells fill their bounding rectangle.</summary>
    public bool IsRectangular => !IsEmpty && _cells.Length == Height * Width;

    /// <summary>The rows with at least one selected cell, ascending.</summary>
    public IReadOnlyList<int> RowIndices => _cells.Select(c => c.Row).Distinct().ToArray();

    /// <summary>True when <paramref name="cell"/> is selected.</summary>
    public bool Contains(SheetCellAddress cell) => _set.Contains(cell);

    /// <summary>The same cells moved by <paramref name="rows"/> rows (after a row move).</summary>
    public SheetSelection Offset(int rows) =>
        new(_cells.Select(c => new SheetCellAddress(c.Row + rows, c.Column)));
}
