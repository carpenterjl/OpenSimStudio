namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>The undo steps a sheet records: one for row content, three for row structure.</summary>
public sealed partial class SheetDocument
{
    /// <summary>A change to one row's content, undone by restoring the snapshot before it.</summary>
    private sealed class RowContentEdit : IUndoableEdit
    {
        private readonly SheetRow _row;
        private readonly RowSnapshot _before;
        private readonly RowSnapshot _after;

        public RowContentEdit(SheetRow row, RowSnapshot before, RowSnapshot after, string description)
        {
            _row = row;
            _before = before;
            _after = after;
            Description = description;
        }

        public string Description { get; }

        public void Undo() => _row.Restore(_before);

        public void Redo() => _row.Restore(_after);
    }

    /// <summary>Rows inserted as a run at one index.</summary>
    private sealed class InsertRowsEdit : IUndoableEdit
    {
        private readonly SheetDocument _document;
        private readonly int _index;
        private readonly SheetRow[] _rows;

        public InsertRowsEdit(SheetDocument document, int index, SheetRow[] rows)
        {
            _document = document;
            _index = index;
            _rows = rows;
        }

        public string Description => _rows.Length == 1 ? "Insert row" : "Insert rows";

        public void Redo()
        {
            _document.RaiseStructureChanging();
            for (int i = 0; i < _rows.Length; i++) _document.InsertAt(_index + i, _rows[i]);
            _document.Renumber();
        }

        public void Undo()
        {
            _document.RaiseStructureChanging();
            for (int i = _rows.Length - 1; i >= 0; i--) _document.RemoveAt(_index + i);
            _document.Renumber();
        }
    }

    /// <summary>Rows removed from their places (indices ascending, as they were before).</summary>
    private sealed class RemoveRowsEdit : IUndoableEdit
    {
        private readonly SheetDocument _document;
        private readonly (int Index, SheetRow Row)[] _removed;

        public RemoveRowsEdit(SheetDocument document, (int Index, SheetRow Row)[] removed)
        {
            _document = document;
            _removed = removed;
        }

        public string Description => _removed.Length == 1 ? "Delete row" : "Delete rows";

        public void Redo()
        {
            _document.RaiseStructureChanging();
            for (int i = _removed.Length - 1; i >= 0; i--) _document.RemoveAt(_removed[i].Index);
            _document.Renumber();
        }

        public void Undo()
        {
            _document.RaiseStructureChanging();
            foreach (var (index, row) in _removed) _document.InsertAt(index, row);
            _document.Renumber();
        }
    }

    /// <summary>A reordering of all rows.</summary>
    private sealed class ReorderEdit : IUndoableEdit
    {
        private readonly SheetDocument _document;
        private readonly SheetRow[] _before;
        private readonly SheetRow[] _after;

        public ReorderEdit(SheetDocument document, SheetRow[] before, SheetRow[] after, string description)
        {
            _document = document;
            _before = before;
            _after = after;
            Description = description;
        }

        public string Description { get; }

        public void Redo() => Apply(_after);

        public void Undo() => Apply(_before);

        private void Apply(SheetRow[] order)
        {
            _document.RaiseStructureChanging();
            _document.ApplyOrder(order);
            _document.Renumber();
        }
    }
}
