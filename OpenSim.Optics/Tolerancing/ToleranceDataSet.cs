using System.Globalization;
using System.Text;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;

namespace OpenSim.Optics.Tolerancing;

// OP-66: the Tolerance Data Viewer, Histogram and Yield (docs/Optics-Conventions.md §51). Sources:
// OpticStudio 2025 R2 help, "Tolerance Data Viewer", "Histogram", "Yield" (saved under
// plans/sources/optics/op-65-help/).

/// <summary>One column of a <see cref="ToleranceDataSet"/>: a name, its unit, and one value per trial.</summary>
public sealed record ToleranceDataColumn(string Name, string Unit, IReadOnlyList<double> Values);

/// <summary>
/// The data of a Monte Carlo run as the Tolerance Data Viewer shows it (help, "Tolerance Data Viewer":
/// "all tolerance operands, compensators, criteria, and reported values during a tolerance run ...
/// in a spreadsheet format"): one row per evaluated trial, one column per quantity: the criterion,
/// its change, each compensator and each drawn tolerance. "Statistics" gives each column's statistics
/// (<see cref="Statistics"/>); <see cref="Histogram"/> and <see cref="YieldCurve"/> are the Histogram
/// and Yield analyses of one column (help: "The tolerance operand, compensator, or reported value to
/// display"). Failed trials have no criterion and are left out, as from the run's statistics. Not
/// built: the .ZTD file (the viewer shows the run in memory; <see cref="ToCsv"/> writes the same table
/// as text) and tolerance-script reported values (OP-67).
/// </summary>
public sealed class ToleranceDataSet
{
    private ToleranceDataSet(IReadOnlyList<int> trials, IReadOnlyList<ToleranceDataColumn> columns, bool higherIsBetter, string criterionName)
    {
        Trials = trials;
        Columns = columns;
        HigherIsBetter = higherIsBetter;
        CriterionName = criterionName;
    }

    /// <summary>The 1-based trial of each row.</summary>
    public IReadOnlyList<int> Trials { get; }

    /// <summary>The columns: criterion, change, the compensators, then the drawn tolerances.</summary>
    public IReadOnlyList<ToleranceDataColumn> Columns { get; }

    /// <summary>True when the criterion is larger-is-better (its yield counts values at or above a limit).</summary>
    public bool HigherIsBetter { get; }

    public string CriterionName { get; }

    /// <summary>The name of the criterion column (column 0).</summary>
    public const string CriterionColumn = "Criterion";

    /// <summary>The data of a Monte Carlo result, its evaluated trials only.</summary>
    public static ToleranceDataSet FromMonteCarlo(MonteCarloResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var ok = result.Trials.Where(t => t.Succeeded).ToArray();
        var columns = new List<ToleranceDataColumn>
        {
            new(CriterionColumn, result.Unit, ok.Select(t => t.Criterion).ToArray()),
            new("Change", result.Unit, ok.Select(t => t.Change).ToArray()),
        };
        for (int c = 0; c < result.Compensators.Count; c++)
            columns.Add(new ToleranceDataColumn($"Compensator {c + 1}: {result.Compensators[c]}", "", ok.Select(t => t.Compensators[c]).ToArray()));
        for (int r = 0; r < result.Rows.Count; r++)
        {
            string code = ok.Length > 0 ? ok[0].Draws[r].Code : result.Trials.FirstOrDefault()?.Draws[r].Code ?? "";
            var unit = ToleranceOperandRegistry.Find(code)?.Unit ?? "";
            columns.Add(new ToleranceDataColumn($"Row {result.Rows[r]} ({code})", unit, ok.Select(t => t.Draws[r].Value).ToArray()));
        }
        return new ToleranceDataSet(ok.Select(t => t.Index).ToArray(), columns, result.HigherIsBetter, ToleranceCriteria.Info(result.RunOptions.Criterion).Name);
    }

    /// <summary>The column named <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentException">No such column.</exception>
    public ToleranceDataColumn Column(string name) => Columns.FirstOrDefault(c => c.Name == name)
        ?? throw new ArgumentException($"The tolerance data has no column '{name}'; it has {string.Join(", ", Columns.Select(c => $"'{c.Name}'"))}.");

    /// <summary>"Statistics": each column's count, mean, sample standard deviation, min, max and percentiles.</summary>
    public IReadOnlyList<(ToleranceDataColumn Column, MonteCarloStatistics? Statistics)> Statistics(IReadOnlyList<double>? percentiles = null)
    {
        var p = percentiles ?? new double[] { 10, 50, 90 };
        return Columns.Select(c => (c, c.Values.Count == 0 ? null : MonteCarloStatistics.Of(c.Values, p))).ToArray();
    }

    /// <summary>The Histogram analysis of one column (help, "Histogram").</summary>
    public MonteCarloHistogram? Histogram(string column, int bins, double? min = null, double? max = null) =>
        ToleranceHistograms.Of(Column(column).Values, bins, min, max);

    /// <summary>The Histogram analysis as a staircase line plot.</summary>
    public LinePlot? HistogramPlot(string column, int bins, double? min = null, double? max = null)
    {
        var c = Column(column);
        var h = ToleranceHistograms.Of(c.Values, bins, min, max);
        return h is null ? null : ToleranceHistograms.Plot(h, $"Histogram: {c.Name}, {c.Values.Count} trials", Label(c), new[] { ("Trials per bin", h) });
    }

    /// <summary>The Yield analysis of one column (help, "Yield": "a cumulative distribution function, or yield
    /// curve"): for each value v, the percentage of trials at or below v; for the criterion of a larger-is-better
    /// run, at or above v, so the curve reads as the yield a limit v would give. <paramref name="quick"/> adds
    /// a Quick Yield curve of the same criterion, dashed.</summary>
    public LinePlot? YieldCurve(string column, QuickYieldResult? quick = null)
    {
        var c = Column(column);
        if (c.Values.Count == 0) return null;
        bool above = HigherIsBetter && c.Name == CriterionColumn;
        var curves = new List<AnalysisCurve> { ToleranceHistograms.Cumulative($"Monte Carlo, {c.Values.Count} trials", c.Values, above) };
        if (quick is { Samples.Count: > 0 } && c.Name == CriterionColumn)
            curves.Add(ToleranceHistograms.Cumulative($"Quick Yield, {quick.Samples.Count} samples", quick.Samples, above) with { Dashed = true });
        return new LinePlot($"Yield: {c.Name}", Label(c), above ? "Trials at or above (%)" : "Trials at or below (%)", curves);
    }

    /// <summary>The table as comma-separated text (invariant culture, full precision): a header, then one line per trial.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.Append("Trial");
        foreach (var c in Columns) sb.Append(',').Append(Quote(c.Unit.Length == 0 ? c.Name : $"{c.Name} ({c.Unit})"));
        sb.Append('\n');
        for (int i = 0; i < Trials.Count; i++)
        {
            sb.Append(Trials[i].ToString(CultureInfo.InvariantCulture));
            foreach (var c in Columns) sb.Append(',').Append(c.Values[i].ToString("R", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Quote(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string Label(ToleranceDataColumn c) => c.Unit.Length == 0 ? c.Name : $"{c.Name} ({c.Unit})";
}

/// <summary>Histograms and cumulative curves of a list of values, shared by the data viewer and Quick Yield.</summary>
public static class ToleranceHistograms
{
    /// <summary>A histogram with the rule of the help's "Histogram": <paramref name="bins"/> bins span Min Value
    /// to Max Value (null: the smallest and largest value); "Results outside of this value will be summed up
    /// into one bar" each side (<see cref="MonteCarloHistogram.Below"/>, <see cref="MonteCarloHistogram.Above"/>).
    /// The same rule as <see cref="MonteCarloResult.Histogram"/>. Null for no values.</summary>
    public static MonteCarloHistogram? Of(IReadOnlyList<double> values, int bins, double? min = null, double? max = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return null;
        if (bins < 1) throw new ArgumentException($"The histogram needs at least one bin, not {bins}.");
        double lo = min ?? values.Min(), hi = max ?? values.Max();
        if (!(hi >= lo)) throw new ArgumentException($"The histogram's Max Value {hi} lies below its Min Value {lo}.");
        var edges = new double[bins + 1];
        for (int i = 0; i <= bins; i++) edges[i] = i == bins ? hi : lo + (hi - lo) * i / bins;
        var counts = new int[bins];
        int below = 0, above = 0;
        foreach (double v in values)
        {
            if (v < lo) { below++; continue; }
            if (v > hi) { above++; continue; }
            int k = hi > lo ? (int)Math.Floor((v - lo) / (hi - lo) * bins) : 0;
            counts[Math.Clamp(k, 0, bins - 1)]++;
        }
        return new MonteCarloHistogram(lo, hi, edges, counts, below, above);
    }

    /// <summary>Histograms as staircase curves on one plot (counts, or each as a percentage when there are several).</summary>
    public static LinePlot Plot(MonteCarloHistogram first, string title, string xLabel, IReadOnlyList<(string Name, MonteCarloHistogram Histogram)> histograms)
    {
        bool percent = histograms.Count > 1;
        var curves = new List<AnalysisCurve>();
        foreach (var (name, h) in histograms)
        {
            double total = h.Counts.Sum() + h.Below + h.Above;
            double Scale(int n) => percent ? 100.0 * n / total : n;
            var x = new List<double>();
            var y = new List<double>();
            for (int i = 0; i < h.Counts.Count; i++)
            {
                x.Add(h.Edges[i]); y.Add(i == 0 ? 0 : Scale(h.Counts[i - 1]));
                x.Add(h.Edges[i]); y.Add(Scale(h.Counts[i]));
            }
            x.Add(h.Edges[^1]); y.Add(Scale(h.Counts[^1]));
            x.Add(h.Edges[^1]); y.Add(0);
            curves.Add(new AnalysisCurve(name, x, y, Dashed: curves.Count > 0));
        }
        string outside = first.Below + first.Above > 0 ? $"; {first.Below} below, {first.Above} above the range" : "";
        return new LinePlot(title + outside, xLabel, percent ? "Share per bin (%)" : "Trials per bin", curves);
    }

    /// <summary>The empirical distribution function as a curve: the sorted values against the percentage at or
    /// below each (<paramref name="above"/>: at or above), a step at every value.</summary>
    public static AnalysisCurve Cumulative(string name, IReadOnlyList<double> values, bool above)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int n = sorted.Length;
        var x = new List<double>(2 * n);
        var y = new List<double>(2 * n);
        for (int i = 0; i < n; i++)
        {
            // At or below sorted[i]: (i + 1)/n; at or above: (n − i)/n.
            double before = above ? 100.0 * (n - i) / n : 100.0 * i / n;
            double after = above ? 100.0 * (n - i - 1) / n : 100.0 * (i + 1) / n;
            x.Add(sorted[i]); y.Add(before);
            x.Add(sorted[i]); y.Add(after);
        }
        return new AnalysisCurve(name, x, y);
    }

    /// <summary>A number for a report.</summary>
    internal static string N(double v) => double.IsNaN(v) ? "—" : StudyReport.Number(v, "g6");
}
