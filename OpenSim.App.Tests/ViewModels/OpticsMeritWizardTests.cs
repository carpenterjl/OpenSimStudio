using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Merit;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-56: the Optimization Wizard dialog on the Merit Function Editor. The rows it
/// writes are the library's (<see cref="DefaultMeritWizard"/>, checked in OpenSim.Tests); here,
/// that Apply inserts them at Start At as one undo step, that OK closes, and that Save, Load and
/// Reset Settings reach the project. Radii, thicknesses and indices are arbitrary inputs.</summary>
public sealed class OpticsMeritWizardTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>A singlet in air (mm): n = 1.5, front radius 40, back −120, EPD from the defaults.</summary>
    private OpticsMeritEditorViewModel WithSinglet()
    {
        var notes = new List<string>();
        Optics.LensEditor.LoadSettings(new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true },
            new() { Radius = -120, Thickness = 90 },
            new(),
        }, notes);
        Assert.Empty(notes);
        return Optics.MeritEditor;
    }

    [Fact]
    public void Apply_InsertsTheRowsAfterDmfs_AsOneUndoStep()
    {
        var editor = WithSinglet();
        editor.Load(new MeritFunction(new[] { MeritOperandRow.Of("EFFL", 0, 1, 50, 1), MeritOperandRow.Of("DMFS"), MeritOperandRow.Of("TOTR", 0, 0, 100, 1) }), new List<string>());
        var wizard = editor.CreateWizard();
        Assert.Equal(3, wizard.StartAt);                         // the row after DMFS
        wizard.Criterion = MeritWizardCriterion.SpotRadius;
        Assert.True(wizard.TryApply(), wizard.Status);
        var rows = editor.ToFunction().Rows;
        Assert.Equal(new[] { "EFFL", "DMFS" }, rows.Take(2).Select(r => r.Code));
        Assert.Equal("TOTR", rows[^1].Code);
        Assert.Contains(rows, r => r.Code == "TRAC");
        Assert.StartsWith("Inserted", wizard.Status);

        editor.UpdateCommand.Execute(null);
        Assert.NotNull(editor.LastEvaluation);
        Assert.False(editor.LastEvaluation!.HasFailures, editor.ProblemsText);

        editor.UndoCommand.Execute(null);
        Assert.Equal(new[] { "EFFL", "DMFS", "TOTR" }, editor.ToFunction().Rows.Select(r => r.Code));
    }

    [Fact]
    public void Ok_ClosesOnlyWhenTheRowsWentIn()
    {
        var editor = WithSinglet();
        var wizard = editor.CreateWizard();
        int closed = 0;
        wizard.CloseRequested += (_, _) => closed++;
        wizard.Reference = MeritWizardReference.Unreferenced;    // spot radius cannot be unreferenced
        wizard.OkCommand.Execute(null);
        Assert.Equal(0, closed);
        Assert.Contains("wavefront criterion only", wizard.Status);
        Assert.Equal(0, editor.OperandCount);
        wizard.Criterion = MeritWizardCriterion.Wavefront;
        wizard.OkCommand.Execute(null);
        Assert.Equal(1, closed);
        Assert.Contains(editor.ToFunction().Rows, r => r.Code == "OPDC");
    }

    [Fact]
    public void SaveLoadAndResetSettings_TravelWithTheProject()
    {
        var editor = WithSinglet();
        var wizard = editor.CreateWizard();
        wizard.Integration = PupilIntegration.RectangularArray;
        wizard.Grid = 10;
        wizard.GlassBoundaries = true;
        wizard.SaveSettingsCommand.Execute(null);
        Assert.Equal("RectangularArray", Optics.ToSettings().MeritWizard!.Integration);
        Assert.Equal(10, Optics.ToSettings().MeritWizard!.Grid);

        wizard.ResetSettingsCommand.Execute(null);
        Assert.Equal(PupilIntegration.GaussianQuadrature, wizard.Integration);
        wizard.LoadSettingsCommand.Execute(null);
        Assert.Equal(10, wizard.Grid);
        Assert.True(wizard.GlassBoundaries);

        // A new dialog opens with the saved settings; a reloaded project keeps them.
        Assert.Equal(PupilIntegration.RectangularArray, editor.CreateWizard().Integration);
        var saved = Optics.ToSettings();
        Optics.Reset();
        Assert.Null(Optics.ToSettings().MeritWizard);
        Optics.LoadSettings(saved);
        Assert.Equal(10, Optics.MeritEditor.CreateWizard().Grid);
    }
}
