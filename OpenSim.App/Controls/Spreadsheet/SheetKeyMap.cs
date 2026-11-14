using System.Windows.Input;

namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// The grid's keys beyond the DataGrid's own (arrows, Tab, Home/End, Page Up/Down, F2, Enter,
/// Escape, Shift and Ctrl selection, Ctrl+A). Copy, cut and paste answer both the Ctrl+C/X/V keys
/// and the older Ctrl+Insert, Shift+Delete and Shift+Insert forms Windows applications accept.
/// Alt+Enter opens the row's properties, as it opens an item's properties in Windows Explorer.
/// These keys apply only while no cell is being edited: inside a cell's text box Ctrl+C copies
/// text and Delete deletes characters.
/// </summary>
public static class SheetKeyMap
{
    /// <summary>The key list shown in menus and tooltips, one entry per command.</summary>
    public static IReadOnlyDictionary<SheetCommand, string> GestureText { get; } = new Dictionary<SheetCommand, string>
    {
        [SheetCommand.Copy] = "Ctrl+C",
        [SheetCommand.Cut] = "Ctrl+X",
        [SheetCommand.Paste] = "Ctrl+V",
        [SheetCommand.Undo] = "Ctrl+Z",
        [SheetCommand.Redo] = "Ctrl+Y",
        [SheetCommand.InsertRowAbove] = "Insert",
        [SheetCommand.InsertRowBelow] = "Alt+Insert",
        [SheetCommand.DeleteRows] = "Ctrl+Delete",
        [SheetCommand.ClearCells] = "Delete",
        [SheetCommand.MoveRowsUp] = "Alt+Up",
        [SheetCommand.MoveRowsDown] = "Alt+Down",
        [SheetCommand.ToggleVariable] = "Ctrl+Shift+V",
        [SheetCommand.EditFlag] = "Ctrl+Shift+F",
        [SheetCommand.RowProperties] = "Alt+Enter",
    };

    /// <summary>The command for a key press, or null when the grid should leave it to the
    /// DataGrid. Pass <see cref="KeyEventArgs.SystemKey"/> when the key is <see cref="Key.System"/>
    /// (an Alt combination).</summary>
    public static SheetCommand? Resolve(Key key, ModifierKeys modifiers)
    {
        bool ctrl = modifiers == ModifierKeys.Control;
        bool shift = modifiers == ModifierKeys.Shift;
        bool alt = modifiers == ModifierKeys.Alt;
        bool ctrlShift = modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        bool none = modifiers == ModifierKeys.None;

        return key switch
        {
            Key.C when ctrl => SheetCommand.Copy,
            Key.Insert when ctrl => SheetCommand.Copy,
            Key.X when ctrl => SheetCommand.Cut,
            Key.Delete when shift => SheetCommand.Cut,
            Key.V when ctrl => SheetCommand.Paste,
            Key.Insert when shift => SheetCommand.Paste,
            Key.Z when ctrl => SheetCommand.Undo,
            Key.Y when ctrl => SheetCommand.Redo,
            Key.Z when ctrlShift => SheetCommand.Redo,
            Key.Insert when none => SheetCommand.InsertRowAbove,
            Key.Insert when alt => SheetCommand.InsertRowBelow,
            Key.Delete when ctrl => SheetCommand.DeleteRows,
            Key.Delete when none => SheetCommand.ClearCells,
            Key.Back when none => SheetCommand.ClearCells,
            Key.Up when alt => SheetCommand.MoveRowsUp,
            Key.Down when alt => SheetCommand.MoveRowsDown,
            Key.V when ctrlShift => SheetCommand.ToggleVariable,
            Key.F when ctrlShift => SheetCommand.EditFlag,
            Key.Enter when alt => SheetCommand.RowProperties,
            _ => null,
        };
    }
}
