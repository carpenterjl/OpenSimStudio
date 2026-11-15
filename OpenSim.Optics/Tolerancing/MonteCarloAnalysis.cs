using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.Tolerancing;

// OP-65: Monte Carlo tolerancing (docs/Optics-Conventions.md §45). Sources: OpticStudio 2025 R2
// help, "Monte Carlo Analysis" and its distribution pages, "Discussion of Monte Carlo Analysis
// Method", "Nesting Rules for Monte Carlo Analysis", "STAT", "SEED", "Tolerancing" (the dialog's
// Monte Carlo tab), "Histogram", "TIRR", saved under plans/sources/optics/op-63-help/,
// op-64-help/ and op-65-help/.

/// <summary>The MTF overlay of a Monte Carlo run (help, "Tolerancing", Monte Carlo tab, "Overlay MC
/// Graphics": "each open analysis graphical window (such as a ray fan or MTF graph) will be updated
/// and overlayed for each Monte Carlo generated lens").</summary>
public sealed record MonteCarloMtfOverlay
{
    /// <summary>The system field, 1-based.</summary>
    public int Field { get; init; } = 1;

    /// <summary>The largest frequency, MTF units (cycles/mm); no default from a source, 0 is unset and refused.</summary>
    public double MaxFrequency { get; init; }

    /// <summary>The number of evenly spaced frequencies from 0 to <see cref="MaxFrequency"/>.</summary>
    public int Points { get; init; } = 21;

    /// <summary>The overlay draws the first this many trials (this program's limit on the curves drawn).</summary>
    public int Trials { get; init; } = 20;
}

/// <summary>Progress of a Monte Carlo run.</summary>
public sealed record MonteCarloProgress(int TrialsDone, int TrialCount, int Failed);

/// <summary>
/// The settings of <see cref="MonteCarloAnalysis.Run"/> besides the criterion and compensators,
/// which are <see cref="ToleranceRunOptions"/>'s (with its cancellation token and thread limit).
/// </summary>
public sealed record MonteCarloOptions
{
    /// <summary>"# Monte Carlo Runs": "The default setting of 20 will generate 20 random lenses" (help, "Tolerancing").</summary>
    public int Trials { get; init; } = 20;

    /// <summary>The run's seed. Trial k draws from streams fixed by (seed, k) alone. 0 draws a seed from the
    /// clock, as SEED 0 does ("a random seed based upon the system clock"), and the result names it.
    /// The default 1 is this program's, so a run repeats unless asked not to.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>The distribution of rows no STAT row governs (help, "Tolerancing", "Statistics": normal,
    /// uniform or parabolic). By default "all parameters are assumed to follow the same normal
    /// distribution" (help, "Monte Carlo Analysis").</summary>
    public ToleranceStatistics Statistics { get; init; } = ToleranceStatistics.Normal;

    /// <summary>n for rows no STAT row governs: "The default value of "n" is two" (help, "Normal Statistical Distribution").</summary>
    public int StandardDeviations { get; init; } = 2;

    /// <summary>The criterion limit the yield counts against, in the criterion's unit; null for none.</summary>
    public double? YieldLimit { get; init; }

    /// <summary>The percentiles reported, in percent. The help does not list the report's statistics;
    /// this default is this program's.</summary>
    public IReadOnlyList<double> Percentiles { get; init; } = new double[] { 10, 20, 50, 80, 90 };

    /// <summary>The folder user defined distribution files (.UDD) are read from (OpticStudio reads them
    /// from "the &lt;data&gt;\Tolerance folder"); null for none.</summary>
    public string? DistributionFolder { get; init; }

    /// <summary>Reads a .UDD file's text by the name in its STAT row's comment; overrides <see cref="DistributionFolder"/>.</summary>
    public Func<string, string>? ReadDistribution { get; init; }

    /// <summary>The MTF overlay; null for none.</summary>
    public MonteCarloMtfOverlay? Overlay { get; init; }

    /// <summary>The histogram's number of bins (help, "Histogram", "Bin Count"); the default 20 is this program's.</summary>
    public int HistogramBins { get; init; } = 20;

    /// <summary>Called after every trial, on a worker thread.</summary>
    public Action<MonteCarloProgress>? Progress { get; init; }
}

/// <summary>The value a trial drew for one tolerance row (1-based), with the random direction of a
/// radial operand and TIRR's random astigmatism angle, degrees.</summary>
public sealed record MonteCarloDraw(int Row, string Code, double Value, double? DirectionDegrees = null, double? AngleDegrees = null);

/// <summary>An MTF curve of one trial: tangential and sagittal modulation at <see cref="Frequencies"/> (MTF units).</summary>
public sealed record MonteCarloMtfCurve(IReadOnlyList<double> Frequencies, IReadOnlyList<double> Tangential, IReadOnlyList<double> Sagittal);

/// <summary>One Monte Carlo trial (1-based <see cref="Index"/>): every tolerance drawn, applied at
/// once, compensated and evaluated. A failed trial keeps its draws and says why.</summary>
public sealed record MonteCarloTrial
{
    public required int Index { get; init; }
    public required IReadOnlyList<MonteCarloDraw> Draws { get; init; }
    public double Criterion { get; init; } = double.NaN;

    /// <summary>The change from nominal (<see cref="ToleranceAnalysis.ChangeOf"/>).</summary>
    public double Change { get; init; } = double.NaN;

    /// <summary>Each compensator's change from the compensated nominal (<see cref="MonteCarloResult.Compensators"/>).</summary>
    public IReadOnlyList<double> Compensators { get; init; } = Array.Empty<double>();

    public string? Failure { get; init; }

    /// <summary>The trial's MTF for the overlay; null when not asked for or it failed (<see cref="MtfFailure"/>).</summary>
    public MonteCarloMtfCurve? Mtf { get; init; }
    public string? MtfFailure { get; init; }

    public bool Succeeded => Failure is null;
}

/// <summary>A percentile: the value <see cref="Percent"/> % of the evaluated trials lie at or below.</summary>
public sealed record MonteCarloPercentile(double Percent, double Value);

/// <summary>The spread of one quantity over the evaluated trials.</summary>
/// <param name="StandardDeviation">The sample standard deviation (n − 1); 0 for one trial.</param>
public sealed record MonteCarloStatistics(int Count, double Mean, double StandardDeviation, double Min, double Max,
    IReadOnlyList<MonteCarloPercentile> Percentiles)
{
    /// <summary>The statistics of <paramref name="values"/> (in trial order). Percentiles interpolate linearly
    /// between order statistics at rank p(n − 1), the definition of ParameterSweep's OutputStatistics.</summary>
    public static MonteCarloStatistics Of(IReadOnlyList<double> values, IReadOnlyList<double> percents)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(percents);
        int n = values.Count;
        if (n == 0) throw new InvalidOperationException("No trial was evaluated, so there are no statistics.");
        double sum = 0;
        for (int i = 0; i < n; i++) sum += values[i];
        double mean = sum / n;
        double ss = 0;
        for (int i = 0; i < n; i++) ss += (values[i] - mean) * (values[i] - mean);
        var sorted = values.ToArray();
        Array.Sort(sorted);
        double At(double p)
        {
            double rank = p / 100 * (n - 1);
            int lo = (int)Math.Floor(rank);
            int hi = Math.Min(lo + 1, n - 1);
            return sorted[lo] + (rank - lo) * (sorted[hi] - sorted[lo]);
        }
        return new MonteCarloStatistics(n, mean, n > 1 ? Math.Sqrt(ss / (n - 1)) : 0, sorted[0], sorted[^1],
            percents.Select(p => new MonteCarloPercentile(p, At(p))).ToArray());
    }
}

/// <summary>The yield: <see cref="Passed"/> of <see cref="Evaluated"/> trials meet the limit (criterion ≤ limit, or ≥ where
/// larger is better). Failed trials are not counted (<see cref="MonteCarloResult.Failed"/>).</summary>
public sealed record MonteCarloYield(double Limit, int Passed, int Evaluated)
{
    public double Fraction => Evaluated == 0 ? double.NaN : (double)Passed / Evaluated;
}

/// <summary>A histogram of the criterion (help, "Histogram": bins spanning Min Value to Max Value; "Results
/// outside of this value will be summed up into one bar").</summary>
public sealed record MonteCarloHistogram(double Min, double Max, IReadOnlyList<double> Edges, IReadOnlyList<int> Counts, int Below, int Above);

/// <summary>The outcome of <see cref="MonteCarloAnalysis.Run"/>.</summary>
public sealed record MonteCarloResult
{
    public required ToleranceRunOptions RunOptions { get; init; }
    public required MonteCarloOptions Options { get; init; }

    /// <summary>The seed used (the clock's when <see cref="MonteCarloOptions.Seed"/> was 0).</summary>
    public required int Seed { get; init; }

    public required double Nominal { get; init; }
    public required string Unit { get; init; }
    public required bool HigherIsBetter { get; init; }
    public required IReadOnlyList<string> Compensators { get; init; }

    /// <summary>The 1-based tolerance rows drawn in every trial, and their distributions.</summary>
    public required IReadOnlyList<int> Rows { get; init; }
    public required IReadOnlyList<ToleranceDistribution> Distributions { get; init; }

    public required IReadOnlyList<MonteCarloTrial> Trials { get; init; }

    /// <summary>The criterion over the evaluated trials; null when none was evaluated.</summary>
    public MonteCarloStatistics? Statistics { get; init; }

    /// <summary>Each compensator's change over the evaluated trials (empty when none was evaluated).</summary>
    public IReadOnlyList<MonteCarloStatistics> CompensatorStatistics { get; init; } = Array.Empty<MonteCarloStatistics>();

    public MonteCarloYield? Yield { get; init; }

    /// <summary>The best and worst evaluated trial (1-based), or null.</summary>
    public int? Best { get; init; }
    public int? Worst { get; init; }

    /// <summary>The compensated nominal lens's MTF for the overlay, or null.</summary>
    public MonteCarloMtfCurve? NominalMtf { get; init; }

    /// <summary>The overlay's frequency unit ("cycles/mm" or "cycles/mrad").</summary>
    public string MtfUnit { get; init; } = "cycles/mm";

    public required IReadOnlyList<string> Assumptions { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public int Failed => Trials.Count(t => !t.Succeeded);
    public int Evaluated => Trials.Count(t => t.Succeeded);

    /// <summary>The histogram of the evaluated trials' criterion; null bounds take the smallest and largest value.</summary>
    public MonteCarloHistogram? Histogram(int? bins = null, double? min = null, double? max = null)
    {
        var values = Trials.Where(t => t.Succeeded).Select(t => t.Criterion).ToArray();
        if (values.Length == 0) return null;
        int b = bins ?? Options.HistogramBins;
        if (b < 1) throw new ArgumentException($"The histogram needs at least one bin, not {b}.");
        double lo = min ?? values.Min(), hi = max ?? values.Max();
        if (!(hi >= lo)) throw new ArgumentException($"The histogram's Max Value {hi} lies below its Min Value {lo}.");
        var edges = new double[b + 1];
        for (int i = 0; i <= b; i++) edges[i] = i == b ? hi : lo + (hi - lo) * i / b;
        var counts = new int[b];
        int below = 0, above = 0;
        foreach (double v in values)
        {
            if (v < lo) { below++; continue; }
            if (v > hi) { above++; continue; }
            int k = hi > lo ? (int)Math.Floor((v - lo) / (hi - lo) * b) : 0;
            counts[Math.Clamp(k, 0, b - 1)]++;
        }
        return new MonteCarloHistogram(lo, hi, edges, counts, below, above);
    }

    /// <summary>The histogram as a line plot: a staircase over the bins.</summary>
    public LinePlot? HistogramPlot(int? bins = null, double? min = null, double? max = null)
    {
        var h = Histogram(bins, min, max);
        if (h is null) return null;
        var x = new List<double>();
        var y = new List<double>();
        for (int i = 0; i < h.Counts.Count; i++)
        {
            x.Add(h.Edges[i]); y.Add(i == 0 ? 0 : h.Counts[i - 1]);
            x.Add(h.Edges[i]); y.Add(h.Counts[i]);
        }
        x.Add(h.Edges[^1]); y.Add(h.Counts[^1]);
        x.Add(h.Edges[^1]); y.Add(0);
        string name = ToleranceCriteria.Info(RunOptions.Criterion).Name;
        string outside = h.Below + h.Above > 0 ? $"; {h.Below} below, {h.Above} above the range" : "";
        return new LinePlot($"Monte Carlo: {name}, {Evaluated} trials{outside}", Unit.Length == 0 ? name : $"{name} ({Unit})", "Trials",
            new[] { new AnalysisCurve("Trials per bin", x, y) });
    }

    /// <summary>The MTF overlay: the nominal (dashed) and each trial's tangential and sagittal curves; null without an overlay.</summary>
    public LinePlot? MtfOverlayPlot()
    {
        if (Options.Overlay is null || NominalMtf is null) return null;
        var curves = new List<AnalysisCurve>
        {
            new("Nominal T", NominalMtf.Frequencies, NominalMtf.Tangential, Dashed: true),
            new("Nominal S", NominalMtf.Frequencies, NominalMtf.Sagittal, Dashed: true),
        };
        foreach (var t in Trials.Where(t => t.Mtf is not null))
        {
            curves.Add(new AnalysisCurve($"Trial {t.Index} T", t.Mtf!.Frequencies, t.Mtf.Tangential));
            curves.Add(new AnalysisCurve($"Trial {t.Index} S", t.Mtf.Frequencies, t.Mtf.Sagittal));
        }
        return new LinePlot($"Monte Carlo MTF overlay, field {Options.Overlay.Field}", $"Spatial frequency ({MtfUnit})", "Modulation", curves);
    }

    /// <summary>The report, one line each: the settings, every trial, the statistics, the yield, the notes, the assumptions.</summary>
    public IReadOnlyList<string> ReportLines()
    {
        string V(double v) => double.IsNaN(v) ? "—" : StudyReport.Number(v, "g6");
        string u = Unit.Length == 0 ? "" : " " + Unit;
        var info = ToleranceCriteria.Info(RunOptions.Criterion);
        var lines = new List<string>
        {
            $"Monte Carlo: {Trials.Count} trials, seed {Seed}. Criterion: {info.Name}{(Unit.Length == 0 ? "" : $" ({Unit})")}. Compensation: {RunOptions.Compensation}.",
            $"Nominal criterion: {V(Nominal)}{u}.",
            "",
            "Trial  Criterion  Change" + string.Concat(Compensators.Select((_, i) => $"  Comp {i + 1}")),
        };
        foreach (var t in Trials)
            lines.Add(t.Succeeded
                ? $"{t.Index}  {V(t.Criterion)}  {V(t.Change)}" + string.Concat(t.Compensators.Select(c => "  " + V(c)))
                : $"{t.Index}  failed: {t.Failure}");
        lines.Add("");
        lines.Add($"Evaluated: {Evaluated} of {Trials.Count}; failed and left out of the statistics: {Failed}.");
        if (Statistics is { } s)
        {
            lines.Add($"Mean {V(s.Mean)}{u}, standard deviation {V(s.StandardDeviation)}{u}, min {V(s.Min)}{u}, max {V(s.Max)}{u}.");
            foreach (var p in s.Percentiles)
                lines.Add($"  {StudyReport.Number(p.Percent, "g4")} % of trials at or below {V(p.Value)}{u}.");
            if (Best is int b && Worst is int w)
                lines.Add($"Best trial {b} ({V(Trials[b - 1].Criterion)}{u}); worst trial {w} ({V(Trials[w - 1].Criterion)}{u}).");
            for (int i = 0; i < CompensatorStatistics.Count; i++)
            {
                var c = CompensatorStatistics[i];
                lines.Add($"Compensator {i + 1} ({Compensators[i]}): mean {V(c.Mean)}, standard deviation {V(c.StandardDeviation)}, min {V(c.Min)}, max {V(c.Max)}.");
            }
        }
        if (Yield is { } y)
            lines.Add($"Yield: {y.Passed} of {y.Evaluated} evaluated trials ({StudyReport.Number(100 * y.Fraction, "g4")} %) have the criterion {(HigherIsBetter ? "at or above" : "at or below")} {V(y.Limit)}{u}"
                + (Failed > 0 ? $"; {Failed} failed trial{(Failed == 1 ? " is" : "s are")} not counted." : "."));
        if (Notes.Count > 0) { lines.Add(""); lines.Add("Notes:"); lines.AddRange(Notes.Select(n => "  " + n)); }
        lines.Add("");
        lines.Add("Assumptions:");
        lines.AddRange(Assumptions.Select(a => "  " + a));
        return lines;
    }
}

/// <summary>
/// Monte Carlo tolerancing (plan item OP-65; help, "Monte Carlo Analysis": "For each Monte Carlo
/// cycle, all of the parameters which have specified tolerances are randomly set using the defined
/// range of the parameter and a statistical model of the distribution of that parameter over the
/// specified range"; "For each cycle, the compensators are adjusted and then the criterion and
/// compensator values are printed. After all of the Monte Carlo trials a statistical summary is
/// provided").
/// <para>
/// <b>Set-up.</b> OP-64's <see cref="ToleranceAnalysis.Prepare"/> checks the table and builds the
/// criterion evaluator and compensators; the compensated nominal lens gives N.
/// </para>
/// <para>
/// <b>A trial.</b> Every tolerance row (not ignored, not a control row) draws its value from its
/// <see cref="ToleranceDistribution"/>, in row order; TEDR, TSDR, TARR and TRLR also draw a
/// direction uniform over 0–360° ("a random radial direction") and TIRR its astigmatism angle
/// ("chosen randomly between 0 and 360 degrees", help "TIRR"). The rows are then applied one after
/// another to the compensated nominal lens by OP-63's <see cref="TolerancePerturber"/>, each change
/// mapping the table's surface numbers through the surfaces earlier changes inserted (the nesting
/// rules make the order unambiguous), the compensators are evaluated as in OP-64, and the criterion
/// is recorded. Rows whose operand this version cannot apply are left out of every trial and named.
/// </para>
/// <para>
/// <b>Random numbers.</b> Rows under one SEED value draw, in row order, from
/// <c>SplitMix64.ForStream(seed, k)</c> for trial k; rows under no SEED (or SEED 0) use the run's
/// seed. So trial k depends on the seeds and k alone and is the same run alone, in sequence or in
/// parallel. Trials run in parallel, one slot each on one thread; the statistics are reduced in
/// trial order, so the result is bitwise identical at any thread count.
/// </para>
/// <para>
/// <b>Failures.</b> A trial whose perturbation or evaluation fails is kept with its reason and
/// left out of the statistics and the yield, with its count shown. Element decenters and tilts
/// make the lens non-axial, which the ray launcher refuses (§15, OP-22), so trials carrying them
/// fail by name.
/// </para>
/// </summary>
public static class MonteCarloAnalysis
{
    /// <summary>Runs <see cref="MonteCarloOptions.Trials"/> trials on the saved lens <paramref name="nominal"/>.</summary>
    /// <exception cref="ArgumentException">Invalid options, a table that does not fit the lens, a STAT row that
    /// cannot be read, a compensator that cannot be varied.</exception>
    /// <exception cref="ToleranceEvaluationException">The nominal criterion cannot be evaluated.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static MonteCarloResult Run(OpticsSettings nominal, ToleranceTable table, IReadOnlyList<OpticalCatalog> catalogs,
        ToleranceRunOptions? run = null, MonteCarloOptions? options = null)
    {
        var plan = new Plan(nominal, table, catalogs, run ?? new ToleranceRunOptions(), options ?? new MonteCarloOptions());
        var o = plan.Options;
        var token = plan.Run.CancellationToken;
        int done = 0, failed = 0;
        var trials = ParallelSlots.Compute(o.Trials, plan.Run.MaxDegreeOfParallelism, i =>
        {
            var t = plan.Trial(i + 1, out _);
            if (!t.Succeeded) Interlocked.Increment(ref failed);
            int d = Interlocked.Increment(ref done);
            o.Progress?.Invoke(new MonteCarloProgress(d, o.Trials, Volatile.Read(ref failed)));
            return t;
        }, token);
        return plan.Result(trials);
    }

    /// <summary>Trial <paramref name="k"/> (1-based) alone: the same as trial k of <see cref="Run"/> with the same
    /// seed. A seed of 0 draws from the clock, so pass the seed <see cref="MonteCarloResult.Seed"/> names.</summary>
    public static MonteCarloTrial Trial(OpticsSettings nominal, ToleranceTable table, IReadOnlyList<OpticalCatalog> catalogs,
        ToleranceRunOptions run, MonteCarloOptions options, int k)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), k, "Trials are numbered from 1.");
        var plan = new Plan(nominal, table, catalogs, run, options);
        return plan.Trial(k, out _);
    }

    /// <summary>
    /// The lens of trial <paramref name="k"/> (help, "# Monte Carlo Save": "to allow further study on the lenses
    /// being generated"): the perturbed record after compensation. With paraxial focus compensation the focus
    /// shift is written into the thickness before the image; that is refused, naming why, when the lens has more
    /// than one configuration or a solve, pickup or multi-configuration row sets that thickness. With optimized
    /// compensation the compensator cells carry the Variable flags the optimizer used, and the lens's own
    /// variable flags are gone.
    /// </summary>
    /// <exception cref="ArgumentException">The trial failed, or its focus shift cannot be saved.</exception>
    public static OpticsSettings TrialLens(OpticsSettings nominal, ToleranceTable table, IReadOnlyList<OpticalCatalog> catalogs,
        ToleranceRunOptions run, MonteCarloOptions options, int k)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), k, "Trials are numbered from 1.");
        var plan = new Plan(nominal, table, catalogs, run, options with { Overlay = null });
        var t = plan.Trial(k, out var lens);
        if (!t.Succeeded || lens is null) throw new ArgumentException($"Monte Carlo trial {k} failed, so it has no lens: {t.Failure}");
        if (plan.Run.Compensation != ToleranceCompensation.ParaxialFocus) return lens.Settings;
        return WithFocusShift(lens.Settings, t.Compensators[0], plan.Configurations, k);
    }

    private static OpticsSettings WithFocusShift(OpticsSettings s, double shift, int configurations, int k)
    {
        string who = $"Monte Carlo trial {k}";
        if (configurations > 1)
            throw new ArgumentException($"{who}: the paraxial focus shift differs from configuration to configuration, so it cannot be written into one saved thickness; save it from a single-configuration lens or use Optimize compensation with a COMP row.");
        var surfaces = s.Surfaces!.ToList();
        int j = surfaces.Count - 2;
        var last = surfaces[j];
        if (last.ThicknessIsInfinite) throw new ArgumentException($"{who}: surface {j}'s thickness is infinite; the focus shift cannot be added to it.");
        if (SurfaceFlags.Of(last, SavedSurfaces.ThicknessColumn) is { } f && !SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag))
            throw new ArgumentException($"{who}: surface {j}'s thickness is set by a {(SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) ? "pickup" : $"'{f.Solve}' solve")}, which would overwrite the focus shift; remove it, or use Optimize compensation with a COMP row.");
        if (s.MultiConfiguration?.Operands is { } rows && rows.Any(op => op is not null && op.Number1 == j && string.Equals(op.Type, "THIC", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{who}: a multi-configuration THIC row sets surface {j}'s thickness, which would overwrite the focus shift.");
        surfaces[j] = last with { Thickness = last.Thickness + shift };
        return s with { Surfaces = surfaces };
    }

    // ------------------------------------------------------------------ the plan

    private sealed record RowPlan(int Index, string Code, ToleranceDistribution Distribution, int Seed, bool Radial, bool Tirr, bool Element);

    private sealed class Plan
    {
        private readonly ToleranceTable _table;
        private readonly IReadOnlyList<OpticalCatalog> _catalogs;
        private readonly ToleranceSetup _setup;
        private readonly CompensatedLens _start;
        private readonly TolerancePerturber _perturber;
        private readonly RowPlan[] _rows;
        private readonly List<string> _notes;
        private readonly double _n;
        private readonly int[] _overlayRows = Array.Empty<int>();
        private readonly MtfMethodSettings? _mtf;
        private readonly double[] _frequencies = Array.Empty<double>();

        public Plan(OpticsSettings nominal, ToleranceTable table, IReadOnlyList<OpticalCatalog> catalogs, ToleranceRunOptions run, MonteCarloOptions options)
        {
            ArgumentNullException.ThrowIfNull(nominal);
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(catalogs);
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(options);
            // The Monte Carlo run uses the sensitivity set-up; the mode's own settings do not apply.
            Run = run with { Mode = ToleranceMode.Sensitivity };
            Options = options;
            _table = table;
            _catalogs = catalogs;
            Check(options);
            Seed = options.Seed != 0 ? options.Seed : ClockSeed();
            _setup = ToleranceAnalysis.Prepare(nominal, table, catalogs, Run);
            _notes = new List<string>(_setup.Notes);
            if (options.Seed == 0) _notes.Add($"The seed was drawn from the clock: {Seed}. Run again with this seed to repeat the trials.");

            var rows = new List<RowPlan>();
            var udd = new Dictionary<int, UserDefinedDistribution>();
            foreach (int i in _setup.Rows)
            {
                var row = table.Rows[i];
                var d = ToleranceOperandRegistry.Get(row.Code);
                if (d.NotAppliedReason is { } why)
                {
                    _notes.Add($"Row {i + 1} ({d.Code}) is left out of every trial: it cannot be applied in this version ({why})");
                    continue;
                }
                var context = table.ContextAt(i);
                int seed = context.Seed is int s && s != 0 ? s : Seed;
                rows.Add(new RowPlan(i, d.Code, Distribution(i, row, d, context, udd), seed, d.IsRadial, d.Code == "TIRR", d.IsElement));
            }
            _rows = rows.ToArray();
            foreach (var u in udd.Values) _notes.AddRange(u.Notes);

            var token = Run.CancellationToken;
            token.ThrowIfCancellationRequested();
            _start = _setup.Compensator.Nominal(token);
            _n = _start.Criterion;
            _notes.AddRange(_start.Notes);
            _perturber = new TolerancePerturber(_start.Settings, catalogs);

            if (options.Overlay is { } ov)
            {
                var system = _setup.System;
                if (ov.Field < 1 || ov.Field > system.Fields.Points.Count)
                    throw new ArgumentException($"MTF overlay: field {ov.Field} is not a field of the system (1 to {system.Fields.Points.Count}).");
                if (!(ov.MaxFrequency > 0) || !double.IsFinite(ov.MaxFrequency))
                    throw new ArgumentException($"MTF overlay: the largest frequency must be above zero; it is {ov.MaxFrequency}.");
                if (ov.Points < 2 || ov.Points > 1000) throw new ArgumentException($"MTF overlay: 2 to 1000 frequencies, not {ov.Points}.");
                if (ov.Trials < 0) throw new ArgumentException("MTF overlay: the number of trials drawn cannot be negative.");
                if (Run.PsfSampling < 16 || Run.PsfSampling > 1024)
                    throw new ArgumentException($"MTF overlay: the pupil grid must be 16 to 1024, not {Run.PsfSampling}.");
                MtfUnitsCheck.Check(system, "MTF overlay");
                MtfUnit = MtfUnitsCheck.Label(system);
                _overlayRows = Enumerable.Range(0, system.Wavelengths.Wavelengths.Count).ToArray();
                _mtf = new MtfOptions { Method = MtfMethod.Fft, Sampling = Run.PsfSampling }.ToMethod();
                _frequencies = MtfCore.Frequencies(ov.Points, ov.MaxFrequency);
                NominalMtf = Mtf(_start, out string? failure);
                if (failure is not null) _notes.Add($"MTF overlay: the nominal lens's MTF could not be computed: {failure}");
            }
        }

        public ToleranceRunOptions Run { get; }
        public MonteCarloOptions Options { get; }
        public int Seed { get; }
        public int Configurations => _setup.Configurations;
        public MonteCarloMtfCurve? NominalMtf { get; }
        public string MtfUnit { get; } = "cycles/mm";

        private static int ClockSeed()
        {
            int s = unchecked((int)DateTime.UtcNow.Ticks);
            return s == 0 ? 1 : s;
        }

        private static void Check(MonteCarloOptions o)
        {
            if (o.Trials < 1) throw new ArgumentException($"Monte Carlo: at least one trial, not {o.Trials}.");
            if (o.Statistics is not (ToleranceStatistics.Normal or ToleranceStatistics.Uniform or ToleranceStatistics.Parabolic))
                throw new ArgumentException("Monte Carlo: the default statistics are normal, uniform or parabolic (help, \"Tolerancing\", \"Statistics\"); a user defined distribution is chosen by a STAT row.");
            if (o.Statistics == ToleranceStatistics.Normal && (o.StandardDeviations < 1 || o.StandardDeviations > 10))
                throw new ArgumentException($"Monte Carlo: the normal distribution needs 1 to 10 standard deviations to the extremes (help, \"STAT\"), not {o.StandardDeviations}.");
            if (o.YieldLimit is double y && !double.IsFinite(y)) throw new ArgumentException("Monte Carlo: the yield limit must be a finite number.");
            if (o.Percentiles.Any(p => !(p >= 0 && p <= 100))) throw new ArgumentException("Monte Carlo: percentiles must lie from 0 to 100.");
            if (o.HistogramBins < 1) throw new ArgumentException($"Monte Carlo: the histogram needs at least one bin, not {o.HistogramBins}.");
        }

        private ToleranceDistribution Distribution(int i, ToleranceOperandRow row, ToleranceOperandDefinition definition, ToleranceRowContext context,
            Dictionary<int, UserDefinedDistribution> udd)
        {
            string who = $"Tolerance row {i + 1} ({definition.Code})";
            try
            {
                if (context.StatisticsRow is not int statRow)
                    return Make(Options.Statistics, Options.StandardDeviations, null);
                int n = context.StandardDeviations ?? 0;
                if (context.Statistics == ToleranceStatistics.Normal && (n < 1 || n > 10))
                    throw new ArgumentException($"STAT row {statRow} sets {n} standard deviations; the normal distribution needs 1 to 10 (help, \"STAT\": \"the number of standard deviations between the mean and the tolerance extremes ... capped at 10\").");
                UserDefinedDistribution? table = null;
                if (context.Statistics == ToleranceStatistics.UserDefined && !udd.TryGetValue(statRow, out table))
                {
                    table = ReadTable(statRow);
                    udd[statRow] = table;
                }
                return Make(context.Statistics, n, table);
            }
            catch (ArgumentException e) { throw new ArgumentException($"{who}: {e.Message}", e); }

            ToleranceDistribution Make(ToleranceStatistics s, int n, UserDefinedDistribution? t)
            {
                bool centred = definition.IsRadial && (row.Min == 0 || row.Max == 0);
                return s switch
                {
                    ToleranceStatistics.Normal => ToleranceDistribution.Normal(row.Min, row.Max, n, centred),
                    ToleranceStatistics.Uniform => ToleranceDistribution.Uniform(row.Min, row.Max),
                    ToleranceStatistics.Parabolic => ToleranceDistribution.Parabolic(row.Min, row.Max, centred),
                    _ => ToleranceDistribution.UserDefined(row.Min, row.Max, t!),
                };
            }
        }

        private UserDefinedDistribution ReadTable(int statRow)
        {
            string name = _table.Rows[statRow - 1].Comment.Trim();
            if (name.Length == 0)
                throw new ArgumentException($"STAT row {statRow} chooses a user defined distribution, but its comment names no .UDD file (help: \"the file name (with extension) must be placed in the comments column ... on the same line as the STAT command\").");
            string text;
            try
            {
                if (Options.ReadDistribution is { } read) text = read(name);
                else if (Options.DistributionFolder is { Length: > 0 } folder) text = File.ReadAllText(Path.Combine(folder, name));
                else throw new ArgumentException($"STAT row {statRow} names the distribution file '{name}', but no distribution folder is set; set the folder that holds it.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new ArgumentException($"STAT row {statRow}: the distribution file '{name}' cannot be read: {e.Message}", e);
            }
            return UserDefinedDistribution.Parse(text, $"STAT row {statRow}, file '{name}'");
        }

        /// <summary>Trial k: the draws, the perturbed and compensated lens, the criterion.</summary>
        public MonteCarloTrial Trial(int k, out CompensatedLens? lens)
        {
            var token = Run.CancellationToken;
            token.ThrowIfCancellationRequested();
            lens = null;
            var streams = new Dictionary<int, SplitMix64>();
            var draws = new MonteCarloDraw[_rows.Length];
            for (int r = 0; r < _rows.Length; r++)
            {
                var p = _rows[r];
                if (!streams.TryGetValue(p.Seed, out var g)) streams[p.Seed] = g = SplitMix64.ForStream(p.Seed, k);
                double value = p.Distribution.Draw(g);
                double? direction = p.Radial ? 360 * g.NextDouble() : null;
                double? angle = p.Tirr ? 360 * g.NextDouble() : null;
                draws[r] = new MonteCarloDraw(p.Index + 1, p.Code, value, direction, angle);
            }
            var trial = new MonteCarloTrial { Index = k, Draws = draws };

            var current = _start.Settings;
            Func<int, int> map = n => n;
            try
            {
                foreach (var d in draws)
                {
                    var before = map;
                    var change = _perturber.Apply(current, _table, d.Row - 1, d.Value, new ToleranceApplyOptions
                    {
                        SurfaceMap = before, DirectionDegrees = d.DirectionDegrees ?? 90, AstigmatismAngleDegrees = d.AngleDegrees ?? 0,
                    });
                    current = change.Perturbed;
                    map = n => change.MapSurface(before(n));
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return trial with { Failure = e.Message };
            }

            CompensatedLens evaluated;
            try
            {
                evaluated = _setup.Compensator.Evaluate(current, map, token);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                var element = _rows.Where(r => r.Element).Select(r => $"{r.Index + 1} ({r.Code})").ToArray();
                string why = element.Length == 0 ? e.Message
                    : $"{e.Message} (The trial carries element tolerance row{(element.Length == 1 ? "" : "s")} {string.Join(", ", element)}, which make the lens non-axial; the ray launcher refuses such lenses, §15, OP-22.)";
                return trial with { Failure = why };
            }
            lens = evaluated;
            trial = trial with
            {
                Criterion = evaluated.Criterion,
                Change = ToleranceAnalysis.ChangeOf(evaluated.Criterion, _n, Run.Change),
                Compensators = evaluated.Compensators,
            };
            if (_mtf is not null && k <= Options.Overlay!.Trials)
            {
                var curve = Mtf(evaluated, out string? failure);
                trial = trial with { Mtf = curve, MtfFailure = failure };
            }
            return trial;
        }

        /// <summary>The MTF of a compensated lens at the overlay's field, every wavelength.</summary>
        private MonteCarloMtfCurve? Mtf(CompensatedLens c, out string? failure)
        {
            failure = null;
            try
            {
                var (lens, system) = ToleranceCriterionEvaluator.Build(c.Settings, _setup.Configuration, _catalogs);
                if (Run.Compensation == ToleranceCompensation.ParaxialFocus && c.Compensators.Count > 0)
                    lens = RmsCore.Focused(lens, OpticsConventions.ToMeters(c.Compensators[0], ProjectLens.LensUnitOf(c.Settings.SystemData)));
                var point = MtfCore.Evaluate(_mtf!, lens, system, Options.Overlay!.Field - 1, _overlayRows, _overlayRows.Length > 1, Run.CancellationToken);
                var t = _frequencies.Select(f => MtfCore.Value(point, f * MtfEngine.PerMtfUnit, MtfOrientation.Tangential, MtfDataType.Modulation)).ToArray();
                var s = _frequencies.Select(f => MtfCore.Value(point, f * MtfEngine.PerMtfUnit, MtfOrientation.Sagittal, MtfDataType.Modulation)).ToArray();
                return new MonteCarloMtfCurve(_frequencies, t, s);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                failure = e.Message;
                return null;
            }
        }

        public MonteCarloResult Result(MonteCarloTrial[] trials)
        {
            var info = _setup.Evaluator.Info;
            var ok = trials.Where(t => t.Succeeded).ToArray();
            MonteCarloStatistics? stats = null;
            var compStats = new List<MonteCarloStatistics>();
            int? best = null, worst = null;
            MonteCarloYield? yield = null;
            var notes = new List<string>(_notes);
            if (ok.Length > 0)
            {
                stats = MonteCarloStatistics.Of(ok.Select(t => t.Criterion).ToArray(), Options.Percentiles);
                for (int c = 0; c < _setup.Compensator.Labels.Count; c++)
                    compStats.Add(MonteCarloStatistics.Of(ok.Select(t => t.Compensators[c]).ToArray(), Options.Percentiles));
                var b = ok[0];
                var w = ok[0];
                foreach (var t in ok)
                {
                    bool better = info.HigherIsBetter ? t.Criterion > b.Criterion : t.Criterion < b.Criterion;
                    bool worse = info.HigherIsBetter ? t.Criterion < w.Criterion : t.Criterion > w.Criterion;
                    if (better) b = t;
                    if (worse) w = t;
                }
                best = b.Index;
                worst = w.Index;
            }
            else notes.Add("No trial could be evaluated, so there are no statistics; see each trial's failure.");
            if (Options.YieldLimit is double limit)
            {
                int passed = ok.Count(t => info.HigherIsBetter ? t.Criterion >= limit : t.Criterion <= limit);
                yield = new MonteCarloYield(limit, passed, ok.Length);
            }
            int failed = trials.Length - ok.Length;
            if (failed > 0) notes.Add($"{failed} of {trials.Length} trials failed and are left out of the statistics and the yield.");
            foreach (var t in trials.Where(t => t.MtfFailure is not null)) notes.Add($"MTF overlay, trial {t.Index}: {t.MtfFailure}");

            var assumptions = ToleranceAnalysis.Assumptions(Run, _setup.Evaluator, _setup.Compensator, _setup.NominalLens, _setup.System,
                    _setup.Configuration, _setup.Configurations)
                .Where(a => !a.StartsWith("Not built:", StringComparison.Ordinal) && !a.StartsWith("Radial operands", StringComparison.Ordinal))
                .ToList();
            assumptions.AddRange(MonteCarloAssumptions());

            return new MonteCarloResult
            {
                RunOptions = Run, Options = Options, Seed = Seed, Nominal = _n,
                Unit = _setup.Unit, HigherIsBetter = info.HigherIsBetter, Compensators = _setup.Compensator.Labels,
                Rows = _rows.Select(r => r.Index + 1).ToArray(), Distributions = _rows.Select(r => r.Distribution).ToArray(),
                Trials = trials, Statistics = stats, CompensatorStatistics = compStats, Yield = yield, Best = best, Worst = worst,
                NominalMtf = NominalMtf, MtfUnit = MtfUnit, Assumptions = assumptions, Notes = notes,
            };
        }

        private IEnumerable<string> MonteCarloAssumptions()
        {
            string Stat(ToleranceStatistics s) => s.ToString().ToLowerInvariant();
            yield return $"Rows no STAT row governs: {Stat(Options.Statistics)} statistics{(Options.Statistics == ToleranceStatistics.Normal ? $", {Options.StandardDeviations} standard deviations from the midpoint to the extremes, truncated there (help, \"Normal Statistical Distribution\")" : "")}.";
            yield return "Uniform: equal probability over [Min, Max]; parabolic: p(x) = 3x²/(2Δ³) about the midpoint (help); user defined: the .UDD table, linear between its points, over [Min, Max].";
            yield return "TEDR, TSDR, TARR and TRLR draw a direction uniform over 0–360°; with a zero limit their normal and parabolic draws are centred at 0 over ±max(|Min|, |Max|) (help). TIRR draws its astigmatism angle uniform over 0–360° (help, \"TIRR\").";
            yield return $"Random numbers: SplitMix64, one stream per SEED value and trial (the run's seed {Seed} for rows under no SEED or SEED 0); trial k depends only on the seeds and k.";
            yield return $"Statistics over the evaluated trials only: the sample standard deviation (n − 1); percentiles by linear interpolation between order statistics.";
            if (Options.Overlay is { } ov)
                yield return $"MTF overlay: OP-39's FFT MTF of the compensated lens at field {ov.Field}, every wavelength by its weight, {Run.PsfSampling} × {Run.PsfSampling} pupil grid, {ov.Points} frequencies to {StudyReport.Number(ov.MaxFrequency, "g6")} {MtfUnit}, the first {ov.Trials} trials.";
            yield return "Not built: TEZI's random per-term coefficients (each trial applies the drawn RMS equally over the terms, as the sensitivity analysis does), correlated draws, Save Best and Worst files by name, tolerance scripts (OP-67), the Tolerance Data Viewer and Yield curve (OP-66); element decenter and tilt trials fail while the ray launcher refuses non-axial lenses.";
        }
    }
}

/// <summary>The Monte Carlo options to and from the files' <see cref="OpticsToleranceMonteCarloSettings"/>.</summary>
public static class MonteCarloSettings
{
    /// <summary>The saved form; null when every option is the default. Callbacks are not saved. The overlay is on
    /// when <see cref="MonteCarloOptions.Overlay"/> is set; <paramref name="keptOverlay"/> keeps its settings while it is off.</summary>
    public static OpticsToleranceMonteCarloSettings? ToSettings(MonteCarloOptions o, MonteCarloMtfOverlay? keptOverlay = null)
    {
        ArgumentNullException.ThrowIfNull(o);
        var d = new MonteCarloOptions();
        var ov = o.Overlay ?? keptOverlay;
        var dov = new MonteCarloMtfOverlay();
        var s = new OpticsToleranceMonteCarloSettings
        {
            Trials = o.Trials == d.Trials ? null : o.Trials,
            Seed = o.Seed == d.Seed ? null : o.Seed,
            Statistics = o.Statistics == d.Statistics ? null : o.Statistics.ToString(),
            StandardDeviations = o.StandardDeviations == d.StandardDeviations ? null : o.StandardDeviations,
            YieldLimit = o.YieldLimit,
            Percentiles = o.Percentiles.SequenceEqual(d.Percentiles) ? null : o.Percentiles.ToList(),
            DistributionFolder = string.IsNullOrEmpty(o.DistributionFolder) ? null : o.DistributionFolder,
            OverlayMtf = o.Overlay is not null ? true : null,
            OverlayField = ov is null || ov.Field == dov.Field ? null : ov.Field,
            OverlayMaxFrequency = ov is null || ov.MaxFrequency == 0 ? null : ov.MaxFrequency,
            OverlayPoints = ov is null || ov.Points == dov.Points ? null : ov.Points,
            OverlayTrials = ov is null || ov.Trials == dov.Trials ? null : ov.Trials,
            HistogramBins = o.HistogramBins == d.HistogramBins ? null : o.HistogramBins,
        };
        return s == new OpticsToleranceMonteCarloSettings() ? null : s;
    }

    /// <summary>The options of saved settings; null is the defaults. An unknown statistics name keeps the
    /// default and is named in <paramref name="notes"/>. The overlay's settings are kept even when it is off
    /// (<paramref name="overlay"/> is false then and <see cref="MonteCarloOptions.Overlay"/> null).</summary>
    public static MonteCarloOptions FromSettings(OpticsToleranceMonteCarloSettings? saved, ICollection<string> notes,
        out MonteCarloMtfOverlay overlaySettings, out bool overlay)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var o = new MonteCarloOptions();
        overlaySettings = new MonteCarloMtfOverlay();
        overlay = false;
        if (saved is null) return o;
        var stat = o.Statistics;
        if (saved.Statistics is { } name)
        {
            if (Enum.TryParse<ToleranceStatistics>(name, true, out var v) && !int.TryParse(name, out _)
                && v is ToleranceStatistics.Normal or ToleranceStatistics.Uniform or ToleranceStatistics.Parabolic) stat = v;
            else notes.Add($"Monte Carlo: the statistics '{name}' are not normal, uniform or parabolic; '{stat}' is used.");
        }
        overlaySettings = new MonteCarloMtfOverlay
        {
            Field = saved.OverlayField ?? overlaySettings.Field,
            MaxFrequency = saved.OverlayMaxFrequency ?? 0,
            Points = saved.OverlayPoints ?? overlaySettings.Points,
            Trials = saved.OverlayTrials ?? overlaySettings.Trials,
        };
        overlay = saved.OverlayMtf == true;
        return o with
        {
            Trials = saved.Trials ?? o.Trials,
            Seed = saved.Seed ?? o.Seed,
            Statistics = stat,
            StandardDeviations = saved.StandardDeviations ?? o.StandardDeviations,
            YieldLimit = saved.YieldLimit,
            Percentiles = saved.Percentiles?.ToArray() ?? o.Percentiles,
            DistributionFolder = saved.DistributionFolder,
            Overlay = overlay ? overlaySettings : null,
            HistogramBins = saved.HistogramBins ?? o.HistogramBins,
        };
    }

    internal static string F(double v) => v.ToString("G8", CultureInfo.InvariantCulture);
}
