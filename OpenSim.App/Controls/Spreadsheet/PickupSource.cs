namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// Where a pickup cell takes its value from: value = <see cref="Scale"/> × source +
/// <see cref="Offset"/> for numbers, the source's text for text cells.
/// <para>
/// The source row is named by its <see cref="SheetRow.Id"/>, not its position, so inserting,
/// deleting or moving other rows does not silently redirect the pickup. Evaluating the pickup
/// is the owning editor's job (OP-31 and the editors after it); the sheet stores and validates it.
/// </para>
/// </summary>
/// <param name="RowId">The source row's <see cref="SheetRow.Id"/>.</param>
/// <param name="Column">The source grid column (1 or more; column 0 is the row type).</param>
/// <param name="Scale">The factor applied to a numeric source.</param>
/// <param name="Offset">The amount added after scaling.</param>
public sealed record PickupSource(long RowId, int Column, double Scale = 1, double Offset = 0);
