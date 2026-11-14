using System.Collections.ObjectModel;

namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// The content of one editor's sheet and every edit to it. Each public edit validates first and
/// either changes nothing (returning a failed <see cref="SheetEditResult"/> that names the row,
/// the column and the problem) or makes the whole change as one undo step. The grid control is a
/// view of this class; editors (OP-31 and the ones after it) read and write through it too, so a
/// change from a tool lands on the same undo stack as a typed one.
/// <para>Decisions recorded here, as they are not obvious from the editors this serves:</para>
/// <list type="bullet">
/// <item>Typing a value into a cell carrying a solve or pickup makes it Fixed (the solve would
/// otherwise overwrite what was typed); a Variable cell stays Variable.</item>
/// <item>A pickup whose source goes away (its row deleted, or retyped so the column is unused or
/// of another kind) becomes Fixed, keeping its value, as part of the same undo step.</item>
/// <item>Empty cells in a pasted block leave their targets alone (a spreadsheet's "skip blanks"),
/// so a non-rectangular copy pastes back without clearing the cells between.</item>
/// </list>
/// </summary>
public sealed partial class SheetDocument
{
    private readonly ObservableCollection<SheetRow> _rows = new();
    private readonly Dictionary<long, SheetRow> _byId = new();
    private long _nextId = 1;

    /// <summary>An empty sheet of <paramref name="schema"/>.</summary>
    public SheetDocument(SheetSchema schema, int undoCapacity = 1000)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Rows = new ReadOnlyObservableCollection<SheetRow>(_rows);
        History = new UndoStack(undoCapacity);
    }

    /// <summary>Raised after every successful edit, undo and redo: the owning editor recomputes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised just before rows are inserted, removed or reordered (also by undo and
    /// redo), so a view can end an edit in progress first.</summary>
    public event EventHandler? StructureChanging;

    /// <summary>The schema.</summary>
    public SheetSchema Schema { get; }

    /// <summary>The rows, in order (what the grid binds to).</summary>
    public ReadOnlyObservableCollection<SheetRow> Rows { get; }

    /// <summary>The undo history.</summary>
    public UndoStack History { get; }

    /// <summary>The number of rows.</summary>
    public int RowCount => _rows.Count;

    /// <summary>The number of grid columns, the type column included.</summary>
    public int ColumnCount => Schema.ColumnCount;

    /// <summary>
    /// Asked before rows are inserted at an index (by insert or by a paste past the end): returns
    /// why not, or null to allow it. An editor uses it to keep rows from going before the object
    /// surface or after the image surface.
    /// </summary>
    public Func<int, string?>? InsertGuard { get; set; }

    /// <summary>The row with this <see cref="SheetRow.Id"/> while it is in the sheet, else null.</summary>
    public SheetRow? FindRow(long id) => _byId.TryGetValue(id, out var row) && row.IsAttached ? row : null;

    /// <summary>The text of a cell as edited and copied: the type name in column 0.</summary>
    public string GetText(int row, int column)
    {
        var r = RowAt(row);
        CheckColumn(column);
        return column == SheetSchema.TypeColumn ? r.Type.Name : r.Cells[column - 1].EditText;
    }

    /// <summary>True when the user may type into the cell (the type column always; a slot when
    /// the row's type uses it and it is not computed).</summary>
    public bool IsEditable(int row, int column)
    {
        var r = RowAt(row);
        CheckColumn(column);
        return column == SheetSchema.TypeColumn || r.Cells[column - 1].IsEditable;
    }

    // ------------------------------------------------------------------ undo

    /// <summary>Undoes the last edit; false when there is none.</summary>
    public bool Undo()
    {
        if (!History.Undo()) return false;
        RaiseChanged();
        return true;
    }

    /// <summary>Redoes the last undone edit; false when there is none.</summary>
    public bool Redo()
    {
        if (!History.Redo()) return false;
        RaiseChanged();
        return true;
    }

    // ------------------------------------------------------------------ rows

    /// <summary>Inserts <paramref name="count"/> rows of <paramref name="type"/> (the default
    /// type when null) before row <paramref name="index"/> (<see cref="RowCount"/> appends).</summary>
    public SheetEditResult InsertRows(int index, int count = 1, SheetRowType? type = null)
    {
        if (index < 0 || index > _rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "Insert at least one row.");
        type ??= Schema.DefaultRowType;
        if (!Schema.RowTypes.Contains(type)) throw new ArgumentException($"Row type '{type.Name}' is not in this sheet's schema.", nameof(type));
        var result = InsertRowsCore(index, count, type);
        if (!result.Succeeded) return result;
        RaiseChanged();
        return SheetEditResult.Ok with
        {
            RowsAdded = count,
            Affected = SheetSelection.WholeRows(Enumerable.Range(index, count), ColumnCount),
        };
    }

    /// <summary>Appends a row of <paramref name="type"/> and returns it (for building a sheet in
    /// code; clear <see cref="History"/> afterwards if the build should not be undoable).</summary>
    public SheetRow AddRow(SheetRowType? type = null)
    {
        var result = InsertRows(_rows.Count, 1, type);
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
        return _rows[^1];
    }

    /// <summary>Deletes the rows at <paramref name="indices"/>. Pickups from them become Fixed.</summary>
    public SheetEditResult DeleteRows(IEnumerable<int> indices)
    {
        var sorted = indices.Distinct().OrderBy(i => i).ToArray();
        if (sorted.Length == 0) return SheetEditResult.Fail("No rows are selected.");
        foreach (int i in sorted) RowAt(i);
        if (sorted.Select(i => _rows[i]).FirstOrDefault(r => r.IsLocked) is { } locked)
            return SheetEditResult.Fail($"Row {locked.Number} ({locked.Type.Name}) cannot be deleted.");

        using (History.BeginGroup(sorted.Length == 1 ? "Delete row" : "Delete rows"))
        {
            var edit = new RemoveRowsEdit(this, sorted.Select(i => (i, _rows[i])).ToArray());
            edit.Redo();
            History.Push(edit);
            RevertDanglingPickups();
        }
        RaiseChanged();
        return SheetEditResult.Ok;
    }

    /// <summary>
    /// Moves the rows at <paramref name="indices"/> by <paramref name="offset"/> places (negative
    /// is up), keeping their order; rows in between close up behind them. Fails, changing nothing,
    /// if a row would leave the sheet or a locked row would change place.
    /// </summary>
    public SheetEditResult MoveRows(IEnumerable<int> indices, int offset)
    {
        var selected = indices.Distinct().OrderBy(i => i).ToArray();
        if (selected.Length == 0) return SheetEditResult.Fail("No rows are selected.");
        foreach (int i in selected) RowAt(i);
        if (offset == 0) return SheetEditResult.Ok;
        if (selected[0] + offset < 0) return SheetEditResult.Fail("The rows are already at the top.");
        if (selected[^1] + offset >= _rows.Count) return SheetEditResult.Fail("The rows are already at the bottom.");

        var before = _rows.ToArray();
        var after = (SheetRow[])before.Clone();
        var positions = (int[])selected.Clone();
        for (int step = 0; step < Math.Abs(offset); step++)
        {
            if (offset < 0)
                for (int k = 0; k < positions.Length; k++) { Swap(after, positions[k] - 1, positions[k]); positions[k]--; }
            else
                for (int k = positions.Length - 1; k >= 0; k--) { Swap(after, positions[k], positions[k] + 1); positions[k]++; }
        }
        for (int i = 0; i < after.Length; i++)
            if (after[i].IsLocked && !ReferenceEquals(after[i], before[i]))
                return SheetEditResult.Fail($"Row {after[i].Number} ({after[i].Type.Name}) cannot be moved.");

        var edit = new ReorderEdit(this, before, after, selected.Length == 1 ? "Move row" : "Move rows");
        edit.Redo();
        History.Push(edit);
        RaiseChanged();
        return SheetEditResult.Ok;
    }

    /// <summary>Changes a row's type (see <see cref="RowSnapshot.Retyped"/> for what carries over).</summary>
    public SheetEditResult SetRowType(int row, SheetRowType type)
    {
        var r = RowAt(row);
        ArgumentNullException.ThrowIfNull(type);
        if (!Schema.RowTypes.Contains(type)) throw new ArgumentException($"Row type '{type.Name}' is not in this sheet's schema.", nameof(type));
        if (ReferenceEquals(r.Type, type)) return SheetEditResult.Ok;
        using (History.BeginGroup("Change type"))
        {
            ModifyRow(r, r.Capture().Retyped(type), "Change type");
            RevertDanglingPickups();
        }
        RaiseChanged();
        return SheetEditResult.Ok;
    }

    /// <summary>Sets a row's tint (an index into the control's palette, or null).</summary>
    public SheetEditResult SetTint(int row, int? tint)
    {
        var r = RowAt(row);
        if (tint is < 0) throw new ArgumentOutOfRangeException(nameof(tint));
        if (r.Tint == tint) return SheetEditResult.Ok;
        ModifyRow(r, r.Capture() with { Tint = tint }, "Row colour");
        RaiseChanged();
        return SheetEditResult.Ok;
    }

    /// <summary>Sets a field of the row's properties flyout from text.</summary>
    public SheetEditResult SetProperty(int row, string key, string text)
    {
        var r = RowAt(row);
        var definition = r.Type.FindProperty(key)
            ?? throw new ArgumentException($"Row type '{r.Type.Name}' has no property '{key}'.", nameof(key));
        if (!CellText.TryParse(definition, text, out var value, out string? error))
            return SheetEditResult.Fail($"Row {r.Number}, {definition.Header}: {error}");
        if (r.Properties.TryGetValue(key, out var old) && old == value) return SheetEditResult.Ok;
        var after = r.Capture().Copy();
        ((Dictionary<string, CellValue>)after.Properties)[key] = value;
        ModifyRow(r, after, definition.Header);
        RaiseChanged();
        return SheetEditResult.Ok;
    }

    // ------------------------------------------------------------------ cells

    /// <summary>Sets a cell from typed text: the type name in column 0, else a value of the
    /// column's kind. A solve or pickup on the cell becomes Fixed.</summary>
    public SheetEditResult SetText(int row, int column, string text)
    {
        var r = RowAt(row);
        CheckColumn(column);
        text ??= "";
        if (column == SheetSchema.TypeColumn)
        {
            var type = Schema.FindRowType(text);
            return type is null
                ? SheetEditResult.Fail($"Row {r.Number}: there is no row type '{text.Trim()}'. The types are {string.Join(", ", Schema.RowTypes.Select(t => t.Name))}.")
                : SetRowType(row, type);
        }
        var cell = r.Cells[column - 1];
        if (EditableProblem(r, column) is { } problem) return SheetEditResult.Fail(problem);
        if (!CellText.TryParse(cell.Definition!, text, out var value, out string? error))
            return SheetEditResult.Fail($"Row {r.Number}, {cell.Definition!.Header}: {error}");
        return SetState(r, cell, value, TypedFlag(cell.Flag), cell.Definition!.Header);
    }

    /// <summary>Sets a cell's value directly (a tool's edit). The value must suit the column; the
    /// flag is treated as for <see cref="SetText"/>.</summary>
    public SheetEditResult SetValue(int row, int column, CellValue value)
    {
        var r = RowAt(row);
        CheckColumn(column);
        if (column == SheetSchema.TypeColumn) throw new ArgumentException("Set the type column with SetRowType.", nameof(column));
        if (EditableProblem(r, column) is { } problem) return SheetEditResult.Fail(problem);
        var cell = r.Cells[column - 1];
        if (CellText.Validate(cell.Definition!, value) is { } error)
            return SheetEditResult.Fail($"Row {r.Number}, {cell.Definition!.Header}: {error}");
        return SetState(r, cell, value, TypedFlag(cell.Flag), cell.Definition!.Header);
    }

    /// <summary>
    /// Writes a computed value (a merit operand's value, a contribution) into a cell, read-only
    /// columns included, without an undo step or a <see cref="Changed"/> event: the editor writes
    /// these in response to Changed, and recording them would make every edit two steps.
    /// </summary>
    public void SetComputed(int row, int column, CellValue value)
    {
        var r = RowAt(row);
        CheckColumn(column);
        if (column == SheetSchema.TypeColumn) throw new ArgumentException("The type column holds no value.", nameof(column));
        var cell = r.Cells[column - 1];
        if (cell.Definition is null) throw new InvalidOperationException($"Row {r.Number}: column {column} is not used by {r.Type.Name}.");
        if (CellText.Validate(cell.Definition, value) is { } error) throw new ArgumentException(error, nameof(value));
        cell.Set(cell.Definition, value, cell.Flag);
    }

    /// <summary>Sets a cell's flag after <see cref="ValidateFlag"/>. A solve given fewer
    /// parameters than its code names is padded with zeros.</summary>
    public SheetEditResult SetFlag(int row, int column, CellFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        if (ValidateFlag(row, column, flag) is { } error) return SheetEditResult.Fail(error);
        var r = _rows[row];
        var cell = r.Cells[column - 1];
        return SetState(r, cell, cell.Value, Normalised(cell.Definition!, flag), FlagDescription(flag));
    }

    /// <summary>Why <paramref name="flag"/> cannot go on the cell, or null when it can. A pickup's
    /// source must be another cell in the sheet, in use, of the same kind (number or text), and
    /// must not lead back to this cell through other pickups.</summary>
    public string? ValidateFlag(int row, int column, CellFlag flag)
    {
        var r = RowAt(row);
        CheckColumn(column);
        ArgumentNullException.ThrowIfNull(flag);
        if (column == SheetSchema.TypeColumn) return $"Row {r.Number}: the type column carries no flag.";
        if (EditableProblem(r, column) is { } problem) return problem;
        var definition = r.Cells[column - 1].Definition!;
        string where = $"Row {r.Number}, {definition.Header}";
        if (!definition.Supports(flag.Kind)) return $"{where}: cannot be {flag.Kind.ToString().ToLowerInvariant()}.";
        switch (flag.Kind)
        {
            case CellFlagKind.Solve:
                var code = definition.FindSolveCode(flag.SolveCode);
                if (code is null) return $"{where}: there is no solve '{flag.SolveCode}'.";
                if (flag.SolveParameters.Count > code.ParameterNames.Count)
                    return $"{where}: the {code.Name} solve takes {code.ParameterNames.Count} parameters, not {flag.SolveParameters.Count}.";
                if (flag.SolveParameters.Any(p => !double.IsFinite(p))) return $"{where}: solve parameters must be finite.";
                return null;

            case CellFlagKind.Pickup:
                var pickup = flag.Pickup!;
                if (!double.IsFinite(pickup.Scale) || !double.IsFinite(pickup.Offset)) return $"{where}: the scale and offset must be finite.";
                var source = FindRow(pickup.RowId);
                if (source is null) return $"{where}: the pickup's source row is not in the sheet.";
                if (pickup.Column < 1 || pickup.Column >= ColumnCount) return $"{where}: the pickup's source column {pickup.Column} does not exist.";
                var sourceCell = source.Cells[pickup.Column - 1];
                if (sourceCell.Definition is null)
                    return $"{where}: row {source.Number} ({source.Type.Name}) does not use column {pickup.Column}.";
                if (sourceCell.Definition.IsNumeric != definition.IsNumeric)
                    return $"{where}: cannot pick up {(definition.IsNumeric ? "a number" : "text")} from {sourceCell.Definition.Header}.";
                if (LeadsTo(source.Id, pickup.Column, r.Id, column)) return $"{where}: the pickup would refer back to itself.";
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Toggles Variable on the cells: when every cell that can be variable already is, they all
    /// become Fixed; otherwise they all become Variable. Cells that cannot be variable are skipped.
    /// </summary>
    public SheetEditResult ToggleVariable(IEnumerable<SheetCellAddress> cells)
    {
        var eligible = new List<SheetCell>();
        int skipped = 0;
        foreach (var address in cells)
        {
            var r = RowAt(address.Row);
            CheckColumn(address.Column);
            var cell = address.Column == SheetSchema.TypeColumn ? null : r.Cells[address.Column - 1];
            if (cell is { IsEditable: true } && cell.Definition!.Supports(CellFlagKind.Variable)) eligible.Add(cell);
            else skipped++;
        }
        if (eligible.Count == 0) return SheetEditResult.Fail("None of the selected cells can be variable.");
        var target = eligible.All(c => c.Flag.Kind == CellFlagKind.Variable) ? CellFlag.Fixed : CellFlag.Variable;
        int changed = 0;
        using (History.BeginGroup(target.Kind == CellFlagKind.Variable ? "Make variable" : "Make fixed"))
        {
            foreach (var group in eligible.Where(c => !c.Flag.Equals(target)).GroupBy(c => c.Row))
            {
                var after = group.Key.Capture().Copy();
                foreach (var cell in group) { after.Cells[cell.Slot] = new CellState(cell.Value, target); changed++; }
                ModifyRow(group.Key, after, "Flag");
            }
        }
        if (changed > 0) RaiseChanged();
        return SheetEditResult.Ok with { CellsChanged = changed, CellsSkipped = skipped };
    }

    /// <summary>Resets the cells to their column's default value and flag (Fixed unless the column says otherwise). The type column, unused and
    /// computed cells are skipped.</summary>
    public SheetEditResult ClearCells(IEnumerable<SheetCellAddress> cells, string description = "Clear")
    {
        int changed = 0, skipped = 0;
        var byRow = new Dictionary<SheetRow, RowSnapshot>();
        foreach (var address in cells)
        {
            var r = RowAt(address.Row);
            CheckColumn(address.Column);
            var cell = address.Column == SheetSchema.TypeColumn ? null : r.Cells[address.Column - 1];
            if (cell is not { IsEditable: true }) { skipped++; continue; }
            var state = new CellState(cell.Definition!.DefaultValue, cell.Definition!.DefaultFlag ?? CellFlag.Fixed);
            if (cell.State == state) continue;
            if (!byRow.TryGetValue(r, out var after)) byRow[r] = after = r.Capture().Copy();
            after.Cells[cell.Slot] = state;
            changed++;
        }
        if (changed == 0) return SheetEditResult.Ok with { CellsSkipped = skipped };
        using (History.BeginGroup(description))
        {
            foreach (var (r, after) in byRow) ModifyRow(r, after, description);
            RevertDanglingPickups();
        }
        RaiseChanged();
        return SheetEditResult.Ok with { CellsChanged = changed, CellsSkipped = skipped };
    }

    // ------------------------------------------------------------------ clipboard

    /// <summary>
    /// The block of the selection's bounding rectangle, with the cells' flags. Cells inside the
    /// rectangle that are not selected copy as empty text, which a paste skips.
    /// </summary>
    public SheetBlock Copy(SheetSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.IsEmpty) return new SheetBlock(Array.Empty<IReadOnlyList<string>>());
        var records = new List<IReadOnlyList<string>>(selection.Height);
        var flags = new CellFlag?[selection.Height][];
        for (int r = 0; r < selection.Height; r++)
        {
            var record = new string[selection.Width];
            flags[r] = new CellFlag?[selection.Width];
            for (int c = 0; c < selection.Width; c++)
            {
                var address = new SheetCellAddress(selection.Top + r, selection.Left + c);
                if (!selection.Contains(address)) { record[c] = ""; continue; }
                record[c] = GetText(address.Row, address.Column);
                if (address.Column != SheetSchema.TypeColumn) flags[r][c] = _rows[address.Row].Cells[address.Column - 1].Flag;
            }
            records.Add(record);
        }
        return new SheetBlock(records, flags, this);
    }

    /// <summary>Pastes tab-separated text (see <see cref="Paste(SheetBlock, SheetSelection)"/>).</summary>
    public SheetEditResult PasteText(string text, SheetSelection target) => Paste(SheetBlock.Parse(text), target);

    /// <summary>
    /// Pastes <paramref name="block"/> with its top-left at the selection's top-left. When the
    /// selection is a rectangle that is a whole multiple of the block, the block repeats to fill
    /// it, as in a spreadsheet. Rows past the end are appended (default type); columns past the
    /// last are dropped. A type name in column 0 retypes the row before its cells are read.
    /// Unused and computed cells are skipped; empty texts leave their cells alone. If any text
    /// does not parse, nothing changes and the message names the first bad cell.
    /// </summary>
    public SheetEditResult Paste(SheetBlock block, SheetSelection target)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(target);
        if (block.IsEmpty) return SheetEditResult.Fail("There is nothing to paste.");
        if (target.IsEmpty) return SheetEditResult.Fail("Select a cell to paste into.");
        CheckColumn(target.Left);

        int repeatRows = 1, repeatColumns = 1;
        if (target.IsRectangular && target.Height % block.RowCount == 0 && target.Width % block.ColumnCount == 0)
        {
            repeatRows = target.Height / block.RowCount;
            repeatColumns = target.Width / block.ColumnCount;
        }
        int height = block.RowCount * repeatRows;
        int width = block.ColumnCount * repeatColumns;
        int lastColumn = Math.Min(ColumnCount - 1, target.Left + width - 1);
        bool flagsApply = ReferenceEquals(block.Source, this);

        int changed = 0, skipped = 0, flagsDropped = 0, added = 0;
        using (var group = History.BeginGroup("Paste"))
        {
            int needed = target.Top + height - _rows.Count;
            if (needed > 0)
            {
                var inserted = InsertRowsCore(_rows.Count, needed, Schema.DefaultRowType);
                if (!inserted.Succeeded) { group.Rollback(); return inserted; }
                added = needed;
            }

            for (int dr = 0; dr < height; dr++)
            {
                var r = _rows[target.Top + dr];
                var after = r.Capture().Copy();
                bool rowChanged = false;
                for (int dc = 0; dc < width; dc++)
                {
                    int column = target.Left + dc;
                    string text = block.TextAt(dr % block.RowCount, dc % block.ColumnCount);
                    if (column >= ColumnCount) { if (text.Length > 0) skipped++; continue; }
                    if (text.Length == 0) continue;
                    if (column == SheetSchema.TypeColumn)
                    {
                        var type = Schema.FindRowType(text);
                        if (type is null)
                        {
                            group.Rollback();
                            return SheetEditResult.Fail($"Row {r.Number}: there is no row type '{text.Trim()}'. The types are {string.Join(", ", Schema.RowTypes.Select(t => t.Name))}.");
                        }
                        if (!ReferenceEquals(after.Type, type)) { after = after.Retyped(type); rowChanged = true; changed++; }
                        continue;
                    }

                    int slot = column - 1;
                    var definition = after.Type.ColumnAt(slot);
                    if (definition is null || definition.IsReadOnly) { skipped++; continue; }
                    if (!CellText.TryParse(definition, text, out var value, out string? error))
                    {
                        group.Rollback();
                        return SheetEditResult.Fail($"Row {r.Number}, {definition.Header}: {error}");
                    }
                    var flag = TypedFlag(after.Cells[slot].Flag);
                    if (flagsApply && block.FlagAt(dr % block.RowCount, dc % block.ColumnCount) is { } copied)
                    {
                        if (definition.Supports(copied.Kind)
                            && (copied.Kind != CellFlagKind.Solve || definition.FindSolveCode(copied.SolveCode) is not null))
                            flag = Normalised(definition, copied);
                        else
                        {
                            flag = CellFlag.Fixed;
                            flagsDropped++;
                        }
                    }
                    var state = new CellState(value, flag);
                    if (after.Cells[slot] == state) continue;
                    after.Cells[slot] = state;
                    rowChanged = true;
                    changed++;
                }
                if (rowChanged) ModifyRow(r, after, "Paste");
            }

            // A pasted pickup that now refers back to itself, or to something gone, is not kept.
            flagsDropped += RevertBadPickups();
        }
        RaiseChanged();

        var notes = new List<string>();
        if (skipped > 0) notes.Add($"{skipped} cell{(skipped == 1 ? "" : "s")} skipped (not used, computed, or past the last column)");
        if (flagsDropped > 0) notes.Add($"{flagsDropped} flag{(flagsDropped == 1 ? "" : "s")} set to fixed (not accepted where pasted)");
        int pastedRows = Math.Min(height, _rows.Count - target.Top);
        return new SheetEditResult(true, notes.Count > 0 ? "Pasted; " + string.Join("; ", notes) + "." : null)
        {
            CellsChanged = changed,
            CellsSkipped = skipped,
            RowsAdded = added,
            Affected = SheetSelection.Block(target.Top, target.Left, pastedRows, lastColumn - target.Left + 1),
        };
    }

    // ------------------------------------------------------------------ internals

    internal void RaiseStructureChanging() => StructureChanging?.Invoke(this, EventArgs.Empty);

    internal void InsertAt(int index, SheetRow row) => _rows.Insert(index, row);

    internal void RemoveAt(int index)
    {
        _rows[index].Index = -1;
        _rows.RemoveAt(index);
    }

    internal void ApplyOrder(IReadOnlyList<SheetRow> order)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (ReferenceEquals(_rows[i], order[i])) continue;
            _rows.Move(_rows.IndexOf(order[i]), i);
        }
    }

    /// <summary>Renumbers the rows and refreshes the pickup tooltips that show row numbers.</summary>
    internal void Renumber()
    {
        for (int i = 0; i < _rows.Count; i++) _rows[i].Index = i;
        foreach (var row in _rows)
            foreach (var cell in row.Cells)
                if (cell.Flag.Kind == CellFlagKind.Pickup) cell.Raise();
    }

    private SheetRow NewRow(SheetRowType type)
    {
        var row = new SheetRow(this, _nextId++, type);
        _byId[row.Id] = row;
        return row;
    }

    private SheetEditResult InsertRowsCore(int index, int count, SheetRowType type)
    {
        if (InsertGuard?.Invoke(index) is { } refusal) return SheetEditResult.Fail(refusal);
        var rows = new SheetRow[count];
        for (int i = 0; i < count; i++) rows[i] = NewRow(type);
        var edit = new InsertRowsEdit(this, index, rows);
        edit.Redo();
        History.Push(edit);
        return SheetEditResult.Ok;
    }

    private SheetEditResult SetState(SheetRow row, SheetCell cell, CellValue value, CellFlag flag, string description)
    {
        var state = new CellState(value, flag);
        if (cell.State == state) return SheetEditResult.Ok;
        var after = row.Capture().Copy();
        after.Cells[cell.Slot] = state;
        using (History.BeginGroup(description))
        {
            ModifyRow(row, after, description);
            RevertDanglingPickups();
        }
        RaiseChanged();
        return SheetEditResult.Ok with { CellsChanged = 1 };
    }

    private void ModifyRow(SheetRow row, RowSnapshot after, string description)
    {
        var before = row.Capture();
        row.Restore(after);
        History.Push(new RowContentEdit(row, before, after, description));
    }

    /// <summary>Pickups whose source is gone, unused or of the other kind become Fixed.</summary>
    private void RevertDanglingPickups() => RevertPickups(checkCycles: false);

    /// <summary>As <see cref="RevertDanglingPickups"/>, and cycles too (after a paste, which can
    /// bring in several pickups at once). Returns the number reverted.</summary>
    private int RevertBadPickups() => RevertPickups(checkCycles: true);

    private int RevertPickups(bool checkCycles)
    {
        int reverted = 0;
        foreach (var row in _rows)
        {
            RowSnapshot? after = null;
            foreach (var cell in row.Cells)
            {
                if (cell.Flag.Kind != CellFlagKind.Pickup || cell.Definition is null) continue;
                var pickup = cell.Flag.Pickup!;
                var source = FindRow(pickup.RowId);
                bool bad = source is null
                    || pickup.Column < 1 || pickup.Column >= ColumnCount
                    || source.Cells[pickup.Column - 1].Definition is not { } d
                    || d.IsNumeric != cell.Definition.IsNumeric
                    || (checkCycles && LeadsTo(source.Id, pickup.Column, row.Id, cell.Column));
                if (!bad) continue;
                after ??= row.Capture().Copy();
                after.Cells[cell.Slot] = new CellState(cell.Value, CellFlag.Fixed);
                reverted++;
            }
            if (after is not null) ModifyRow(row, after, "Pickup removed");
        }
        return reverted;
    }

    /// <summary>True when following pickups from (row, column) reaches (targetRow, targetColumn).</summary>
    private bool LeadsTo(long rowId, int column, long targetRowId, int targetColumn)
    {
        var visited = new HashSet<(long, int)>();
        while (visited.Add((rowId, column)))
        {
            if (rowId == targetRowId && column == targetColumn) return true;
            var row = FindRow(rowId);
            if (row is null || column < 1 || column >= ColumnCount) return false;
            var flag = row.Cells[column - 1].Flag;
            if (flag.Kind != CellFlagKind.Pickup) return false;
            rowId = flag.Pickup!.RowId;
            column = flag.Pickup.Column;
        }
        return false;   // a cycle further down the chain that does not pass through the target
    }

    private static CellFlag TypedFlag(CellFlag flag) =>
        flag.Kind is CellFlagKind.Solve or CellFlagKind.Pickup ? CellFlag.Fixed : flag;

    private static CellFlag Normalised(SheetColumn definition, CellFlag flag)
    {
        if (flag.Kind != CellFlagKind.Solve) return flag;
        var code = definition.FindSolveCode(flag.SolveCode)!;
        if (flag.SolveParameters.Count >= code.ParameterNames.Count) return flag;
        var padded = new double[code.ParameterNames.Count];
        for (int i = 0; i < flag.SolveParameters.Count; i++) padded[i] = flag.SolveParameters[i];
        return flag.WithParameters(padded);
    }

    private static string FlagDescription(CellFlag flag) => flag.Kind switch
    {
        CellFlagKind.Variable => "Make variable",
        CellFlagKind.Solve => "Solve",
        CellFlagKind.Pickup => "Pickup",
        _ => "Make fixed",
    };

    private string? EditableProblem(SheetRow row, int column)
    {
        var cell = row.Cells[column - 1];
        if (cell.Definition is null) return $"Row {row.Number}: {row.Type.Name} does not use column {column} ({Schema.SlotHeaders[column - 1]}).";
        if (cell.Definition.IsReadOnly) return $"Row {row.Number}, {cell.Definition.Header}: computed, not editable.";
        return null;
    }

    private SheetRow RowAt(int index)
    {
        if (index < 0 || index >= _rows.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The sheet has {_rows.Count} rows.");
        return _rows[index];
    }

    private void CheckColumn(int column)
    {
        if (column < 0 || column >= ColumnCount)
            throw new ArgumentOutOfRangeException(nameof(column), column, $"The sheet has {ColumnCount} columns.");
    }

    private static void Swap(SheetRow[] rows, int a, int b) => (rows[a], rows[b]) = (rows[b], rows[a]);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
