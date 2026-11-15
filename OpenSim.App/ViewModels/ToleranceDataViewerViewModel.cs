using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.App.ViewModels;

/// <summary>One column's statistics in the data viewer.</summary>
public sealed record ToleranceDataStatisticView(string Column, int Count, string Mean, string StandardDeviation, string Min, string Max);

/// <summary>
/// The Tolerance Data Viewer (plan item OP-66; OpticStudio 2025 R2 help, "Tolerance Data Viewer", "Histogram",
/// "Yield"): the last sensitivity rows, the last Monte Carlo trials as a table (criterion, change,
/// compensators, drawn tolerances), each column's statistics, and the Histogram and Yield analyses of the
/// chosen column ("The tolerance operand, compensator, or reported value to display"), the yield curve of the
/// criterion overlaid with the last Quick Yield's. The help's .ZTD file is not built: the viewer shows the
/// runs in memory and can save the table as CSV.
/// </summary>
public sealed partial class ToleranceDataViewerViewModel : ObservableObject
{
    private readonly ToleranceDataSet? _data;
    private readonly QuickYieldResult? _quick;

    public ToleranceDataViewerViewModel(ToleranceRunResult? sensitivity, MonteCarloResult? monteCarlo, QuickYieldResult? quickYield)
    {
        _quick = quickYield;
        if (sensitivity is not null)
            foreach (var r in sensitivity.Rows)
                SensitivityRows.Add(new ToleranceRunRowView(r.Row, r.Code, N(r.Min), N(r.Max), N(r.AtMin.Criterion), N(r.AtMin.Change),
                    N(r.AtMax.Criterion), N(r.AtMax.Change), r.AtMin.Failure ?? r.AtMax.Failure ?? ""));
        SensitivityText = sensitivity is null ? "No sensitivity run yet." : string.Join(Environment.NewLine, sensitivity.ReportLines());
        QuickYieldText = quickYield is null ? "No Quick Yield run yet." : string.Join(Environment.NewLine, quickYield.ReportLines());
        if (monteCarlo is not null)
        {
            _data = ToleranceDataSet.FromMonteCarlo(monteCarlo);
            Columns = _data.Columns.Select(c => c.Name).ToArray();
            Trials = BuildTable(_data).DefaultView;
            Bins = monteCarlo.Options.HistogramBins;
            _selectedColumn = Columns.FirstOrDefault();
            Status = $"Monte Carlo: {_data.Trials.Count} evaluated trials of {monteCarlo.Trials.Count}"
                + (monteCarlo.Failed > 0 ? $"; {monteCarlo.Failed} failed and are not shown." : ".");
        }
        else Status = "No Monte Carlo run yet: run one in the Tolerance Data Editor to see its trials, histogram and yield.";
        Refresh();
    }

    public ObservableCollection<ToleranceRunRowView> SensitivityRows { get; } = new();

    public string SensitivityText { get; }

    public string QuickYieldText { get; }

    /// <summary>The trial table (one column per quantity); null without a Monte Carlo run.</summary>
    public DataView? Trials { get; }

    public IReadOnlyList<string> Columns { get; } = Array.Empty<string>();

    public ObservableCollection<ToleranceDataStatisticView> Statistics { get; } = new();

    [ObservableProperty] private string? _selectedColumn;
    [ObservableProperty] private int _bins = 20;

    /// <summary>The histogram's Min Value; empty for the smallest value.</summary>
    [ObservableProperty] private string _minValueText = "";

    /// <summary>The histogram's Max Value; empty for the largest value.</summary>
    [ObservableProperty] private string _maxValueText = "";

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private AnalysisPlot? _histogramPlot;
    [ObservableProperty] private AnalysisPlot? _yieldPlot;

    public bool HasMonteCarlo => _data is not null;

    partial void OnSelectedColumnChanged(string? value) => Refresh();
    partial void OnBinsChanged(int value) => Refresh();
    partial void OnMinValueTextChanged(string value) => Refresh();
    partial void OnMaxValueTextChanged(string value) => Refresh();

    /// <summary>Recomputes the plots of the chosen column.</summary>
    private void Refresh()
    {
        if (_data is null)
        {
            HistogramPlot = null;
            YieldPlot = _quick?.YieldPlot();
            return;
        }
        if (SelectedColumn is not { } column) return;
        try
        {
            double? min = Parse(MinValueText, "Min Value"), max = Parse(MaxValueText, "Max Value");
            HistogramPlot = _data.HistogramPlot(column, Bins, min, max);
            YieldPlot = _data.YieldCurve(column, _quick);
        }
        catch (ArgumentException ex) { Status = ex.Message; }
    }

    /// <summary>"Statistics": each column's statistics (help, "Tolerance Data Viewer").</summary>
    [RelayCommand]
    private void ComputeStatistics()
    {
        Statistics.Clear();
        if (_data is null) { Status = "There is no Monte Carlo run to take statistics of."; return; }
        foreach (var (c, s) in _data.Statistics())
            Statistics.Add(s is null
                ? new ToleranceDataStatisticView(c.Name, 0, "—", "—", "—", "—")
                : new ToleranceDataStatisticView(c.Name, s.Count, N(s.Mean), N(s.StandardDeviation), N(s.Min), N(s.Max)));
    }

    /// <summary>The trial table as CSV text, or null without a Monte Carlo run.</summary>
    public string? Csv() => _data?.ToCsv();

    [RelayCommand]
    private void SaveCsv()
    {
        if (Csv() is not { } csv) { Status = "There is no Monte Carlo run to save."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Tolerance data.csv", Title = "Save the tolerance data" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, csv);
            Status = $"Saved to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Status = $"Not saved: {ex.Message}"; }
    }

    private static DataTable BuildTable(ToleranceDataSet data)
    {
        var table = new DataTable("Trials");
        table.Columns.Add("Trial", typeof(int));
        // Plain column names (a binding path cannot hold brackets or dots); the caption is the header the window shows.
        for (int k = 0; k < data.Columns.Count; k++)
        {
            var c = data.Columns[k];
            table.Columns.Add(new DataColumn($"C{k}", typeof(double)) { Caption = c.Unit.Length == 0 ? c.Name : $"{c.Name} ({c.Unit})" });
        }
        for (int i = 0; i < data.Trials.Count; i++)
        {
            var row = table.NewRow();
            row[0] = data.Trials[i];
            for (int k = 0; k < data.Columns.Count; k++) row[k + 1] = data.Columns[k].Values[i];
            table.Rows.Add(row);
        }
        return table;
    }

    private static double? Parse(string? text, string what)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            throw new ArgumentException($"Histogram {what}: '{t}' is not a number; leave it empty for the data's own.");
        return v;
    }

    private static string N(double v) => double.IsNaN(v) ? "—" : v.ToString("G6", CultureInfo.InvariantCulture);
}
