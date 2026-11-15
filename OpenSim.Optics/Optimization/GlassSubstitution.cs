using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization;

// OP-59: the discrete glass substitution search.

/// <summary>One candidate glass tried on one surface.</summary>
/// <param name="Round">The round (1-based).</param>
/// <param name="Surface">The surface whose glass was replaced (0-based).</param>
/// <param name="Glass">The glass tried, "catalog:glass".</param>
/// <param name="Merit">The merit function after re-optimizing with it; null when it failed.</param>
/// <param name="Error">Why the trial failed (the lens could not be built or the merit function
/// not evaluated with this glass); null when it worked.</param>
/// <remarks>Error is the reason a trial failed (Merit null), or a note on a trial that ran but
/// whose exact constraints are not met (Merit set).</remarks>
public sealed record GlassSubstitutionTrial(int Round, int Surface, string Glass, double? Merit, string? Error);

/// <summary>Progress after each trial.</summary>
public sealed record GlassSubstitutionProgress(int Round, int Surface, string Glass, int Trials, double BestMerit, double StartMerit);

/// <summary>Settings of <see cref="GlassSubstitution.Run"/>. Every member has a usable default.</summary>
public sealed class GlassSubstitutionOptions
{
    /// <summary>The local optimization run for every trial (its cancellation token and progress
    /// callback are replaced by this search's). Null takes the defaults (damped least squares,
    /// automatic cycles).</summary>
    public LocalOptimizationOptions? Optimization { get; init; }

    /// <summary>The surfaces whose glass may be replaced (0-based); null takes the surfaces
    /// flagged for substitution (<see cref="GlassVariables.SubstituteSurfaces"/>).</summary>
    public IReadOnlyList<int>? Surfaces { get; init; }

    /// <summary>The most rounds (a round tries every candidate on every surface once).</summary>
    public int MaxRounds { get; init; } = 3;

    /// <summary>A trial replaces the best design only when its merit is below
    /// best·(1 − this); ties keep the earlier glass.</summary>
    public double RelativeImprovement { get; init; } = 1e-9;

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Called on the search's thread after every trial; null for none.</summary>
    public Action<GlassSubstitutionProgress>? Progress { get; init; }
}

/// <summary>The outcome of <see cref="GlassSubstitution.Run"/>.</summary>
public sealed record GlassSubstitutionResult
{
    /// <summary>The record the search started from.</summary>
    public required OpticsSettings Start { get; init; }

    /// <summary>The best design: its glasses and its re-optimized variables.</summary>
    public required OpticsSettings Best { get; init; }

    /// <summary>The merit function of <see cref="Start"/> as given.</summary>
    public required double StartMerit { get; init; }

    /// <summary>The merit function of the start after re-optimizing with its own glasses: what
    /// the substitutes had to beat.</summary>
    public required double ReoptimizedStartMerit { get; init; }

    public required double Merit { get; init; }

    /// <summary>The surfaces searched and their glass before and after ("catalog:glass" or the
    /// cell's text).</summary>
    public required IReadOnlyList<(int Surface, string Before, string After)> Glasses { get; init; }

    public required IReadOnlyList<GlassSubstitutionTrial> Trials { get; init; }

    public int Rounds { get; init; }

    public bool Cancelled { get; init; }

    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Glass substitution by discrete search (plan item OP-59; docs/Optics-Conventions.md §41). The
/// help describes the method ("Global Optimization of Glass Selection", "Using Glass
/// Substitution": "directly alter the glass types, and then reoptimize to see if the new glasses
/// yield a better solution") but not OpticStudio's search order, which runs inside its Hammer
/// and Global optimizers (not built). This program's search is a coordinate search:
/// <list type="number">
/// <item>The start is re-optimized with its own glasses (<see cref="LocalOptimizer"/>; with no
/// variables the merit function is only evaluated). That is the best design.</item>
/// <item>A round takes the surfaces in order; on each it tries every candidate glass
/// (<see cref="GlassSubstitutionTemplate.Candidates"/>: free for substitution, untabulated, valid
/// at every wavelength, within the template, from the template's catalogs) other than the
/// current one, each time starting from the best design and re-optimizing, and keeps the best
/// trial when it beats the best design by more than <see cref="GlassSubstitutionOptions.RelativeImprovement"/>.</item>
/// <item>Rounds repeat until one changes no glass or <see cref="GlassSubstitutionOptions.MaxRounds"/>.</item>
/// </list>
/// A trial whose lens or merit function fails is recorded with its reason and skipped. Trials run
/// one after another, so the result does not depend on threads beyond what the local optimizer
/// already guarantees (bitwise identical at any thread count). Cancellation returns the best
/// design so far. The glass names written are "catalog:glass".
/// </summary>
public static class GlassSubstitution
{
    /// <exception cref="ArgumentException">No surface to substitute, a surface without a glass,
    /// a template catalog that is not active, an empty or invalid merit function.</exception>
    /// <exception cref="InvalidOperationException">A surface has no candidate glass, or the start
    /// cannot be evaluated.</exception>
    public static GlassSubstitutionResult Run(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs,
        GlassSubstitutionOptions? options = null, MeritFunction? function = null, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new GlassSubstitutionOptions();
        registry ??= MeritOperandRegistry.CreateDefault();
        if (options.MaxRounds < 1) throw new ArgumentException("Glass substitution needs at least one round.");
        var token = options.CancellationToken;
        var surfaces = (optics.Surfaces ?? new List<OpticsSurfaceSettings>()).ToList();
        var which = options.Surfaces ?? GlassVariables.SubstituteSurfaces(optics);
        if (which.Count == 0)
            throw new ArgumentException("No surface is flagged for glass substitution: tick Substitute for the glasses that may change.");
        foreach (int j in which)
        {
            if (j < 0 || j >= surfaces.Count) throw new ArgumentException($"Glass substitution names surface {j}; the lens has surfaces 0 to {surfaces.Count - 1}.");
            var s = surfaces[j];
            bool mirror = string.Equals(s?.Material?.Trim(), SavedSurfaces.Mirror, StringComparison.OrdinalIgnoreCase);
            if (s is null || mirror || (string.IsNullOrWhiteSpace(s.Material) && s.ModelGlass is null))
                throw new ArgumentException($"{SavedSurfaces.Where(j, surfaces.Count)} has no glass to substitute (it is {(mirror ? "a mirror" : "air or a constant index")}).");
        }
        var wavelengths = GlassVariables.Wavelengths(optics);
        var pool = GlassSubstitutionTemplate.Candidates(catalogs, wavelengths, optics.GlassSubstitution);
        if (pool.Count == 0)
            throw new InvalidOperationException("No glass of the active catalogs is free for substitution, untabulated, valid at every wavelength of the lens"
                + (optics.GlassSubstitution is { UseTemplate: true } ? " and within the Glass Substitution Template" : "") + ", so there is nothing to substitute.");

        var trials = new List<GlassSubstitutionTrial>();
        var log = new List<string>();
        var localOptions = options.Optimization ?? new LocalOptimizationOptions();
        double startMerit = Evaluate(optics, catalogs, function, registry, localOptions).Value;
        var (best, bestMerit, bestMet) = Optimize(optics, catalogs, function, registry, localOptions, token)
            ?? throw new InvalidOperationException("The start was cancelled before it was re-optimized.");
        double reoptimizedStart = bestMerit;
        log.Add($"Glass substitution on {string.Join(", ", which.Select(j => SavedSurfaces.Where(j, surfaces.Count).ToLowerInvariant()))}: "
            + $"{pool.Count} candidate glass{(pool.Count == 1 ? "" : "es")}; merit {F(startMerit)}, {F(reoptimizedStart)} re-optimized with the starting glasses"
            + (bestMet ? "." : " (its constraints are not met)."));

        int rounds = 0;
        bool cancelled = false;
        for (int round = 1; round <= options.MaxRounds && !cancelled; round++)
        {
            rounds = round;
            bool changed = false;
            foreach (int j in which)
            {
                string current = MaterialText(best.Surfaces![j]);
                (OpticsSettings Design, double Merit, bool Met, string Glass)? winner = null;
                foreach (var candidate in pool)
                {
                    if (token.IsCancellationRequested) { cancelled = true; break; }
                    string text = candidate.MaterialText;
                    if (SameGlass(current, candidate, catalogs)) continue;
                    // While the best design does not meet its exact constraints its variables may
                    // sit somewhere degenerate (a curvature driven to ~1e-31 by an unreachable
                    // colour constraint, whose relative derivative step then freezes it), so the
                    // trial starts from the user's own design with the current glasses instead.
                    var baseDesign = bestMet ? best : WithGlassesOf(optics, best, which);
                    var list = baseDesign.Surfaces!.ToList();
                    list[j] = list[j] with { Material = text, ModelGlass = null, Index = null };
                    foreach (string column in GlassVariables.ModelColumns) list[j] = GlassVariables.WithFlag(list[j], column, false);
                    var trial = baseDesign with { Surfaces = list };
                    try
                    {
                        var outcome = Optimize(trial, catalogs, function, registry, localOptions, token);
                        if (outcome is null) { cancelled = true; break; }
                        trials.Add(new GlassSubstitutionTrial(round, j, text, outcome.Value.Merit,
                            outcome.Value.Met ? null : "the exact (negative-weight) constraints are not met with this glass"));
                        double reference = winner?.Merit ?? bestMerit;
                        bool referenceMet = winner?.Met ?? bestMet;
                        // A design that meets the exact constraints always beats one that does not;
                        // the merit value excludes the constraint rows, so it only ranks designs of
                        // equal standing.
                        bool better = outcome.Value.Met != referenceMet
                            ? outcome.Value.Met
                            : outcome.Value.Merit < reference * (1 - options.RelativeImprovement);
                        if (better) winner = (outcome.Value.Design, outcome.Value.Merit, outcome.Value.Met, text);
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                        trials.Add(new GlassSubstitutionTrial(round, j, text, null, e.Message));
                    }
                    options.Progress?.Invoke(new GlassSubstitutionProgress(round, j, text, trials.Count, Math.Min(bestMerit, winner?.Merit ?? bestMerit), startMerit));
                }
                if (winner is { } w)
                {
                    log.Add($"Round {round}, {SavedSurfaces.Where(j, surfaces.Count).ToLowerInvariant()}: {current} → {w.Glass}, merit {F(bestMerit)} → {F(w.Merit)}.");
                    best = w.Design;
                    bestMerit = w.Merit;
                    bestMet = w.Met;
                    changed = true;
                }
                if (cancelled) break;
            }
            if (!changed && !cancelled) { log.Add($"Round {round} changed no glass: done."); break; }
        }
        int failed = trials.Count(t => t.Merit is null);
        if (!bestMet) log.Add("No design tried meets the exact (negative-weight) constraints; the best by merit is kept.");
        if (failed > 0) log.Add($"{failed} trial{(failed == 1 ? "" : "s")} failed (the lens or the merit function could not be evaluated with that glass); each is listed with its reason.");
        if (cancelled) log.Add("Stopped: the best design so far is kept.");
        log.Add($"Merit {F(startMerit)} → {F(bestMerit)}.");
        return new GlassSubstitutionResult
        {
            Start = optics, Best = best, StartMerit = startMerit, ReoptimizedStartMerit = reoptimizedStart, Merit = bestMerit,
            Glasses = which.Select(j => (j, MaterialText(surfaces[j]), MaterialText(best.Surfaces![j]))).ToArray(),
            Trials = trials, Rounds = rounds, Cancelled = cancelled, Log = log,
        };
    }

    /// <summary>The design re-optimized (or only evaluated when it has no variables) and its
    /// merit; null when cancelled. Throws when the merit function cannot be evaluated.</summary>
    private static (OpticsSettings Design, double Merit, bool Met)? Optimize(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs,
        MeritFunction? function, MeritOperandRegistry registry, LocalOptimizationOptions local, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        if (LensVariables.Find(optics).Count == 0)
        {
            var e = Evaluate(optics, catalogs, function, registry, local);
            if (e.HasFailures) throw new InvalidOperationException(string.Join(Environment.NewLine, e.Errors));
            return (optics, e.Value, true);
        }
        var result = LocalOptimizer.Run(optics, catalogs, new LocalOptimizationOptions
        {
            Algorithm = local.Algorithm, Cycles = local.Cycles == LocalOptimizationCycles.Infinite ? LocalOptimizationCycles.Automatic : local.Cycles,
            CycleCount = local.CycleCount, Kinds = local.Kinds, MaxDegreeOfParallelism = local.MaxDegreeOfParallelism,
            MaxEvaluations = local.MaxEvaluations, ConstraintTolerance = local.ConstraintTolerance, MaxConstraintRounds = local.MaxConstraintRounds,
            Merit = local.Merit, CancellationToken = token,
        }, function, registry);
        if (result.Cancelled) return null;
        if (result.Evaluation.HasFailures) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Evaluation.Errors));
        return (result.Optimized, result.Merit, result.Stop != LocalOptimizationStop.ConstraintsNotMet);
    }

    private static MeritEvaluation Evaluate(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, MeritFunction? function,
        MeritOperandRegistry registry, LocalOptimizationOptions local)
    {
        function ??= MeritFunctionSettings.FromSettings(optics.MeritFunction, registry, new List<string>());
        if (function.Rows.Count == 0)
            throw new ArgumentException("The merit function has no rows: build one in the Merit Function Editor before substituting glasses.");
        return MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(optics, catalogs), registry,
            (local.Merit ?? new MeritEvaluationOptions()) with { MaxDegreeOfParallelism = local.MaxDegreeOfParallelism });
    }

    /// <summary><paramref name="design"/> with the glasses (material, model glass, index and the
    /// model-glass flags) of <paramref name="source"/> on the surfaces <paramref name="which"/>.</summary>
    private static OpticsSettings WithGlassesOf(OpticsSettings design, OpticsSettings source, IReadOnlyList<int> which)
    {
        var list = design.Surfaces!.ToList();
        foreach (int j in which)
        {
            var from = source.Surfaces![j];
            list[j] = list[j] with { Material = from.Material, ModelGlass = from.ModelGlass, Index = from.Index };
        }
        return design with { Surfaces = list };
    }

    private static bool SameGlass(string current, SubstitutionCandidate candidate, IReadOnlyList<OpticalCatalog> catalogs) =>
        string.Equals(current, candidate.MaterialText, StringComparison.OrdinalIgnoreCase)
        || ReferenceEquals(Analysis.ProjectLens.FindGlass(current, catalogs), candidate.Glass);

    private static string MaterialText(OpticsSurfaceSettings s) => SavedSurfaces.FormatMaterial(s);

    private static string F(double v) => v.ToString("G10", CultureInfo.InvariantCulture);
}
