namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// A kind of row (a surface type, an operand, a tolerance operand) and the columns it uses.
/// Slot <c>i</c> of every row shows <see cref="Columns"/>[i] of that row's type; a null entry, or
/// a slot past the end of the list, is a cell this type does not use. This is how the columns
/// change with the row's type while the grid keeps one set of columns for all rows: the headers
/// follow the current row, as in the editors this control serves.
/// </summary>
public sealed class SheetRowType
{
    /// <summary>A row type named <paramref name="name"/> using <paramref name="columns"/>.</summary>
    public SheetRowType(string name, IReadOnlyList<SheetColumn?> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        Name = name;
        Columns = columns.ToArray();
    }

    /// <summary>The name shown in the type column and matched (without case) when pasted.</summary>
    public string Name { get; }

    /// <summary>The definition of each slot; null where the type has no column.</summary>
    public IReadOnlyList<SheetColumn?> Columns { get; }

    /// <summary>Other names typed or pasted text matches this type by (ignoring case): the merit
    /// function editor's four-letter operand codes (OP-54).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>A sentence on what the type is, shown as the type cell's tooltip.</summary>
    public string? Description { get; init; }

    /// <summary>The fields of the row's properties flyout (apertures, tilts, coatings...): values
    /// that belong to the row but have no column.</summary>
    public IReadOnlyList<SheetColumn> Properties { get; init; } = Array.Empty<SheetColumn>();

    /// <summary>The tint a new row of this type gets (an index into the control's palette), or null.</summary>
    public int? DefaultTint { get; init; }

    /// <summary>The definition of slot <paramref name="slot"/>, or null when unused.</summary>
    public SheetColumn? ColumnAt(int slot) => slot >= 0 && slot < Columns.Count ? Columns[slot] : null;

    /// <summary>The property field with this key, or null.</summary>
    public SheetColumn? FindProperty(string key) =>
        Properties.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

    /// <inheritdoc/>
    public override string ToString() => Name;
}
