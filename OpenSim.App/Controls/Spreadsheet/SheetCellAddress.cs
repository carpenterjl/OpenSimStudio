namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>A cell's position: row index (0-based) and grid column (0 is the row type).</summary>
public readonly record struct SheetCellAddress(int Row, int Column);
