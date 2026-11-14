namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>The flags a column accepts besides <see cref="CellFlagKind.Fixed"/>, which every
/// editable column accepts.</summary>
[Flags]
public enum CellFlagSupport
{
    /// <summary>Only fixed values.</summary>
    None = 0,

    /// <summary>The cell may be marked variable.</summary>
    Variable = 1,

    /// <summary>The cell may carry one of the column's solve codes.</summary>
    Solve = 2,

    /// <summary>The cell may pick its value up from another cell.</summary>
    Pickup = 4,

    /// <summary>Every flag.</summary>
    All = Variable | Solve | Pickup,
}
