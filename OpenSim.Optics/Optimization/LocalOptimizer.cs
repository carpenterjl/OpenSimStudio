using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics.Optimization;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;

namespace OpenSim.Optics.Optimization;

// OP-57: local optimization of the project's lens.

/// <summary>The local optimization algorithm (OpticStudio 2025 R2 help, Optimize!).</summary>
public enum LocalOptimizationAlgorithm
{
    /// <summary>Damped least squares on finite-difference derivatives (<see cref="DampedLeastSquares"/>).</summary>
    DampedLeastSquares,
    /// <summary>Orthonormal directions and discrete trial steps, no derivatives (<see cref="Core.Numerics.Optimization.OrthogonalDescent"/>).</summary>
    OrthogonalDescent,
}

/// <summary>How many cycles to run (OpticStudio 2025 R2 help, Optimize!, "Cycles").</summary>
public enum LocalOptimizationCycles
{
    /// <summary>"Run until no progress is being made": until the algorithm's own stopping tests hold.</summary>
    Automatic,
    /// <summary><see cref="LocalOptimizationOptions.CycleCount"/> cycles, or fewer if the algorithm stops first.</summary>
    Fixed,
    /// <summary>"An infinite, continuous loop until Stop is pressed": the algorithm is restarted
    /// from the best design each time it stops, until cancelled.</summary>
    Infinite,
}

/// <summary>Why a run ended.</summary>
public enum LocalOptimizationStop
{
    /// <summary>The algorithm's stopping tests held (and every Lagrange constraint is met).</summary>
    Converged,
    /// <summary>The fixed number of cycles ran.</summary>
    CycleLimit,
    /// <summary>The evaluation limit was reached.</summary>
    EvaluationLimit,
    /// <summary>The run was cancelled (Stop); the result is the best design so far.</summary>
    Cancelled,
    /// <summary>Some negative-weight (Lagrange) rows are still off target after the last round.</summary>
    ConstraintsNotMet,
}

/// <summary>The scale and derivative increment of one type of variable (<see cref="LensVariableKind"/>).</summary>
public sealed record LensVariableKindSettings
{
    /// <summary>The variable's unit for the optimizer, in the variable's own unit: the algorithms
    /// work on value / Scale. Damped least squares is insensitive to it (its columns are scaled
    /// by their lengths, <see cref="DampedLeastSquares"/>) except through the default derivative
    /// increment; Orthogonal Descent's first step is 5 % of |value|, or 5 % of Scale for a value of
    /// zero. Null is 1.</summary>
    public double? Scale { get; init; }

    /// <summary>The finite-difference step of damped least squares, in the variable's unit; null
    /// for the kernel's relative step (<see cref="LeastSquaresOptions.FiniteDifferenceSteps"/>).</summary>
    public double? DerivativeIncrement { get; init; }
}

/// <summary>A cycle that improved the design.</summary>
/// <param name="Cycle">Cycles run so far (DLS iterations or OD passes, over every restart and round).</param>
/// <param name="Merit">The merit function of the design.</param>
/// <param name="StartMerit">The merit function before the run.</param>
/// <param name="Values">The variables (saved units, a radius as its curvature).</param>
public sealed record LocalOptimizationProgress(int Cycle, double Merit, double StartMerit, int Evaluations, IReadOnlyList<double> Values);

/// <summary>Settings of <see cref="LocalOptimizer.Run"/>. Every member has a usable default.</summary>
public sealed class LocalOptimizationOptions
{
    public LocalOptimizationAlgorithm Algorithm { get; init; } = LocalOptimizationAlgorithm.DampedLeastSquares;

    public LocalOptimizationCycles Cycles { get; init; } = LocalOptimizationCycles.Automatic;

    /// <summary>The number of cycles of <see cref="LocalOptimizationCycles.Fixed"/>.</summary>
    public int CycleCount { get; init; } = 5;

    /// <summary>Scale and derivative increment per type of variable; a missing type takes the defaults.</summary>
    public IReadOnlyDictionary<LensVariableKind, LensVariableKindSettings>? Kinds { get; init; }

    /// <summary>The thread limit for the finite-difference columns and the ray traces; null for no
    /// limit. The result is bitwise identical at every setting.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>Merit function evaluations per algorithm run (each restart and Lagrange round
    /// has its own budget).</summary>
    public int MaxEvaluations { get; init; } = 100_000;

    /// <summary>A negative-weight row is met when |V − T| ≤ this × max(1, |T|).</summary>
    public double ConstraintTolerance { get; init; } = 1e-9;

    /// <summary>The most augmented-Lagrangian rounds when the merit function has negative-weight rows.</summary>
    public int MaxConstraintRounds { get; init; } = 30;

    /// <summary>How the merit function is evaluated (failed-row penalty); its thread limit is
    /// replaced by <see cref="MaxDegreeOfParallelism"/>.</summary>
    public MeritEvaluationOptions? Merit { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Called on the optimizer's thread after every cycle that improved the design; null for none.</summary>
    public Action<LocalOptimizationProgress>? Progress { get; init; }
}

/// <summary>The outcome of <see cref="LocalOptimizer.Run"/>.</summary>
public sealed record LocalOptimizationResult
{
    /// <summary>The optics record the run started from.</summary>
    public required OpticsSettings Start { get; init; }

    /// <summary>The optics record with the variables at their best values (the best design so
    /// far when cancelled); everything else as in <see cref="Start"/>.</summary>
    public required OpticsSettings Optimized { get; init; }

    public required IReadOnlyList<LensVariable> Variables { get; init; }

    /// <summary>The variables before the run (saved units, a radius as its curvature).</summary>
    public required IReadOnlyList<double> StartValues { get; init; }

    /// <summary>The variables after the run.</summary>
    public required IReadOnlyList<double> Values { get; init; }

    public required double StartMerit { get; init; }

    public required double Merit { get; init; }

    /// <summary>The merit function evaluated on <see cref="Optimized"/>.</summary>
    public required MeritEvaluation Evaluation { get; init; }

    public required LocalOptimizationStop Stop { get; init; }

    public bool Cancelled => Stop == LocalOptimizationStop.Cancelled;

    public int Cycles { get; init; }

    public int Evaluations { get; init; }

    /// <summary>Evaluations at which the lens could not be built or traced (the algorithms
    /// stepped away from them).</summary>
    public int FailedEvaluations { get; init; }

    /// <summary>What happened, one line each: the algorithm, merit before and after, each
    /// variable before and after, why it stopped.</summary>
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    /// <summary>Undo of the run: <paramref name="current"/> with the variable cells put back as
    /// they were saved before the run, everything else kept. Throws <see cref="ArgumentException"/> when the
    /// variables of <paramref name="current"/> are no longer the run's (cells added, removed or
    /// surfaces renumbered since).</summary>
    public OpticsSettings UndoOn(OpticsSettings current)
    {
        CheckSameVariables(current, "it cannot be undone here; use the editors' undo");
        return LensVariables.CopyCells(Start, current, Variables);
    }

    /// <summary>The result written into <paramref name="current"/>: the variable cells set as in
    /// <see cref="Optimized"/>, everything else (edits made during the run) kept. Throws
    /// <see cref="ArgumentException"/> when the variables of <paramref name="current"/> are no
    /// longer the run's.</summary>
    public OpticsSettings ApplyTo(OpticsSettings current)
    {
        CheckSameVariables(current, "the result cannot be applied; run the optimization again");
        return LensVariables.CopyCells(Optimized, current, Variables);
    }

    private void CheckSameVariables(OpticsSettings current, string consequence)
    {
        ArgumentNullException.ThrowIfNull(current);
        var now = LensVariables.Find(current);
        if (now.Count != Variables.Count || now.Zip(Variables).Any(p => p.First != p.Second))
            throw new ArgumentException($"The variables have changed since the optimization ran, so {consequence}.");
    }
}

/// <summary>
/// Local optimization of an optical system (OP-57; docs/Optics-Conventions.md §35): the variable
/// cells (<see cref="LensVariables"/>) are driven by damped least squares or Orthogonal Descent to
/// lower the merit function (OP-54) over every configuration. Each evaluation writes the
/// variables into the optics record, rebuilds every configuration through the settings-to-lens
/// path (<see cref="ProjectMeritConfigurations"/>: multi-configuration pickups, then solves and
/// pickups, <see cref="Analysis.ProjectLens"/>) and evaluates the merit function, so solves and
/// pickups follow the variables inside every residual.
/// <para>
/// Bound operands are ordinary weighted rows (OP-54). Rows with a negative weight are exact
/// constraints, OpticStudio's Lagrangian multipliers (help: Modifying the merit function,
/// "Operand weights less than zero"); OpticStudio does not publish its method, and this program
/// meets them by the method of multipliers (augmented Lagrangian, Hestenes and Powell): with
/// cᵢ the row's residual, rows √ρ(cᵢ + λᵢ/2ρ) are added and, after each run of the algorithm,
/// λᵢ += 2ρcᵢ, with ρ (starting at 1) ten times larger whenever ‖c‖ fell by less than a factor 4
/// (this program's choices). The merit value reported is OpticStudio's (absolute weights).
/// </para>
/// <para>
/// The start must evaluate without a failed row, as OpticStudio requires ("If any of the
/// operands cannot be computed, the optimization cannot begin", help: Optimize!); afterwards a
/// design whose lens cannot be built or traced gets the failed-row penalty and the algorithm
/// steps away. Cancellation returns the best design so far. The result is bitwise identical at
/// any thread count.
/// </para>
/// </summary>
public static class LocalOptimizer
{
    /// <summary>Optimizes <paramref name="optics"/> against its saved merit function
    /// (<see cref="OpticsSettings.MeritFunction"/>) or <paramref name="function"/>.</summary>
    /// <exception cref="ArgumentException">No variables, a variable that cannot be varied, an empty
    /// or invalid merit function.</exception>
    /// <exception cref="InvalidOperationException">The merit function has failed rows at the start.</exception>
    public static LocalOptimizationResult Run(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs,
        LocalOptimizationOptions? options = null, MeritFunction? function = null, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new LocalOptimizationOptions();
        registry ??= MeritOperandRegistry.CreateDefault();
        var log = new List<string>();
        if (function is null)
        {
            var notes = new List<string>();
            function = MeritFunctionSettings.FromSettings(optics.MeritFunction, registry, notes);
            log.AddRange(notes);
        }
        if (function.Rows.Count == 0)
            throw new ArgumentException("The merit function has no rows: build one in the Merit Function Editor before optimizing.");
        int configurations = MultiConfiguration.ConfigurationCount(optics);
        if (MeritEvaluator.Validate(function, registry, configurations) is { Count: > 0 } invalid)
            throw new ArgumentException("Merit function:" + Environment.NewLine + string.Join(Environment.NewLine, invalid));
        if (options.Cycles == LocalOptimizationCycles.Fixed && options.CycleCount < 1)
            throw new ArgumentException($"A fixed number of cycles must be at least 1, not {options.CycleCount}.");
        if (options.MaxEvaluations < 1) throw new ArgumentException("The evaluation limit must be at least 1.");
        var variables = LensVariables.Find(optics);
        if (variables.Count == 0)
            throw new ArgumentException("There are no variables: flag cells Variable in the Lens Data Editor or the Multi-Configuration Editor.");

        var token = options.CancellationToken;
        var meritOptions = (options.Merit ?? new MeritEvaluationOptions()) with { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
        int n = variables.Count;
        var scale = new double[n];
        double?[] increments = new double?[n];
        for (int i = 0; i < n; i++)
        {
            var kind = options.Kinds is { } kinds && kinds.TryGetValue(variables[i].Kind, out var k) ? k : null;
            scale[i] = kind?.Scale ?? 1;
            if (!(scale[i] > 0) || !double.IsFinite(scale[i]))
                throw new ArgumentException($"The scale of {variables[i].Kind} variables must be positive and finite, not {scale[i].ToString(CultureInfo.InvariantCulture)}.");
            increments[i] = kind?.DerivativeIncrement;
            if (increments[i] is double h && (!(h > 0) || !double.IsFinite(h)))
                throw new ArgumentException($"The derivative increment of {variables[i].Kind} variables must be positive and finite, not {h.ToString(CultureInfo.InvariantCulture)}.");
        }

        MeritEvaluation Evaluate(double[] values) =>
            MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(LensVariables.Apply(optics, variables, values), catalogs), registry, meritOptions);
        double[] ToValues(double[] z) { var v = new double[n]; for (int i = 0; i < n; i++) v[i] = z[i] * scale[i]; return v; }

        var startValues = LensVariables.Values(optics, variables);
        var start = Evaluate(startValues);
        if (start.HasFailures)
            throw new InvalidOperationException("The merit function cannot be evaluated on the starting design, so the optimization cannot begin:"
                + Environment.NewLine + string.Join(Environment.NewLine, start.Errors));
        int rows = function.Rows.Count;
        var lagrange = Enumerable.Range(0, rows).Where(i => start.Rows[i].Counts && start.Rows[i].Row.Weight < 0).ToArray();
        int m = rows + lagrange.Length;
        var lambda = new double[lagrange.Length];
        double rho = 1;

        void Residuals(double[] z, double[] r)
        {
            token.ThrowIfCancellationRequested();
            var e = Evaluate(ToValues(z));
            for (int i = 0; i < rows; i++) r[i] = e.Residuals[i];
            double root = Math.Sqrt(rho);
            for (int k = 0; k < lagrange.Length; k++) r[rows + k] = root * (e.Residuals[lagrange[k]] + lambda[k] / (2 * rho));
        }
        double Cost(double[] z)
        {
            var r = new double[m];
            Residuals(z, r);
            double sum = 0;
            foreach (double v in r) sum += v * v;
            return sum;
        }

        var best = new double[n];
        for (int i = 0; i < n; i++) best[i] = startValues[i] / scale[i];
        int cycles = 0, evaluations = 1, failures = 0;
        double startMerit = start.Value;
        void Report(int cycle, double[] z, double cost)
        {
            best = (double[])z.Clone();
            cycles = cycle;
            if (options.Progress is not { } progress) return;
            double merit;
            if (lagrange.Length == 0) merit = Math.Sqrt(cost);
            else { merit = Evaluate(ToValues(z)).Value; evaluations++; }
            progress(new LocalOptimizationProgress(cycle, merit, startMerit, evaluations, ToValues(z)));
        }

        log.Insert(0, $"{(options.Algorithm == LocalOptimizationAlgorithm.DampedLeastSquares ? "Damped least squares" : "Orthogonal descent")}, "
            + $"{(options.Cycles switch { LocalOptimizationCycles.Fixed => $"{options.CycleCount} cycles", LocalOptimizationCycles.Infinite => "infinite cycles", _ => "automatic cycles" })}, "
            + $"{n} variable{(n == 1 ? "" : "s")}, {rows} rows{(lagrange.Length > 0 ? $" ({lagrange.Length} with negative weight: exact constraints)" : "")}, {configurations} configuration{(configurations == 1 ? "" : "s")}.");
        log.Add($"Merit function before: {Format(startMerit)}.");

        LocalOptimizationStop stop = LocalOptimizationStop.Converged;
        double previousViolation = double.PositiveInfinity;
        int round = 0;
        while (true)
        {
            int cycleBase = cycles;
            int? cycleLimit = options.Cycles == LocalOptimizationCycles.Fixed ? options.CycleCount - cycles : null;
            if (cycleLimit is <= 0) { stop = LocalOptimizationStop.CycleLimit; break; }
            bool stoppedByLimit = false, stoppedByEvaluations = false;
            try
            {
                if (options.Algorithm == LocalOptimizationAlgorithm.DampedLeastSquares)
                {
                    double[]? steps = null;
                    if (increments.Any(h => h is not null))
                    {
                        // A type without its own increment takes the kernel's forward step at the start.
                        steps = new double[n];
                        for (int i = 0; i < n; i++)
                            steps[i] = increments[i] is double h ? h / scale[i] : Math.Sqrt(2.220446049250313e-16) * (best[i] != 0 ? Math.Abs(best[i]) : 1);
                    }
                    var result = DampedLeastSquares.Solve(Residuals, m, best, new LeastSquaresOptions
                    {
                        MaxIterations = cycleLimit ?? (options.Cycles == LocalOptimizationCycles.Infinite ? int.MaxValue : 1000),
                        MaxEvaluations = options.MaxEvaluations,
                        FiniteDifferenceSteps = steps,
                        MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                        CancellationToken = token,
                        OnAcceptedStep = it => Report(cycleBase + it.Iteration, it.X, it.SumOfSquares),
                    });
                    best = result.X;
                    cycles = cycleBase + result.Iterations;
                    evaluations += result.Evaluations;
                    failures += result.FailedEvaluations;
                    stoppedByLimit = result.Status == LeastSquaresStatus.IterationLimit;
                    stoppedByEvaluations = result.Status == LeastSquaresStatus.EvaluationLimit;
                }
                else
                {
                    var initial = new double[n];
                    for (int i = 0; i < n; i++) initial[i] = best[i] != 0 ? 0.05 * Math.Abs(best[i]) : 0.05;
                    var result = OrthogonalDescent.Minimize(Cost, best, new OrthogonalDescentOptions
                    {
                        InitialSteps = initial,
                        MaxCycles = cycleLimit,
                        MaxEvaluations = options.MaxEvaluations,
                        CancellationToken = token,
                        OnImprovedCycle = it => Report(cycleBase + it.Cycle, it.X, it.Value),
                    });
                    best = result.X;
                    cycles = cycleBase + result.Cycles;
                    evaluations += result.Evaluations;
                    failures += result.FailedEvaluations;
                    stoppedByLimit = result.Status == OrthogonalDescentStatus.CycleLimit;
                    stoppedByEvaluations = result.Status == OrthogonalDescentStatus.EvaluationLimit;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                stop = LocalOptimizationStop.Cancelled;
                break;
            }
            if (token.IsCancellationRequested)
            {
                // Cancelled as the algorithm stopped on its own: still a stop, with the best design.
                stop = LocalOptimizationStop.Cancelled;
                break;
            }

            if (lagrange.Length > 0)
            {
                var e = Evaluate(ToValues(best));
                evaluations++;
                bool met = true;
                double violation = 0;
                for (int k = 0; k < lagrange.Length; k++)
                {
                    var row = e.Rows[lagrange[k]];
                    double off = row.Status == MeritRowStatus.Evaluated ? Math.Abs(row.Value - row.Row.Target) : double.PositiveInfinity;
                    if (!(off <= options.ConstraintTolerance * Math.Max(1, Math.Abs(row.Row.Target)))) met = false;
                    double c = e.Residuals[lagrange[k]];
                    violation += c * c;
                    lambda[k] += 2 * rho * c;
                }
                violation = Math.Sqrt(violation);
                round++;
                if (met && !stoppedByLimit && !stoppedByEvaluations && options.Cycles != LocalOptimizationCycles.Infinite) { stop = LocalOptimizationStop.Converged; break; }
                if (stoppedByLimit) { stop = met ? LocalOptimizationStop.CycleLimit : LocalOptimizationStop.ConstraintsNotMet; break; }
                if (stoppedByEvaluations) { stop = LocalOptimizationStop.EvaluationLimit; break; }
                if (round >= options.MaxConstraintRounds && options.Cycles != LocalOptimizationCycles.Infinite)
                {
                    stop = met ? LocalOptimizationStop.Converged : LocalOptimizationStop.ConstraintsNotMet;
                    break;
                }
                if (violation > 0.25 * previousViolation) rho *= 10;
                previousViolation = violation;
                continue;
            }
            if (options.Cycles == LocalOptimizationCycles.Infinite) continue;   // restart from the best design until Stop
            stop = stoppedByLimit ? LocalOptimizationStop.CycleLimit
                : stoppedByEvaluations ? LocalOptimizationStop.EvaluationLimit
                : LocalOptimizationStop.Converged;
            break;
        }

        var values = ToValues(best);
        var optimized = LensVariables.Apply(optics, variables, values);
        var final = Evaluate(values);
        evaluations++;
        log.Add($"Merit function after: {Format(final.Value)} ({cycles} cycle{(cycles == 1 ? "" : "s")}, {evaluations} evaluations"
            + $"{(failures > 0 ? $", {failures} at designs that could not be built or traced" : "")}).");
        for (int i = 0; i < n; i++) log.Add($"{variables[i].Label}: {Format(startValues[i])} → {Format(values[i])}.");
        log.Add(stop switch
        {
            LocalOptimizationStop.Cancelled => "Stopped: the best design so far is kept.",
            LocalOptimizationStop.CycleLimit => "Stopped after the set number of cycles.",
            LocalOptimizationStop.EvaluationLimit => "Stopped at the evaluation limit.",
            LocalOptimizationStop.ConstraintsNotMet => "Stopped with negative-weight rows still off target: they may not be reachable with these variables.",
            _ => "Converged: no further progress.",
        });
        foreach (string error in final.Errors) log.Add($"Failed at the end: {error}");
        return new LocalOptimizationResult
        {
            Start = optics, Optimized = optimized, Variables = variables, StartValues = startValues, Values = values,
            StartMerit = startMerit, Merit = final.Value, Evaluation = final, Stop = stop,
            Cycles = cycles, Evaluations = evaluations, FailedEvaluations = failures, Log = log,
        };
    }

    private static string Format(double value) => value.ToString("G10", CultureInfo.InvariantCulture);
}
