using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.App.ViewModels;

// OP-65: the Monte Carlo run inside the Tolerance Data Editor. It uses the criterion, compensator,
// field and sampling settings of the tolerance run (OpticsToleranceEditorViewModel.Run.cs).

/// <summary>One trial of the Monte Carlo results table, as text.</summary>
public sealed record MonteCarloTrialView(int Trial, string Criterion, string Change, string Compensators, string Note);

/// <summary>One line of the Monte Carlo statistics table.</summary>
public sealed record MonteCarloStatisticView(string Quantity, string Value);

public sealed partial class OpticsToleranceEditorViewModel
{
    private MonteCarloResult? _lastMonteCarlo;
    private (OpticsSettings Lens, ToleranceTable Table, ToleranceRunOptions Run, MonteCarloOptions Options)? _monteCarloInputs;

    public IReadOnlyList<ToleranceChoice<ToleranceStatistics>> MonteCarloStatisticsChoices { get; } = new[]
    {
        new ToleranceChoice<ToleranceStatistics>(ToleranceStatistics.Normal, "Normal"),
        new ToleranceChoice<ToleranceStatistics>(ToleranceStatistics.Uniform, "Uniform"),
        new ToleranceChoice<ToleranceStatistics>(ToleranceStatistics.Parabolic, "Parabolic"),
    };

    [ObservableProperty] private int _monteCarloTrials = 20;
    [ObservableProperty] private int _monteCarloSeed = 1;
    [ObservableProperty] private ToleranceChoice<ToleranceStatistics>? _selectedMonteCarloStatistics;
    [ObservableProperty] private int _monteCarloStandardDeviations = 2;

    /// <summary>The yield limit in the criterion's unit; empty for none.</summary>
    [ObservableProperty] private string _yieldLimitText = "";

    /// <summary>The percentiles, "10, 50, 90".</summary>
    [ObservableProperty] private string _percentilesText = "10, 20, 50, 80, 90";

    /// <summary>The folder of the .UDD files a STAT 3 row names.</summary>
    [ObservableProperty] private string _distributionFolder = "";

    [ObservableProperty] private bool _overlayMtf;
    [ObservableProperty] private int _overlayField = 1;
    [ObservableProperty] private double _overlayMaxFrequency;
    [ObservableProperty] private int _overlayPoints = 21;
    [ObservableProperty] private int _overlayTrials = 20;
    [ObservableProperty] private int _histogramBins = 20;

    /// <summary>The trial Save trial writes as a lens file.</summary>
    [ObservableProperty] private int _trialToSave = 1;

    [ObservableProperty] private string _monteCarloStatus = "Monte Carlo uses the criterion, compensators, fields and sampling above. Set the trials, then Run Monte Carlo.";

    [ObservableProperty] private string _monteCarloReportText = "";

    [ObservableProperty] private AnalysisPlot? _histogramPlot;

    [ObservableProperty] private AnalysisPlot? _mtfOverlayPlot;

    public ObservableCollection<MonteCarloTrialView> MonteCarloRows { get; } = new();

    public ObservableCollection<MonteCarloStatisticView> MonteCarloStatistics { get; } = new();

    /// <summary>The last Monte Carlo run's result, or null.</summary>
    public MonteCarloResult? LastMonteCarlo => _lastMonteCarlo;

    private static readonly HashSet<string> MonteCarloSettingNames = new(StringComparer.Ordinal)
    {
        nameof(MonteCarloTrials), nameof(MonteCarloSeed), nameof(SelectedMonteCarloStatistics), nameof(MonteCarloStandardDeviations),
        nameof(YieldLimitText), nameof(PercentilesText), nameof(DistributionFolder), nameof(OverlayMtf), nameof(OverlayField),
        nameof(OverlayMaxFrequency), nameof(OverlayPoints), nameof(OverlayTrials), nameof(HistogramBins),
    };

    private void InitializeMonteCarlo() => LoadMonteCarloOptions(new MonteCarloOptions(), new MonteCarloMtfOverlay(), false);

    private MonteCarloMtfOverlay OverlaySettings() => new()
    {
        Field = OverlayField, MaxFrequency = OverlayMaxFrequency, Points = OverlayPoints, Trials = OverlayTrials,
    };

    /// <summary>The Monte Carlo options the dialog holds.</summary>
    /// <exception cref="ArgumentException">The yield limit or a percentile is not a number.</exception>
    public MonteCarloOptions MonteCarloOptions()
    {
        double? limit = null;
        string y = (YieldLimitText ?? "").Trim();
        if (y.Length > 0)
        {
            if (!double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new ArgumentException($"Yield limit: '{y}' is not a number; leave it empty for no yield.");
            limit = v;
        }
        var percents = new List<double>();
        foreach (var part in (PercentilesText ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                throw new ArgumentException($"Percentiles: '{part}' is not a number; list them as \"10, 50, 90\".");
            percents.Add(p);
        }
        return new MonteCarloOptions
        {
            Trials = MonteCarloTrials, Seed = MonteCarloSeed,
            Statistics = SelectedMonteCarloStatistics?.Value ?? ToleranceStatistics.Normal,
            StandardDeviations = MonteCarloStandardDeviations, YieldLimit = limit, Percentiles = percents,
            DistributionFolder = string.IsNullOrWhiteSpace(DistributionFolder) ? null : DistributionFolder.Trim(),
            Overlay = OverlayMtf ? OverlaySettings() : null, HistogramBins = HistogramBins,
        };
    }

    private void LoadMonteCarloOptions(MonteCarloOptions o, MonteCarloMtfOverlay overlay, bool overlayOn)
    {
        bool was = _applying;
        _applying = true;
        try
        {
            MonteCarloTrials = o.Trials;
            MonteCarloSeed = o.Seed;
            SelectedMonteCarloStatistics = MonteCarloStatisticsChoices.FirstOrDefault(c => c.Value == o.Statistics) ?? MonteCarloStatisticsChoices[0];
            MonteCarloStandardDeviations = o.StandardDeviations;
            YieldLimitText = o.YieldLimit is double v ? v.ToString("R", CultureInfo.InvariantCulture) : "";
            PercentilesText = string.Join(", ", o.Percentiles.Select(p => p.ToString("R", CultureInfo.InvariantCulture)));
            DistributionFolder = o.DistributionFolder ?? "";
            OverlayMtf = overlayOn;
            OverlayField = overlay.Field;
            OverlayMaxFrequency = overlay.MaxFrequency;
            OverlayPoints = overlay.Points;
            OverlayTrials = overlay.Trials;
            HistogramBins = o.HistogramBins;
        }
        finally { _applying = was; }
    }

    /// <summary>The Monte Carlo settings as saved; null for the defaults or settings that do not parse.</summary>
    private OpticsToleranceMonteCarloSettings? MonteCarloSettingsToSave()
    {
        try { return MonteCarloSettings.ToSettings(MonteCarloOptions(), OverlaySettings()); }
        catch (ArgumentException) { return null; }
    }

    private void LoadMonteCarloSettings(OpticsToleranceMonteCarloSettings? saved, ICollection<string> notes)
    {
        var o = MonteCarloSettings.FromSettings(saved, notes, out var overlay, out bool on);
        LoadMonteCarloOptions(o, overlay, on);
    }

    private bool CanSaveTrial() => !IsRunning && _lastMonteCarlo is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunMonteCarloAsync()
    {
        ToleranceRunOptions run;
        MonteCarloOptions mc;
        try
        {
            run = RunOptions();
            mc = MonteCarloOptions();
        }
        catch (ArgumentException ex) { MonteCarloStatus = ex.Message; return; }
        var lens = _lens();
        var table = ToTable();
        var catalogs = _catalogs();
        _cancel = new CancellationTokenSource();
        var context = SynchronizationContext.Current;
        void Post(Action action) { if (context is null) action(); else context.Post(_ => action(), null); }
        run = run with { CancellationToken = _cancel.Token };
        mc = mc with
        {
            Progress = p => Post(() => { if (IsRunning) MonteCarloStatus = $"Running: {p.TrialsDone} of {p.TrialCount} trials, {p.Failed} failed…"; }),
        };
        IsRunning = true;
        MonteCarloStatus = $"Monte Carlo: {mc.Trials} trials…";
        try
        {
            var result = await Task.Run(() => MonteCarloAnalysis.Run(lens, table, catalogs, run, mc));
            _monteCarloInputs = (lens, table, run with { CancellationToken = default }, mc with { Progress = null, Seed = result.Seed });
            ShowMonteCarlo(result);
            MonteCarloStatus = $"Done: {result.Evaluated} of {result.Trials.Count} trials evaluated"
                + (result.Failed > 0 ? $", {result.Failed} failed and left out of the statistics (see each trial's note)" : "")
                + (result.Statistics is { } s ? $"; mean {N(s.Mean)} {result.Unit}, standard deviation {N(s.StandardDeviation)} {result.Unit}" : "")
                + (result.Yield is { } y ? $"; yield {N(100 * y.Fraction)} %" : "") + ".";
        }
        catch (OperationCanceledException) { MonteCarloStatus = "Cancelled."; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { MonteCarloStatus = ex.Message; }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsRunning = false;
        }
    }

    private void ShowMonteCarlo(MonteCarloResult result)
    {
        _lastMonteCarlo = result;
        OnPropertyChanged(nameof(LastMonteCarlo));
        SaveTrialCommand.NotifyCanExecuteChanged();
        MonteCarloRows.Clear();
        foreach (var t in result.Trials)
            MonteCarloRows.Add(new MonteCarloTrialView(t.Index, N(t.Criterion), N(t.Change), string.Join(", ", t.Compensators.Select(N)),
                t.Failure ?? (t.MtfFailure is { } m ? $"MTF: {m}" : t.Index == result.Best ? "best" : t.Index == result.Worst ? "worst" : "")));
        MonteCarloStatistics.Clear();
        string u = result.Unit.Length == 0 ? "" : " " + result.Unit;
        void Add(string q, string v) => MonteCarloStatistics.Add(new MonteCarloStatisticView(q, v));
        Add("Nominal", N(result.Nominal) + u);
        Add("Trials evaluated", $"{result.Evaluated} of {result.Trials.Count}");
        Add("Trials failed (left out)", result.Failed.ToString(CultureInfo.InvariantCulture));
        if (result.Statistics is { } s)
        {
            Add("Mean", N(s.Mean) + u);
            Add("Standard deviation", N(s.StandardDeviation) + u);
            Add("Min", N(s.Min) + u);
            Add("Max", N(s.Max) + u);
            foreach (var p in s.Percentiles) Add($"{N(p.Percent)} % at or below", N(p.Value) + u);
            if (result.Best is int b) Add("Best trial", b.ToString(CultureInfo.InvariantCulture));
            if (result.Worst is int w) Add("Worst trial", w.ToString(CultureInfo.InvariantCulture));
        }
        if (result.Yield is { } y)
            Add($"Yield ({(result.HigherIsBetter ? "≥" : "≤")} {N(y.Limit)}{u})", $"{N(100 * y.Fraction)} % ({y.Passed} of {y.Evaluated})");
        Add("Seed", result.Seed.ToString(CultureInfo.InvariantCulture));
        HistogramPlot = result.HistogramPlot();
        MtfOverlayPlot = result.MtfOverlayPlot();
        MonteCarloReportText = string.Join(Environment.NewLine, result.ReportLines());
    }

    /// <summary>Writes the lens of trial <paramref name="k"/> of the last run to a lens file. The trial is generated
    /// again from the run's lens, rows, settings and seed, so it is the trial the table shows.</summary>
    /// <exception cref="InvalidOperationException">No run yet.</exception>
    /// <exception cref="ArgumentException">No such trial, the trial failed, or its focus shift cannot be saved.</exception>
    public void SaveTrialLensFile(int k, string path)
    {
        if (_monteCarloInputs is not { } i || _lastMonteCarlo is null) throw new InvalidOperationException("Run Monte Carlo first.");
        if (k < 1 || k > _lastMonteCarlo.Trials.Count) throw new ArgumentException($"There is no trial {k}; the last run has trials 1 to {_lastMonteCarlo.Trials.Count}.");
        var lens = MonteCarloAnalysis.TrialLens(i.Lens, i.Table, _catalogs(), i.Run, i.Options, k);
        new OpticsFileSerializer().Save(new OpticsLensFile { Name = $"Monte Carlo trial {k}", Optics = lens }, path);
    }

    [RelayCommand(CanExecute = nameof(CanSaveTrial))]
    private void SaveTrial()
    {
        int k = TrialToSave;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = OpticsFileSerializer.FileFilter,
            FileName = $"MC_T{k:0000}{OpticsFileSerializer.FileExtension}",
            Title = $"Save Monte Carlo trial {k}",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            SaveTrialLensFile(k, dialog.FileName);
            MonteCarloStatus = $"Trial {k} saved to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            MonteCarloStatus = $"Trial {k} not saved: {ex.Message}";
        }
    }
}
