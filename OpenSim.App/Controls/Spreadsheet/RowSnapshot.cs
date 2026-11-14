namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>A cell's value and flag, as captured for undo.</summary>
internal readonly record struct CellState(CellValue Value, CellFlag Flag);

/// <summary>
/// Everything about a row an edit can change: its type, cells, tint and properties. Undo of any
/// edit to a row's content restores the snapshot taken before it, which keeps every content edit
/// (a typed value, a flag, a type change, a paste) one simple, exact inverse.
/// </summary>
internal sealed record RowSnapshot(
    SheetRowType Type,
    CellState[] Cells,
    int? Tint,
    IReadOnlyDictionary<string, CellValue> Properties)
{
    /// <summary>A copy whose cell array and properties can be changed without touching this one.</summary>
    public RowSnapshot Copy() =>
        this with { Cells = (CellState[])Cells.Clone(), Properties = new Dictionary<string, CellValue>(Properties, StringComparer.Ordinal) };

    /// <summary>
    /// This row retyped to <paramref name="type"/>: a value and its flag carry to the new type's
    /// slot with the same key and kind (wherever that slot is), a flag the new column does not
    /// accept becomes Fixed, and everything else takes the new column's default. Properties carry
    /// over the same way. The tint is kept.
    /// </summary>
    public RowSnapshot Retyped(SheetRowType type)
    {
        var cells = new CellState[Cells.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            var definition = type.ColumnAt(i);
            if (definition is null) { cells[i] = new CellState(CellValue.Empty, CellFlag.Fixed); continue; }
            int from = -1;
            for (int j = 0; j < Cells.Length; j++)
                if (Type.ColumnAt(j) is { } d && d.Key == definition.Key && d.Kind == definition.Kind) { from = j; break; }
            if (from < 0 || CellText.Validate(definition, Cells[from].Value) is not null)
            {
                cells[i] = new CellState(definition.DefaultValue, CellFlag.Fixed);
                continue;
            }
            var flag = Cells[from].Flag;
            bool keepsFlag = definition.Supports(flag.Kind)
                && (flag.Kind != CellFlagKind.Solve || definition.FindSolveCode(flag.SolveCode) is not null);
            cells[i] = new CellState(Cells[from].Value, keepsFlag ? flag : CellFlag.Fixed);
        }

        var properties = new Dictionary<string, CellValue>(StringComparer.Ordinal);
        foreach (var property in type.Properties)
        {
            var oldDefinition = Type.FindProperty(property.Key);
            properties[property.Key] =
                oldDefinition is not null && oldDefinition.Kind == property.Kind
                && Properties.TryGetValue(property.Key, out var value) && CellText.Validate(property, value) is null
                    ? value
                    : property.DefaultValue;
        }
        return new RowSnapshot(type, cells, Tint, properties);
    }
}
