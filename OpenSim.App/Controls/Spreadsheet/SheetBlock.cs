namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// A rectangle of cell texts on its way to or from the clipboard. A block copied from a sheet
/// also carries each cell's flag; those are reapplied only when it is pasted back into the same
/// sheet (a pickup names rows by id, which means nothing elsewhere). Text from another program
/// has no flags.
/// </summary>
public sealed class SheetBlock
{
    private readonly string[][] _texts;
    private readonly CellFlag?[][]? _flags;

    /// <summary>A block of <paramref name="records"/>; short records are padded with empty text.</summary>
    public SheetBlock(IReadOnlyList<IReadOnlyList<string>> records)
        : this(records, null, null)
    {
    }

    internal SheetBlock(IReadOnlyList<IReadOnlyList<string>> records, CellFlag?[][]? flags, SheetDocument? source)
    {
        ArgumentNullException.ThrowIfNull(records);
        ColumnCount = records.Count == 0 ? 0 : records.Max(r => r.Count);
        _texts = records.Select(r => Enumerable.Range(0, ColumnCount).Select(c => c < r.Count ? r[c] : "").ToArray()).ToArray();
        _flags = flags;
        Source = source;
    }

    /// <summary>The number of rows.</summary>
    public int RowCount => _texts.Length;

    /// <summary>The number of columns (the longest record's).</summary>
    public int ColumnCount { get; }

    /// <summary>True when there is nothing to paste.</summary>
    public bool IsEmpty => RowCount == 0 || ColumnCount == 0;

    /// <summary>The sheet the block was copied from, or null for text from elsewhere.</summary>
    internal SheetDocument? Source { get; }

    /// <summary>The text at (<paramref name="row"/>, <paramref name="column"/>).</summary>
    public string TextAt(int row, int column) => _texts[row][column];

    /// <summary>The flag copied with the cell, or null (an unselected cell, a type cell, or text
    /// from elsewhere).</summary>
    public CellFlag? FlagAt(int row, int column) => _flags?[row][column];

    /// <summary>The block parsed from clipboard text.</summary>
    public static SheetBlock Parse(string text) => new(TabularText.Parse(text));

    /// <summary>The block as tab-separated text.</summary>
    public string ToText() => TabularText.Format(_texts);
}
