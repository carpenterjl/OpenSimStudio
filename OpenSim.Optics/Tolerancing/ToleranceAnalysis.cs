using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Tolerancing;

// OP-64: sensitivity and inverse sensitivity analysis (docs/Optics-Conventions.md §42).

/// <summary>The extreme of a tolerance a trial sets.</summary>
public enum ToleranceSide
{
    Min = 0,
    Max = 1,
}

/// <summary>
/// One trial: tolerance row <see cref="Row"/> (1-based) set to <see cref="Value"/>, compensated,
/// and the criterion evaluated. <see cref="Criterion"/> and <see cref="Change"/> are NaN and
/// <see cref="Failure"/> says why when the trial could not be evaluated.
/// </summary>
public sealed record ToleranceTrial
{
    public required int Row { get; init; }
    public required string Code { get; init; }
    public required ToleranceSide Side { get; init; }
    public required double Value { get; init; }
    public double Criterion { get; init; } = double.NaN;

    /// <summary>The change from nominal (<see cref="ToleranceAnalysis.ChangeOf"/>).</summary>
    public double Change { get; init; } = double.NaN;

    /// <summary>Each compensator's change from its value on the compensated nominal lens, in its
    /// own unit (<see cref="ToleranceRunResult.Compensators"/>); empty when not compensated.</summary>
    public IReadOnlyList<double> Compensators { get; init; } = Array.Empty<double>();

    public string? Failure { get; init; }

    public bool Succeeded => Failure is null;

    // OP-67
    /// <summary>What the tolerance script reported in this trial; null without a script.</summary>
    public ToleranceScriptOutput? Script { get; init; }
}

/// <summary>One tolerance row's result: the trials at its two extremes and, in the inverse modes,
/// the limits after adjustment.</summary>
public sealed record ToleranceRowResult
{
    /// <summary>The 1-based row.</summary>
    public required int Row { get; init; }
    public required string Code { get; init; }
    public string Comment { get; init; } = "";

    /// <summary>The limits as the table held them before the run.</summary>
    public required double StartMin { get; init; }
    public required double StartMax { get; init; }

    /// <summary>The limits the trials used: the table's in Sensitivity mode, the adjusted ones in
    /// the inverse modes.</summary>
    public required double Min { get; init; }
    public required double Max { get; init; }

    public required ToleranceTrial AtMin { get; init; }
    public required ToleranceTrial AtMax { get; init; }

    /// <summary>Inverse modes: what happened to each limit (kept, tightened, not adjusted and why).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool Adjusted => Min != StartMin || Max != StartMax;
}

/// <summary>The outcome of <see cref="ToleranceAnalysis.Run"/>.</summary>
public sealed record ToleranceRunResult
{
    public required ToleranceRunOptions Options { get; init; }

    /// <summary>The criterion of the compensated nominal lens, N.</summary>
    public required double Nominal { get; init; }

    /// <summary>The criterion's unit ("mm", "waves", "rad", or "" for the merit function, the Strehl ratio, the MTF and the encircled energy).</summary>
    public required string Unit { get; init; }

    /// <summary>What each entry of <see cref="ToleranceTrial.Compensators"/> is.</summary>
    public required IReadOnlyList<string> Compensators { get; init; }

    public required IReadOnlyList<ToleranceRowResult> Rows { get; init; }

    /// <summary>The trials sorted from the largest degradation down, at most <see cref="ToleranceRunOptions.ShowWorst"/>.</summary>
    public required IReadOnlyList<ToleranceTrial> WorstOffenders { get; init; }

    /// <summary>The RSS estimated change C (help, "The RSS Estimated Change"); null when a trial failed.</summary>
    public double? EstimatedChange { get; init; }

    /// <summary>The estimated performance F; null when a trial failed or the help does not define it.</summary>
    public double? EstimatedPerformance { get; init; }

    /// <summary>The table with the adjusted limits (inverse modes); the table as given in Sensitivity mode.</summary>
    public required ToleranceTable Table { get; init; }

    /// <summary>How the criterion and the compensators were evaluated.</summary>
    public required IReadOnlyList<string> Assumptions { get; init; }

    /// <summary>Failed trials, rows that could not be evaluated, and the like.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    public int Trials { get; init; }

    // OP-67
    /// <summary>What the tolerance script reported on the nominal lens; null without a script.</summary>
    public ToleranceScriptOutput? NominalScript { get; init; }

    /// <summary>The files the tolerance script's OPENFILE and SAVE wrote, in the order written.</summary>
    public IReadOnlyList<string> ScriptFiles { get; init; } = Array.Empty<string>();

    /// <summary>The report, one line each: nominal, every row, the worst offenders, the estimate, the assumptions.</summary>
    public IReadOnlyList<string> ReportLines()
    {
        string V(double v) => double.IsNaN(v) ? "—" : StudyReport.Number(v, "g6");
        string U = Unit.Length == 0 ? "" : " " + Unit;
        var info = ToleranceCriteria.Info(Options.Criterion);
        var lines = new List<string>
        {
            $"Mode: {Options.Mode}. Criterion: {info.Name}{(Unit.Length == 0 ? "" : $" ({Unit})")}. Change: {(Options.Change == ToleranceChangeMode.LinearDifference ? "linear difference" : "RSS difference")}. Compensation: {Options.Compensation}.",
            $"Nominal criterion: {V(Nominal)}{U}.",
            "",
            "Row  Type  Min  Max  Criterion(min)  Change(min)  Criterion(max)  Change(max)",
        };
        foreach (var r in Rows)
            lines.Add($"{r.Row}  {r.Code}  {V(r.Min)}  {V(r.Max)}  {V(r.AtMin.Criterion)}  {V(r.AtMin.Change)}  {V(r.AtMax.Criterion)}  {V(r.AtMax.Change)}"
                + (r.Adjusted ? $"  (was {V(r.StartMin)}, {V(r.StartMax)})" : ""));
        lines.Add("");
        lines.Add("Worst offenders:");
        foreach (var t in WorstOffenders)
            lines.Add($"  row {t.Row} {t.Code} {t.Side.ToString().ToLowerInvariant()} {V(t.Value)}: criterion {V(t.Criterion)}, change {V(t.Change)}");
        lines.Add("");
        lines.Add(EstimatedChange is double c ? $"Estimated change (RSS): {V(c)}{U}." : "Estimated change (RSS): not available (see the notes).");
        lines.Add(EstimatedPerformance is double f ? $"Estimated performance: {V(f)}{U}." : "Estimated performance: not available (see the notes).");
        if (Notes.Count > 0) { lines.Add(""); lines.Add("Notes:"); lines.AddRange(Notes.Select(n => "  " + n)); }
        // OP-67: the script's output (REPORT unless QUIET ON, and PRINT), the nominal first.
        var script = new List<(string Who, ToleranceScriptOutput Output)>();
        if (NominalScript is { } ns) script.Add(("Nominal", ns));
        foreach (var r in Rows)
        {
            if (r.AtMin.Script is { } a) script.Add(($"Row {r.Row} {r.Code} min", a));
            if (r.AtMax.Script is { } b) script.Add(($"Row {r.Row} {r.Code} max", b));
        }
        if (script.Any(s => s.Output.Text.Length > 0))
        {
            lines.Add("");
            lines.Add("Script output:");
            foreach (var (who, output) in script.Where(s => s.Output.Text.Length > 0))
            {
                lines.Add("  " + who + ":");
                lines.AddRange(output.Text.TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n').Select(l => "    " + l));
            }
        }
        if (ScriptFiles.Count > 0) lines.Add("Script files written: " + string.Join(", ", ScriptFiles) + ".");
        lines.Add("");
        lines.Add("Assumptions:");
        lines.AddRange(Assumptions.Select(a => "  " + a));
        return lines;
    }
}

/// <summary>
/// Tolerance sensitivity and inverse sensitivity (plan item OP-64; OpticStudio 2025 R2 help,
/// "Tolerancing", "How OpticStudio Computes the Tolerance Analysis", "Evaluating Compensators",
/// "Sensitivity Analysis", "The RSS Estimated Change", "Inverse Sensitivity Analysis", "Quick
/// Sensitivity"; docs/Optics-Conventions.md §42).
/// <para>
/// <b>Nominal.</b> The compensators are first evaluated on the nominal lens (Optimize: the
/// optimizer drives them; paraxial focus and none: the lens as entered) and its criterion is N.
/// </para>
/// <para>
/// <b>Sensitivity.</b> "Each tolerance is evaluated independently": the compensated nominal lens,
/// the row's parameter "adjusted to the extreme minimum value" by OP-63's
/// <see cref="TolerancePerturber"/>, "the compensators are adjusted", the criterion P recorded;
/// "the procedure is repeated for the maximum tolerance". The change is Δ = P − N (Linear
/// Difference) or Δ = S(P − N)√|P² − N²| (RSS Difference, S(x) = +1 for x ≥ 0, −1 otherwise). The RSS
/// estimate is C² = Σᵢ (Δᵢ,min² + Δᵢ,max²)/2 and the estimated performance F = N + C (linear) or
/// F = √(N² + C²) (RSS). For a criterion where larger is better (the Strehl ratio, the MTF, the encircled energy), the degradation is −Δ and
/// F = N − C with the linear difference (this program's reading, §10 item 38); with the RSS
/// difference F is not given.
/// </para>
/// <para>
/// <b>Inverse.</b> For each limit of each row the degradation g (Δ, or −Δ when larger is better)
/// at the starting limit L is compared with the target T: the Increment (Inverse Increment), or the
/// change the Limit makes from N (Inverse Limit). "The tolerance will never be loosened": when
/// g(L) ≤ T the limit is kept; a row marked Do Not Adjust is kept. Otherwise Brent's method
/// (<see cref="RootFinding.Brent"/>) finds t in (0, 1) with g(t·L) = T, assuming, as the help does,
/// that "the criterion must smoothly and monotonically change with the tolerance value"; the
/// limit becomes t·L and its trial is evaluated again. The estimate then uses the adjusted limits.
/// </para>
/// <para>
/// Rows are independent and run in parallel, one slot each; within a slot everything runs on one
/// thread, so the result is bitwise identical at any thread count. A trial that cannot be
/// evaluated is recorded with its reason, and the RSS estimate is then not given (the help: "The
/// statistical data which follows the sensitivity analysis is usually meaningless if any of the
/// tolerances have a criterion value of infinity").
/// </para>
/// </summary>
public static class ToleranceAnalysis
{
    /// <summary>The criteria Quick Sensitivity offers (help, "Quick Sensitivity"): spot, wavefront and boresight error.</summary>
    public static IReadOnlyList<ToleranceCriterion> QuickCriteria { get; } =
        ToleranceCriteria.All.Where(i => i.InQuickSensitivity).Select(i => i.Criterion).ToArray();

    /// <summary>
    /// Quick Sensitivity (help: "a sensitivity-only analysis with Paraxial Focus compensation"):
    /// <see cref="Run"/> in Sensitivity mode with the paraxial focus compensator, for the criteria
    /// in <see cref="QuickCriteria"/>.
    /// </summary>
    public static ToleranceRunResult QuickSensitivity(OpticsSettings nominal, ToleranceTable table, IReadOnlyList<OpticalCatalog> catalogs,
        ToleranceRunOptions? options = null)
    {
        options ??= new ToleranceRunOptions();
        if (!QuickCriteria.Contains(options.Criterion))
            throw new ArgumentException($"Quick Sensitivity offers RMS spot radius, x and y, RMS wavefront and boresight error, not {ToleranceCriteria.Info(options.Criterion).Name}.");
        return Run(nominal, table, catalogs, options with { Mode = ToleranceMode.Sensitivity, Compensation = ToleranceCompensation.ParaxialFocus });
    }

    /// <summary>The change Δ of a criterion P from the nominal N (help, "Tolerancing", "Change").</summary>
    public static double ChangeOf(double p, double n, ToleranceChangeMode mode) => mode == ToleranceChangeMode.LinearDifference
        ? p - n
        : (p - n >= 0 ? 1 : -1) * Math.Sqrt(Math.Abs(p * p - n * n));

    /// <summary>Runs the analysis on the saved lens <paramref name="nominal"/> (its current
    /// configuration) with the tolerance rows of <paramref name="table"/>.</summary>
    /// <exception cref="ArgumentException">Invalid options, a table that does not fit the lens, a
    /// compensator that cannot be varied, an empty merit function for that criterion, a Limit or
    /// Increment the help does not allow.</exception>
    /// <exception cref="ToleranceEvaluationException">The nominal criterion cannot be evaluated.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static ToleranceRunResult Run(OpticsSettings nominal, ToleranceTable table, IReadOnlyList<OpticalCatalog> catalogs,
        ToleranceRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nominal);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new ToleranceRunOptions();
        var token = options.CancellationToken;
        var setup = Prepare(nominal, table, catalogs, options);   // OP-65: shared with the Monte Carlo run
        var rows = setup.Rows;
        var notes = setup.Notes;
        int configuration = setup.Configuration, configurations = setup.Configurations;
        var nominalLens = setup.NominalLens;
        var system = setup.System;
        var evaluator = setup.Evaluator;
        var compensator = setup.Compensator;
        var unit = setup.Unit;

        // The compensated nominal.
        token.ThrowIfCancellationRequested();
        var start = compensator.Nominal(token);
        double n = start.Criterion;
        var info = evaluator.Info;
        double target = Target(options, n, info, unit);
        var perturber = new TolerancePerturber(start.Settings, catalogs);
        notes.AddRange(start.Notes);

        int done = 0, trials = 0;
        var slots = ParallelSlots.Compute(rows.Length, options.MaxDegreeOfParallelism, k =>
        {
            int i = rows[k];
            var r = RunRow(i, table, perturber, start.Settings, compensator, options, n, info, target, token, out int count);
            Interlocked.Add(ref trials, count);
            int d = Interlocked.Increment(ref done);
            options.Progress?.Invoke(new ToleranceRunProgress(d, rows.Length, Volatile.Read(ref trials), $"row {i + 1} ({r.Code})"));
            return r;
        }, token);

        // Statistics (help, "The RSS Estimated Change").
        var all = slots.SelectMany(r => new[] { r.AtMin, r.AtMax }).ToArray();
        var failed = all.Where(t => !t.Succeeded).ToArray();
        foreach (var t in failed) notes.Add($"Row {t.Row} ({t.Code}) at its {t.Side.ToString().ToLowerInvariant()} {ToleranceRunSettings.F(t.Value)}: {t.Failure}");
        foreach (var r in slots) notes.AddRange(r.Notes.Select(x => $"Row {r.Row} ({r.Code}): {x}"));
        double? c = null, f = null;
        if (failed.Length == 0)
        {
            double c2 = 0;
            foreach (var r in slots) c2 += (r.AtMin.Change * r.AtMin.Change + r.AtMax.Change * r.AtMax.Change) / 2;
            c = Math.Sqrt(c2);
            if (!info.HigherIsBetter) f = options.Change == ToleranceChangeMode.LinearDifference ? n + c : Math.Sqrt(n * n + c.Value * c.Value);
            else if (options.Change == ToleranceChangeMode.LinearDifference) f = n - c;
            else notes.Add("The help defines the estimated performance with the RSS difference only for a criterion that grows worse upward; for a criterion where larger is better (the Strehl ratio, the MTF, the encircled energy) it is not given.");
        }
        else notes.Add($"{failed.Length} trial{(failed.Length == 1 ? "" : "s")} could not be evaluated, so the RSS estimate is not given (the help: the statistics are \"usually meaningless\" then). Tighten those tolerances and run again.");

        var worst = all.Where(t => t.Succeeded)
            .OrderByDescending(t => Degradation(t.Change, info))
            .ThenBy(t => t.Row).ThenBy(t => t.Side)
            .Take(Math.Max(0, options.ShowWorst)).ToArray();

        var newRows = table.Rows.ToArray();
        foreach (var r in slots) newRows[r.Row - 1] = newRows[r.Row - 1] with { Min = r.Min, Max = r.Max };

        // OP-67: the script's files, the nominal first and then the rows in order (min, then max).
        IReadOnlyList<string> scriptFiles = compensator.Script is { } script
            ? ToleranceScriptFiles.Write(new[] { start.Script }.Concat(slots.SelectMany(r => new[] { r.AtMin.Script, r.AtMax.Script })), script, notes)
            : Array.Empty<string>();

        return new ToleranceRunResult
        {
            Options = options, Nominal = n, Unit = unit, Compensators = compensator.Labels, Rows = slots, WorstOffenders = worst,
            EstimatedChange = c, EstimatedPerformance = f, Table = new ToleranceTable(newRows, table.IrregularityModel),
            Assumptions = Assumptions(options, evaluator, compensator, nominalLens, system, configuration, configurations),
            Notes = notes, Trials = trials + 1,
            NominalScript = start.Script, ScriptFiles = scriptFiles,   // OP-67
        };
    }

    // OP-65: the set-up the sensitivity run and the Monte Carlo run (MonteCarloAnalysis) share.
    /// <summary>Checks the options and the table against the lens and builds the criterion
    /// evaluator and the compensators; the tolerance rows are the non-control rows not ignored.</summary>
    internal static ToleranceSetup Prepare(OpticsSettings nominal, ToleranceTable table, IReadOnlyList<OpticalCatalog> catalogs,
        ToleranceRunOptions options)
    {
        Check(options);

        // The table: control and ignored rows set context only; rows this version cannot apply are
        // recorded as failed trials, not refused.
        var problems = ToleranceValidation.Validate(table, nominal)
            .Where(p => !p.Contains("defined but not applied in this version", StringComparison.Ordinal)).ToList();
        if (problems.Count > 0)
            throw new ArgumentException("The tolerance data has problems; fix them first (Update in the Tolerance Data Editor):"
                + Environment.NewLine + string.Join(Environment.NewLine, problems));
        var rows = Enumerable.Range(0, table.Rows.Count)
            .Where(i => !table.Rows[i].Ignore && ToleranceOperandRegistry.Find(table.Rows[i].Code) is { IsControl: false })
            .ToArray();

        var notes = new List<string>();
        int configuration = MultiConfiguration.CurrentConfiguration(nominal);
        int configurations = MultiConfiguration.ConfigurationCount(nominal);
        var (nominalLens, system) = ToleranceCriterionEvaluator.Build(nominal, configuration, catalogs);
        var fields = options.Criterion is ToleranceCriterion.MeritFunction or ToleranceCriterion.BoresightError or ToleranceCriterion.UserScript   // OP-67
            ? Array.Empty<ToleranceField>() : ToleranceCriterionEvaluator.FieldsOf(system, options.Fields, notes);
        MeritFunction? merit = null;
        if (options.Criterion == ToleranceCriterion.MeritFunction)
        {
            var registry = MeritOperandRegistry.CreateDefault();
            var mfNotes = new List<string>();
            merit = MeritFunctionSettings.FromSettings(nominal.MeritFunction, registry, mfNotes);
            notes.AddRange(mfNotes);
            if (merit.Rows.Count == 0)
                throw new ArgumentException("The merit function criterion needs a merit function: build one in the Merit Function Editor.");
        }
        var evaluator = new ToleranceCriterionEvaluator(options, catalogs, configuration, fields, merit);
        var compensator = new ToleranceCompensators(options, table, nominal, catalogs, evaluator, configurations);
        var unit = ToleranceCriteria.Unit(options.Criterion, system.Units.LensUnit);

        return new ToleranceSetup(rows, notes, configuration, configurations, nominalLens, system, evaluator, compensator, unit);
    }

    private static void Check(ToleranceRunOptions o)
    {
        if (!Enum.IsDefined(o.Mode) || !Enum.IsDefined(o.Criterion) || !Enum.IsDefined(o.Change) || !Enum.IsDefined(o.Compensation) || !Enum.IsDefined(o.Fields))
            throw new ArgumentException("Tolerance run: an option is not one of its choices.");
        if (o.Sampling < 1 || o.Sampling > Analysis.Sampling.PupilQuadrature.MaximumRings)
            throw new ArgumentException($"Tolerance run: the sampling (rays along a radial arm) must be 1 to {Analysis.Sampling.PupilQuadrature.MaximumRings}, not {o.Sampling}.");
        if (ToleranceCriteria.UsesPupilGrid(o.Criterion) && (o.PsfSampling < 16 || o.PsfSampling > 1024))
            throw new ArgumentException($"Tolerance run: the pupil grid of the {ToleranceCriteria.Info(o.Criterion).Name} must be 16 to 1024, not {o.PsfSampling}.");
        if (ToleranceCriteria.IsMtf(o.Criterion) && !(o.MtfFrequency > 0 && double.IsFinite(o.MtfFrequency)))
            throw new ArgumentException($"Tolerance run: the {ToleranceCriteria.Info(o.Criterion).Name} needs an MTF Frequency above zero (cycles/mm); it is {ToleranceRunSettings.F(o.MtfFrequency)}.");
        if (ToleranceCriteria.IsEncircledEnergy(o.Criterion) && !(o.EncircledRadius > 0 && double.IsFinite(o.EncircledRadius)))
            throw new ArgumentException($"Tolerance run: the {ToleranceCriteria.Info(o.Criterion).Name} needs a radius above zero (micrometers); it is {ToleranceRunSettings.F(o.EncircledRadius)}.");
        if (o.OptimizerCycles is int k && k < 0) throw new ArgumentException("Tolerance run: the optimizer cycles must be 0 (automatic) or more.");
        if (!(o.InverseTolerance > 0) || !(o.InverseTolerance < 0.1))
            throw new ArgumentException("Tolerance run: the inverse tolerance must lie between 0 and 0.1 of the starting limit.");
    }

    /// <summary>The inverse target T as a degradation; NaN in Sensitivity mode.</summary>
    private static double Target(ToleranceRunOptions o, double n, ToleranceCriterionInfo info, string unit)
    {
        string u = unit.Length == 0 ? "" : " " + unit;
        switch (o.Mode)
        {
            case ToleranceMode.InverseIncrement:
                if (!(o.Increment > 0) || !double.IsFinite(o.Increment))
                    throw new ArgumentException($"Inverse Increment: the Increment must be positive (help: \"The Increment value must be positive to represent a degradation in performance\"); it is {ToleranceRunSettings.F(o.Increment)}.");
                return o.Increment;
            case ToleranceMode.InverseLimit:
                if (!double.IsFinite(o.Limit) || (info.HigherIsBetter ? !(o.Limit < n) : !(o.Limit > n)))
                    throw new ArgumentException($"Inverse Limit: the Limit {ToleranceRunSettings.F(o.Limit)}{u} must be {(info.HigherIsBetter ? "below" : "above")} the nominal {ToleranceRunSettings.F(n)}{u} (help: \"The Limit value must represent worse performance than the nominal system has\").");
                return Degradation(ChangeOf(o.Limit, n, o.Change), info);
            default:
                return double.NaN;
        }
    }

    private static double Degradation(double change, ToleranceCriterionInfo info) => info.HigherIsBetter ? -change : change;

    private static ToleranceRowResult RunRow(int i, ToleranceTable table, TolerancePerturber perturber, OpticsSettings start,
        ToleranceCompensators compensator, ToleranceRunOptions o, double n, ToleranceCriterionInfo info, double target,
        CancellationToken token, out int count)
    {
        var row = table.Rows[i];
        string code = row.Code.ToUpperInvariant();
        int trials = 0;
        ToleranceTrial Trial(ToleranceSide side, double value)
        {
            token.ThrowIfCancellationRequested();
            trials++;
            var t = new ToleranceTrial { Row = i + 1, Code = code, Side = side, Value = value };
            try
            {
                // TC-15: with the aim twin (the row at zero) when the row decenters or tilts.
                var trial = ToleranceAim.Apply(perturber, start, table, new[] { new ToleranceStep(i, value) });
                var e = compensator.Evaluate(trial.Perturbed, trial.Map, token, trial.Twin,
                    ToleranceScriptTrialId.Sensitivity(i + 1, side, o.Mode != ToleranceMode.Sensitivity));   // OP-67
                return t with { Criterion = e.Criterion, Change = ChangeOf(e.Criterion, n, o.Change), Compensators = e.Compensators, Script = e.Script };
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return t with { Failure = ex.Message };
            }
        }

        var rowNotes = new List<string>();
        (double Limit, ToleranceTrial Trial) Side(ToleranceSide side, double limit)
        {
            var at = Trial(side, limit);
            if (o.Mode == ToleranceMode.Sensitivity || limit == 0 || !at.Succeeded) return (limit, at);
            string s = side.ToString().ToLowerInvariant();
            if (Degradation(at.Change, info) <= target)
            {
                rowNotes.Add($"the {s} {ToleranceRunSettings.F(limit)} already meets the target; kept (the help: \"the tolerance will never be loosened\").");
                return (limit, at);
            }
            if (row.DoNotAdjust)
            {
                rowNotes.Add($"the {s} exceeds the target but the row is marked Do Not Adjust During Inverse Tolerancing; kept.");
                return (limit, at);
            }
            ToleranceTrial? last = null;
            double F(double t)
            {
                if (t == 0) return -target;
                var x = Trial(side, t * limit);
                if (!x.Succeeded) throw new ToleranceEvaluationException(x.Failure!);
                last = x;
                return Degradation(x.Change, info) - target;
            }
            try
            {
                double root = RootFinding.Brent(F, 0, 1, o.InverseTolerance, 200);
                double adjusted = root * limit;
                var final = last is { } l && l.Value == adjusted ? l : Trial(side, adjusted);
                if (!final.Succeeded) return (limit, at with { Failure = final.Failure });
                rowNotes.Add($"the {s} was tightened from {ToleranceRunSettings.F(limit)} to {ToleranceRunSettings.F(adjusted)}.");
                return (adjusted, final);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return (limit, at with { Failure = $"the inverse search failed: {ex.Message} Reduce the starting tolerance and repeat (help, \"Inverse Sensitivity Analysis\")." });
            }
        }

        var (min, atMin) = Side(ToleranceSide.Min, row.Min);
        var (max, atMax) = Side(ToleranceSide.Max, row.Max);
        // An operand whose Min must be −Max (help; ToleranceValidation) keeps the tighter of the two.
        if (o.Mode != ToleranceMode.Sensitivity && ToleranceOperandRegistry.Get(code).MinIsNegativeMax && -min != max && atMin.Succeeded && atMax.Succeeded)
        {
            double m = Math.Min(Math.Abs(min), Math.Abs(max));
            if (Math.Abs(min) != m) { min = -m; atMin = Trial(ToleranceSide.Min, min); }
            if (Math.Abs(max) != m) { max = m; atMax = Trial(ToleranceSide.Max, max); }
            rowNotes.Add($"Min must be −Max for {code} (help), so both are set to ±{ToleranceRunSettings.F(m)}, the tighter.");
        }
        count = trials;
        return new ToleranceRowResult
        {
            Row = i + 1, Code = code, Comment = row.Comment, StartMin = row.Min, StartMax = row.Max, Min = min, Max = max,
            AtMin = atMin, AtMax = atMax, Notes = rowNotes,
        };
    }

    // OP-65: internal for the Monte Carlo run.
    internal static IReadOnlyList<string> Assumptions(ToleranceRunOptions o, ToleranceCriterionEvaluator e, ToleranceCompensators comp,
        SequentialLens lens, OpticalSystemData system, int configuration, int configurations)
    {
        var lines = new List<string>();
        var info = e.Info;
        lines.Add($"Criterion: {info.Name}{(info.HigherIsBetter ? " (larger is better: the degradation is the decrease)" : "")}.");
        switch (o.Criterion)
        {
            case ToleranceCriterion.MeritFunction:
                lines.Add("The lens's own merit function, over every configuration (help: \"Uses whatever merit function has been defined for the lens\"); compensator bounds are not added (help).");
                break;
            case ToleranceCriterion.UserScript:   // OP-67: the script's lines follow with the compensators'
                break;
            case ToleranceCriterion.BoresightError:
                lines.Add($"Boresight error: the radial image coordinate of the on-axis chief ray at the primary wavelength over the magnitude of the paraxial effective focal length, radians (help, \"Tolerancing\"), configuration {configuration}.");
                break;
            default:
                lines.Add($"Fields ({o.Fields}): " + string.Join("; ", e.Fields.Select(f => $"{f.Label} weight {StudyReport.Number(f.Weight, "g4")}"))
                    + (FieldTypes.IsAngle(system.Fields.Type) ? " (degrees)." : " (meters)."));
                lines.Add(info.HigherIsBetter
                    ? "The field values are combined as their weighted mean."
                    : "The field values are combined as √(Σ w v² / Σ w), the form of the merit function OpticStudio builds for tolerancing.");
                lines.Add($"Every wavelength of the system, by its weight; configuration {configuration} of {configurations}.");
                if (ToleranceCriteria.IsMtf(o.Criterion))
                {
                    bool geometric = o.Criterion is ToleranceCriterion.GeometricMtfAverage or ToleranceCriterion.GeometricMtfTangential or ToleranceCriterion.GeometricMtfSagittal;
                    lines.Add($"MTF at {ToleranceRunSettings.F(o.MtfFrequency)} cycles/mm (help: \"MTF Frequency\"), modulation, {(geometric ? "OP-39's geometric engine (the ray spot's transform) times the diffraction-limited MTF of the same pupil" : "OP-39's FFT engine (the pupil autocorrelation)")}, {o.PsfSampling} × {o.PsfSampling} pupil grid; the average is (T + S)/2 (help).");
                }
                else if (ToleranceCriteria.IsEncircledEnergy(o.Criterion))
                    lines.Add($"Encircled energy within {ToleranceRunSettings.F(o.EncircledRadius)} µm of the centroid, OP-41's {(o.Criterion == ToleranceCriterion.DiffractionEncircledEnergy ? "diffraction" : "geometric")} engine, {o.PsfSampling} × {o.PsfSampling} pupil grid.");
                else
                {
                    try
                    {
                        var setup = RmsCore.Prepare(lens, system, false, "Tolerance criterion", new List<string>(), default);
                        var q = ToleranceCriterionEvaluator.Quantity(o.Criterion);
                        lines.AddRange(RmsCore.Assumptions(setup, q, RmsReference.Centroid, e.Rule, o.PsfSampling));
                    }
                    catch (Exception x) when (x is ArgumentException or InvalidOperationException) { lines.Add(x.Message); }
                }
                if (!ToleranceCriteria.UsesPupilGrid(o.Criterion))
                    lines.Add($"Sampling {o.Sampling}: {o.Sampling} rays along each of {2 * o.Sampling} arms (help: \"The number of arms is always twice the number of rays along each arm\"); centroid reference (help: \"OpticStudio always uses a centroid reference for tolerancing\").");
                break;
        }
        lines.Add(o.Change == ToleranceChangeMode.LinearDifference ? "Change: Δ = P − N." : "Change: Δ = S(P − N)·√|P² − N²|.");
        lines.AddRange(comp.Assumptions);
        if (o.Mode != ToleranceMode.Sensitivity)
            lines.Add($"Inverse: each limit is searched by Brent's method to {ToleranceRunSettings.F(o.InverseTolerance)} of its starting value, assuming the criterion changes monotonically with the tolerance (help); limits are only ever tightened.");
        lines.Add("Radial operands (TSDR, TEDR, TARR, TRLR) are applied along +y, TIRR's astigmatism along y (OP-63's defaults).");
        lines.Add("A trial its decenters and tilts make non-axial is traced as it is, with the entrance pupil, fields, paraxial focus and focal length of the same trial with its decenters and tilts at zero (TC-15, docs/Optics-Conventions.md §42).");
        lines.Add("Not built: Separate Fields/Configs, the polynomial fit and the cache, Force Ray Aiming, the Config \"All\" choice (the current configuration is toleranced), the RMS angular criteria, Skip Sensitivity (Monte Carlo runs on its own: MonteCarloAnalysis, OP-65).");
        return lines;
    }
}

// OP-65
/// <summary>What <see cref="ToleranceAnalysis.Prepare"/> builds: the tolerance rows (0-based), the
/// notes so far, the configuration toleranced, the nominal lens and system, the criterion evaluator,
/// the compensators and the criterion's unit.</summary>
internal sealed record ToleranceSetup(int[] Rows, List<string> Notes, int Configuration, int Configurations, SequentialLens NominalLens,
    OpticalSystemData System, ToleranceCriterionEvaluator Evaluator, ToleranceCompensators Compensator, string Unit);
