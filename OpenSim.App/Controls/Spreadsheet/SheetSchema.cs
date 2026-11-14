namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// The shape of one editor's sheet: the type column, a fixed number of slots after it, and the
/// row types that give those slots meaning. Grid column 0 is always the row type; grid column
/// <c>k</c> (k ≥ 1) is slot <c>k − 1</c>.
/// <para>Validated on construction, so a mistake in an editor's table fails when the editor is
/// built, naming the type and column, instead of when a user first meets the row.</para>
/// </summary>
public sealed class SheetSchema
{
    /// <summary>The grid column that holds the row type.</summary>
    public const int TypeColumn = 0;

    /// <summary>A schema.</summary>
    /// <param name="typeHeader">The type column's header ("Surface type", "Operand").</param>
    /// <param name="slotHeaders">The header of each slot when the current row does not use it
    /// ("Par 1"...); their count is the number of slots.</param>
    /// <param name="rowTypes">The row types; the first is the default for new rows.</param>
    public SheetSchema(string typeHeader, IReadOnlyList<string> slotHeaders, IReadOnlyList<SheetRowType> rowTypes)
    {
        ArgumentNullException.ThrowIfNull(typeHeader);
        ArgumentNullException.ThrowIfNull(slotHeaders);
        ArgumentNullException.ThrowIfNull(rowTypes);
        if (rowTypes.Count == 0) throw new ArgumentException("A sheet needs at least one row type.", nameof(rowTypes));
        TypeHeader = typeHeader;
        SlotHeaders = slotHeaders.ToArray();
        RowTypes = rowTypes.ToArray();
        Validate();
    }

    /// <summary>The type column's header.</summary>
    public string TypeHeader { get; }

    /// <summary>The fallback header of each slot.</summary>
    public IReadOnlyList<string> SlotHeaders { get; }

    /// <summary>The row types, the default first.</summary>
    public IReadOnlyList<SheetRowType> RowTypes { get; }

    /// <summary>The number shown on the first row: 0 where the first row is the object surface,
    /// 1 for operand lists.</summary>
    public int FirstRowNumber { get; init; }

    /// <summary>The number of slots after the type column.</summary>
    public int SlotCount => SlotHeaders.Count;

    /// <summary>The number of grid columns, the type column included.</summary>
    public int ColumnCount => SlotHeaders.Count + 1;

    /// <summary>The type new rows get.</summary>
    public SheetRowType DefaultRowType => RowTypes[0];

    /// <summary>The row type named <paramref name="name"/> (ignoring case and surrounding
    /// spaces), or null.</summary>
    public SheetRowType? FindRowType(string name) =>
        RowTypes.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The header of grid column <paramref name="column"/> for a row of
    /// <paramref name="type"/>: the type's own header where it uses the slot, else the fallback.</summary>
    public string HeaderOf(int column, SheetRowType? type)
    {
        if (column == TypeColumn) return TypeHeader;
        if (column < 1 || column > SlotCount) throw new ArgumentOutOfRangeException(nameof(column));
        return type?.ColumnAt(column - 1)?.Header ?? SlotHeaders[column - 1];
    }

    private void Validate()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in RowTypes)
        {
            if (!names.Add(type.Name))
                throw new ArgumentException($"Row type '{type.Name}' is defined twice.");
            if (type.Columns.Count > SlotCount)
                throw new ArgumentException(
                    $"Row type '{type.Name}' defines {type.Columns.Count} columns; the sheet has {SlotCount} slots.");
            if (type.DefaultTint is < 0)
                throw new ArgumentException($"Row type '{type.Name}' has a negative default tint.");
            ValidateColumns(type, type.Columns.Where(c => c is not null).Select(c => c!), "column");
            ValidateColumns(type, type.Properties, "property");
            foreach (var property in type.Properties)
                if (property.Flags != CellFlagSupport.None)
                    throw new ArgumentException(
                        $"Row type '{type.Name}', property '{property.Key}': properties carry no flags.");
        }
    }

    private static void ValidateColumns(SheetRowType type, IEnumerable<SheetColumn> columns, string what)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            string where = $"Row type '{type.Name}', {what} '{column.Key}'";
            if (string.IsNullOrWhiteSpace(column.Key)) throw new ArgumentException($"Row type '{type.Name}' has a {what} with no key.");
            if (!keys.Add(column.Key)) throw new ArgumentException($"{where} is defined twice.");
            if (column.Kind == CellKind.Choice && column.Choices.Count == 0)
                throw new ArgumentException($"{where} is a choice with no choices.");
            if (column.Flags.HasFlag(CellFlagSupport.Solve) && column.SolveCodes.Count == 0)
                throw new ArgumentException($"{where} accepts solves but offers none.");
            if (column.SolveCodes.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != column.SolveCodes.Count)
                throw new ArgumentException($"{where} has two solves with the same key.");
            if (column.Flags != CellFlagSupport.None && !column.IsNumeric && column.Flags != CellFlagSupport.Pickup)
                throw new ArgumentException($"{where}: only numeric columns can be variable or solved.");
            if (column.DefaultFlag is { } defaultFlag && (defaultFlag.Kind == CellFlagKind.Pickup || !column.Supports(defaultFlag.Kind)
                || (defaultFlag.Kind == CellFlagKind.Solve && column.FindSolveCode(defaultFlag.SolveCode) is null)))
                throw new ArgumentException($"{where}: the default flag {defaultFlag} is not one the column accepts.");
            if (column.Default is { } value && CellText.Validate(column, value) is { } error)
                throw new ArgumentException($"{where}: the default is invalid: {error}");
        }
    }
}
