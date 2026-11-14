namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>What a spreadsheet cell holds, which decides how its text is parsed and shown.</summary>
public enum CellKind
{
    /// <summary>A real number; infinity only where the column allows it (a flat radius).</summary>
    Number,

    /// <summary>A whole number (a surface or configuration index), stored as a double.</summary>
    Integer,

    /// <summary>Free text (a comment, a glass name).</summary>
    Text,

    /// <summary>One of the column's fixed choices, matched without regard to case.</summary>
    Choice,
}
