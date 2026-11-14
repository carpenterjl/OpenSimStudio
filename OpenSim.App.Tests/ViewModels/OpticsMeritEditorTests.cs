using System.Globalization;
using System.Text.Json;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using S = OpenSim.App.ViewModels.OpticsMeritEditorSchema;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-54: the Merit Function Editor as the Optics workspace edits it. The values it
/// shows are checked against the library's evaluation of the same rows on the same lens
/// (the library's own tests check those against hand-computed values). Radii, thicknesses and
/// indices are arbitrary inputs.</summary>
public sealed class OpticsMeritEditorTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>A singlet in air (mm): n = 1.5, front radius 40, back −120.</summary>
    private static List<OpticsSurfaceSettings> Singlet() => new()
    {
        new() { ThicknessIsInfinite = true },
        new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true },
        new() { Radius = -120, Thickness = 90 },
        new(),
    };

    private OpticsMeritEditorViewModel WithSinglet()
    {
        var notes = new List<string>();
        Optics.LensEditor.LoadSettings(Singlet(), notes);
        Assert.Empty(notes);
        return Optics.MeritEditor;
    }

    private static void Type(SheetDocument doc, int row, int slot, string text)
    {
        var result = doc.SetText(row, slot + 1, text);
        Assert.True(result.Succeeded, result.Message);
    }

    /// <summary>Three rows typed as a user would: by code, by lower-case code and by name.</summary>
    private static void TypeThreeRows(OpticsMeritEditorViewModel m)
    {
        var doc = m.Document;
        m.InsertOperandCommand.Execute(null);
        Type(doc, 0, -1, "EFFL");
        Type(doc, 0, S.Int2Slot, "1");
        Type(doc, 0, S.TargetSlot, "60");
        m.CurrentRow = 0;
        m.InsertOperandAfterCommand.Execute(null);
        Type(doc, 1, -1, "ctva");
        Type(doc, 1, S.Int1Slot, "1");
        Type(doc, 1, S.TargetSlot, "5");
        Type(doc, 1, S.WeightSlot, "2");
        m.CurrentRow = 1;
        m.InsertOperandAfterCommand.Execute(null);
        Type(doc, 2, -1, "Real ray y");
        Type(doc, 2, S.Int1Slot, "3");
        Type(doc, 2, S.Int2Slot, "1");
        Type(doc, 2, S.Data1Slot + 3, "50");      // Py = 50: the ray misses the front surface
    }

    [Fact]
    public void TypedRows_ResolveByCodeOrName_AndUpdateShowsTheLibrarysValues()
    {
        var m = WithSinglet();
        TypeThreeRows(m);
        var doc = m.Document;
        Assert.Equal(new[] { "EFFL", "CTVA", "REAY" }, doc.Rows.Select(r => m.DefinitionOf(r).Code));
        Assert.Equal("Wave", doc.Schema.HeaderOf(S.Int2Slot + 1, doc.Rows[0].Type));
        Assert.Equal("Py", doc.Schema.HeaderOf(S.Data1Slot + 4, doc.Rows[2].Type));
        Assert.Equal(S.DefaultWeight, doc.Rows[0].ValueOf(S.WeightKey)!.Value.Number);

        m.UpdateCommand.Execute(null);

        var expected = MeritEvaluator.Evaluate(m.ToFunction(),
            SingleMeritConfiguration.FromProject(Optics.ToSettings(), Array.Empty<OpticalCatalog>()));
        Assert.Equal(expected.Value, m.LastEvaluation!.Value);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(expected.Rows[i].Value.ToString("G10", CultureInfo.InvariantCulture), doc.Rows[i].Cells[S.ValueSlot].Value.Text);
            Assert.Equal(expected.Rows[i].Contribution.ToString("F3", CultureInfo.InvariantCulture), doc.Rows[i].Cells[S.ContributionSlot].Value.Text);
        }
        Assert.Equal("failed", doc.Rows[2].Cells[S.ValueSlot].Value.Text);
        Assert.StartsWith("Row 3 (REAY):", doc.Rows[2].Cells[S.ValueSlot].Note);
        Assert.True(doc.Rows[2].Cells[S.ValueSlot].NoteIsError);
        Assert.Contains("1 row failed", m.MeritValueText);
        Assert.Single(m.Problems);
    }

    [Fact]
    public void AnEdit_ClearsTheValues_PublishesTheRows_AndUndoes()
    {
        var m = WithSinglet();
        TypeThreeRows(m);
        m.UpdateCommand.Execute(null);
        Assert.NotNull(m.LastEvaluation);

        Type(m.Document, 1, S.TargetSlot, "4");
        Assert.Null(m.LastEvaluation);
        Assert.Equal("", m.Document.Rows[1].Cells[S.ValueSlot].Value.Text);
        Assert.Equal("Not evaluated: press Update.", m.MeritValueText);
        var published = _app.Resolve<ProjectSession>().OpticsSystem!.MeritFunction!;
        Assert.Equal(3, published.Operands!.Count);
        Assert.Equal(4, published.Operands[1].Target);

        m.UndoCommand.Execute(null);
        Assert.Equal(5, m.ToFunction().Rows[1].Target);
        m.CurrentRow = 0;
        m.DeleteOperandCommand.Execute(null);
        Assert.Equal(2, m.OperandCount);
    }

    [Fact]
    public void TheMeritFunction_SavesWithTheProject_AndReloads()
    {
        var m = WithSinglet();
        TypeThreeRows(m);
        Type(m.Document, 0, S.CommentSlot, "focal length");
        var saved = Optics.ToSettings();
        var json = JsonSerializer.Serialize(saved.MeritFunction);

        using var other = new AppComposition();
        var optics = other.Resolve<OpticsViewModel>();
        Assert.Null(optics.ToSettings().MeritFunction);                       // a new project has none
        Assert.Empty(optics.LoadSettings(saved));
        Assert.Equal(m.ToFunction().Rows, optics.MeritEditor.ToFunction().Rows);
        Assert.Equal(json, JsonSerializer.Serialize(optics.ToSettings().MeritFunction));
        Assert.Equal(0, optics.MeritEditor.Document.History.UndoCount);

        var notes = optics.LoadSettings(saved with
        {
            MeritFunction = new OpticsMeritFunctionSettings { Operands = new() { new() { Type = "QQQQ" } } }
        });
        Assert.Contains(notes, n => n.Contains("'QQQQ'"));
        Assert.Equal("OOFF", optics.MeritEditor.ToFunction().Rows.Single().Code);

        // A file from before OP-54 opens with an empty merit function, not the previous project's.
        optics.LoadSettings(saved with { MeritFunction = null });
        Assert.Equal(0, optics.MeritEditor.OperandCount);
    }

    [Fact]
    public void AMalformedMeritFunction_IsNamed_NotEvaluated()
    {
        var m = WithSinglet();
        m.InsertOperandCommand.Execute(null);
        Type(m.Document, 0, -1, "GOTO");
        Type(m.Document, 0, S.Int1Slot, "1");
        m.UpdateCommand.Execute(null);
        Assert.Null(m.LastEvaluation);
        Assert.Contains("Row 1 (GOTO): Op# 1 must be a later row", m.ProblemsText);
        Assert.StartsWith("Not evaluated", m.MeritValueText);
    }

    [Fact]
    public void FindingAnOperand_SearchesCodesAndNames()
    {
        var m = Optics.MeritEditor;
        m.FindText = "total";
        Assert.StartsWith("TOTR", Assert.Single(m.FindResults));
        m.FindText = "";
        Assert.Empty(m.FindResults);
    }
}
