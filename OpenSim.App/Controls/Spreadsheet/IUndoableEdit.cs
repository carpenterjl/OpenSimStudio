namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>An edit that has been done and can be undone and redone, any number of times.</summary>
public interface IUndoableEdit
{
    /// <summary>What the edit did, for the Undo and Redo menu items ("Paste", "Delete rows").</summary>
    string Description { get; }

    /// <summary>Puts the state back as it was before the edit.</summary>
    void Undo();

    /// <summary>Does the edit again on the state <see cref="Undo"/> left.</summary>
    void Redo();
}
