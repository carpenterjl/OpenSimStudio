using System.Text.Json;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Tolerancing;
using S = OpenSim.App.ViewModels.OpticsToleranceEditorSchema;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-63: the Tolerance Data Editor as the Optics workspace edits it: rows typed by code
/// or name, saved with the project, validated by Update, scaled by Loosen and Tighten. The lens
/// data are arbitrary inputs.</summary>
public sealed class OpticsToleranceEditorTests : IDisposable
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

    private OpticsToleranceEditorViewModel WithSinglet()
    {
        var notes = new List<string>();
        Optics.LensEditor.LoadSettings(Singlet(), notes);
        Assert.Empty(notes);
        return Optics.ToleranceEditor;
    }

    private static void Type(SheetDocument doc, int row, int slot, string text)
    {
        var result = doc.SetText(row, slot + 1, text);
        Assert.True(result.Succeeded, result.Message);
    }

    /// <summary>TWAV, TRAD 1 ±0.1 and TETX 1 2 ±0.05, typed as a user would: by code, by name.</summary>
    private static void TypeRows(OpticsToleranceEditorViewModel t)
    {
        var doc = t.Document;
        t.InsertOperandCommand.Execute(null);
        Type(doc, 0, -1, "TWAV");
        t.CurrentRow = 0;
        t.InsertOperandAfterCommand.Execute(null);
        Type(doc, 1, -1, "trad");
        Type(doc, 1, S.Int1Slot, "1");
        Type(doc, 1, S.MinSlot, "-0.1");
        Type(doc, 1, S.MaxSlot, "0.1");
        t.CurrentRow = 1;
        t.InsertOperandAfterCommand.Execute(null);
        Type(doc, 2, -1, "Tolerance on element tilt about x");
        Type(doc, 2, S.Int1Slot, "1");
        Type(doc, 2, S.Int2Slot, "2");
        Type(doc, 2, S.MinSlot, "-0.05");
        Type(doc, 2, S.MaxSlot, "0.05");
    }

    [Fact]
    public void TypedRows_BecomeTheTable_AndRoundTripThroughTheSettings()
    {
        var t = WithSinglet();
        TypeRows(t);
        var table = t.ToTable();
        Assert.Equal(new[] { "TWAV", "TRAD", "TETX" }, table.Rows.Select(r => r.Code));
        Assert.Equal(0.6328, table.Rows[0].Min);                 // the column's default test wavelength
        Assert.Equal((1, -0.1, 0.1), (table.Rows[1].Int1, table.Rows[1].Min, table.Rows[1].Max));
        Assert.Equal((1, 2), (table.Rows[2].Int1, table.Rows[2].Int2));

        t.SelectedIrregularityModel = IrregularityModelOption.All.Single(o => o.Model == IrregularityModel.OpticStudio);
        var saved = Optics.ToSettings();
        Assert.Equal(3, saved.Tolerances!.Operands!.Count);
        Assert.Equal("OpticStudio", saved.Tolerances.IrregularityModel);

        Optics.Reset();
        Assert.Equal(0, Optics.ToleranceEditor.OperandCount);
        Assert.Null(Optics.ToSettings().Tolerances);
        var notes = Optics.LoadSettings(saved);
        Assert.Empty(notes);
        Assert.Equal(JsonSerializer.Serialize(saved.Tolerances), JsonSerializer.Serialize(Optics.ToSettings().Tolerances));
        Assert.Equal(IrregularityModel.OpticStudio, Optics.ToleranceEditor.SelectedIrregularityModel.Model);
    }

    [Fact]
    public void Update_FillsTheNominalColumn_AndNamesTheProblems()
    {
        var t = WithSinglet();
        TypeRows(t);
        t.UpdateCommand.Execute(null);
        Assert.Empty(t.Problems);
        Assert.Equal("40", t.Document.Rows[1].ValueOf(S.NominalKey)!.Value.Text);

        // A surface the lens does not have, and Min above Max.
        Type(t.Document, 1, S.Int1Slot, "7");
        Type(t.Document, 2, S.MinSlot, "0.5");
        t.UpdateCommand.Execute(null);
        Assert.Contains(t.Problems, p => p.StartsWith("Tolerance row 2") && p.Contains("Surf"));
        Assert.Contains(t.Problems, p => p.StartsWith("Tolerance row 3") && p.Contains("greater than Max"));
    }

    [Fact]
    public void LoosenAndTighten_ScaleTheLimits_AsOneUndoStep()
    {
        var t = WithSinglet();
        TypeRows(t);
        t.LoosenCommand.Execute(null);
        var table = t.ToTable();
        Assert.Equal((-0.2, 0.2), (table.Rows[1].Min, table.Rows[1].Max));
        Assert.Equal((-0.1, 0.1), (table.Rows[2].Min, table.Rows[2].Max));
        Assert.Equal(0.6328, table.Rows[0].Min);                 // a control row is not scaled
        t.UndoCommand.Execute(null);
        Assert.Equal((-0.1, 0.1), (t.ToTable().Rows[1].Min, t.ToTable().Rows[1].Max));
        t.TightenCommand.Execute(null);
        Assert.Equal(-0.025, t.ToTable().Rows[2].Min);
    }

    [Fact]
    public void AnUnknownCode_LoadsAsToff_AndIsNamed()
    {
        var t = WithSinglet();
        var notes = new List<string>();
        t.LoadSettings(new OpticsToleranceSettings { Operands = new() { new() { Type = "QQQQ", Int1 = 3 } } }, notes);
        Assert.Contains(notes, n => n.Contains("QQQQ"));
        Assert.Equal(ToleranceOperandRegistry.Off, t.ToTable().Rows[0].Code);
    }
}
