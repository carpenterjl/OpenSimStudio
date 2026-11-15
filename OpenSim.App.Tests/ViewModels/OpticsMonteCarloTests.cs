using System.IO;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-65: the Monte Carlo run in the Tolerance Data Editor: its settings saved with the tolerance
/// data, a run filling the trials, statistics and histogram, and a trial saved as a lens file. The lens data
/// are arbitrary inputs.</summary>
public sealed class OpticsMonteCarloTests : IDisposable
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
    public void MonteCarloSettings_AreSavedWithTheToleranceData_AndReset()
    {
        var t = WithSinglet(Trad(0.5));
        t.MonteCarloTrials = 50;
        t.MonteCarloSeed = 9;
        t.SelectedMonteCarloStatistics = t.MonteCarloStatisticsChoices.Single(c => c.Value == ToleranceStatistics.Uniform);
        t.YieldLimitText = "0.002";
        t.PercentilesText = "5, 95";
        t.OverlayMaxFrequency = 30;
        var saved = t.ToSettings();
        Assert.Equal(50, saved!.MonteCarlo!.Trials);
        Assert.Equal("Uniform", saved.MonteCarlo.Statistics);
        Assert.Equal(30, saved.MonteCarlo.OverlayMaxFrequency);
        Assert.Null(saved.MonteCarlo.OverlayMtf);

        t.Reset();
        Assert.Equal(20, t.MonteCarloTrials);
        Assert.Null(t.ToSettings());

        var notes = new List<string>();
        t.LoadSettings(saved, notes);
        Assert.Empty(notes);
        var o = t.MonteCarloOptions();
        Assert.Equal(50, o.Trials);
        Assert.Equal(9, o.Seed);
        Assert.Equal(ToleranceStatistics.Uniform, o.Statistics);
        Assert.Equal(0.002, o.YieldLimit);
        Assert.Equal(new double[] { 5, 95 }, o.Percentiles);
        Assert.Null(o.Overlay);
        Assert.Equal(30, t.OverlayMaxFrequency);
    }

    [Fact]
    public void ABadYieldLimit_IsNamed_AndNothingRuns()
    {
        var t = WithSinglet(Trad(0.5));
        t.YieldLimitText = "small";
        t.RunMonteCarloCommand.Execute(null);
        Assert.Contains("'small'", t.MonteCarloStatus);
        Assert.Empty(t.MonteCarloRows);
    }

    [Fact]
    public async Task ARun_FillsTheTrialsAndStatistics_AndATrialSavesAsALensFile()
    {
        var t = WithSinglet(Trad(1), new ToleranceOperandRow { Code = "TTHI", Int1 = 1, Int2 = 1, Min = -0.1, Max = 0.1 });
        t.SelectedCriterion = t.Criteria.Single(c => c.Value == ToleranceCriterion.RmsWavefront);
        t.MonteCarloTrials = 5;
        t.YieldLimitText = "1000";
        Assert.False(t.SaveTrialCommand.CanExecute(null));
        await t.RunMonteCarloCommand.ExecuteAsync(null);
        Assert.Equal(5, t.MonteCarloRows.Count);
        Assert.Equal(0, t.LastMonteCarlo!.Failed);
        Assert.Contains(t.MonteCarloStatistics, s => s.Quantity == "Mean");
        Assert.Contains(t.MonteCarloStatistics, s => s.Quantity.StartsWith("Yield", StringComparison.Ordinal) && s.Value.StartsWith("100 %", StringComparison.Ordinal));
        Assert.NotNull(t.HistogramPlot);
        Assert.Null(t.MtfOverlayPlot);
        Assert.Contains("Monte Carlo: 5 trials", t.MonteCarloReportText);
        Assert.True(t.SaveTrialCommand.CanExecute(null));

        string path = Path.Combine(Path.GetTempPath(), $"op65-{Guid.NewGuid():N}{OpticsFileSerializer.FileExtension}");
        try
        {
            t.SaveTrialLensFile(3, path);
            var file = new OpticsFileSerializer().Load(path);
            Assert.Equal("Monte Carlo trial 3", file.Name);
            Assert.NotEqual(40, file.Optics!.Surfaces![1].Radius);   // the drawn radius
        }
        finally { File.Delete(path); }
        Assert.Throws<ArgumentException>(() => t.SaveTrialLensFile(6, path));
    }
}
