namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// One solve a column offers (a marginal-ray height, an F-number, a chief-ray angle...). The sheet
/// only stores which solve a cell carries and its parameters; the editor that owns the sheet
/// defines what each code computes.
/// </summary>
/// <param name="Key">The code stored in the flag; unique within the column.</param>
/// <param name="Name">The name shown in menus.</param>
/// <param name="Badge">The one or two characters shown in the cell beside its value.</param>
public sealed record SheetSolveCode(string Key, string Name, string Badge)
{
    /// <summary>The names of the solve's numeric parameters, in order (empty for none).</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = Array.Empty<string>();
}
