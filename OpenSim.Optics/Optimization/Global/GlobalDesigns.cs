using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Optimization.Global;

// OP-58: the parts the Global and Hammer searches share.

/// <summary>Which search produced a result.</summary>
public enum GlobalSearchKind
{
    /// <summary><see cref="GlobalSearch"/>: new design forms from random starts.</summary>
    Global,
    /// <summary><see cref="HammerSearch"/>: perturb and re-optimize the current design.</summary>
    Hammer,
}

/// <summary>Why a search ended.</summary>
public enum GlobalSearchStop
{
    /// <summary>The set number of generations (Global) or rounds (Hammer) ran.</summary>
    IterationLimit,
    /// <summary>The time limit passed; checked between generations or rounds, so the last one
    /// started always finishes.</summary>
    TimeLimit,
    /// <summary>Stop was pressed; the designs finished before it are kept.</summary>
    Cancelled,
}

/// <summary>The interval a variable's random start (Global) or random step (Hammer) is drawn
/// from, in the variable's saved unit (a radius as its curvature). It bounds only the drawing:
/// the local optimizer may leave it.</summary>
public sealed record GlobalVariableRange(double Lower, double Upper)
{
    public double Width => Upper - Lower;
}

/// <summary>One locally optimized design found by a search.</summary>
public sealed record GlobalDesign
{
    /// <summary>The optics record: the variables at their optimized values and, for the Hammer
    /// search, the glasses it chose; everything else as in the start.</summary>
    public required OpticsSettings Design { get; init; }

    /// <summary>The merit function (OpticStudio's value, <see cref="LocalOptimizationResult.Merit"/>).</summary>
    public required double Merit { get; init; }

    /// <summary>Every negative-weight (exact) row is on target within the local optimizer's
    /// constraint tolerance. A design that meets them always ranks above one that does not.</summary>
    public required bool ConstraintsMet { get; init; }

    /// <summary>The variables (saved units, a radius as its curvature), in the order of
    /// <see cref="GlobalSearchResult.Variables"/>.</summary>
    public required IReadOnlyList<double> Values { get; init; }

    /// <summary>The Material text of each surface of <see cref="GlobalSearchResult.SubstituteSurfaces"/>.</summary>
    public required IReadOnlyList<string> Glasses { get; init; }

    /// <summary>The trial's number in the run (0 is the first); it breaks ties of merit and
    /// depends only on the seed and the settings, never on the threads.</summary>
    public required int Trial { get; init; }

    /// <summary>The generation (Global) or round (Hammer) of the trial; 0 for the start.</summary>
    public required int Iteration { get; init; }

    /// <summary>The order of the best list: a design that meets the exact constraints first
    /// (the rule of OP-59's <see cref="GlassSubstitution"/>), then the lower merit, then the
    /// earlier trial.</summary>
    public static int Compare(GlobalDesign a, GlobalDesign b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.ConstraintsMet != b.ConstraintsMet) return a.ConstraintsMet ? -1 : 1;
        int byMerit = a.Merit.CompareTo(b.Merit);
        return byMerit != 0 ? byMerit : a.Trial.CompareTo(b.Trial);
    }
}

/// <summary>Progress after each generation (Global) or round (Hammer), on the search's thread.</summary>
/// <param name="Iteration">Generations or rounds done (0: the start or the first population).</param>
/// <param name="Trials">Local optimizations finished so far.</param>
/// <param name="FailedTrials">Trials whose design could not be evaluated.</param>
/// <param name="Best">The best list now, best first.</param>
public sealed record GlobalSearchProgress(GlobalSearchKind Kind, int Iteration, int Trials, int FailedTrials, double StartMerit, IReadOnlyList<GlobalDesign> Best);

/// <summary>
/// The list of the best designs a search keeps (OpticStudio 2025 R2 help, Global Optimizer:
/// "each time a new lens is found which is better than the worst in the list of best lenses, it
/// is placed in the correct place in the list"; the dialog shows ten). Ordered by
/// <see cref="GlobalDesign.Compare"/>. To keep the list diverse ("the older design being replaced
/// is rejected if it has the same basic form as the new lens", same page) a design offered while
/// one of the same form is in the list replaces it when better and is dropped otherwise. Two
/// designs have the same form when they have the same glasses and every variable differs by at
/// most <c>formTolerance</c> × the width of its range (this program's definition; OpticStudio
/// does not publish its own).
/// </summary>
public sealed class GlobalBestList
{
    private readonly List<GlobalDesign> _items = new();
    private readonly IReadOnlyList<GlobalVariableRange> _ranges;

    public GlobalBestList(int capacity, IReadOnlyList<GlobalVariableRange> ranges, double formTolerance)
    {
        if (capacity < 1) throw new ArgumentException($"The number of designs to keep must be at least 1, not {capacity}.", nameof(capacity));
        if (!(formTolerance >= 0) || !double.IsFinite(formTolerance))
            throw new ArgumentException($"The form tolerance must be zero or positive, not {formTolerance.ToString(CultureInfo.InvariantCulture)}.", nameof(formTolerance));
        Capacity = capacity;
        _ranges = ranges ?? throw new ArgumentNullException(nameof(ranges));
        FormTolerance = formTolerance;
    }

    public int Capacity { get; }

    public double FormTolerance { get; }

    /// <summary>The designs kept, best first.</summary>
    public IReadOnlyList<GlobalDesign> Items => _items;

    /// <summary>A copy of <see cref="Items"/>.</summary>
    public IReadOnlyList<GlobalDesign> Snapshot() => _items.ToArray();

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> have the same glasses and
    /// every variable within the form tolerance of its range's width.</summary>
    public bool SameForm(GlobalDesign a, GlobalDesign b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Glasses.Count != b.Glasses.Count || a.Values.Count != b.Values.Count) return false;
        for (int i = 0; i < a.Glasses.Count; i++)
            if (!string.Equals(a.Glasses[i], b.Glasses[i], StringComparison.OrdinalIgnoreCase)) return false;
        for (int i = 0; i < a.Values.Count; i++)
        {
            double width = i < _ranges.Count ? _ranges[i].Width : 0;
            if (!(Math.Abs(a.Values[i] - b.Values[i]) <= FormTolerance * width)) return false;
        }
        return true;
    }

    /// <summary>Offers a design; returns true when it is in the list afterwards.</summary>
    public bool Offer(GlobalDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        if (double.IsNaN(design.Merit)) return false;
        var same = _items.Where(e => SameForm(e, design)).ToList();
        if (same.Any(e => GlobalDesign.Compare(e, design) <= 0)) return false;
        foreach (var e in same) _items.Remove(e);
        int at = 0;
        while (at < _items.Count && GlobalDesign.Compare(_items[at], design) < 0) at++;
        _items.Insert(at, design);
        if (_items.Count > Capacity) _items.RemoveAt(_items.Count - 1);
        return _items.Contains(design);
    }
}

/// <summary>The outcome of <see cref="GlobalSearch.Run"/> or <see cref="HammerSearch.Run"/>.</summary>
public sealed record GlobalSearchResult
{
    public required GlobalSearchKind Kind { get; init; }

    /// <summary>The optics record the search started from.</summary>
    public required OpticsSettings Start { get; init; }

    /// <summary>The merit function of <see cref="Start"/> as given.</summary>
    public required double StartMerit { get; init; }

    public required IReadOnlyList<LensVariable> Variables { get; init; }

    /// <summary>The ranges the random starts or steps were drawn from (one per variable).</summary>
    public required IReadOnlyList<GlobalVariableRange> Ranges { get; init; }

    /// <summary>The surfaces whose glass the Hammer search could replace (0-based); empty for Global.</summary>
    public required IReadOnlyList<int> SubstituteSurfaces { get; init; }

    /// <summary>The best designs found, best first (<see cref="GlobalBestList"/>); empty when the
    /// search was stopped before any trial finished.</summary>
    public required IReadOnlyList<GlobalDesign> Best { get; init; }

    public required GlobalSearchStop Stop { get; init; }

    public bool Cancelled => Stop == GlobalSearchStop.Cancelled;

    /// <summary>Generations (Global, not counting the first population) or rounds (Hammer, not
    /// counting the start) finished.</summary>
    public int Iterations { get; init; }

    /// <summary>Local optimizations finished.</summary>
    public int Trials { get; init; }

    /// <summary>Trials whose design could not be evaluated (each named in <see cref="Log"/> when
    /// few, counted otherwise).</summary>
    public int FailedTrials { get; init; }

    public long Seed { get; init; }

    /// <summary>What happened, one line each.</summary>
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    /// <summary>
    /// <paramref name="design"/> written into <paramref name="current"/>: the variable cells as in
    /// the design and, for the Hammer search, each substituted glass; everything else (edits made
    /// since) kept. Throws <see cref="ArgumentException"/> when the variables of
    /// <paramref name="current"/> are no longer the search's.
    /// </summary>
    public OpticsSettings ApplyTo(OpticsSettings current, GlobalDesign design)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(design);
        var now = LensVariables.Find(current);
        if (now.Count != Variables.Count || now.Zip(Variables).Any(p => p.First != p.Second))
            throw new ArgumentException("The variables have changed since the search ran, so the design cannot be applied; run the search again.");
        var result = LensVariables.CopyCells(design.Design, current, Variables);
        if (SubstituteSurfaces.Count == 0) return result;
        var surfaces = (result.Surfaces ?? new List<OpticsSurfaceSettings>()).ToList();
        var startSurfaces = Start.Surfaces ?? new List<OpticsSurfaceSettings>();
        for (int k = 0; k < SubstituteSurfaces.Count; k++)
        {
            int j = SubstituteSurfaces[k];
            if (j >= surfaces.Count || j >= startSurfaces.Count)
                throw new ArgumentException($"Surface {j} is no longer in the lens, so the design cannot be applied; run the search again.");
            string glass = design.Glasses[k];
            if (string.Equals(glass, SavedSurfaces.FormatMaterial(startSurfaces[j]), StringComparison.OrdinalIgnoreCase)) continue;
            surfaces[j] = GlassVariables.WithGlass(surfaces[j], glass);
        }
        return result with { Surfaces = surfaces };
    }
}

/// <summary>Ranges, trials and the merit-function set-up the two searches share.</summary>
public static class GlobalTrials
{
    /// <summary>The default curvature limit in units of 1/|EFL| (this program's choice: a
    /// radius no shorter than a third of the starting focal length, which the help says
    /// OpticStudio uses "as a scaling parameter", The Global Optimization Algorithm).</summary>
    public const double DefaultCurvatureTimesFocalLength = 3;

    /// <summary>
    /// The default ranges (this program's choices, docs/Optics-Conventions.md §43): a curvature
    /// ±<paramref name="curvatureLimit"/> (default 3/|EFL| of the start, configuration 1, lens
    /// units), widened to contain the start's value; a thickness from half to one and a half
    /// times its value (0 to |EFL|/10 for a zero thickness); a model glass's nd with n − 1 from
    /// half to one and a half times; anything else ±50 % of its value, or ±1 when it is zero.
    /// Throws <see cref="ArgumentException"/> when a curvature or zero-thickness variable needs
    /// the focal length and the start has none (afocal, or the lens cannot be built).
    /// </summary>
    public static IReadOnlyList<GlobalVariableRange> DefaultRanges(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs,
        IReadOnlyList<LensVariable> variables, double? curvatureLimit = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(variables);
        if (curvatureLimit is double given && (!(given > 0) || !double.IsFinite(given)))
            throw new ArgumentException($"The curvature limit must be positive and finite, not {given.ToString(CultureInfo.InvariantCulture)}.");
        var values = LensVariables.Values(optics, variables);
        double? focal = null;
        double Focal() => focal ??= FocalLength(optics, catalogs);
        var ranges = new GlobalVariableRange[variables.Count];
        for (int i = 0; i < variables.Count; i++)
        {
            double v = values[i];
            ranges[i] = variables[i].Kind switch
            {
                LensVariableKind.Curvature => Curvature(curvatureLimit ?? DefaultCurvatureTimesFocalLength / Focal(), v),
                LensVariableKind.Thickness => v == 0 ? new GlobalVariableRange(0, Focal() / 10) : Around(v),
                LensVariableKind.ModelIndex => new GlobalVariableRange(Math.Min(1 + 0.5 * (v - 1), 1 + 1.5 * (v - 1)), Math.Max(1 + 0.5 * (v - 1), 1 + 1.5 * (v - 1))),
                _ => v == 0 ? new GlobalVariableRange(-1, 1) : Around(v),
            };
        }
        return ranges;

        static GlobalVariableRange Curvature(double limit, double v) => new(Math.Min(-limit, v), Math.Max(limit, v));
        static GlobalVariableRange Around(double v) => new(Math.Min(0.5 * v, 1.5 * v), Math.Max(0.5 * v, 1.5 * v));
    }

    /// <summary>|EFL| of configuration 1 in lens units; throws <see cref="ArgumentException"/>
    /// when there is none.</summary>
    private static double FocalLength(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs)
    {
        double? efl;
        try
        {
            var configurations = new ProjectMeritConfigurations(optics, catalogs);
            var lens = configurations.LensFor(1);
            efl = ParaxialSystem.Create(lens.ToParaxialLens(), configurations.SystemFor(1)).EffectiveFocalLength;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException($"The starting lens's focal length, which sets the default curvature range, cannot be computed ({e.Message}); enter a curvature limit.");
        }
        if (efl is not double f || !(Math.Abs(f) > 0) || !double.IsFinite(f))
            throw new ArgumentException("The starting lens is afocal, so the default curvature range (a multiple of 1/EFL) does not exist; enter a curvature limit.");
        return Math.Abs(OpticsConventions.FromMeters(f, ProjectLens.LensUnitOf(optics.SystemData)));
    }

    /// <summary>Checks ranges given by the caller (one per variable, finite, lower ≤ upper).</summary>
    internal static void Check(IReadOnlyList<GlobalVariableRange> ranges, IReadOnlyList<LensVariable> variables)
    {
        if (ranges.Count != variables.Count)
            throw new ArgumentException($"There are {variables.Count} variables; {ranges.Count} ranges were given.");
        for (int i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i] ?? throw new ArgumentException($"{variables[i].Label}: no range.");
            if (!double.IsFinite(r.Lower) || !double.IsFinite(r.Upper) || r.Lower > r.Upper)
                throw new ArgumentException($"{variables[i].Label}: the range {F(r.Lower)} to {F(r.Upper)} is not a finite interval.");
        }
    }

    /// <summary>The merit function to use: <paramref name="function"/>, or the saved one (notes
    /// added to <paramref name="log"/>); checked like the local optimizer checks it.</summary>
    internal static MeritFunction Resolve(OpticsSettings optics, MeritFunction? function, MeritOperandRegistry registry, List<string> log)
    {
        if (function is null)
        {
            var notes = new List<string>();
            function = MeritFunctionSettings.FromSettings(optics.MeritFunction, registry, notes);
            log.AddRange(notes);
        }
        if (function.Rows.Count == 0)
            throw new ArgumentException("The merit function has no rows: build one in the Merit Function Editor before optimizing.");
        int configurations = MultiConfig.MultiConfiguration.ConfigurationCount(optics);
        if (MeritEvaluator.Validate(function, registry, configurations) is { Count: > 0 } invalid)
            throw new ArgumentException("Merit function:" + Environment.NewLine + string.Join(Environment.NewLine, invalid));
        return function;
    }

    /// <summary>The merit function of the start; throws <see cref="InvalidOperationException"/>
    /// when it has failed rows (as the local optimizer requires).</summary>
    internal static MeritEvaluation EvaluateStart(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs, MeritFunction function,
        MeritOperandRegistry registry, LocalOptimizationOptions local, int? threads)
    {
        var e = MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(optics, catalogs), registry,
            (local.Merit ?? new MeritEvaluationOptions()) with { MaxDegreeOfParallelism = threads });
        if (e.HasFailures)
            throw new InvalidOperationException("The merit function cannot be evaluated on the starting design, so the search cannot begin:"
                + Environment.NewLine + string.Join(Environment.NewLine, e.Errors));
        return e;
    }

    /// <summary>The local optimization of one trial: the caller's algorithm, cycles (infinite
    /// becomes automatic), scales and constraint settings, on one thread (the trials themselves
    /// run in parallel), with the trial's evaluation limit and the search's cancellation.</summary>
    internal static LocalOptimizationOptions Inner(LocalOptimizationOptions? local, int maxEvaluations, CancellationToken token)
    {
        local ??= new LocalOptimizationOptions();
        return new LocalOptimizationOptions
        {
            Algorithm = local.Algorithm,
            Cycles = local.Cycles == LocalOptimizationCycles.Infinite ? LocalOptimizationCycles.Automatic : local.Cycles,
            CycleCount = local.CycleCount, Kinds = local.Kinds, MaxDegreeOfParallelism = 1, MaxEvaluations = maxEvaluations,
            ConstraintTolerance = local.ConstraintTolerance, MaxConstraintRounds = local.MaxConstraintRounds, Merit = local.Merit,
            CancellationToken = token,
        };
    }

    /// <summary>What one trial gave.</summary>
    internal sealed record Outcome(OpticsSettings? Design, double Merit, bool Met, double[]? Values, string? Error, bool Cancelled)
    {
        public bool Ok => Design is not null;
        public static readonly Outcome WasCancelled = new(null, double.NaN, false, null, null, true);
    }

    /// <summary>Locally optimizes <paramref name="design"/> (only evaluates it when it has no
    /// variables). Never throws for a design that cannot be built or evaluated: the outcome
    /// carries the reason.</summary>
    internal static Outcome Run(OpticsSettings design, IReadOnlyList<OpticalCatalog> catalogs, MeritFunction function,
        MeritOperandRegistry registry, LocalOptimizationOptions inner, IReadOnlyList<LensVariable> variables)
    {
        if (inner.CancellationToken.IsCancellationRequested) return Outcome.WasCancelled;
        try
        {
            if (variables.Count == 0)
            {
                var e = MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(design, catalogs), registry,
                    (inner.Merit ?? new MeritEvaluationOptions()) with { MaxDegreeOfParallelism = 1 });
                if (e.HasFailures) return new Outcome(null, double.NaN, false, null, string.Join(" ", e.Errors), false);
                return new Outcome(design, e.Value, Met(e, inner.ConstraintTolerance), Array.Empty<double>(), null, false);
            }
            var r = LocalOptimizer.Run(design, catalogs, inner, function, registry);
            if (r.Cancelled) return Outcome.WasCancelled;
            if (r.Evaluation.HasFailures) return new Outcome(null, double.NaN, false, null, string.Join(" ", r.Evaluation.Errors), false);
            if (!double.IsFinite(r.Merit)) return new Outcome(null, double.NaN, false, null, $"the merit function is {F(r.Merit)}", false);
            return new Outcome(r.Optimized, r.Merit, Met(r.Evaluation, inner.ConstraintTolerance), r.Values.ToArray(), null, false);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return new Outcome(null, double.NaN, false, null, e.Message.Replace(Environment.NewLine, " "), false);
        }
    }

    /// <summary>Every counted negative-weight row on target: |V − T| ≤ tolerance × max(1, |T|),
    /// the local optimizer's test.</summary>
    internal static bool Met(MeritEvaluation e, double tolerance)
    {
        foreach (var row in e.Rows)
        {
            if (!row.Counts || !(row.Row.Weight < 0)) continue;
            if (row.Status != MeritRowStatus.Evaluated) return false;
            if (!(Math.Abs(row.Value - row.Row.Target) <= tolerance * Math.Max(1, Math.Abs(row.Row.Target)))) return false;
        }
        return true;
    }

    /// <summary>True when a trial's design (<paramref name="met"/>, <paramref name="merit"/>)
    /// beats another: meeting the exact constraints first, then the lower merit.</summary>
    internal static bool Better(bool met, double merit, bool otherMet, double otherMerit) =>
        met != otherMet ? met : merit < otherMerit;

    internal static IReadOnlyList<string> GlassesOf(OpticsSettings design, IReadOnlyList<int> surfaces)
    {
        var list = design.Surfaces ?? new List<OpticsSurfaceSettings>();
        return surfaces.Select(j => SavedSurfaces.FormatMaterial(list[j])).ToArray();
    }

    internal static string F(double v) => v.ToString("G10", CultureInfo.InvariantCulture);
}
