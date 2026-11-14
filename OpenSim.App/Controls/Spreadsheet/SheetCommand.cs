namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>The commands of the spreadsheet grid, run from its keys, its context menu or an
/// editor's toolbar through <see cref="SpreadsheetGrid.Execute"/>.</summary>
public enum SheetCommand
{
    /// <summary>Copy the selection as tab-separated text.</summary>
    Copy,

    /// <summary>Copy the selection, then clear it.</summary>
    Cut,

    /// <summary>Paste clipboard text at the selection.</summary>
    Paste,

    /// <summary>Undo the last edit.</summary>
    Undo,

    /// <summary>Redo the last undone edit.</summary>
    Redo,

    /// <summary>Insert as many rows as are selected, above the selection.</summary>
    InsertRowAbove,

    /// <summary>Insert as many rows as are selected, below the selection.</summary>
    InsertRowBelow,

    /// <summary>Delete the selected rows.</summary>
    DeleteRows,

    /// <summary>Reset the selected cells to their defaults.</summary>
    ClearCells,

    /// <summary>Move the selected rows up one place.</summary>
    MoveRowsUp,

    /// <summary>Move the selected rows down one place.</summary>
    MoveRowsDown,

    /// <summary>Toggle Variable on the selected cells.</summary>
    ToggleVariable,

    /// <summary>Open the flag editor of the current cell.</summary>
    EditFlag,

    /// <summary>Open the current row's properties flyout.</summary>
    RowProperties,
}
