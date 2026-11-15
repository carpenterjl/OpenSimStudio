using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Tolerance Data Editor's sheet (OP-63): one row type per tolerance operand, named
/// "Tolerance on radius (TRAD)" with the four-letter code as an alias, so either can be typed.
/// Slots: comment, Int1 to Int3, the nominal value (read-only, written by Update), Min, Max, and
/// OpticStudio's two operand properties "Ignore this Operand During Tolerancing" and "Do Not
/// Adjust During Inverse Tolerancing" as Yes/No columns. A slot an operand does not use is empty;
/// the headers follow the current row ("Surf", "Adjust", …), as OpticStudio's editor shows them
/// (help page "Tolerance Data Editor", its screenshot: Type, Surf, Code, Nominal, Min, Max, Comment).
/// TWAV keeps its wavelength in Min, as the help says.
/// </summary>
public static class OpticsToleranceEditorSchema
{
    public const int CommentSlot = 0;
    public const int Int1Slot = 1;
    public const int Int2Slot = 2;
    public const int Int3Slot = 3;
    public const int NominalSlot = 4;
    public const int MinSlot = 5;
    public const int MaxSlot = 6;
    public const int IgnoreSlot = 7;
    public const int DoNotAdjustSlot = 8;

    public const string CommentKey = "comment", Int1Key = "int1", Int2Key = "int2", Int3Key = "int3", NominalKey = "nominal",
        MinKey = "min", MaxKey = "max", IgnoreKey = "ignore", DoNotAdjustKey = "fixed";

    public const string No = "No", Yes = "Yes";

    private static readonly IReadOnlyList<string> YesNo = new[] { No, Yes };

    private static readonly string[] SlotHeaders =
        { "Comment", "Int1", "Int2", "Int3", "Nominal", "Min", "Max", "Ignore", "Do not adjust" };

    /// <summary>The sheet for every operand of <see cref="ToleranceOperandRegistry"/>; TOFF first
    /// (the type of a new row).</summary>
    public static SheetSchema Build() =>
        new("Operand", SlotHeaders, ToleranceOperandRegistry.All.Select(RowType).ToArray()) { FirstRowNumber = 1 };

    /// <summary>The row type's name for an operand.</summary>
    public static string TypeName(ToleranceOperandDefinition d) => d.DisplayName;

    private static SheetRowType RowType(ToleranceOperandDefinition d)
    {
        var columns = new SheetColumn?[SlotHeaders.Length];
        columns[CommentSlot] = new SheetColumn(CommentKey, "Comment") { Kind = CellKind.Text };
        if (d.Int1 is { } i1) columns[Int1Slot] = new SheetColumn(Int1Key, i1) { Kind = CellKind.Integer, ResetOnRetype = true };
        if (d.Int2 is { } i2) columns[Int2Slot] = new SheetColumn(Int2Key, i2) { Kind = CellKind.Integer, ResetOnRetype = true };
        if (d.Int3 is { } i3) columns[Int3Slot] = new SheetColumn(Int3Key, i3) { Kind = CellKind.Integer, ResetOnRetype = true };
        if (d.Code == "TWAV")
            columns[MinSlot] = new SheetColumn(MinKey, "Wavelength (µm)") { Default = CellValue.Of(0.6328), ToolTip = "The test wavelength of the fringe operands below, micrometers." };
        else if (d.Unit is { } unit)
        {
            columns[NominalSlot] = new SheetColumn(NominalKey, "Nominal") { Kind = CellKind.Text, IsReadOnly = true, ToolTip = "The nominal value, written by Update." };
            columns[MinSlot] = new SheetColumn(MinKey, "Min") { ResetOnRetype = true, ToolTip = $"The most negative change, {unit}." };
            columns[MaxSlot] = new SheetColumn(MaxKey, "Max") { ResetOnRetype = true, ToolTip = $"The most positive change, {unit}." };
        }
        if (!d.IsControl)
        {
            columns[IgnoreSlot] = new SheetColumn(IgnoreKey, "Ignore") { Kind = CellKind.Choice, Choices = YesNo, ToolTip = "Ignore this operand during tolerancing." };
            columns[DoNotAdjustSlot] = new SheetColumn(DoNotAdjustKey, "Do not adjust") { Kind = CellKind.Choice, Choices = YesNo, ToolTip = "Do not adjust during inverse tolerancing." };
        }
        string note = d.NotAppliedReason is { } why ? $" Defined but not applied in this version: {why}" : "";
        return new SheetRowType(TypeName(d), columns)
        {
            Aliases = new[] { d.Code, d.Name },
            Description = $"{d.Code}: {d.Description}{note}",
        };
    }
}
