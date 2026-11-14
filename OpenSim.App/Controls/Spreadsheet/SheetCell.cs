using System.ComponentModel;
using System.Globalization;

namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// One slot of one row: its value, its flag and, from the row's type, its definition. Read-only
/// from outside; every change goes through <see cref="SheetDocument"/> so it lands on the undo
/// stack. Raises <see cref="PropertyChanged"/> with an empty name (every property) on a change,
/// which is what the grid's cells bind to.
/// </summary>
public sealed class SheetCell : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    internal SheetCell(SheetRow row, int slot)
    {
        Row = row;
        Slot = slot;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The row this cell belongs to.</summary>
    public SheetRow Row { get; }

    /// <summary>The slot index (0-based).</summary>
    public int Slot { get; }

    /// <summary>The grid column (the slot plus one: column 0 is the row type).</summary>
    public int Column => Slot + 1;

    /// <summary>The definition from the row's type; null where the type does not use this slot.</summary>
    public SheetColumn? Definition { get; private set; }

    /// <summary>The value.</summary>
    public CellValue Value { get; private set; } = CellValue.Empty;

    /// <summary>The flag.</summary>
    public CellFlag Flag { get; private set; } = CellFlag.Fixed;

    /// <summary>True when the row's type uses this slot.</summary>
    public bool IsUsed => Definition is not null;

    /// <summary>True when the user may type into the cell.</summary>
    public bool IsEditable => Definition is { IsReadOnly: false };

    /// <summary>The value as shown.</summary>
    public string Display => Definition is null ? "" : CellText.Display(Definition, Value);

    /// <summary>The value as edited and copied (exact round trip).</summary>
    public string EditText => Definition is null ? "" : CellText.Format(Definition, Value);

    /// <summary>A note the owning editor attaches to the cell (a solve's failure, TC-6), shown
    /// in the tooltip; null for none. Not part of the content: no undo step, not copied.</summary>
    public string? Note { get; private set; }

    /// <summary>True when <see cref="Note"/> reports a failure; the badge then ends in "!".</summary>
    public bool NoteIsError { get; private set; }

    /// <summary>The flag's mark beside the value: V, P, a solve's badge, or nothing; followed by
    /// "!" when an error note is attached.</summary>
    public string Badge => (Flag.Kind switch
    {
        CellFlagKind.Variable => "V",
        CellFlagKind.Pickup => "P",
        CellFlagKind.Solve => Definition?.FindSolveCode(Flag.SolveCode)?.Badge ?? "S",
        _ => "",
    }) + (NoteIsError ? "!" : "");

    /// <summary>The tooltip: what the column is, then what decides the value.</summary>
    public string? ToolTip
    {
        get
        {
            if (Definition is null) return $"Not used by {Row.Type.Name}.";
            string? flag = Flag.Kind switch
            {
                CellFlagKind.Variable => "Variable.",
                CellFlagKind.Solve => SolveText(),
                CellFlagKind.Pickup => PickupText(),
                _ => null,
            };
            string? head = Definition.ToolTip;
            if (Definition.IsReadOnly) head = head is null ? "Computed." : head + " Computed.";
            string? text = head is null ? flag : flag is null ? head : head + "\n" + flag;
            return Note is null ? text : text is null ? Note : text + "\n" + Note;
        }
    }

    internal CellState State => new(Value, Flag);

    internal void Set(SheetColumn? definition, CellValue value, CellFlag flag)
    {
        Definition = definition;
        Value = value;
        Flag = flag;
        Raise();
    }

    internal void Raise() => PropertyChanged?.Invoke(this, AllChanged);

    internal void SetNote(string? note, bool isError)
    {
        if (Note == note && NoteIsError == (note is not null && isError)) return;
        Note = note;
        NoteIsError = note is not null && isError;
        Raise();
    }

    private string SolveText()
    {
        var code = Definition?.FindSolveCode(Flag.SolveCode);
        string name = code?.Name ?? Flag.SolveCode ?? "?";
        if (code is null || code.ParameterNames.Count == 0) return $"Solve: {name}.";
        var parts = code.ParameterNames.Select((p, i) =>
            $"{p} = {(i < Flag.SolveParameters.Count ? Flag.SolveParameters[i] : 0).ToString("G8", CultureInfo.InvariantCulture)}");
        return $"Solve: {name} ({string.Join(", ", parts)}).";
    }

    private string PickupText()
    {
        var pickup = Flag.Pickup!;
        var source = Row.Document.FindRow(pickup.RowId);
        if (source is null) return "Pickup from a deleted row.";
        string header = Row.Document.Schema.HeaderOf(pickup.Column, source.Type);
        return string.Create(CultureInfo.InvariantCulture,
            $"Pickup from row {source.Number}, {header}: × {pickup.Scale:G8} + {pickup.Offset:G8}.");
    }
}
