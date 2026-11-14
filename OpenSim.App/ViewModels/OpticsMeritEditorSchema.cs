using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Optics.Merit;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Merit Function Editor's sheet (OP-54): one row type per registered operand, named
/// "Effective focal length (EFFL)" with the four-letter code as an alias, so either can be typed
/// or pasted (OQ-13). Slots: comment, Int1, Int2, Data1 to Data6, target, weight, and the
/// computed value and percent contribution (read-only, written on Update). A slot an operand
/// does not use is empty; the parameter headers follow the current row ("Surf", "Wave", "Hx"...).
/// </summary>
public static class OpticsMeritEditorSchema
{
    public const int CommentSlot = 0;
    public const int Int1Slot = 1;
    public const int Int2Slot = 2;
    public const int Data1Slot = 3;
    public const int TargetSlot = 9;
    public const int WeightSlot = 10;
    public const int ValueSlot = 11;
    public const int ContributionSlot = 12;

    public const string CommentKey = "comment", Int1Key = "int1", Int2Key = "int2", TargetKey = "target",
        WeightKey = "weight", ValueKey = "value", ContributionKey = "contribution";

    public static string DataKey(int k) => $"data{k}";

    private static readonly string[] SlotHeaders =
        { "Comment", "Int1", "Int2", "Data1", "Data2", "Data3", "Data4", "Data5", "Data6", "Target", "Weight", "Value", "% Contrib" };

    /// <summary>The weight a new row starts with. OpticStudio's default was not read; 1 is this
    /// program's choice, so a new operand counts.</summary>
    public const double DefaultWeight = 1;

    /// <summary>The sheet for the operands of <paramref name="registry"/>; BLNK first (the type
    /// of a new row).</summary>
    public static SheetSchema Build(MeritOperandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var ordered = registry.All.Where(d => d.Code == "BLNK").Concat(registry.All.Where(d => d.Code != "BLNK"));
        return new SheetSchema("Operand", SlotHeaders, ordered.Select(RowType).ToArray()) { FirstRowNumber = 1 };
    }

    /// <summary>The row type's name for an operand.</summary>
    public static string TypeName(MeritOperandDefinition d) => d.DisplayName;

    private static SheetRowType RowType(MeritOperandDefinition d)
    {
        var columns = new SheetColumn?[SlotHeaders.Length];
        columns[CommentSlot] = new SheetColumn(CommentKey, "Comment") { Kind = CellKind.Text };
        if (d.Int1 is { } i1) columns[Int1Slot] = new SheetColumn(Int1Key, i1) { Kind = CellKind.Integer, ResetOnRetype = true };
        if (d.Int2 is { } i2) columns[Int2Slot] = new SheetColumn(Int2Key, i2) { Kind = CellKind.Integer, ResetOnRetype = true };
        for (int k = 1; k <= MeritOperandRow.DataCount; k++)
            if (d.DataLabel(k) is { } label)
                columns[Data1Slot + k - 1] = new SheetColumn(DataKey(k), label) { ResetOnRetype = true };
        if (d.UsesTargetAndWeight)
        {
            columns[TargetSlot] = new SheetColumn(TargetKey, "Target");
            columns[WeightSlot] = new SheetColumn(WeightKey, "Weight") { Default = CellValue.Of(DefaultWeight) };
            columns[ValueSlot] = new SheetColumn(ValueKey, "Value") { Kind = CellKind.Text, IsReadOnly = true, ToolTip = "Computed by Update." };
            columns[ContributionSlot] = new SheetColumn(ContributionKey, "% Contrib") { Kind = CellKind.Text, IsReadOnly = true, ToolTip = "Share of the merit function, percent." };
        }
        return new SheetRowType(TypeName(d), columns)
        {
            Aliases = new[] { d.Code, d.Name },
            Description = $"{d.Code}: {d.Description}",
        };
    }
}
