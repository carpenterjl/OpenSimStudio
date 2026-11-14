namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// What one slot of a row type holds: its header, the kind of value, the flags it accepts and its
/// limits. The same definition also describes a field of a row's properties flyout
/// (<see cref="SheetRowType.Properties"/>), where flags do not apply.
/// </summary>
/// <param name="Key">The name the owning editor reads the value by. When a row changes type, a
/// value carries over to the slot of the new type with the same key and kind.</param>
/// <param name="Header">The column header (with its unit, e.g. "Radius [mm]").</param>
public sealed record SheetColumn(string Key, string Header)
{
    /// <summary>The kind of value. Number by default.</summary>
    public CellKind Kind { get; init; } = CellKind.Number;

    /// <summary>The allowed values of a <see cref="CellKind.Choice"/> column, in display order.</summary>
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary>The flags besides Fixed this column accepts.</summary>
    public CellFlagSupport Flags { get; init; } = CellFlagSupport.None;

    /// <summary>The solves offered when <see cref="Flags"/> includes Solve.</summary>
    public IReadOnlyList<SheetSolveCode> SolveCodes { get; init; } = Array.Empty<SheetSolveCode>();

    /// <summary>The value of a new cell, or of a cleared one; see <see cref="DefaultValue"/>.</summary>
    public CellValue? Default { get; init; }

    /// <summary>The flag of a new or cleared cell; null is Fixed. OP-31's clear semi-diameter
    /// starts as the "Automatic" solve, as in OpticStudio's editor.</summary>
    public CellFlag? DefaultFlag { get; init; }

    /// <summary>True for a computed column (a merit operand's value, a contribution) the user does
    /// not type into; the owning editor writes it through <see cref="SheetDocument.SetComputed"/>.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>True when ±infinity is a valid number here (a flat surface's radius).</summary>
    public bool AllowsInfinity { get; init; }

    /// <summary>The smallest valid number, inclusive; null for no limit.</summary>
    public double? Minimum { get; init; }

    /// <summary>The largest valid number, inclusive; null for no limit.</summary>
    public double? Maximum { get; init; }

    /// <summary>The .NET format the cell shows a number in. Copy and edit always use the
    /// shortest round-trip form, so what is shown never loses digits on its way to the clipboard.</summary>
    public string NumberFormat { get; init; } = "G8";

    /// <summary>A description shown when the pointer rests on the cell.</summary>
    public string? ToolTip { get; init; }

    /// <summary>The value a new or cleared cell takes: <see cref="Default"/>, else 0 for numbers,
    /// the first choice for a choice column, and empty text for text.</summary>
    public CellValue DefaultValue => Default ?? Kind switch
    {
        CellKind.Number or CellKind.Integer => CellValue.Of(0.0),
        CellKind.Choice => CellValue.Of(Choices.Count > 0 ? Choices[0] : ""),
        _ => CellValue.Empty,
    };

    /// <summary>True when the column holds numbers (Number or Integer).</summary>
    public bool IsNumeric => Kind is CellKind.Number or CellKind.Integer;

    /// <summary>True when the column accepts <paramref name="kind"/>.</summary>
    public bool Supports(CellFlagKind kind) => kind switch
    {
        CellFlagKind.Fixed => true,
        CellFlagKind.Variable => Flags.HasFlag(CellFlagSupport.Variable),
        CellFlagKind.Solve => Flags.HasFlag(CellFlagSupport.Solve) && SolveCodes.Count > 0,
        CellFlagKind.Pickup => Flags.HasFlag(CellFlagSupport.Pickup),
        _ => false,
    };

    /// <summary>The solve with this key, or null.</summary>
    public SheetSolveCode? FindSolveCode(string? key) =>
        key is null ? null : SolveCodes.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
}
