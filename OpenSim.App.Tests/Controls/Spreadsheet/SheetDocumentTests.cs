using OpenSim.App.Controls.Spreadsheet;
using static OpenSim.App.Tests.Controls.Spreadsheet.TestSheets;

namespace OpenSim.App.Tests.Controls.Spreadsheet;

/// <summary>
/// The sheet model behind the optical editors' grid (OP-6): typed edits, flags, columns that
/// follow the row's type, row insert / delete / move with undo, and block paste.
/// </summary>
public sealed class SheetDocumentTests
{
    // ------------------------------------------------------------------ edits

    [Fact]
    public void ANewRow_TakesItsTypesDefaults()
    {
        var doc = Document(1);
        var row = doc.Rows[0];
        Assert.Equal(Standard, row.Type);
        Assert.True(double.IsPositiveInfinity(row.Cells[RadiusColumn - 1].Value.Number));
        Assert.Equal("Infinity", doc.GetText(0, RadiusColumn));
        Assert.Equal(0.0, row.Cells[ThicknessColumn - 1].Value.Number);
        Assert.Equal("", doc.GetText(0, CommentColumn));
        Assert.Equal("None", row.Properties["aperture"].Text);
        Assert.All(row.Cells, c => Assert.Equal(CellFlagKind.Fixed, c.Flag.Kind));
        Assert.Equal(0, row.Number);   // FirstRowNumber = 0: the object surface
    }

    [Fact]
    public void TypedText_IsParsedByTheColumnsKind_AndUndoes()
    {
        var doc = Document(2);

        Assert.True(doc.SetText(1, RadiusColumn, "-25.4").Succeeded);
        Assert.True(doc.SetText(1, GlassColumn, "N-BK7").Succeeded);
        Assert.True(doc.SetText(1, ThicknessColumn, "  3e-1 ").Succeeded);

        Assert.Equal(-25.4, doc.Rows[1].Cells[RadiusColumn - 1].Value.Number);
        Assert.Equal("N-BK7", doc.Rows[1].Cells[GlassColumn - 1].Value.Text);
        Assert.Equal(0.3, doc.Rows[1].Cells[ThicknessColumn - 1].Value.Number);
        Assert.Equal(3, doc.History.UndoCount);

        doc.Undo();
        Assert.Equal(0.0, doc.Rows[1].Cells[ThicknessColumn - 1].Value.Number);
        doc.Undo();
        doc.Undo();
        Assert.True(double.IsPositiveInfinity(doc.Rows[1].Cells[RadiusColumn - 1].Value.Number));
        doc.Redo();
        Assert.Equal(-25.4, doc.Rows[1].Cells[RadiusColumn - 1].Value.Number);
    }

    [Fact]
    public void ARejectedEdit_ChangesNothing_AndNamesRowAndColumn()
    {
        var doc = Document(3);

        var notANumber = doc.SetText(2, ThicknessColumn, "abc");
        var negative = doc.SetText(2, SemiColumn, "-1");
        var computed = doc.SetText(2, Par1Column, "1");   // Standard does not use Par 1

        Assert.False(notANumber.Succeeded);
        Assert.Equal("Row 2, Thickness: 'abc' is not a number.", notANumber.Message);
        Assert.False(negative.Succeeded);
        Assert.Contains("Semi-diameter", negative.Message);
        Assert.Contains("minimum", negative.Message);
        Assert.False(computed.Succeeded);
        Assert.Contains("does not use column 7", computed.Message);
        Assert.Equal(0, doc.History.UndoCount);
    }

    [Fact]
    public void AComputedColumn_RefusesTyping_ButTakesComputedValuesWithoutAnUndoStep()
    {
        var doc = Document(1);
        doc.SetRowType(0, Break);
        doc.History.Clear();
        int changes = 0;
        doc.Changed += (_, _) => changes++;

        Assert.False(doc.IsEditable(0, Par2Column));
        Assert.False(doc.SetText(0, Par2Column, "2").Succeeded);
        doc.SetComputed(0, Par2Column, CellValue.Of(0.125));

        Assert.Equal(0.125, doc.Rows[0].Cells[Par2Column - 1].Value.Number);
        Assert.Equal(0, doc.History.UndoCount);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void InfinityAndChoices_ParseWithoutRegardToCase()
    {
        var doc = Document(1);
        Assert.True(doc.SetText(0, RadiusColumn, "-inf").Succeeded);
        Assert.True(double.IsNegativeInfinity(doc.Rows[0].Cells[RadiusColumn - 1].Value.Number));
        Assert.False(doc.SetText(0, ThicknessColumn, "inf").Succeeded);   // thickness allows no infinity

        doc.SetRowType(0, Break);
        Assert.True(doc.SetText(0, Par1Column, "tilt, DECENTER").Succeeded);
        Assert.Equal("Tilt, decenter", doc.Rows[0].Cells[Par1Column - 1].Value.Text);
        Assert.False(doc.SetText(0, Par1Column, "sideways").Succeeded);
    }

    [Fact]
    public void EmptyText_RestoresTheDefault()
    {
        var doc = Document(1);
        doc.SetText(0, RadiusColumn, "10");
        doc.SetText(0, RadiusColumn, "");
        Assert.True(double.IsPositiveInfinity(doc.Rows[0].Cells[RadiusColumn - 1].Value.Number));
    }

    [Fact]
    public void CopyText_IsTheShortestRoundTrip_WhileTheCellShowsItsFormat()
    {
        var doc = Document(1);
        double third = 1.0 / 3.0;
        doc.SetValue(0, ThicknessColumn, CellValue.Of(third));

        Assert.Equal("0.33333333", doc.Rows[0].Cells[ThicknessColumn - 1].Display);   // G8
        Assert.Equal(third, double.Parse(doc.GetText(0, ThicknessColumn), System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ flags

    [Fact]
    public void Flags_AreCheckedAgainstTheColumn_AndUndo()
    {
        var doc = Document(2);

        Assert.True(doc.SetFlag(1, RadiusColumn, CellFlag.Variable).Succeeded);
        Assert.True(doc.SetFlag(1, ThicknessColumn, CellFlag.Solve("height", 2.5)).Succeeded);
        var noSolve = doc.SetFlag(1, SemiColumn, CellFlag.Solve("height"));
        var unknown = doc.SetFlag(1, RadiusColumn, CellFlag.Solve("height"));   // radius offers only focus
        var tooMany = doc.SetFlag(1, ThicknessColumn, CellFlag.Solve("height", 1, 2, 3));

        Assert.Equal(CellFlagKind.Variable, doc.Rows[1].Cells[RadiusColumn - 1].Flag.Kind);
        Assert.Equal("V", doc.Rows[1].Cells[RadiusColumn - 1].Badge);
        var solve = doc.Rows[1].Cells[ThicknessColumn - 1].Flag;
        Assert.Equal("height", solve.SolveCode);
        Assert.Equal(new[] { 2.5, 0.0 }, solve.SolveParameters);   // padded to the code's two parameters
        Assert.Equal("H", doc.Rows[1].Cells[ThicknessColumn - 1].Badge);
        Assert.False(noSolve.Succeeded);
        Assert.False(unknown.Succeeded);
        Assert.False(tooMany.Succeeded);

        doc.Undo();
        Assert.Equal(CellFlagKind.Fixed, doc.Rows[1].Cells[ThicknessColumn - 1].Flag.Kind);
        doc.Undo();
        Assert.Equal(CellFlagKind.Fixed, doc.Rows[1].Cells[RadiusColumn - 1].Flag.Kind);
    }

    [Fact]
    public void TypingIntoASolvedCell_MakesItFixed_ButAVariableStaysVariable()
    {
        var doc = Document(1);
        doc.SetFlag(0, ThicknessColumn, CellFlag.Solve("focus"));
        doc.SetFlag(0, RadiusColumn, CellFlag.Variable);

        doc.SetText(0, ThicknessColumn, "5");
        doc.SetText(0, RadiusColumn, "50");

        Assert.Equal(CellFlagKind.Fixed, doc.Rows[0].Cells[ThicknessColumn - 1].Flag.Kind);
        Assert.Equal(CellFlagKind.Variable, doc.Rows[0].Cells[RadiusColumn - 1].Flag.Kind);
    }

    [Fact]
    public void ToggleVariable_SetsAllUnlessAllAreAlready_AndSkipsCellsThatCannotVary()
    {
        var doc = Document(2);
        doc.SetFlag(0, RadiusColumn, CellFlag.Variable);
        var cells = new[]
        {
            new SheetCellAddress(0, RadiusColumn), new SheetCellAddress(1, RadiusColumn),
            new SheetCellAddress(1, GlassColumn), new SheetCellAddress(1, 0),
        };

        var on = doc.ToggleVariable(cells);
        Assert.Equal(1, on.CellsChanged);
        Assert.Equal(2, on.CellsSkipped);
        Assert.Equal(CellFlagKind.Variable, doc.Rows[1].Cells[RadiusColumn - 1].Flag.Kind);

        var off = doc.ToggleVariable(cells);
        Assert.Equal(2, off.CellsChanged);
        Assert.Equal(CellFlagKind.Fixed, doc.Rows[0].Cells[RadiusColumn - 1].Flag.Kind);

        doc.Undo();   // one step for the whole toggle
        Assert.Equal(CellFlagKind.Variable, doc.Rows[0].Cells[RadiusColumn - 1].Flag.Kind);
        Assert.Equal(CellFlagKind.Variable, doc.Rows[1].Cells[RadiusColumn - 1].Flag.Kind);
    }

    [Fact]
    public void APickup_FollowsItsRowThroughMoves_AndRefusesCyclesAndKindMismatches()
    {
        var doc = Document(4);
        var source = doc.Rows[1];

        Assert.True(doc.SetFlag(3, ConicColumn, CellFlag.PickupFrom(source, ConicColumn, -1, 0)).Succeeded);
        Assert.True(doc.SetFlag(3, GlassColumn, CellFlag.PickupFrom(source, GlassColumn)).Succeeded);
        Assert.False(doc.SetFlag(1, ConicColumn, CellFlag.PickupFrom(doc.Rows[3], ConicColumn)).Succeeded);   // a cycle
        Assert.False(doc.SetFlag(3, GlassColumn, CellFlag.PickupFrom(source, ConicColumn)).Succeeded);         // text from a number
        Assert.False(doc.SetFlag(3, ConicColumn, CellFlag.PickupFrom(doc.Rows[3], ConicColumn)).Succeeded);   // itself

        doc.MoveRows(new[] { 1 }, 1);   // the source is now row 2
        var pickup = doc.Rows[3].Cells[ConicColumn - 1];
        Assert.Equal(source.Id, pickup.Flag.Pickup!.RowId);
        Assert.Contains("row 2", pickup.ToolTip);
    }

    [Fact]
    public void DeletingAPickupsSource_MakesItFixed_InTheSameUndoStep()
    {
        var doc = Document(3);
        doc.SetText(2, ConicColumn, "-1");
        doc.SetFlag(2, ConicColumn, CellFlag.PickupFrom(doc.Rows[0], ConicColumn));
        doc.History.Clear();

        Assert.True(doc.DeleteRows(new[] { 0 }).Succeeded);
        var cell = doc.Rows[1].Cells[ConicColumn - 1];
        Assert.Equal(CellFlagKind.Fixed, cell.Flag.Kind);
        Assert.Equal(-1.0, cell.Value.Number);
        Assert.Equal(1, doc.History.UndoCount);

        doc.Undo();
        Assert.Equal(3, doc.RowCount);
        Assert.Equal(CellFlagKind.Pickup, doc.Rows[2].Cells[ConicColumn - 1].Flag.Kind);
    }

    // ------------------------------------------------------------------ type-dependent columns

    [Fact]
    public void ColumnsFollowTheRowsType()
    {
        var schema = Schema();
        Assert.Equal("Radius", schema.HeaderOf(RadiusColumn, Standard));
        Assert.Equal("Decenter X", schema.HeaderOf(RadiusColumn, Break));
        Assert.Equal("Par 1", schema.HeaderOf(Par1Column, Standard));
        Assert.Equal("4th order", schema.HeaderOf(Par1Column, Asphere));
        Assert.Equal("Surface type", schema.HeaderOf(0, Break));

        var doc = Document(1);
        Assert.False(doc.Rows[0].Cells[Par1Column - 1].IsUsed);
        doc.SetRowType(0, Asphere);
        Assert.True(doc.Rows[0].Cells[Par1Column - 1].IsUsed);
        Assert.Equal(1.0, doc.Rows[0].Cells[Par2Column - 1].Value.Number);   // the integer column's default
    }

    [Fact]
    public void ChangingType_KeepsValuesWithTheSameKeyAndKind_AndDropsTheRest()
    {
        var doc = Document(1);
        doc.SetText(0, CommentColumn, "front");
        doc.SetText(0, RadiusColumn, "40");
        doc.SetFlag(0, RadiusColumn, CellFlag.Variable);
        doc.SetText(0, ThicknessColumn, "4");
        doc.SetFlag(0, ThicknessColumn, CellFlag.Solve("height", 1, 0));
        doc.SetProperty(0, "aperture", "circular");
        doc.SetProperty(0, "apertureRadius", "6");

        Assert.True(doc.SetText(0, 0, "coordinate BREAK").Succeeded);
        var row = doc.Rows[0];
        Assert.Equal(Break, row.Type);
        Assert.Equal("front", row.Cells[CommentColumn - 1].Value.Text);
        Assert.Equal(0.0, row.Cells[RadiusColumn - 1].Value.Number);           // the slot is now Decenter X
        Assert.Equal(CellFlagKind.Fixed, row.Cells[RadiusColumn - 1].Flag.Kind);
        Assert.Equal(4.0, row.Cells[ThicknessColumn - 1].Value.Number);        // same key: kept with its solve
        Assert.Equal("height", row.Cells[ThicknessColumn - 1].Flag.SolveCode);
        Assert.False(row.Cells[GlassColumn - 1].IsUsed);
        Assert.Empty(row.Properties);

        doc.Undo();
        row = doc.Rows[0];
        Assert.Equal(Standard, row.Type);
        Assert.Equal(40.0, row.Cells[RadiusColumn - 1].Value.Number);
        Assert.Equal(CellFlagKind.Variable, row.Cells[RadiusColumn - 1].Flag.Kind);
        Assert.Equal("Circular", row.Properties["aperture"].Text);
        Assert.Equal(6.0, row.Properties["apertureRadius"].Number);
    }

    [Fact]
    public void AnUnknownTypeName_IsRefusedWithTheList()
    {
        var doc = Document(1);
        var result = doc.SetText(0, 0, "Paraxial");
        Assert.False(result.Succeeded);
        Assert.Contains("Standard, Even asphere, Coordinate break", result.Message);
    }

    [Fact]
    public void ASchemaMistake_FailsWhenTheSchemaIsBuilt()
    {
        var tooWide = new SheetRowType("Wide", Enumerable.Repeat<SheetColumn?>(null, 9).ToArray());
        Assert.Throws<ArgumentException>(() => new SheetSchema("T", new[] { "a", "b" }, new[] { tooWide }));
        var noChoices = new SheetRowType("Empty choice", new[] { new SheetColumn("c", "C") { Kind = CellKind.Choice } });
        Assert.Throws<ArgumentException>(() => new SheetSchema("T", new[] { "a" }, new[] { noChoices }));
        var variableText = new SheetRowType("Variable text", new[] { new SheetColumn("t", "T") { Kind = CellKind.Text, Flags = CellFlagSupport.Variable } });
        Assert.Throws<ArgumentException>(() => new SheetSchema("T", new[] { "a" }, new[] { variableText }));
        Assert.Throws<ArgumentException>(() => new SheetSchema("T", new[] { "a" }, new[] { Standard, new SheetRowType("STANDARD", Array.Empty<SheetColumn?>()) }));
    }

    // ------------------------------------------------------------------ rows

    [Fact]
    public void InsertAndDelete_UndoAndRedo_ExactlyAndRenumber()
    {
        var doc = Document(3);
        var ids = doc.Rows.Select(r => r.Id).ToArray();
        doc.SetText(1, ThicknessColumn, "7");

        Assert.True(doc.InsertRows(1, 2, Asphere).Succeeded);
        Assert.Equal(5, doc.RowCount);
        Assert.Equal(Asphere, doc.Rows[1].Type);
        Assert.Equal(2, doc.Rows[1].Tint);   // the type's default tint
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, doc.Rows.Select(r => r.Number));
        Assert.Equal(7.0, doc.Rows[3].Cells[ThicknessColumn - 1].Value.Number);

        Assert.True(doc.DeleteRows(new[] { 0, 3 }).Succeeded);
        Assert.Equal(3, doc.RowCount);
        Assert.Equal(new[] { 0, 1, 2 }, doc.Rows.Select(r => r.Index));

        doc.Undo();
        Assert.Equal(5, doc.RowCount);
        Assert.Equal(7.0, doc.Rows[3].Cells[ThicknessColumn - 1].Value.Number);
        doc.Undo();
        Assert.Equal(ids, doc.Rows.Select(r => r.Id));
        doc.Redo();
        doc.Redo();
        Assert.Equal(3, doc.RowCount);
        Assert.Equal(new[] { Asphere, Asphere, Standard }, doc.Rows.Select(r => r.Type));
        Assert.Equal(ids[2], doc.Rows[2].Id);
    }

    [Fact]
    public void Move_KeepsTheSelectionsOrder_AndUndoes()
    {
        var doc = Document(6);
        var ids = doc.Rows.Select(r => r.Id).ToArray();

        Assert.True(doc.MoveRows(new[] { 3, 4 }, -2).Succeeded);
        Assert.Equal(new[] { ids[0], ids[3], ids[4], ids[1], ids[2], ids[5] }, doc.Rows.Select(r => r.Id));

        Assert.True(doc.MoveRows(new[] { 0, 2 }, 1).Succeeded);   // a split selection moves as two
        Assert.Equal(new[] { ids[3], ids[0], ids[1], ids[4], ids[2], ids[5] }, doc.Rows.Select(r => r.Id));

        Assert.False(doc.MoveRows(new[] { 0 }, -1).Succeeded);
        Assert.False(doc.MoveRows(new[] { 5 }, 1).Succeeded);

        doc.Undo();
        doc.Undo();
        Assert.Equal(ids, doc.Rows.Select(r => r.Id));
    }

    [Fact]
    public void LockedRows_CannotBeDeletedOrMovedPast_AndTheGuardCanRefuseInserts()
    {
        var doc = Document(4);
        doc.Rows[0].IsLocked = true;
        doc.Rows[3].IsLocked = true;
        doc.InsertGuard = index => index == 0 ? "Nothing goes before the object." : null;

        Assert.False(doc.DeleteRows(new[] { 0, 1 }).Succeeded);
        Assert.Equal(4, doc.RowCount);
        Assert.False(doc.MoveRows(new[] { 1 }, -1).Succeeded);
        Assert.False(doc.MoveRows(new[] { 2 }, 1).Succeeded);
        Assert.True(doc.MoveRows(new[] { 2 }, -1).Succeeded);
        Assert.Equal("Nothing goes before the object.", doc.InsertRows(0).Message);
        Assert.True(doc.InsertRows(1).Succeeded);
    }

    [Fact]
    public void Tint_AndProperties_AreUndoable()
    {
        var doc = Document(1);
        doc.SetTint(0, 4);
        Assert.False(doc.SetProperty(0, "apertureRadius", "-2").Succeeded);
        Assert.True(doc.SetProperty(0, "apertureRadius", "2.5").Succeeded);

        Assert.Equal(4, doc.Rows[0].Tint);
        Assert.Equal(2.5, doc.Rows[0].ValueOf("apertureRadius")!.Value.Number);
        doc.Undo();
        doc.Undo();
        Assert.Null(doc.Rows[0].Tint);
        Assert.Equal(0.0, doc.Rows[0].Properties["apertureRadius"].Number);
    }

    [Fact]
    public void ClearCells_ResetsToDefaultsInOneStep()
    {
        var doc = Document(2);
        doc.SetText(0, RadiusColumn, "12");
        doc.SetFlag(0, RadiusColumn, CellFlag.Variable);
        doc.SetText(1, GlassColumn, "F2");
        doc.History.Clear();

        var result = doc.ClearCells(new[] { new SheetCellAddress(0, RadiusColumn), new SheetCellAddress(1, GlassColumn), new SheetCellAddress(1, 0) });

        Assert.Equal(2, result.CellsChanged);
        Assert.True(double.IsPositiveInfinity(doc.Rows[0].Cells[RadiusColumn - 1].Value.Number));
        Assert.Equal(CellFlagKind.Fixed, doc.Rows[0].Cells[RadiusColumn - 1].Flag.Kind);
        Assert.Equal(1, doc.History.UndoCount);
        doc.Undo();
        Assert.Equal("F2", doc.Rows[1].Cells[GlassColumn - 1].Value.Text);
    }

    // ------------------------------------------------------------------ paste

    [Fact]
    public void APastedBlock_FillsCellsFromTheTopLeft_InOneUndoStep()
    {
        var doc = Document(3);
        string excel = "10\t2.5\tN-BK7\r\n-10\t5\t\r\n";   // a blank cell: left alone

        doc.SetText(2, GlassColumn, "keep");
        doc.History.Clear();
        var result = doc.PasteText(excel, SheetSelection.Single(1, RadiusColumn));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(5, result.CellsChanged);
        Assert.Equal(10.0, doc.Rows[1].Cells[RadiusColumn - 1].Value.Number);
        Assert.Equal(2.5, doc.Rows[1].Cells[ThicknessColumn - 1].Value.Number);
        Assert.Equal("N-BK7", doc.Rows[1].Cells[GlassColumn - 1].Value.Text);
        Assert.Equal(-10.0, doc.Rows[2].Cells[RadiusColumn - 1].Value.Number);
        Assert.Equal("keep", doc.Rows[2].Cells[GlassColumn - 1].Value.Text);
        Assert.Equal(SheetSelection.Block(1, RadiusColumn, 2, 3).Cells, result.Affected!.Cells);

        doc.Undo();
        Assert.Equal(0, doc.History.UndoCount);
        Assert.True(double.IsPositiveInfinity(doc.Rows[1].Cells[RadiusColumn - 1].Value.Number));
        Assert.Equal(0.0, doc.Rows[2].Cells[ThicknessColumn - 1].Value.Number);
    }

    [Fact]
    public void APasteWithABadCell_ChangesNothing_AndNamesTheCell()
    {
        var doc = Document(2);
        var result = doc.PasteText("1\t2\r\n3\tx\r\n4\t5", SheetSelection.Single(0, RadiusColumn));

        Assert.False(result.Succeeded);
        Assert.Equal("Row 1, Thickness: 'x' is not a number.", result.Message);
        Assert.Equal(2, doc.RowCount);   // the row the third record would have added is gone too
        Assert.True(double.IsPositiveInfinity(doc.Rows[0].Cells[RadiusColumn - 1].Value.Number));
        Assert.Equal(0, doc.History.UndoCount);
    }

    [Fact]
    public void APastePastTheEnd_AddsRows_AndDropsColumnsPastTheLast()
    {
        var doc = Document(1);
        var result = doc.PasteText("1\t2\t3\t4\r\n5\t6\t7\t8\r\n9\t10\t11\t12", SheetSelection.Single(0, ConicColumn));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RowsAdded);
        Assert.Equal(3, doc.RowCount);
        // Per row: the fourth text has no grid column, and Standard uses neither parameter slot.
        Assert.Equal(9, result.CellsSkipped);
        Assert.Equal(new[] { 1.0, 5.0, 9.0 }, doc.Rows.Select(r => r.Cells[ConicColumn - 1].Value.Number));
        Assert.Contains("skipped", result.Message);
        doc.Undo();
        Assert.Equal(1, doc.RowCount);
    }

    [Fact]
    public void APastedTypeColumn_RetypesTheRowBeforeItsCellsAreRead()
    {
        var doc = Document(2);
        var result = doc.PasteText("Coordinate break\tlens\t0.5\t2\r\nEven asphere\t\t30", SheetSelection.Single(0, 0));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Break, doc.Rows[0].Type);
        Assert.Equal(0.5, doc.Rows[0].Cells[RadiusColumn - 1].Value.Number);   // Decenter X
        Assert.Equal(Asphere, doc.Rows[1].Type);
        Assert.Equal(30.0, doc.Rows[1].Cells[RadiusColumn - 1].Value.Number);
    }

    [Fact]
    public void ABlockRepeats_ToFillASelectionThatIsAWholeMultipleOfIt()
    {
        var doc = Document(4);
        doc.PasteText("1.5", SheetSelection.Block(0, ThicknessColumn, 4, 1));
        Assert.All(doc.Rows, r => Assert.Equal(1.5, r.Cells[ThicknessColumn - 1].Value.Number));

        doc.PasteText("1\r\n2", SheetSelection.Block(0, SemiColumn, 3, 1));   // 3 is not a multiple of 2: pasted once
        Assert.Equal(new[] { 1.0, 2.0, 0.0, 0.0 }, doc.Rows.Select(r => r.Cells[SemiColumn - 1].Value.Number));
    }

    [Fact]
    public void CopyAndPasteWithinTheSheet_CarriesFlags_ButTextFromElsewhereDoesNot()
    {
        var doc = Document(4);
        doc.SetText(0, RadiusColumn, "20");
        doc.SetFlag(0, RadiusColumn, CellFlag.Variable);
        doc.SetFlag(0, ThicknessColumn, CellFlag.Solve("focus"));

        var block = doc.Copy(SheetSelection.Block(0, RadiusColumn, 1, 2));
        Assert.Equal("20\t0", block.ToText());

        doc.Paste(block, SheetSelection.Single(2, RadiusColumn));
        Assert.Equal(CellFlagKind.Variable, doc.Rows[2].Cells[RadiusColumn - 1].Flag.Kind);
        Assert.Equal("focus", doc.Rows[2].Cells[ThicknessColumn - 1].Flag.SolveCode);

        doc.PasteText(block.ToText(), SheetSelection.Single(3, RadiusColumn));
        Assert.Equal(20.0, doc.Rows[3].Cells[RadiusColumn - 1].Value.Number);
        Assert.Equal(CellFlagKind.Fixed, doc.Rows[3].Cells[RadiusColumn - 1].Flag.Kind);
    }

    [Fact]
    public void AFlagPastedWhereItIsNotAccepted_BecomesFixed()
    {
        var doc = Document(2);
        doc.SetFlag(0, ThicknessColumn, CellFlag.Solve("height", 3));
        var block = doc.Copy(SheetSelection.Single(0, ThicknessColumn));

        var result = doc.Paste(block, SheetSelection.Single(1, RadiusColumn));   // radius has no height solve

        Assert.True(result.Succeeded);
        Assert.Equal(CellFlagKind.Fixed, doc.Rows[1].Cells[RadiusColumn - 1].Flag.Kind);
        Assert.Contains("set to fixed", result.Message);
    }

    [Fact]
    public void ANonRectangularCopy_LeavesTheUnselectedCellsBlank_SoAPasteSkipsThem()
    {
        var doc = Document(2);
        doc.SetText(0, RadiusColumn, "1");
        doc.SetText(1, ThicknessColumn, "2");
        var diagonal = new SheetSelection(new[] { new SheetCellAddress(0, RadiusColumn), new SheetCellAddress(1, ThicknessColumn) });

        Assert.Equal("1\t\r\n\t2", doc.Copy(diagonal).ToText());
    }

    [Fact]
    public void Changed_IsRaisedForEditsUndoAndRedo_AndStructureChangingBeforeRowChanges()
    {
        var doc = Document(2);
        var events = new List<string>();
        doc.Changed += (_, _) => events.Add("changed");
        doc.StructureChanging += (_, _) => events.Add("structure");

        doc.SetText(0, ThicknessColumn, "1");
        doc.InsertRows(0);
        doc.Undo();

        Assert.Equal(new[] { "changed", "structure", "changed", "structure", "changed" }, events);
    }
}
