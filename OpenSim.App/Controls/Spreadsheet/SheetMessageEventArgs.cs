namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>A message from the grid for the log panel: a rejected edit or paste, or a note on a
/// paste that skipped cells. The editor hosting the grid forwards it to the log.</summary>
public sealed class SheetMessageEventArgs : EventArgs
{
    /// <summary>A message.</summary>
    public SheetMessageEventArgs(string text, bool isError)
    {
        Text = text;
        IsError = isError;
    }

    /// <summary>The message, naming the row and column where it applies.</summary>
    public string Text { get; }

    /// <summary>True when an edit was refused; false for a note on an edit that was made.</summary>
    public bool IsError { get; }
}
