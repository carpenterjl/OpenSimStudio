namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>How a cell's value is decided: typed in, left to the optimizer, computed by a solve,
/// or copied from another cell.</summary>
public enum CellFlagKind
{
    /// <summary>The value is what was typed.</summary>
    Fixed,

    /// <summary>The optimizer may change the value.</summary>
    Variable,

    /// <summary>A solve computes the value; the solve's code and parameters say how.</summary>
    Solve,

    /// <summary>The value follows another cell, scaled and offset.</summary>
    Pickup,
}
