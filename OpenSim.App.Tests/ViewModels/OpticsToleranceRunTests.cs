using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-64: the tolerance run in the Tolerance Data Editor: its settings saved with the
/// tolerance data, a run filling the results, and an inverse run's limits written back as one undo
/// step. The lens data are arbitrary inputs.</summary>
public sealed class OpticsToleranceRunTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>A singlet in air (mm): n = 1.5, front radius 40, back −120.</summary>
    private OpticsToleranceEditorViewModel WithSinglet(params ToleranceOperandRow[] rows)
    {
        var notes = new List<string>();
        Optics.LensEditor.LoadSettings(new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true },
            new() { Radius = -120, Thickness = 90 },
            new(),
        }, notes);
        var t = Optics.ToleranceEditor;
        t.Load(new ToleranceTable(rows), notes);
        Assert.Empty(notes);
        return t;
    }

    private static ToleranceOperandRow Trad(double limit) => new() { Code = "TRAD", Int1 = 1, Min = -limit, Max = limit };

    [Fact]
    public void RunSettings_AreSavedWithTheToleranceData_AndReset()
    {
        var t = WithSinglet(Trad(0.5));
        t.SelectedMode = t.Modes.Single(m => m.Value == ToleranceMode.InverseIncrement);
        t.SelectedCriterion = t.Criteria.Single(c => c.Value == ToleranceCriterion.RmsWavefront);
        t.SelectedCompensation = t.Compensations.Single(c => c.Value == ToleranceCompensation.None);
        t.Increment = 0.01;
        t.Sampling = 4;
        t.CompensatorRowsText = "2, 5";
        var saved = t.ToSettings();
        Assert.Equal("InverseIncrement", saved!.Run!.Mode);
        Assert.Equal(new List<int> { 2, 5 }, saved.Run.CompensatorRows);

        t.Reset();
        Assert.Equal(ToleranceMode.Sensitivity, t.SelectedMode!.Value);
        Assert.Null(t.ToSettings());

        var notes = new List<string>();
        t.LoadSettings(saved, notes);
        Assert.Empty(notes);
        var o = t.RunOptions();
        Assert.Equal(ToleranceMode.InverseIncrement, o.Mode);
        Assert.Equal(ToleranceCriterion.RmsWavefront, o.Criterion);
        Assert.Equal(ToleranceCompensation.None, o.Compensation);
        Assert.Equal(0.01, o.Increment);
        Assert.Equal(4, o.Sampling);
        Assert.Equal(new[] { 2, 5 }, o.CompensatorRows);
    }

    [Fact]
    public void ABadCompensatorList_IsNamed_AndNothingRuns()
    {
        var t = WithSinglet(Trad(0.5));
        t.CompensatorRowsText = "two";
        Assert.Throws<ArgumentException>(() => t.RunOptions());
        t.RunToleranceCommand.Execute(null);
        Assert.Contains("'two'", t.RunStatus);
        Assert.Empty(t.RunRows);
    }

    [Fact]
    public async Task ARun_FillsTheResults_AndAnInverseRunsLimitsAreAppliedAsOneUndoStep()
    {
        var t = WithSinglet(Trad(2));
        await t.RunToleranceCommand.ExecuteAsync(null);
        var row = Assert.Single(t.RunRows);
        Assert.Equal("TRAD", row.Type);
        Assert.Contains("Nominal criterion", t.ReportText);
        Assert.False(t.ApplyInverseLimitsCommand.CanExecute(null));
        var first = t.LastRun!.Rows[0];
        double larger = Math.Max(first.AtMin.Change, first.AtMax.Change);
        Assert.True(larger > 0, t.ReportText);

        t.SelectedMode = t.Modes.Single(m => m.Value == ToleranceMode.InverseIncrement);
        t.Increment = larger / 2;
        await t.RunToleranceCommand.ExecuteAsync(null);
        var r = t.LastRun!.Rows[0];
        Assert.True(r.Adjusted, t.ReportText);
        Assert.True(t.ApplyInverseLimitsCommand.CanExecute(null));
        t.ApplyInverseLimitsCommand.Execute(null);
        var applied = t.ToTable().Rows[0];
        Assert.Equal(r.Min, applied.Min);
        Assert.Equal(r.Max, applied.Max);

        t.UndoCommand.Execute(null);
        Assert.Equal(-2, t.ToTable().Rows[0].Min);
        Assert.Equal(2, t.ToTable().Rows[0].Max);
    }
}
