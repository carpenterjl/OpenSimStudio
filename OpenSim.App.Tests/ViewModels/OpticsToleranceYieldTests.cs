using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-66: the Tolerance Wizard, the Tolerance Summary, Quick Yield and the Tolerance Data Viewer in the
/// Tolerance Data Editor. The lens data are arbitrary inputs.</summary>
public sealed class OpticsToleranceYieldTests : IDisposable
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

    [Fact]
    public void TheWizard_WritesItsRowsAsOneUndoStep_AndItsSettingsAreSaved()
    {
        var t = WithSinglet(new ToleranceOperandRow { Code = "TCON", Int1 = 1, Min = -1, Max = 1 });
        t.Wizard.ElementDecenterX = false;
        t.Wizard.FocusCompensatorRange = "0.5";
        t.ApplyWizardCommand.Execute(null);
        var table = t.ToTable();
        Assert.Equal("TWAV", table.Rows[0].Code);   // Start At Row 1 replaces the rows
        Assert.DoesNotContain(table.Rows, r => r.Code == "TCON");
        Assert.DoesNotContain(table.Rows, r => r.Code == "TEDX");
        Assert.Contains(table.Rows, r => r.Code == "TRAD" && r.Int1 == 1 && r.Max == 0.2);
        var comp = Assert.Single(table.Rows, r => r.Code == "COMP");
        Assert.Equal((2, -0.5, 0.5), (comp.Int1, comp.Min, comp.Max));
        Assert.Contains("written from row 1", t.WizardStatus);

        t.UndoCommand.Execute(null);
        Assert.Equal("TCON", Assert.Single(t.ToTable().Rows).Code);

        var saved = t.ToSettings();
        Assert.False(saved!.Wizard!.ElementDecenterX);
        Assert.Equal(0.5, saved.Wizard.FocusCompensatorRange);
        t.Reset();
        Assert.True(t.Wizard.ElementDecenterX);
        t.LoadSettings(saved, new List<string>());
        Assert.False(t.Wizard.ElementDecenterX);
        Assert.Equal("0.5", t.Wizard.FocusCompensatorRange);
    }

    [Fact]
    public void TheSummary_DescribesTheRowsInWords()
    {
        var t = WithSinglet(new ToleranceOperandRow { Code = "TRAD", Int1 = 1, Min = -0.1, Max = 0.1 });
        t.ShowSummaryCommand.Execute(null);
        Assert.Contains("radius of curvature", t.SummaryText);
        Assert.Contains("± 0.1", t.SummaryText);
        Assert.DoesNotContain("TRAD", t.SummaryText);
    }

    [Fact]
    public async Task QuickYield_AndTheDataViewer_ShowTheLastRuns()
    {
        var t = WithSinglet(new ToleranceOperandRow { Code = "TRAD", Int1 = 1, Min = -1, Max = 1 },
            new ToleranceOperandRow { Code = "TTHI", Int1 = 1, Int2 = 1, Min = -0.1, Max = 0.1 });
        t.SelectedCriterion = t.Criteria.Single(c => c.Value == ToleranceCriterion.RmsWavefront);
        t.MonteCarloTrials = 6;
        t.YieldLimitText = "1000";
        t.QuickYieldSamples = 100;
        Assert.False(t.OpenDataViewerCommand.CanExecute(null));

        await t.RunQuickYieldCommand.ExecuteAsync(null);
        Assert.True(t.LastQuickYield!.Succeeded, t.QuickYieldStatus);
        Assert.Equal(1 + 2 * 2 + 1, t.LastQuickYield.Evaluations);
        Assert.Contains("predicted yield 100 %", t.QuickYieldStatus);
        Assert.NotNull(t.QuickYieldHistogramPlot);
        Assert.NotNull(t.QuickYieldCurvePlot);
        Assert.Equal(100, t.ToSettings()!.QuickYield!.Samples);

        await t.RunMonteCarloCommand.ExecuteAsync(null);
        Assert.True(t.OpenDataViewerCommand.CanExecute(null));
        ToleranceDataViewerViewModel? opened = null;
        t.DataViewerRequested += (_, v) => opened = v;
        t.OpenDataViewerCommand.Execute(null);
        var viewer = Assert.IsType<ToleranceDataViewerViewModel>(opened);
        Assert.True(viewer.HasMonteCarlo);
        Assert.Equal(6, viewer.Trials!.Count);
        Assert.Equal(new[] { "Criterion", "Change", "Compensator 1: Back focus shift (paraxial focus), lens units", "Row 1 (TRAD)", "Row 2 (TTHI)" }, viewer.Columns);
        Assert.NotNull(viewer.HistogramPlot);
        Assert.Equal(2, ((OpenSim.Optics.Analysis.LinePlot)viewer.YieldPlot!).Curves.Count);   // Monte Carlo and Quick Yield
        viewer.ComputeStatisticsCommand.Execute(null);
        Assert.Equal(viewer.Columns.Count, viewer.Statistics.Count);
        Assert.Equal(7, viewer.Csv()!.TrimEnd('\n').Split('\n').Length);

        viewer.SelectedColumn = "Row 1 (TRAD)";
        Assert.Single(((OpenSim.Optics.Analysis.LinePlot)viewer.YieldPlot!).Curves);
        viewer.MinValueText = "x";
        Assert.Contains("'x'", viewer.Status);
    }
}
