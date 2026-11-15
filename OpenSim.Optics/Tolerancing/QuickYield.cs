using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Tolerancing;

// OP-66: Quick Yield (docs/Optics-Conventions.md §51). Source: OpticStudio 2025 R2 help, "Quick Yield"
// (plans/sources/optics/op-66-help/Quick_Yield.html): "Computes the predicted Monte Carlo yield of the
// system using a faster, approximate calculation ... This analysis uses sensitivity results to fit the
// wavefront error. The fit is extrapolated using a modified Rimmer's approach and based on this fit the
// Monte Carlo outcome can be reconstructed. The exact methodology in reconstructing the Monte Carlo
// outcome is confidential and proprietary to ANSYS, Inc." OpticStudio's method is therefore not
// reproduced. What is built is the textbook second-order stand-in the plan names: a quadratic response
// model of the criterion in the tolerances (sensitivities, curvatures and pairwise cross terms, all
// from compensated evaluations of the lens) sampled with the Monte Carlo distributions.

/// <summary>Progress of a Quick Yield run: lens evaluations done of the total.</summary>
public sealed record QuickYieldProgress(int EvaluationsDone, int EvaluationCount);

/// <summary>Quick Yield's own settings; the criterion, compensators, fields and sampling are
/// <see cref="ToleranceRunOptions"/>'s and the statistics, seed, yield limit, percentiles and histogram bins
/// <see cref="MonteCarloOptions"/>' (its trial count is not used).</summary>
public sealed record QuickYieldOptions
{
    /// <summary>Samples of the quadratic model (each costs no lens evaluation). The default 10 000 is this
    /// program's: the binomial standard error of a yield near 50 % is then 0.5 %.</summary>
    public int Samples { get; init; } = 10000;

    /// <summary>Fit the pairwise cross terms (one more lens evaluation per pair of rows); false keeps the
    /// model separable (two evaluations per row).</summary>
    public bool CrossTerms { get; init; } = true;

    /// <summary>Called after every lens evaluation, on a worker thread.</summary>
    public Action<QuickYieldProgress>? Progress { get; init; }
}

/// <summary>
/// The second-order response model of the criterion in the tolerance values x (one per row):
/// f(x) = N + Σᵢ gᵢxᵢ + ½ Σᵢ hᵢxᵢ² + Σᵢ&lt;ⱼ Hᵢⱼ xᵢxⱼ.
/// <para>
/// <b>Fit</b> (derived). Each row i is evaluated alone at two non-zero points aᵢ ≠ bᵢ: with
/// dₐ = (f(aᵢ) − N)/aᵢ and d_b = (f(bᵢ) − N)/bᵢ, the parabola through (0, N), (aᵢ, f(aᵢ)), (bᵢ, f(bᵢ)) has
/// hᵢ = 2(d_b − dₐ)/(bᵢ − aᵢ) and gᵢ = dₐ − ½hᵢaᵢ. Each pair is evaluated together at (bᵢ, bⱼ); since the
/// separable part reproduces f(bᵢ) and f(bⱼ) exactly, Hᵢⱼ = (f(bᵢ, bⱼ) − f(bᵢ) − f(bⱼ) + N)/(bᵢbⱼ). The model
/// is exact for a criterion quadratic in the tolerances. A row whose points are both zero (a zero
/// tolerance) has no terms.
/// </para>
/// </summary>
public sealed record ToleranceQuadraticModel
{
    public required double Nominal { get; init; }
    public required IReadOnlyList<double> A { get; init; }
    public required IReadOnlyList<double> B { get; init; }
    public required IReadOnlyList<double> Gradient { get; init; }

    /// <summary>The second derivatives hᵢ.</summary>
    public required IReadOnlyList<double> Curvature { get; init; }

    /// <summary>The cross terms Hᵢⱼ (symmetric, zero diagonal); all zero without cross terms.</summary>
    public required double[,] Cross { get; init; }

    public int Count => Gradient.Count;

    /// <summary>The model at the tolerance values <paramref name="x"/> (one per row).</summary>
    public double Evaluate(IReadOnlyList<double> x)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (x.Count != Count) throw new ArgumentException($"The model has {Count} rows; {x.Count} values were given.");
        double f = Nominal;
        for (int i = 0; i < Count; i++)
        {
            double xi = x[i];
            if (xi == 0) continue;
            f += Gradient[i] * xi + 0.5 * Curvature[i] * xi * xi;
            for (int j = i + 1; j < Count; j++) f += Cross[i, j] * xi * x[j];
        }
        return f;
    }

    /// <summary>Fits the model (see the remarks) from the nominal, each row's points and values, and the pair values
    /// <paramref name="fb2"/>[i, j] = f(bᵢ, bⱼ) for i &lt; j (null: no cross terms).</summary>
    public static ToleranceQuadraticModel Fit(double nominal, IReadOnlyList<double> a, IReadOnlyList<double> b,
        IReadOnlyList<double> fa, IReadOnlyList<double> fb, double[,]? fb2)
    {
        int n = a.Count;
        if (b.Count != n || fa.Count != n || fb.Count != n) throw new ArgumentException("Quadratic model: one point and one value per row.");
        var g = new double[n];
        var h = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (a[i] == 0 && b[i] == 0) continue;
            if (a[i] == 0 || b[i] == 0 || a[i] == b[i])
                throw new ArgumentException($"Quadratic model: row {i + 1} needs two different non-zero points, not {a[i]} and {b[i]}.");
            double da = (fa[i] - nominal) / a[i], db = (fb[i] - nominal) / b[i];
            h[i] = 2 * (db - da) / (b[i] - a[i]);
            g[i] = da - 0.5 * h[i] * a[i];
        }
        var cross = new double[n, n];
        if (fb2 is not null)
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (b[i] == 0 || b[j] == 0) continue;
                    double v = (fb2[i, j] - fb[i] - fb[j] + nominal) / (b[i] * b[j]);
                    cross[i, j] = v;
                    cross[j, i] = v;
                }
        return new ToleranceQuadraticModel { Nominal = nominal, A = a.ToArray(), B = b.ToArray(), Gradient = g, Curvature = h, Cross = cross };
    }
}

/// <summary>The outcome of <see cref="QuickYield.Run"/>.</summary>
public sealed record QuickYieldResult
{
    public required ToleranceRunOptions RunOptions { get; init; }
    public required MonteCarloOptions MonteCarloOptions { get; init; }
    public required QuickYieldOptions Options { get; init; }

    /// <summary>The seed used (the clock's when <see cref="MonteCarloOptions.Seed"/> was 0).</summary>
    public required int Seed { get; init; }

    public required double Nominal { get; init; }
    public required string Unit { get; init; }
    public required bool HigherIsBetter { get; init; }

    /// <summary>The 1-based tolerance rows in the model, their codes and their distributions.</summary>
    public required IReadOnlyList<int> Rows { get; init; }
    public required IReadOnlyList<string> Codes { get; init; }
    public required IReadOnlyList<ToleranceDistribution> Distributions { get; init; }

    /// <summary>The fitted model; null when an evaluation failed (<see cref="Failures"/>).</summary>
    public ToleranceQuadraticModel? Model { get; init; }

    /// <summary>The lens evaluations made, the compensated nominal included.</summary>
    public required int Evaluations { get; init; }

    /// <summary>The model's value at each sample (sample k = Monte Carlo trial k's draws); empty without a model.</summary>
    public required IReadOnlyList<double> Samples { get; init; }

    public MonteCarloStatistics? Statistics { get; init; }

    /// <summary>The predicted yield over the samples; null without a yield limit or a model.</summary>
    public MonteCarloYield? Yield { get; init; }

    /// <summary>Evaluations that failed, each naming the rows and values; no estimate is given then.</summary>
    public required IReadOnlyList<string> Failures { get; init; }

    public required IReadOnlyList<string> Assumptions { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public bool Succeeded => Model is not null;

    /// <summary>The binomial standard error √(p(1 − p)/M) of the predicted yield over the M samples (the
    /// sampling error of the model's yield, not the model's error).</summary>
    public double? YieldStandardError => Yield is { Evaluated: > 0 } y ? Math.Sqrt(y.Fraction * (1 - y.Fraction) / y.Evaluated) : null;

    /// <summary>The histogram of the model's samples, with a Monte Carlo run's criterion on the same bins when given.</summary>
    public LinePlot? HistogramPlot(int? bins = null, double? min = null, double? max = null, MonteCarloResult? monteCarlo = null)
    {
        if (Samples.Count == 0) return null;
        var mc = monteCarlo?.Trials.Where(t => t.Succeeded).Select(t => t.Criterion).ToArray() ?? Array.Empty<double>();
        double lo = min ?? Samples.Concat(mc).Min(), hi = max ?? Samples.Concat(mc).Max();
        int b = bins ?? MonteCarloOptions.HistogramBins;
        var quick = ToleranceHistograms.Of(Samples, b, lo, hi)!;
        var list = new List<(string, MonteCarloHistogram)> { ($"Quick Yield, {Samples.Count} samples", quick) };
        if (mc.Length > 0) list.Add(($"Monte Carlo, {mc.Length} trials", ToleranceHistograms.Of(mc, b, lo, hi)!));
        string name = ToleranceCriteria.Info(RunOptions.Criterion).Name;
        return ToleranceHistograms.Plot(quick, $"Quick Yield: {name}", Unit.Length == 0 ? name : $"{name} ({Unit})", list);
    }

    /// <summary>The predicted yield curve (the samples' cumulative distribution; at or above for a larger-is-better
    /// criterion), with a Monte Carlo run's curve when given.</summary>
    public LinePlot? YieldPlot(MonteCarloResult? monteCarlo = null)
    {
        if (Samples.Count == 0) return null;
        var curves = new List<AnalysisCurve> { ToleranceHistograms.Cumulative($"Quick Yield, {Samples.Count} samples", Samples, HigherIsBetter) };
        var mc = monteCarlo?.Trials.Where(t => t.Succeeded).Select(t => t.Criterion).ToArray();
        if (mc is { Length: > 0 }) curves.Add(ToleranceHistograms.Cumulative($"Monte Carlo, {mc.Length} trials", mc, HigherIsBetter) with { Dashed = true });
        string name = ToleranceCriteria.Info(RunOptions.Criterion).Name;
        return new LinePlot($"Quick Yield: {name}", Unit.Length == 0 ? name : $"{name} ({Unit})",
            HigherIsBetter ? "Predicted at or above (%)" : "Predicted at or below (%)", curves);
    }

    /// <summary>The report, one line each.</summary>
    public IReadOnlyList<string> ReportLines()
    {
        string V(double v) => ToleranceHistograms.N(v);
        string u = Unit.Length == 0 ? "" : " " + Unit;
        var info = ToleranceCriteria.Info(RunOptions.Criterion);
        var lines = new List<string>
        {
            $"Quick Yield (quadratic response model, sampled): {Rows.Count} rows, {Evaluations} lens evaluations, {Samples.Count} samples, seed {Seed}. Criterion: {info.Name}{(Unit.Length == 0 ? "" : $" ({Unit})")}. Compensation: {RunOptions.Compensation}.",
            $"Nominal criterion: {V(Nominal)}{u}.",
        };
        if (Model is { } m)
        {
            lines.Add("");
            lines.Add("Row  Type  Points  Gradient  Curvature");
            for (int i = 0; i < Rows.Count; i++)
                lines.Add($"{Rows[i]}  {Codes[i]}  {V(m.A[i])}, {V(m.B[i])}  {V(m.Gradient[i])}  {V(m.Curvature[i])}");
            if (Options.CrossTerms && Rows.Count > 1)
            {
                var largest = Enumerable.Range(0, Rows.Count).SelectMany(i => Enumerable.Range(i + 1, Rows.Count - i - 1).Select(j => (i, j)))
                    .OrderByDescending(p => Math.Abs(m.Cross[p.i, p.j] * m.B[p.i] * m.B[p.j])).Take(5).ToArray();
                lines.Add("Largest cross terms (Hᵢⱼ·bᵢ·bⱼ, criterion units):");
                foreach (var (i, j) in largest) lines.Add($"  rows {Rows[i]} and {Rows[j]}: {V(m.Cross[i, j] * m.B[i] * m.B[j])}");
            }
        }
        lines.Add("");
        if (Statistics is { } s)
        {
            lines.Add($"Predicted mean {V(s.Mean)}{u}, standard deviation {V(s.StandardDeviation)}{u}, min {V(s.Min)}{u}, max {V(s.Max)}{u}.");
            foreach (var p in s.Percentiles) lines.Add($"  {StudyReport.Number(p.Percent, "g4")} % of samples at or below {V(p.Value)}{u}.");
        }
        if (Yield is { } y)
            lines.Add($"Predicted yield: {StudyReport.Number(100 * y.Fraction, "g4")} % ± {StudyReport.Number(100 * YieldStandardError!.Value, "g2")} % (one binomial standard error of the samples) with the criterion {(HigherIsBetter ? "at or above" : "at or below")} {V(y.Limit)}{u}.");
        else if (Model is not null) lines.Add("Predicted yield: no yield limit is set.");
        if (Failures.Count > 0) { lines.Add(""); lines.Add("Failed evaluations (no estimate is given):"); lines.AddRange(Failures.Select(f => "  " + f)); }
        if (Notes.Count > 0) { lines.Add(""); lines.Add("Notes:"); lines.AddRange(Notes.Select(n => "  " + n)); }
        lines.Add("");
        lines.Add("Assumptions:");
        lines.AddRange(Assumptions.Select(a => "  " + a));
        return lines;
    }
}

/// <summary>
/// Quick Yield (plan item OP-66; help, "Quick Yield"): a predicted Monte Carlo yield without a full Monte
/// Carlo run.
/// <para>
/// <b>What is built.</b> OpticStudio's reconstruction is proprietary (help); this is the textbook second-order
/// stand-in. (1) The set-up, the compensated nominal N, the criterion and the compensators are OP-64's and
/// OP-65's (<c>ToleranceAnalysis.Prepare</c>). (2) Each tolerance row with a non-zero range is applied alone
/// at the two ends of its Monte Carlo range, Mid ± Δ (a zero end is replaced by half the other end; a range of
/// one value v by v and v/2), and, with cross terms, each pair together at their upper points; every such
/// lens is compensated and evaluated exactly as a Monte Carlo trial is. (3) The quadratic
/// <see cref="ToleranceQuadraticModel"/> is fitted through those values. (4) The model is sampled with the
/// Monte Carlo distributions and random streams of OP-65: sample k draws exactly what Monte Carlo trial k
/// draws (same seeds, same streams, same order), so the two compare trial by trial. The yield, statistics
/// and histogram are those of the samples. Cost: 1 + 2n + n(n − 1)/2 evaluations for n rows (1 + 2n without
/// cross terms), against one evaluation per Monte Carlo trial.
/// </para>
/// <para>
/// <b>Limits.</b> The model is exact for a criterion quadratic in the tolerances and wrong where the response
/// is strongly non-linear (the help: "limited in cases where the perturbation effects a highly non-linear"
/// response, "for example ... significant beam clipping"), and where compensators reach their bounds (help).
/// Radial operands (TSDR, TEDR, TARR, TRLR) and TIRR are modelled along +y, as the sensitivity analysis
/// applies them; their drawn directions and angles are drawn (to keep the streams aligned) but not used, so
/// cross terms between two of them are those of a common direction. The help's Precision levels and its own
/// field, sampling and compensation controls are not built; the tolerance run's settings are used.
/// </para>
/// <para>
/// Evaluations run in parallel, one slot each on one thread; the samples are drawn in order on one thread. The
/// result is bitwise identical at any thread count.
/// </para>
/// </summary>
public static class QuickYield
{
    /// <exception cref="ArgumentException">Invalid options, a table that does not fit the lens, a STAT row that cannot
    /// be read, a compensator that cannot be varied.</exception>
    /// <exception cref="ToleranceEvaluationException">The nominal criterion cannot be evaluated.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static QuickYieldResult Run(OpticsSettings nominal, ToleranceTable table, IReadOnlyList<OpticalCatalog> catalogs,
        ToleranceRunOptions? run = null, MonteCarloOptions? monteCarlo = null, QuickYieldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nominal);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(catalogs);
        var r = (run ?? new ToleranceRunOptions()) with { Mode = ToleranceMode.Sensitivity };
        var mc = monteCarlo ?? new MonteCarloOptions();
        var q = options ?? new QuickYieldOptions();
        if (q.Samples < 1) throw new ArgumentException($"Quick Yield: at least one sample, not {q.Samples}.");
        // OP-67: a script's PERTURB and per-trial files have no place in a fitted response model.
        if (r.Criterion == ToleranceCriterion.UserScript)
            throw new ArgumentException("Quick Yield fits a response model to the criterion over a few lens evaluations; the user script criterion is not offered there. Run Monte Carlo with the script instead.");
        CheckMonteCarlo(mc);
        var token = r.CancellationToken;
        int seed = mc.Seed != 0 ? mc.Seed : ClockSeed();

        var setup = ToleranceAnalysis.Prepare(nominal, table, catalogs, r);
        var notes = new List<string>(setup.Notes);
        if (mc.Seed == 0) notes.Add($"The seed was drawn from the clock: {seed}. Run again with this seed to repeat the samples.");

        // The rows and their distributions, as the Monte Carlo run builds them.
        var plans = new List<(int Index, string Code, ToleranceDistribution D, int Seed, bool Radial, bool Tirr)>();
        var udd = new Dictionary<int, UserDefinedDistribution>();
        foreach (int i in setup.Rows)
        {
            var row = table.Rows[i];
            var d = ToleranceOperandRegistry.Get(row.Code);
            if (d.NotAppliedReason is { } why)
            {
                notes.Add($"Row {i + 1} ({d.Code}) is left out: it cannot be applied in this version ({why})");
                continue;
            }
            var context = table.ContextAt(i);
            int s = context.Seed is int v && v != 0 ? v : seed;
            plans.Add((i, d.Code, MonteCarloAnalysis.RowDistribution(table, i, row, d, context, mc, udd), s, d.IsRadial, d.Code == "TIRR"));
        }
        foreach (var u in udd.Values) notes.AddRange(u.Notes);
        int n = plans.Count;

        token.ThrowIfCancellationRequested();
        var start = setup.Compensator.Nominal(token);
        double nominalCriterion = start.Criterion;
        notes.AddRange(start.Notes);
        var perturber = new TolerancePerturber(start.Settings, catalogs);

        // The points of each row.
        var a = new double[n];
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            var d = plans[i].D;
            double lo = d.Mid - d.HalfWidth, hi = d.Mid + d.HalfWidth;
            if (lo == hi) { lo = hi == 0 ? 0 : hi; hi = hi == 0 ? 0 : hi / 2; }
            else if (lo == 0) lo = hi / 2;
            else if (hi == 0) hi = lo / 2;
            a[i] = lo;
            b[i] = hi;
            if (lo == 0 && hi == 0) notes.Add($"Row {plans[i].Index + 1} ({plans[i].Code}) has a zero range: it has no terms in the model.");
        }

        // The evaluations: each row at a and b, each pair at (b, b).
        var jobs = new List<(int[] Rows, double[] Values)>();
        for (int i = 0; i < n; i++)
        {
            if (a[i] == 0 && b[i] == 0) continue;
            jobs.Add((new[] { i }, new[] { a[i] }));
            jobs.Add((new[] { i }, new[] { b[i] }));
        }
        if (q.CrossTerms)
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (b[i] != 0 && b[j] != 0) jobs.Add((new[] { i, j }, new[] { b[i], b[j] }));

        int done = 0;
        var results = ParallelSlots.Compute(jobs.Count, r.MaxDegreeOfParallelism, k =>
        {
            var (rows, values) = jobs[k];
            var value = Evaluate(rows, values);
            int c = Interlocked.Increment(ref done);
            q.Progress?.Invoke(new QuickYieldProgress(c, jobs.Count));
            return value;
        }, token);

        (double Criterion, string? Failure) Evaluate(int[] rows, double[] values)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // TC-15: with the aim twin when a row decenters or tilts.
                var steps = Enumerable.Range(0, rows.Length).Select(m => new ToleranceStep(plans[rows[m]].Index, values[m])).ToArray();
                var trial = ToleranceAim.Apply(perturber, start.Settings, table, steps);
                return (setup.Compensator.Evaluate(trial.Perturbed, trial.Map, token, trial.Twin).Criterion, null);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return (double.NaN, e.Message);
            }
        }

        var failures = new List<string>();
        var fa = new double[n];
        var fb = new double[n];
        var fb2 = q.CrossTerms ? new double[n, n] : null;
        for (int k = 0; k < jobs.Count; k++)
        {
            var (rows, values) = jobs[k];
            var (c, failure) = results[k];
            if (failure is not null)
            {
                failures.Add(string.Join(" with ", rows.Select((x, m) => $"row {plans[x].Index + 1} ({plans[x].Code}) at {F(values[m])}")) + ": " + failure);
                continue;
            }
            if (rows.Length == 2) fb2![rows[0], rows[1]] = c;
            else if (values[0] == a[rows[0]]) fa[rows[0]] = c;
            else fb[rows[0]] = c;
        }

        var info = setup.Evaluator.Info;
        ToleranceQuadraticModel? model = null;
        var samples = Array.Empty<double>();
        MonteCarloStatistics? stats = null;
        MonteCarloYield? yield = null;
        if (failures.Count == 0)
        {
            model = ToleranceQuadraticModel.Fit(nominalCriterion, a, b, fa, fb, fb2);
            samples = new double[q.Samples];
            var x = new double[n];
            for (int k = 1; k <= q.Samples; k++)
            {
                if ((k & 1023) == 0) token.ThrowIfCancellationRequested();
                // The draws of Monte Carlo trial k (MonteCarloAnalysis: streams per SEED value and trial, rows in order,
                // a radial operand's direction and TIRR's angle drawn after its value).
                var streams = new Dictionary<int, SplitMix64>();
                for (int i = 0; i < n; i++)
                {
                    var p = plans[i];
                    if (!streams.TryGetValue(p.Seed, out var g)) streams[p.Seed] = g = SplitMix64.ForStream(p.Seed, k);
                    x[i] = p.D.Draw(g);
                    if (p.Radial) g.NextDouble();
                    if (p.Tirr) g.NextDouble();
                }
                samples[k - 1] = model.Evaluate(x);
            }
            stats = MonteCarloStatistics.Of(samples, mc.Percentiles);
            if (mc.YieldLimit is double limit)
                yield = new MonteCarloYield(limit, samples.Count(v => info.HigherIsBetter ? v >= limit : v <= limit), samples.Length);
        }
        else notes.Add($"{failures.Count} evaluation{(failures.Count == 1 ? "" : "s")} failed, so the model and the predicted yield are not given; tighten or remove those rows.");

        var assumptions = ToleranceAnalysis.Assumptions(r, setup.Evaluator, setup.Compensator, setup.NominalLens, setup.System,
                setup.Configuration, setup.Configurations)
            .Where(x => !x.StartsWith("Not built:", StringComparison.Ordinal) && !x.StartsWith("Radial operands", StringComparison.Ordinal))
            .ToList();
        assumptions.Add("Quick Yield is this program's second-order stand-in, not OpticStudio's method (help: \"The exact methodology in reconstructing the Monte Carlo outcome is confidential and proprietary to ANSYS, Inc.\").");
        assumptions.Add("Model: f(x) = N + Σ gᵢxᵢ + ½ Σ hᵢxᵢ² + Σᵢ<ⱼ Hᵢⱼxᵢxⱼ, through each row's compensated evaluations at the two ends of its Monte Carlo range (a zero end replaced by half the other)"
            + (q.CrossTerms ? " and each pair's at their upper points." : "; cross terms left out (Hᵢⱼ = 0)."));
        assumptions.Add($"Sampling: {q.Samples} samples; sample k draws what Monte Carlo trial k draws (OP-65's distributions and streams, seed {seed}).");
        assumptions.Add("Radial operands and TIRR are modelled along +y (their drawn directions and angles are not used); the model is wrong where the response is strongly non-linear or a compensator reaches its bounds (help).");
        assumptions.Add("Not built: OpticStudio's Precision levels and the dialog's own wavelength, field, sampling and compensation controls (the tolerance run's are used).");

        return new QuickYieldResult
        {
            RunOptions = r, MonteCarloOptions = mc, Options = q, Seed = seed, Nominal = nominalCriterion, Unit = setup.Unit,
            HigherIsBetter = info.HigherIsBetter, Rows = plans.Select(p => p.Index + 1).ToArray(), Codes = plans.Select(p => p.Code).ToArray(),
            Distributions = plans.Select(p => p.D).ToArray(), Model = model, Evaluations = jobs.Count + 1, Samples = samples,
            Statistics = stats, Yield = yield, Failures = failures, Assumptions = assumptions, Notes = notes,
        };
    }

    private static int ClockSeed()
    {
        int s = unchecked((int)DateTime.UtcNow.Ticks);
        return s == 0 ? 1 : s;
    }

    private static void CheckMonteCarlo(MonteCarloOptions o)
    {
        if (o.Statistics is not (ToleranceStatistics.Normal or ToleranceStatistics.Uniform or ToleranceStatistics.Parabolic))
            throw new ArgumentException("Quick Yield: the default statistics are normal, uniform or parabolic; a user defined distribution is chosen by a STAT row.");
        if (o.Statistics == ToleranceStatistics.Normal && (o.StandardDeviations < 1 || o.StandardDeviations > 10))
            throw new ArgumentException($"Quick Yield: the normal distribution needs 1 to 10 standard deviations to the extremes (help, \"STAT\"), not {o.StandardDeviations}.");
        if (o.YieldLimit is double y && !double.IsFinite(y)) throw new ArgumentException("Quick Yield: the yield limit must be a finite number.");
        if (o.Percentiles.Any(p => !(p >= 0 && p <= 100))) throw new ArgumentException("Quick Yield: percentiles must lie from 0 to 100.");
        if (o.HistogramBins < 1) throw new ArgumentException($"Quick Yield: the histogram needs at least one bin, not {o.HistogramBins}.");
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>Quick Yield's options to and from the files' <see cref="OpticsToleranceQuickYieldSettings"/>.</summary>
public static class QuickYieldSettings
{
    /// <summary>The saved form; null when every option is the default. The progress callback is not saved.</summary>
    public static OpticsToleranceQuickYieldSettings? ToSettings(QuickYieldOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var d = new QuickYieldOptions();
        var s = new OpticsToleranceQuickYieldSettings
        {
            Samples = o.Samples == d.Samples ? null : o.Samples,
            CrossTerms = o.CrossTerms == d.CrossTerms ? null : o.CrossTerms,
        };
        return s == new OpticsToleranceQuickYieldSettings() ? null : s;
    }

    /// <summary>The options of saved settings; null is the defaults.</summary>
    public static QuickYieldOptions FromSettings(OpticsToleranceQuickYieldSettings? saved)
    {
        var d = new QuickYieldOptions();
        if (saved is null) return d;
        return d with { Samples = saved.Samples ?? d.Samples, CrossTerms = saved.CrossTerms ?? d.CrossTerms };
    }
}
