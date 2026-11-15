using System.Diagnostics;
using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization.Global;

// OP-58: the Hammer search.

/// <summary>Settings of <see cref="HammerSearch.Run"/>. Every member has a usable default.</summary>
public sealed class HammerSearchOptions
{
    /// <summary>The local optimization of each trial (as <see cref="GlobalSearchOptions.Local"/>).</summary>
    public LocalOptimizationOptions? Local { get; init; }

    /// <summary>Merit function evaluations per trial's local optimization.</summary>
    public int MaxEvaluationsPerTrial { get; init; } = 2000;

    /// <summary>The ranges whose widths scale the random steps; null takes
    /// <see cref="GlobalTrials.DefaultRanges"/>.</summary>
    public IReadOnlyList<GlobalVariableRange>? Ranges { get; init; }

    /// <summary>The curvature range ±this (1/lens unit) of the default ranges; null is 3/|EFL|.</summary>
    public double? CurvatureLimit { get; init; }

    /// <summary>A numeric trial moves every variable by a uniform random number in [−1, 1] times
    /// this fraction of its range's width (Wales and Doye 1997: "all coordinates were displaced
    /// by a random number in the range [−1, 1] times the step size").</summary>
    public double StepFraction { get; init; } = 0.1;

    /// <summary>Trials per round, run in parallel from the current design.</summary>
    public int TrialsPerRound { get; init; } = 8;

    /// <summary>Rounds after the start's re-optimization.</summary>
    public int Rounds { get; init; } = 25;

    /// <summary>With surfaces to substitute, the chance that a trial replaces one glass instead
    /// of moving the variables (1 when there are no variables).</summary>
    public double GlassChangeProbability { get; init; } = 0.5;

    /// <summary>The surfaces whose glass may be replaced; null takes those flagged Substitute
    /// (<see cref="GlassVariables.SubstituteSurfaces"/>).</summary>
    public IReadOnlyList<int>? SubstituteSurfaces { get; init; }

    /// <summary>A round's winner replaces the current design when it meets the exact
    /// constraints and the current one does not, or its merit is below current·(1 − this).</summary>
    public double RelativeImprovement { get; init; } = 1e-9;

    public long Seed { get; init; } = 1;

    public int SaveCount { get; init; } = 10;

    public double FormTolerance { get; init; } = 0.01;

    /// <summary>Checked between rounds; null for none.</summary>
    public TimeSpan? TimeLimit { get; init; }

    public int? MaxDegreeOfParallelism { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Called on the search's thread after the start and after each round.</summary>
    public Action<GlobalSearchProgress>? Progress { get; init; }
}

/// <summary>
/// The Hammer search (plan item OP-58; docs/Optics-Conventions.md §43): repeated perturbation and
/// re-optimization of the current design to escape its local minimum, also replacing glasses
/// (OpticStudio 2025 R2 help, Hammer Optimizer: it "will take the lens and exhaustively attempt to
/// refine it by making adjustments and optimizations"; Using Glass Substitution). OpticStudio's
/// algorithm is unpublished; this program's is monotonic basin hopping (D. J. Wales and
/// J. P. K. Doye, J. Phys. Chem. A 101, 5111, 1997; arXiv:cond-mat/9803344): a step displaces
/// every variable by a uniform random number in [−1, 1] times the step, then the design is
/// locally optimized (OP-57's <see cref="LocalOptimizer"/>, the transformed surface
/// Ẽ(X) = min{E(X)}, their eq. 2), and the search resets to the minimum after each successful
/// step. Wales and Doye accept steps by a Metropolis test at a finite temperature; this search
/// accepts only improvements (temperature zero, this program's choice, as OpticStudio's Hammer
/// keeps the best design).
/// <list type="bullet">
/// <item>The start is first re-optimized (trial 0); that is the current design.</item>
/// <item>Each round runs <see cref="HammerSearchOptions.TrialsPerRound"/> trials from the
/// current design: with surfaces flagged Substitute, a trial replaces the glass of one of them,
/// drawn uniformly, by a candidate drawn uniformly from OP-59's
/// <see cref="GlassSubstitutionTemplate.Candidates"/> (the template, the catalogs, the
/// wavelengths) other than its glass now, with probability
/// <see cref="HammerSearchOptions.GlassChangeProbability"/>, through
/// <see cref="GlassVariables.WithGlass"/>; otherwise it moves the variables. The round's best
/// trial (exact constraints, then merit, then the lower trial number) becomes the current
/// design when it is better.</item>
/// </list>
/// Model-glass variables (nd, Vd, ΔPgF) are moved like any variable. Trial t of round r ≥ 1 draws
/// only from <see cref="SplitMix64.ForStream"/>(seed, (r − 1)·K + k), so a seed reproduces a run
/// bitwise at any thread count. Every trial is offered to a <see cref="GlobalBestList"/>.
/// </summary>
public static class HammerSearch
{
    /// <exception cref="ArgumentException">Nothing to vary, invalid settings or merit function.</exception>
    /// <exception cref="InvalidOperationException">The start cannot be evaluated or re-optimized,
    /// or no glass can be substituted.</exception>
    public static GlobalSearchResult Run(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs,
        HammerSearchOptions? options = null, MeritFunction? function = null, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new HammerSearchOptions();
        registry ??= MeritOperandRegistry.CreateDefault();
        var log = new List<string>();
        function = GlobalTrials.Resolve(optics, function, registry, log);
        var variables = LensVariables.Find(optics);
        var surfaces = optics.Surfaces ?? new List<OpticsSurfaceSettings>();
        var substitute = options.SubstituteSurfaces ?? GlassVariables.SubstituteSurfaces(optics);
        foreach (int j in substitute)
        {
            if (j < 0 || j >= surfaces.Count) throw new ArgumentException($"The Hammer search names surface {j}; the lens has surfaces 0 to {surfaces.Count - 1}.");
            var s = surfaces[j];
            bool mirror = string.Equals(s?.Material?.Trim(), SavedSurfaces.Mirror, StringComparison.OrdinalIgnoreCase);
            if (s is null || mirror || (string.IsNullOrWhiteSpace(s.Material) && s.ModelGlass is null))
                throw new ArgumentException($"{SavedSurfaces.Where(j, surfaces.Count)} has no glass to substitute (it is {(mirror ? "a mirror" : "air or a constant index")}).");
            if (GlassVariables.ModelColumns.Any(c => GlassVariables.IsFlagged(s, c)))
                throw new ArgumentException($"{SavedSurfaces.Where(j, surfaces.Count)} is flagged Substitute and also varies its model glass; clear one of the two.");
        }
        if (variables.Count == 0 && substitute.Count == 0)
            throw new ArgumentException("There is nothing to vary: flag cells Variable, or tick Substitute on a glass.");
        if (options.Rounds < 0) throw new ArgumentException($"The number of rounds must be 0 or more, not {options.Rounds}.");
        if (options.TrialsPerRound < 1) throw new ArgumentException($"A round needs at least one trial, not {options.TrialsPerRound}.");
        if (!(options.StepFraction > 0) || !double.IsFinite(options.StepFraction))
            throw new ArgumentException($"The step must be positive, not {GlobalTrials.F(options.StepFraction)}.");
        if (!(options.GlassChangeProbability >= 0 && options.GlassChangeProbability <= 1))
            throw new ArgumentException($"The glass change probability must be in [0, 1], not {GlobalTrials.F(options.GlassChangeProbability)}.");
        if (!(options.RelativeImprovement >= 0) || !double.IsFinite(options.RelativeImprovement))
            throw new ArgumentException("The relative improvement must be zero or positive.");
        if (options.MaxEvaluationsPerTrial < 1) throw new ArgumentException("The evaluation limit per trial must be at least 1.");
        if (options.TimeLimit is { } limit && limit <= TimeSpan.Zero) throw new ArgumentException("The time limit must be positive.");
        var ranges = variables.Count == 0 ? Array.Empty<GlobalVariableRange>()
            : options.Ranges ?? GlobalTrials.DefaultRanges(optics, catalogs, variables, options.CurvatureLimit);
        GlobalTrials.Check(ranges, variables);
        IReadOnlyList<SubstitutionCandidate> pool = Array.Empty<SubstitutionCandidate>();
        if (substitute.Count > 0)
        {
            pool = GlassSubstitutionTemplate.Candidates(catalogs, GlassVariables.Wavelengths(optics), optics.GlassSubstitution);
            if (pool.Count == 0)
                throw new InvalidOperationException("No glass of the active catalogs is free for substitution, untabulated, valid at every wavelength of the lens"
                    + (optics.GlassSubstitution is { UseTemplate: true } ? " and within the Glass Substitution Template" : "") + ", so there is nothing to substitute.");
        }
        var best = new GlobalBestList(options.SaveCount, ranges, options.FormTolerance);
        var token = options.CancellationToken;
        var inner = GlobalTrials.Inner(options.Local, options.MaxEvaluationsPerTrial, token);
        double startMerit = GlobalTrials.EvaluateStart(optics, catalogs, function, registry, inner, options.MaxDegreeOfParallelism).Value;
        log.Insert(0, $"Hammer search: monotonic basin hopping (Wales and Doye 1997) with glass substitution; "
            + $"{options.Rounds} round{(options.Rounds == 1 ? "" : "s")} of {options.TrialsPerRound} trial{(options.TrialsPerRound == 1 ? "" : "s")}, step {GlobalTrials.F(options.StepFraction)} of each range, "
            + $"{variables.Count} variable{(variables.Count == 1 ? "" : "s")}, {substitute.Count} surface{(substitute.Count == 1 ? "" : "s")} to substitute ({pool.Count} candidate glass{(pool.Count == 1 ? "" : "es")}), "
            + $"seed {options.Seed.ToString(CultureInfo.InvariantCulture)}. Merit function of the start: {GlobalTrials.F(startMerit)}.");

        GlobalDesign Design(GlobalTrials.Outcome o, int trial, int round) => new()
        {
            Design = o.Design!, Merit = o.Merit, ConstraintsMet = o.Met, Values = o.Values!, Glasses = GlobalTrials.GlassesOf(o.Design!, substitute),
            Trial = trial, Iteration = round,
        };

        var start = GlobalTrials.Run(optics, catalogs, function, registry, inner, variables);
        if (start.Cancelled)
            return Result(Array.Empty<GlobalDesign>(), GlobalSearchStop.Cancelled, 0, 0, 0, log, "Stopped before the start was re-optimized.");
        if (!start.Ok) throw new InvalidOperationException($"The start could not be re-optimized, so the search cannot begin: {start.Error}");
        var current = Design(start, 0, 0);
        best.Offer(current);
        int trials = 1, failed = 0, rounds = 0, improvements = 0;
        var failures = new List<string>();
        log.Add($"Start re-optimized: merit {GlobalTrials.F(current.Merit)}{(current.ConstraintsMet ? "" : " (exact constraints not met)")}.");
        options.Progress?.Invoke(new GlobalSearchProgress(GlobalSearchKind.Hammer, 0, trials, failed, startMerit, best.Snapshot()));

        var clock = Stopwatch.StartNew();
        var stop = GlobalSearchStop.IterationLimit;
        int k = options.TrialsPerRound;
        for (int round = 1; round <= options.Rounds; round++)
        {
            if (token.IsCancellationRequested) { stop = GlobalSearchStop.Cancelled; break; }
            if (options.TimeLimit is { } t && clock.Elapsed >= t) { stop = GlobalSearchStop.TimeLimit; break; }
            var from = current;
            int r = round;
            var outcomes = ParallelSlots.Compute(k, options.MaxDegreeOfParallelism, i =>
            {
                var rng = SplitMix64.ForStream(options.Seed, (long)(r - 1) * k + i);
                var (design, what) = Trial(from, rng);
                return (Outcome: design is null ? null : GlobalTrials.Run(design, catalogs, function, registry, inner, variables), What: what);
            });
            bool cancelled = false;
            GlobalDesign? winner = null;
            for (int i = 0; i < k; i++)
            {
                int trial = (round - 1) * k + i + 1;
                var (o, what) = outcomes[i];
                if (o is null) continue;   // nothing to change (no other glass); not a trial
                if (o.Cancelled) { cancelled = true; continue; }
                trials++;
                if (!o.Ok)
                {
                    failed++;
                    if (failures.Count < 20) failures.Add($"Trial {trial} (round {round}, {what}) could not be evaluated: {o.Error}");
                    continue;
                }
                var design = Design(o, trial, round);
                best.Offer(design);
                if (winner is null || GlobalTrials.Better(design.ConstraintsMet, design.Merit, winner.ConstraintsMet, winner.Merit)) winner = design;
            }
            if (cancelled) { stop = GlobalSearchStop.Cancelled; break; }
            rounds = round;
            if (winner is not null && (winner.ConstraintsMet != current.ConstraintsMet
                    ? winner.ConstraintsMet
                    : winner.Merit < current.Merit * (1 - options.RelativeImprovement)))
            {
                log.Add($"Round {round}: merit {GlobalTrials.F(current.Merit)} → {GlobalTrials.F(winner.Merit)} (trial {winner.Trial}"
                    + $"{Changes(current, winner)}).");
                current = winner;
                improvements++;
            }
            options.Progress?.Invoke(new GlobalSearchProgress(GlobalSearchKind.Hammer, round, trials, failed, startMerit, best.Snapshot()));
        }

        log.AddRange(failures);
        if (failed > failures.Count) log.Add($"… {failed - failures.Count} more trials could not be evaluated.");
        return Result(best.Snapshot(), stop, rounds, trials, failed, log, stop switch
        {
            GlobalSearchStop.Cancelled => $"Stopped: the designs found so far are kept ({improvements} improvement{(improvements == 1 ? "" : "s")}).",
            GlobalSearchStop.TimeLimit => $"Stopped at the time limit after round {rounds} ({improvements} improvement{(improvements == 1 ? "" : "s")}).",
            _ => $"Finished the set number of rounds ({improvements} improvement{(improvements == 1 ? "" : "s")}).",
        });

        // One trial's design from the current one, and what was changed.
        (OpticsSettings? Design, string What) Trial(GlobalDesign from, SplitMix64 rng)
        {
            bool glass = pool.Count > 0 && (variables.Count == 0 || rng.NextDouble() < options.GlassChangeProbability);
            if (glass)
            {
                int which = (int)(rng.NextDouble() * substitute.Count);
                int j = substitute[which];
                var others = pool.Where(c => !GlassVariables.IsSameGlass(from.Glasses[which], c, catalogs)).ToArray();
                if (others.Length > 0)
                {
                    var pick = others[(int)(rng.NextDouble() * others.Length)];
                    // As OP-59's search does: while the current design misses its exact constraints its
                    // variables may sit somewhere degenerate (a curvature driven towards zero by an
                    // unreachable colour constraint), so the glass trial starts from the user's
                    // variables with the current glasses instead.
                    var baseDesign = from.ConstraintsMet ? from.Design : WithGlassesOf(from);
                    var list = baseDesign.Surfaces!.ToList();
                    list[j] = GlassVariables.WithGlass(list[j], pick.MaterialText);
                    return (baseDesign with { Surfaces = list }, $"surface {j} glass {pick.MaterialText}");
                }
                if (variables.Count == 0) return (null, $"surface {j}: no other glass");
            }
            var values = new double[variables.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = from.Values[i] + (2 * rng.NextDouble() - 1) * options.StepFraction * ranges[i].Width;
            return (LensVariables.Apply(from.Design, variables, values), "variables moved");
        }

        // The start with the glasses of `design` on the substitute surfaces.
        OpticsSettings WithGlassesOf(GlobalDesign design)
        {
            var list = optics.Surfaces!.ToList();
            for (int i = 0; i < substitute.Count; i++)
            {
                int j = substitute[i];
                if (!string.Equals(design.Glasses[i], SavedSurfaces.FormatMaterial(list[j]), StringComparison.OrdinalIgnoreCase))
                    list[j] = GlassVariables.WithGlass(list[j], design.Glasses[i]);
            }
            return optics with { Surfaces = list };
        }

        string Changes(GlobalDesign a, GlobalDesign b)
        {
            var parts = new List<string>();
            for (int i = 0; i < substitute.Count; i++)
                if (!string.Equals(a.Glasses[i], b.Glasses[i], StringComparison.OrdinalIgnoreCase))
                    parts.Add($"surface {substitute[i]} {a.Glasses[i]} → {b.Glasses[i]}");
            return parts.Count > 0 ? "; " + string.Join(", ", parts) : "";
        }

        GlobalSearchResult Result(IReadOnlyList<GlobalDesign> list, GlobalSearchStop why, int iterations, int trialCount, int failedCount, List<string> lines, string last)
        {
            lines.Add(last);
            for (int i = 0; i < list.Count; i++)
                lines.Add($"{i + 1}. merit {GlobalTrials.F(list[i].Merit)}{(list[i].ConstraintsMet ? "" : " (exact constraints not met)")}, trial {list[i].Trial}"
                    + (substitute.Count > 0 ? $", {string.Join(", ", list[i].Glasses)}" : "") + ".");
            return new GlobalSearchResult
            {
                Kind = GlobalSearchKind.Hammer, Start = optics, StartMerit = startMerit, Variables = variables, Ranges = ranges,
                SubstituteSurfaces = substitute.ToArray(), Best = list, Stop = why, Iterations = iterations,
                Trials = trialCount, FailedTrials = failedCount, Seed = options.Seed, Log = lines,
            };
        }
    }
}
