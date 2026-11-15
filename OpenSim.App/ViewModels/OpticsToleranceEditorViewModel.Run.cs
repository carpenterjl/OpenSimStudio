using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Core.Model;
using OpenSim.Optics.Tolerancing;
using S = OpenSim.App.ViewModels.OpticsToleranceEditorSchema;

namespace OpenSim.App.ViewModels;

// OP-64: the tolerance run (sensitivity, inverse limit, inverse increment, Quick Sensitivity)
// inside the Tolerance Data Editor.

/// <summary>A choice of a picker: the value and its label.</summary>
public sealed record ToleranceChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the run's results table, as text.</summary>
public sealed record ToleranceRunRowView(int Row, string Type, string Min, string Max, string CriterionAtMin, string ChangeAtMin,
    string CriterionAtMax, string ChangeAtMax, string Note);

public sealed partial class OpticsToleranceEditorViewModel
{
    private CancellationTokenSource? _cancel;
    private ToleranceRunResult? _lastRun;
    private ToleranceTable? _lastRunTable;

    public IReadOnlyList<ToleranceChoice<ToleranceMode>> Modes { get; } = new[]
    {
        new ToleranceChoice<ToleranceMode>(ToleranceMode.Sensitivity, "Sensitivity"),
        new ToleranceChoice<ToleranceMode>(ToleranceMode.InverseLimit, "Inverse Limit"),
        new ToleranceChoice<ToleranceMode>(ToleranceMode.InverseIncrement, "Inverse Increment"),
    };

    public IReadOnlyList<ToleranceChoice<ToleranceCriterion>> Criteria { get; } =
        ToleranceCriteria.All.Select(i => new ToleranceChoice<ToleranceCriterion>(i.Criterion, i.Name)).ToArray();

    public IReadOnlyList<ToleranceChoice<ToleranceChangeMode>> ChangeModes { get; } = new[]
    {
        new ToleranceChoice<ToleranceChangeMode>(ToleranceChangeMode.LinearDifference, "Linear difference"),
        new ToleranceChoice<ToleranceChangeMode>(ToleranceChangeMode.RssDifference, "RSS difference"),
    };

    public IReadOnlyList<ToleranceChoice<ToleranceCompensation>> Compensations { get; } = new[]
    {
        new ToleranceChoice<ToleranceCompensation>(ToleranceCompensation.ParaxialFocus, "Paraxial focus"),
        new ToleranceChoice<ToleranceCompensation>(ToleranceCompensation.OptimizeDls, "Optimize (DLS)"),
        new ToleranceChoice<ToleranceCompensation>(ToleranceCompensation.OptimizeOd, "Optimize (OD)"),
        new ToleranceChoice<ToleranceCompensation>(ToleranceCompensation.None, "None"),
    };

    public IReadOnlyList<ToleranceChoice<ToleranceFieldSet>> FieldSets { get; } = new[]
    {
        new ToleranceChoice<ToleranceFieldSet>(ToleranceFieldSet.YSymmetric, "Y-Symmetric"),
        new ToleranceChoice<ToleranceFieldSet>(ToleranceFieldSet.XYSymmetric, "XY-Symmetric"),
        new ToleranceChoice<ToleranceFieldSet>(ToleranceFieldSet.UserDefined, "User defined"),
    };

    /// <summary>The criteria not built yet, one line each, for the dialog's note.</summary>
    public string CriteriaNotBuilt => "Not built: " + string.Join(" ", ToleranceCriteria.NotBuilt);

    [ObservableProperty] private ToleranceChoice<ToleranceMode>? _selectedMode;
    [ObservableProperty] private ToleranceChoice<ToleranceCriterion>? _selectedCriterion;
    [ObservableProperty] private ToleranceChoice<ToleranceChangeMode>? _selectedChange;
    [ObservableProperty] private ToleranceChoice<ToleranceCompensation>? _selectedCompensation;
    [ObservableProperty] private ToleranceChoice<ToleranceFieldSet>? _selectedFieldSet;
    [ObservableProperty] private int _sampling = 3;
    [ObservableProperty] private int _psfSampling = 64;

    /// <summary>The MTF criteria's frequency, cycles/mm.</summary>
    [ObservableProperty] private double _mtfFrequency;

    /// <summary>The encircled-energy criteria's radius, micrometers.</summary>
    [ObservableProperty] private double _encircledRadius;

    /// <summary>The compensator optimizer's cycles; 0 is automatic.</summary>
    [ObservableProperty] private int _optimizerCycles;

    /// <summary>The compensator rows to optimize, "2, 5"; empty for all.</summary>
    [ObservableProperty] private string _compensatorRowsText = "";

    [ObservableProperty] private double _limit;
    [ObservableProperty] private double _increment;
    [ObservableProperty] private int _showWorst = 10;

    /// <summary>The thread limit; the result is the same at any number.</summary>
    [ObservableProperty] private int _threads = Environment.ProcessorCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunToleranceCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickSensitivityCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyInverseLimitsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunMonteCarloCommand))]   // OP-65
    [NotifyCanExecuteChangedFor(nameof(SaveTrialCommand))]   // OP-65
    [NotifyPropertyChangedFor(nameof(IsRunIdle))]
    private bool _isRunning;

    public bool IsRunIdle => !IsRunning;

    [ObservableProperty] private string _runStatus = "Choose the settings, then Run. Quick Sensitivity runs Sensitivity with the paraxial focus compensator.";

    [ObservableProperty] private string _reportText = "";

    /// <summary>The last run's rows.</summary>
    public ObservableCollection<ToleranceRunRowView> RunRows { get; } = new();

    /// <summary>The last run's result, or null.</summary>
    public ToleranceRunResult? LastRun => _lastRun;

    private void InitializeRun() => LoadRunOptions(new ToleranceRunOptions());

    /// <summary>The run options the dialog holds.</summary>
    public ToleranceRunOptions RunOptions()
    {
        var rows = new List<int>();
        foreach (var part in (CompensatorRowsText ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) || r < 1)
                throw new ArgumentException($"Compensator rows: '{part}' is not a row number; list rows as \"2, 5\" or leave empty for all.");
            rows.Add(r);
        }
        return new ToleranceRunOptions
        {
            Mode = SelectedMode?.Value ?? ToleranceMode.Sensitivity,
            Criterion = SelectedCriterion?.Value ?? ToleranceCriterion.RmsSpotRadius,
            Change = SelectedChange?.Value ?? ToleranceChangeMode.LinearDifference,
            Compensation = SelectedCompensation?.Value ?? ToleranceCompensation.ParaxialFocus,
            Fields = SelectedFieldSet?.Value ?? ToleranceFieldSet.YSymmetric,
            Sampling = Sampling, PsfSampling = PsfSampling, MtfFrequency = MtfFrequency, EncircledRadius = EncircledRadius,
            OptimizerCycles = OptimizerCycles > 0 ? OptimizerCycles : null,
            CompensatorRows = rows.Count == 0 ? null : rows,
            Limit = Limit, Increment = Increment, ShowWorst = ShowWorst,
            MaxDegreeOfParallelism = Math.Max(1, Threads),
        };
    }

    private void LoadRunOptions(ToleranceRunOptions o)
    {
        bool was = _applying;
        _applying = true;
        try
        {
            SelectedMode = Modes.First(c => c.Value == o.Mode);
            SelectedCriterion = Criteria.First(c => c.Value == o.Criterion);
            SelectedChange = ChangeModes.First(c => c.Value == o.Change);
            SelectedCompensation = Compensations.First(c => c.Value == o.Compensation);
            SelectedFieldSet = FieldSets.First(c => c.Value == o.Fields);
            Sampling = o.Sampling;
            PsfSampling = o.PsfSampling;
            MtfFrequency = o.MtfFrequency;
            EncircledRadius = o.EncircledRadius;
            OptimizerCycles = o.OptimizerCycles ?? 0;
            CompensatorRowsText = o.CompensatorRows is { } r ? string.Join(", ", r) : "";
            Limit = o.Limit;
            Increment = o.Increment;
            ShowWorst = o.ShowWorst;
        }
        finally { _applying = was; }
    }

    /// <summary>The run settings as saved; null for the defaults or settings that do not parse.</summary>
    private OpticsToleranceRunSettings? RunSettingsToSave()
    {
        try { return ToleranceRunSettings.ToSettings(RunOptions()); }
        catch (ArgumentException) { return null; }
    }

    private static readonly HashSet<string> RunSettingNames = new(StringComparer.Ordinal)
    {
        nameof(SelectedMode), nameof(SelectedCriterion), nameof(SelectedChange), nameof(SelectedCompensation), nameof(SelectedFieldSet),
        nameof(Sampling), nameof(PsfSampling), nameof(MtfFrequency), nameof(EncircledRadius), nameof(OptimizerCycles), nameof(CompensatorRowsText), nameof(Limit), nameof(Increment), nameof(ShowWorst),
    };

    /// <summary>A change of a saved run setting publishes the system, so it is saved with the project.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && (RunSettingNames.Contains(name) || MonteCarloSettingNames.Contains(name)) && !_applying) _publish();   // OP-65: Monte Carlo settings too
    }

    private bool CanRun() => !IsRunning;
    private bool CanCancelRun() => IsRunning;
    private bool CanApplyInverse() => !IsRunning && _lastRun is { Options.Mode: not ToleranceMode.Sensitivity } r && r.Rows.Any(x => x.Adjusted);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunToleranceAsync() => RunAsync(quick: false);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task QuickSensitivityAsync() => RunAsync(quick: true);

    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private void CancelRun()
    {
        _cancel?.Cancel();
        RunStatus = "Cancelling…";
    }

    private async Task RunAsync(bool quick)
    {
        ToleranceRunOptions options;
        try { options = RunOptions(); }
        catch (ArgumentException ex) { RunStatus = ex.Message; return; }
        var lens = _lens();
        var table = ToTable();
        var catalogs = _catalogs();
        _cancel = new CancellationTokenSource();
        var context = SynchronizationContext.Current;
        void Post(Action action) { if (context is null) action(); else context.Post(_ => action(), null); }
        options = options with
        {
            CancellationToken = _cancel.Token,
            Progress = p => Post(() => { if (IsRunning) RunStatus = $"Running: {p.RowsDone} of {p.RowCount} rows, {p.Trials} trials (last {p.Last})…"; }),
        };
        IsRunning = true;
        RunStatus = quick ? "Quick Sensitivity…" : $"{SelectedMode?.Label}…";
        try
        {
            var result = await Task.Run(() => quick
                ? ToleranceAnalysis.QuickSensitivity(lens, table, catalogs, options)
                : ToleranceAnalysis.Run(lens, table, catalogs, options));
            Show(result, table);
            int failed = result.Rows.Count(r => !r.AtMin.Succeeded || !r.AtMax.Succeeded);
            RunStatus = $"Done: nominal {N(result.Nominal)} {result.Unit}, {result.Rows.Count} rows, {result.Trials} trials"
                + (failed > 0 ? $"; {failed} row{(failed == 1 ? "" : "s")} could not be evaluated (see the report)." : ".")
                + (result.Options.Mode != ToleranceMode.Sensitivity && result.Rows.Any(r => r.Adjusted) ? " Apply inverse limits writes the tightened limits into the table." : "");
        }
        catch (OperationCanceledException) { RunStatus = "Cancelled."; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { RunStatus = ex.Message; }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            IsRunning = false;
        }
    }

    private void Show(ToleranceRunResult result, ToleranceTable table)
    {
        _lastRun = result;
        _lastRunTable = table;
        OnPropertyChanged(nameof(LastRun));
        ApplyInverseLimitsCommand.NotifyCanExecuteChanged();
        RunRows.Clear();
        foreach (var r in result.Rows)
        {
            string note = !r.AtMin.Succeeded ? $"min: {r.AtMin.Failure}" : !r.AtMax.Succeeded ? $"max: {r.AtMax.Failure}"
                : r.Adjusted ? $"was {N(r.StartMin)}, {N(r.StartMax)}" : "";
            RunRows.Add(new ToleranceRunRowView(r.Row, r.Code, N(r.Min), N(r.Max), N(r.AtMin.Criterion), N(r.AtMin.Change),
                N(r.AtMax.Criterion), N(r.AtMax.Change), note));
        }
        ReportText = string.Join(Environment.NewLine, result.ReportLines());
    }

    /// <summary>Writes the inverse run's tightened limits into the table, as one undo step. Refused
    /// when the rows have changed since the run.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyInverse))]
    private void ApplyInverseLimits()
    {
        if (_lastRun is not { } run || _lastRunTable is not { } before) return;
        var now = ToTable();
        if (now.Rows.Count != before.Rows.Count || now.Rows.Zip(before.Rows).Any(p => p.First != p.Second))
        {
            RunStatus = "The tolerance rows have changed since the run; run it again before applying its limits.";
            return;
        }
        int changed = 0;
        using (Document.History.BeginGroup("Inverse limits"))
        {
            foreach (var r in run.Rows.Where(r => r.Adjusted))
            {
                int i = r.Row - 1;
                if (r.Min != r.StartMin && Document.SetValue(i, S.MinSlot + 1, CellValue.Of(r.Min)).Succeeded) changed++;
                if (r.Max != r.StartMax && Document.SetValue(i, S.MaxSlot + 1, CellValue.Of(r.Max)).Succeeded) changed++;
            }
        }
        _lastRunTable = ToTable();
        RunStatus = $"Inverse limits applied: {changed} limit{(changed == 1 ? "" : "s")} tightened. Undo puts them back.";
    }

    private static string N(double v) => double.IsNaN(v) ? "—" : v.ToString("G6", CultureInfo.InvariantCulture);
}
