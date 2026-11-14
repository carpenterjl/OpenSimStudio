namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// Undo and redo history. Edits are pushed after they are done; a new edit clears the redo side.
/// Edits made inside a <see cref="BeginGroup"/> become one entry, so a paste of a hundred cells
/// undoes in one step, and a group can be rolled back instead, which is how a paste that fails
/// on its tenth cell leaves the sheet as it was.
/// </summary>
public sealed class UndoStack
{
    private readonly List<IUndoableEdit> _undo = new();
    private readonly Stack<IUndoableEdit> _redo = new();
    private readonly Stack<UndoGroup> _open = new();
    private bool _replaying;

    /// <summary>A history keeping at most <paramref name="capacity"/> undo steps (the oldest go first).</summary>
    public UndoStack(int capacity = 1000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity must be at least one.");
        Capacity = capacity;
    }

    /// <summary>Raised after a push, undo, redo or clear.</summary>
    public event EventHandler? Changed;

    /// <summary>The most undo steps kept.</summary>
    public int Capacity { get; }

    /// <summary>True when there is an edit to undo.</summary>
    public bool CanUndo => _undo.Count > 0 && _open.Count == 0;

    /// <summary>True when there is an edit to redo.</summary>
    public bool CanRedo => _redo.Count > 0 && _open.Count == 0;

    /// <summary>The number of undo steps.</summary>
    public int UndoCount => _undo.Count;

    /// <summary>The number of redo steps.</summary>
    public int RedoCount => _redo.Count;

    /// <summary>The description of the edit <see cref="Undo"/> would undo, or null.</summary>
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    /// <summary>The description of the edit <see cref="Redo"/> would redo, or null.</summary>
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    /// <summary>True while a group is open.</summary>
    public bool IsInGroup => _open.Count > 0;

    /// <summary>Records an edit that has just been done.</summary>
    public void Push(IUndoableEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_replaying) throw new InvalidOperationException("An edit was pushed while undoing or redoing.");
        if (_open.Count > 0)
        {
            _open.Peek().Add(edit);
            return;
        }
        _undo.Add(edit);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undoes the last edit; false when there is none.</summary>
    public bool Undo()
    {
        if (_open.Count > 0) throw new InvalidOperationException("Cannot undo while a group is open.");
        if (_undo.Count == 0) return false;
        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Replay(edit.Undo);
        _redo.Push(edit);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Redoes the last undone edit; false when there is none.</summary>
    public bool Redo()
    {
        if (_open.Count > 0) throw new InvalidOperationException("Cannot redo while a group is open.");
        if (_redo.Count == 0) return false;
        var edit = _redo.Pop();
        Replay(edit.Redo);
        _undo.Add(edit);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Opens a group: edits pushed until it is disposed become one step named
    /// <paramref name="description"/>. Groups nest; an inner group becomes one edit of the outer.</summary>
    public UndoGroup BeginGroup(string description)
    {
        var group = new UndoGroup(this, description);
        _open.Push(group);
        return group;
    }

    /// <summary>Forgets all history (after loading a file, say).</summary>
    public void Clear()
    {
        if (_open.Count > 0) throw new InvalidOperationException("Cannot clear while a group is open.");
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Close(UndoGroup group, IUndoableEdit? result)
    {
        if (_open.Count == 0 || !ReferenceEquals(_open.Peek(), group))
            throw new InvalidOperationException("Undo groups must be closed in the reverse order they were opened.");
        _open.Pop();
        if (result is not null) Push(result);
    }

    internal void Replay(Action action)
    {
        _replaying = true;
        try { action(); }
        finally { _replaying = false; }
    }
}
