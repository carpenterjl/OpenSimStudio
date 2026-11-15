using System.Diagnostics;
using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;

namespace OpenSim.Optics.Optimization.Global;

// OP-58: the Global search.

/// <summary>Settings of <see cref="GlobalSearch.Run"/>. Every member has a usable default.</summary>
public sealed class GlobalSearchOptions
{
    /// <summary>The local optimization of each trial: algorithm, cycles (infinite runs as
    /// automatic), scales, constraint settings. Its thread limit, evaluation limit, cancellation
    /// and progress are replaced by this search's. Null takes the defaults (damped least squares,
    /// automatic cycles).</summary>
    public LocalOptimizationOptions? Local { get; init; }

    /// <summary>Merit function evaluations per trial's local optimization (each augmented-
    /// Lagrangian round has its own).</summary>
    public int MaxEvaluationsPerTrial { get; init; } = 2000;

    /// <summary>The range of each variable's random start, in the order of
    /// <see cref="LensVariables.Find"/>; null takes <see cref="GlobalTrials.DefaultRanges"/>.</summary>
    public IReadOnlyList<GlobalVariableRange>? Ranges { get; init; }

    /// <summary>The curvature range ±this (1/lens unit) of the default ranges; null is
    /// 3/|EFL| of the start.</summary>
    public double? CurvatureLimit { get; init; }

    /// <summary>The population NP (at least 4, Storn and Price 1997, §2); null is max(8, 2D)
    /// for D variables.</summary>
    public int? PopulationSize { get; init; }

    /// <summary>Generations after the first population; 0 is a plain multi-start.</summary>
    public int Generations { get; init; } = 10;

    /// <summary>The differential weight F (Storn and Price 1997, eq. 2; "F = 0.5 is usually a
    /// good initial choice", §4).</summary>
    public double DifferentialWeight { get; init; } = 0.5;

    /// <summary>The crossover constant CR (Storn and Price 1997, eq. 4; "to first try CR = 0.9
    /// or CR = 1.0 is appropriate", §4).</summary>
    public double Crossover { get; init; } = 0.9;

    /// <summary>Member 0 of the first population is the start itself (re-optimized).</summary>
    public bool IncludeStart { get; init; } = true;

    /// <summary>The seed: a run is a fixed function of the seed and the settings.</summary>
    public long Seed { get; init; } = 1;

    /// <summary>The designs kept (OpticStudio's "# To Save"; its dialog shows ten).</summary>
    public int SaveCount { get; init; } = 10;

    /// <summary>The form tolerance of <see cref="GlobalBestList"/> (a fraction of each range).</summary>
    public double FormTolerance { get; init; } = 0.01;

    /// <summary>Checked between generations; null for none. A run stopped by it equals a run
    /// with that many generations.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>The thread limit across trials; null for none. The result is bitwise the same
    /// at every setting.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Called on the search's thread after each generation; null for none.</summary>
    public Action<GlobalSearchProgress>? Progress { get; init; }
}

/// <summary>
/// The Global search (plan item OP-58; docs/Optics-Conventions.md §43): new design forms from
/// a merit function and a rough start, keeping the best ten (OpticStudio 2025 R2 help, Global
/// Optimizer). OpticStudio's algorithm is unpublished ("a combination of genetic algorithms,
/// multi-start, conventional damped least squares, and some expert system heuristics", help:
/// Global Optimization Capabilities of OpticStudio); this program's algorithm is its own choice
/// from the literature:
/// <list type="bullet">
/// <item>Differential evolution DE/rand/1/bin (R. Storn and K. Price, "Differential Evolution –
/// A Simple and Efficient Heuristic for Global Optimization over Continuous Spaces", J. Global
/// Optim. 11, 341–359, 1997, eqs. 1–4): a population of NP vectors; for target i a mutant
/// v = x_r1 + F(x_r2 − x_r3) with r1, r2, r3 distinct and ≠ i, clipped into the ranges (this
/// program's choice); a trial u taking v_j where randb(j) ≤ CR or j = rnbr(i), else x_ij; u
/// replaces x_i when better (the greedy criterion). The first population is uniform in the
/// ranges ("should cover the entire parameter space"), with the start as member 0.</item>
/// <item>Every trial vector is locally optimized by OP-57's <see cref="LocalOptimizer"/> and the
/// population holds the optimized designs, so the evolution runs on the transformed surface
/// Ẽ(X) = min{E(X)} of basin hopping (D. J. Wales and J. P. K. Doye, J. Phys. Chem. A 101, 5111,
/// 1997; arXiv:cond-mat/9803344, eq. 2), "reset to that of the minimum after each successful
/// step".</item>
/// </list>
/// "Better" puts a design that meets the exact (negative-weight) constraints above one that does
/// not, then the lower merit; a trial that cannot be evaluated never replaces its target (a
/// failed member of the first population keeps its unoptimized vector with an infinite merit).
/// Every optimized trial is offered to the <see cref="GlobalBestList"/>.
/// <para>
/// <b>Determinism.</b> Trial t (= generation × NP + i) draws only from
/// <see cref="SplitMix64.ForStream"/>(seed, t); the trials of a generation read the previous
/// generation's population and run in parallel slots, and selection and the best list take them
/// in index order, so a seed reproduces a run bitwise at any thread count. Stop returns the
/// designs finished so far.
/// </para>
/// </summary>
public static class GlobalSearch
{
    /// <exception cref="ArgumentException">No variables, invalid settings or merit function.</exception>
    /// <exception cref="InvalidOperationException">The merit function fails on the start.</exception>
    public static GlobalSearchResult Run(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs,
        GlobalSearchOptions? options = null, MeritFunction? function = null, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new GlobalSearchOptions();
        registry ??= MeritOperandRegistry.CreateDefault();
        var log = new List<string>();
        function = GlobalTrials.Resolve(optics, function, registry, log);
        var variables = LensVariables.Find(optics);
        if (variables.Count == 0)
            throw new ArgumentException("There are no variables: flag cells Variable in the Lens Data Editor or the Multi-Configuration Editor.");
        int d = variables.Count;
        int np = options.PopulationSize ?? Math.Max(8, 2 * d);
        if (np < 4) throw new ArgumentException($"The population must be at least 4 (differential evolution draws three others for each member), not {np}.");
        if (options.Generations < 0) throw new ArgumentException($"The number of generations must be 0 or more, not {options.Generations}.");
        if (!(options.DifferentialWeight > 0 && options.DifferentialWeight <= 2))
            throw new ArgumentException($"The differential weight F must be in (0, 2], not {GlobalTrials.F(options.DifferentialWeight)}.");
        if (!(options.Crossover >= 0 && options.Crossover <= 1))
            throw new ArgumentException($"The crossover constant CR must be in [0, 1], not {GlobalTrials.F(options.Crossover)}.");
        if (options.MaxEvaluationsPerTrial < 1) throw new ArgumentException("The evaluation limit per trial must be at least 1.");
        if (options.TimeLimit is { } limit && limit <= TimeSpan.Zero) throw new ArgumentException("The time limit must be positive.");
        var ranges = options.Ranges ?? GlobalTrials.DefaultRanges(optics, catalogs, variables, options.CurvatureLimit);
        GlobalTrials.Check(ranges, variables);
        var best = new GlobalBestList(options.SaveCount, ranges, options.FormTolerance);

        var token = options.CancellationToken;
        var inner = GlobalTrials.Inner(options.Local, options.MaxEvaluationsPerTrial, token);
        double startMerit = GlobalTrials.EvaluateStart(optics, catalogs, function, registry, inner, options.MaxDegreeOfParallelism).Value;
        var startValues = LensVariables.Values(optics, variables);
        var noGlasses = Array.Empty<int>();
        log.Insert(0, $"Global search: differential evolution DE/rand/1/bin (Storn and Price 1997) of locally optimized designs; "
            + $"population {np}, F {GlobalTrials.F(options.DifferentialWeight)}, CR {GlobalTrials.F(options.Crossover)}, {options.Generations} generation{(options.Generations == 1 ? "" : "s")}, "
            + $"seed {options.Seed.ToString(CultureInfo.InvariantCulture)}, {d} variable{(d == 1 ? "" : "s")}. Merit function of the start: {GlobalTrials.F(startMerit)}.");

        // The population: values, merit, constraints met.
        var x = new double[np][];
        var merit = new double[np];
        var met = new bool[np];
        int trials = 0, failed = 0, generations = 0;
        var failures = new List<string>();
        var clock = Stopwatch.StartNew();
        var stop = GlobalSearchStop.IterationLimit;

        for (int g = 0; g <= options.Generations; g++)
        {
            if (token.IsCancellationRequested) { stop = GlobalSearchStop.Cancelled; break; }
            if (g > 0 && options.TimeLimit is { } t && clock.Elapsed >= t) { stop = GlobalSearchStop.TimeLimit; break; }
            int generation = g;
            var snapshot = generation == 0 ? null : x.Select(v => (double[])v.Clone()).ToArray();
            var candidates = new double[np][];
            for (int i = 0; i < np; i++)
                candidates[i] = generation == 0 ? FirstMember(i) : TrialVector(snapshot!, i, Stream(generation, i));
            var outcomes = ParallelSlots.Compute(np, options.MaxDegreeOfParallelism,
                i => GlobalTrials.Run(LensVariables.Apply(optics, variables, candidates[i]), catalogs, function, registry, inner, variables));

            bool cancelled = false;
            for (int i = 0; i < np; i++)
            {
                var o = outcomes[i];
                int trial = generation * np + i;
                if (o.Cancelled) { cancelled = true; if (generation == 0) { x[i] = candidates[i]; merit[i] = double.PositiveInfinity; } continue; }
                trials++;
                if (!o.Ok)
                {
                    failed++;
                    if (failures.Count < 20) failures.Add($"Trial {trial} (generation {generation}, member {i}) could not be evaluated: {o.Error}");
                    if (generation == 0) { x[i] = candidates[i]; merit[i] = double.PositiveInfinity; met[i] = false; }
                    continue;
                }
                best.Offer(new GlobalDesign
                {
                    Design = o.Design!, Merit = o.Merit, ConstraintsMet = o.Met, Values = o.Values!, Glasses = Array.Empty<string>(),
                    Trial = trial, Iteration = generation,
                });
                if (generation == 0 || GlobalTrials.Better(o.Met, o.Merit, met[i], merit[i]))
                {
                    x[i] = o.Values!;
                    merit[i] = o.Merit;
                    met[i] = o.Met;
                }
            }
            if (cancelled) { stop = GlobalSearchStop.Cancelled; break; }
            generations = generation;
            var items = best.Items;
            log.Add($"Generation {generation}: {(items.Count > 0 ? $"best merit {GlobalTrials.F(items[0].Merit)}{(items[0].ConstraintsMet ? "" : " (exact constraints not met)")}" : "no design yet")}; {trials} trials, {failed} failed.");
            options.Progress?.Invoke(new GlobalSearchProgress(GlobalSearchKind.Global, generation, trials, failed, startMerit, best.Snapshot()));
        }

        log.AddRange(failures);
        if (failed > failures.Count) log.Add($"… {failed - failures.Count} more trials could not be evaluated.");
        log.Add(stop switch
        {
            GlobalSearchStop.Cancelled => "Stopped: the designs found so far are kept.",
            GlobalSearchStop.TimeLimit => $"Stopped at the time limit after generation {generations}.",
            _ => "Finished the set number of generations.",
        });
        var list = best.Snapshot();
        for (int k = 0; k < list.Count; k++)
            log.Add($"{k + 1}. merit {GlobalTrials.F(list[k].Merit)}{(list[k].ConstraintsMet ? "" : " (exact constraints not met)")}, trial {list[k].Trial}.");
        return new GlobalSearchResult
        {
            Kind = GlobalSearchKind.Global, Start = optics, StartMerit = startMerit, Variables = variables, Ranges = ranges,
            SubstituteSurfaces = noGlasses, Best = list, Stop = stop, Iterations = stop == GlobalSearchStop.Cancelled && trials == 0 ? 0 : generations,
            Trials = trials, FailedTrials = failed, Seed = options.Seed, Log = log,
        };

        SplitMix64 Stream(int generation, int i) => SplitMix64.ForStream(options.Seed, (long)generation * np + i);

        double[] FirstMember(int i)
        {
            if (i == 0 && options.IncludeStart) return (double[])startValues.Clone();
            var rng = Stream(0, i);
            var v = new double[d];
            for (int j = 0; j < d; j++) v[j] = ranges[j].Lower + ranges[j].Width * rng.NextDouble();
            return v;
        }

        // Storn and Price 1997, eqs. 2 (mutation) and 4 (binomial crossover).
        double[] TrialVector(double[][] pop, int i, SplitMix64 rng)
        {
            int r1 = Pick(rng, i, -1, -1), r2 = Pick(rng, i, r1, -1), r3 = Pick(rng, i, r1, r2);
            int rnbr = (int)(rng.NextDouble() * d);
            var u = new double[d];
            for (int j = 0; j < d; j++)
            {
                double v = pop[r1][j] + options.DifferentialWeight * (pop[r2][j] - pop[r3][j]);
                v = Math.Clamp(v, ranges[j].Lower, ranges[j].Upper);
                bool take = rng.NextDouble() <= options.Crossover || j == rnbr;
                u[j] = take ? v : pop[i][j];
            }
            return u;
        }

        int Pick(SplitMix64 rng, int not0, int not1, int not2)
        {
            while (true)
            {
                int r = (int)(rng.NextDouble() * np);
                if (r != not0 && r != not1 && r != not2) return r;
            }
        }
    }
}
