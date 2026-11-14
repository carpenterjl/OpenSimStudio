namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// An open group of edits (see <see cref="UndoStack.BeginGroup"/>). Disposing it records the
/// edits as one step; <see cref="Rollback"/> first undoes them and records nothing.
/// </summary>
public sealed class UndoGroup : IDisposable
{
    private readonly UndoStack _stack;
    private readonly List<IUndoableEdit> _edits = new();
    private bool _rolledBack;
    private bool _closed;

    internal UndoGroup(UndoStack stack, string description)
    {
        _stack = stack;
        Description = description;
    }

    /// <summary>The name the step gets.</summary>
    public string Description { get; }

    /// <summary>The number of edits so far.</summary>
    public int Count => _edits.Count;

    internal void Add(IUndoableEdit edit) => _edits.Add(edit);

    /// <summary>Undoes every edit of the group, last first, and discards them.</summary>
    public void Rollback()
    {
        if (_closed) throw new InvalidOperationException("The group is already closed.");
        _stack.Replay(() =>
        {
            for (int i = _edits.Count - 1; i >= 0; i--) _edits[i].Undo();
        });
        _edits.Clear();
        _rolledBack = true;
    }

    /// <summary>Closes the group, recording its edits as one step unless it was rolled back or empty.</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        IUndoableEdit? result = _rolledBack || _edits.Count == 0 ? null : new CompoundEdit(Description, _edits.ToArray());
        _stack.Close(this, result);
    }

    private sealed class CompoundEdit : IUndoableEdit
    {
        private readonly IUndoableEdit[] _edits;

        public CompoundEdit(string description, IUndoableEdit[] edits)
        {
            Description = description;
            _edits = edits;
        }

        public string Description { get; }

        public void Undo()
        {
            for (int i = _edits.Length - 1; i >= 0; i--) _edits[i].Undo();
        }

        public void Redo()
        {
            foreach (var edit in _edits) edit.Redo();
        }
    }
}
