// OP-88
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using S = OpenSim.App.ViewModels.OpticsNonSequentialEditorSchema;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-88 in the app: Variable flags on object cells in the Non-Sequential Component
/// Editor (shown, saved, refused on a flag parameter), the optimizer's object values written back
/// as one undo step, and the non-sequential wizards writing rows into the Merit Function Editor.
/// Positions and sizes are arbitrary test inputs.</summary>
public sealed class OpticsNscOptimizationTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private OpticsNonSequentialEditorViewModel Loaded(params OpticsNscObjectSettings[] rows)
    {
        var notes = Optics.LoadSettings(Optics.ToSettings() with { NonSequential = new OpticsNonSequentialSettings { Objects = rows.ToList() } });
        Assert.Empty(notes);
        return Optics.NonSequentialEditor;
    }

    [Fact]
    public void VariableFlags_AreShownSavedAndRefusedOnAFlagParameter()
    {
        var flags = new List<OpticsCellFlagSettings> { new() { Column = "Z", Kind = "Variable" }, new() { Column = "Par1", Kind = "Variable" } };
        var editor = Loaded(new OpticsNscObjectSettings { Type = "Sphere", Parameters = new() { 2, 1 }, Z = 5, Flags = flags });
        var row = editor.Document.Rows[0];
        Assert.Equal(CellFlagKind.Variable, row.Cells[S.ZSlot].Flag.Kind);
        Assert.Equal(CellFlagKind.Variable, row.Cells[S.FirstParameterSlot].Flag.Kind);
        Assert.Equal(CellFlagKind.Fixed, row.Cells[S.XSlot].Flag.Kind);
        Assert.Equal(new[] { "Z", "Par1" }, editor.Rows()[0].Flags!.Select(f => f.Column));   // written back as loaded

        Assert.True(editor.Document.SetFlag(0, S.XSlot + 1, CellFlag.Variable).Succeeded);
        Assert.Equal(new[] { "X", "Z", "Par1" }, editor.Rows()[0].Flags!.Select(f => f.Column));
        Assert.False(editor.Document.SetFlag(0, S.FirstParameterSlot + 2, CellFlag.Variable).Succeeded);   // "Is Volume?" is a flag
        Assert.True(editor.Document.Undo());
        Assert.Equal(new[] { "Z", "Par1" }, editor.Rows()[0].Flags!.Select(f => f.Column));
    }

    [Fact]
    public void OptimizedObjectValues_AreWrittenAsOneUndoStep()
    {
        var editor = Loaded(
            new OpticsNscObjectSettings { Type = "Rectangle", Parameters = new() { 1, 2 }, Z = 5 },
            new OpticsNscObjectSettings { Type = "Sphere", Parameters = new() { 3, 1 }, TiltY = 1 });
        var changed = editor.Rows();
        changed[0] = changed[0] with { Z = 7.25, Parameters = new() { 1, 2.5 } };
        changed[1] = changed[1] with { TiltY = -4 };
        Assert.Null(editor.ApplyObjectValues(changed, "Optimize"));
        var now = editor.Rows();
        Assert.Equal((7.25, 2.5, -4.0), (now[0].Z!.Value, now[0].Parameters![1], now[1].TiltY!.Value));
        Assert.True(editor.Document.Undo());
        now = editor.Rows();
        Assert.Equal((5.0, 2.0, 1.0), (now[0].Z!.Value, now[0].Parameters![1], now[1].TiltY!.Value));
        Assert.Contains("changed", editor.ApplyObjectValues(changed.Take(1).ToList(), "Optimize"));
    }

    [Fact]
    public void TheNscWizards_WriteRowsIntoTheMeritFunctionEditor()
    {
        Loaded(
            new OpticsNscObjectSettings { Type = "Source Point", Z = -20, Parameters = new() { 10, 1000, 1, 0, 0, 90 } },
            new OpticsNscObjectSettings { Type = "Detector Rectangle", Parameters = new() { 20, 20, 10, 10 } });
        var merit = Optics.MeritEditor;
        var wizard = merit.CreateNscWizard();
        Assert.Equal(2, wizard.Detector);
        Assert.Equal(new[] { 0, 1 }, wizard.Sources.Select(s => s.Value));
        Assert.Equal(13, wizard.Criteria.Count);
        wizard.Target = 0.3;
        Assert.NotNull(wizard.ApplyRows());
        var codes = merit.ToFunction().Rows.Select(r => r.Code).ToArray();
        Assert.Equal(new[] { "NSDD", "NSTR", "NSDD" }, codes);
        Assert.False(wizard.IncludeClearAndTrace);
        Assert.Equal(4, wizard.StartAt);
        wizard.Criterion = NscWizardCriterion.CentroidX;
        Assert.NotNull(wizard.ApplyRows());
        Assert.Equal(new[] { "NSDD", "NSTR", "NSDD", "NSDD" }, merit.ToFunction().Rows.Select(r => r.Code));

        wizard.IsBitmap = true;
        wizard.ImagePath = "target.jpg";
        Assert.Null(wizard.ApplyRows());
        Assert.Contains("JPG", wizard.Status);
    }
}
