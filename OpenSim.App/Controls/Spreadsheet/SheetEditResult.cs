namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// The outcome of a sheet edit. A failed edit changed nothing and its <see cref="Message"/> names
/// the row and column and what was wrong, ready for the log panel.
/// </summary>
/// <param name="Succeeded">True when the edit was made (or there was nothing to change).</param>
/// <param name="Message">Why it failed, or a note on a success (cells skipped); null otherwise.</param>
public sealed record SheetEditResult(bool Succeeded, string? Message)
{
    /// <summary>A success with nothing to report.</summary>
    public static SheetEditResult Ok { get; } = new(true, null);

    /// <summary>A failure.</summary>
    public static SheetEditResult Fail(string message) => new(false, message);

    /// <summary>The number of cells whose value or flag changed (paste, clear, toggle).</summary>
    public int CellsChanged { get; init; }

    /// <summary>The number of cells passed over: unused, computed, past the last column, or a
    /// column that does not accept the flag.</summary>
    public int CellsSkipped { get; init; }

    /// <summary>The number of rows added (a paste past the last row).</summary>
    public int RowsAdded { get; init; }

    /// <summary>The cells the edit covered, for the grid to select afterwards; null when not applicable.</summary>
    public SheetSelection? Affected { get; init; }
}
