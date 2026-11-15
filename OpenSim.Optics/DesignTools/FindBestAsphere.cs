using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.DesignTools;

// OP-61: Find Best Asphere (docs/Optics-Conventions.md §59).

/// <summary>The help's Asphere Type: "an asphere defined by a conic or an even polynomial
/// asphere to the specified maximum order" (OpticStudio 2025 R2 help, "Find Best Asphere";
/// plans/sources/optics/aspheres/Find_Best_Asphere.html).</summary>
public enum BestAsphereKind
{
    Conic,
    EvenAsphere,
}

/// <summary>Settings of <see cref="FindBestAsphere.Run"/>.</summary>
public sealed class FindBestAsphereOptions
{
    /// <summary>The help's First Surface; null for surface 1.</summary>
    public int? FirstSurface { get; init; }

    /// <summary>The help's Last Surface; null for the surface before the image.</summary>
    public int? LastSurface { get; init; }

    public BestAsphereKind Kind { get; init; } = BestAsphereKind.Conic;

    /// <summary>The highest power of r of the even polynomial, 4 to 16 in steps of 2: the terms
    /// α2 r⁴ … α(order/2) r^order are varied (α1 r², which duplicates the curvature, is not).</summary>
    public int MaxOrder { get; init; } = 8;

    /// <summary>The local optimization of every trial (its cancellation token is replaced by this
    /// search's). Null takes the defaults (damped least squares, automatic cycles).</summary>
    public LocalOptimizationOptions? Optimization { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Called on the search's thread after every trial; null for none.</summary>
    public Action<DesignToolProgress>? Progress { get; init; }
}

/// <summary>One surface made aspheric and the design re-optimized.</summary>
/// <param name="Merit">The merit function after re-optimizing; null when the trial failed.</param>
/// <param name="Error">Why it failed, or a note that the exact constraints are not met.</param>
public sealed record FindBestAsphereTrial(int Surface, double? Merit, bool ConstraintsMet, string? Error);

/// <summary>The outcome of <see cref="FindBestAsphere.Run"/>.</summary>
public sealed record FindBestAsphereResult
{
    public required OpticsSettings Start { get; init; }

    /// <summary>The design with the best asphere, re-optimized; the start when no trial worked.</summary>
    public required OpticsSettings Best { get; init; }

    /// <summary>The surface whose asphere gave the lowest merit; null when none did.</summary>
    public int? BestSurface { get; init; }

    public required double StartMerit { get; init; }

    /// <summary>The start re-optimized as it is (no asphere): what an asphere has to beat. This
    /// program's addition to the help's report.</summary>
    public required double ReoptimizedStartMerit { get; init; }

    public required double BestMerit { get; init; }

    public required BestAsphereKind Kind { get; init; }

    public int MaxOrder { get; init; }

    /// <summary>The trials, in surface order.</summary>
    public required IReadOnlyList<FindBestAsphereTrial> Trials { get; init; }

    /// <summary>The surfaces in the range that are not candidates, with the help's reason.</summary>
    public required IReadOnlyList<(int Surface, string Reason)> Skipped { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>The report: the trials ranked by merit, then the skipped surfaces.</summary>
    public IReadOnlyList<string> ReportLines()
    {
        var ci = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"Find Best Asphere: {(Kind == BestAsphereKind.Conic ? "conic" : $"even asphere to r^{MaxOrder}")}.",
            string.Create(ci, $"Merit {StartMerit:G10}; {ReoptimizedStartMerit:G10} re-optimized without an asphere."),
            "",
            string.Format(ci, "{0,-10} {1,18} {2,18}  {3}", "Surface", "Merit", "Change", "Note"),
        };
        foreach (var t in Trials.OrderBy(t => t.Merit is null).ThenBy(t => t.ConstraintsMet ? 0 : 1).ThenBy(t => t.Merit ?? 0))
            lines.Add(string.Format(ci, "{0,-10} {1,18} {2,18}  {3}", t.Surface,
                t.Merit is double m ? m.ToString("G10", ci) : "failed", t.Merit is double m2 ? (m2 - ReoptimizedStartMerit).ToString("G6", ci) : "", t.Error ?? ""));
        if (Trials.Count == 0) lines.Add("No surface in the range is a candidate.");
        lines.Add("");
        lines.Add(BestSurface is int b
            ? string.Create(ci, $"Best: surface {b}, merit {BestMerit:G10}.")
            : "No asphere was found.");
        foreach (var (s, why) in Skipped) lines.Add($"Surface {s} skipped: {why}");
        if (Cancelled) lines.Add("Stopped: the trials so far are listed.");
        return lines;
    }
}

/// <summary>
/// Find Best Asphere (plan item OP-61; OpticStudio 2025 R2 help, "Find Best Asphere"): "replaces
/// spherical surfaces with aspherical surfaces and reoptimizes the design to determine which
/// surface would benefit the most from aspherization". Following the help:
/// <list type="bullet">
/// <item>A candidate is "of type Standard, [has] no conic value, define[s] a boundary between air
/// and glass", has "a curvature that is either variable or controlled by a marginal ray angle or
/// F/# solve", and "a curvature that is not under control of the Multi-configuration Editor"
/// (no CRVT row on it). Ignored surfaces and mirrors are not candidates either.</item>
/// <item>Each candidate in turn, starting from the design as given, is "converted into an asphere
/// of the user-selected type. The aspheric terms are set as variables" and the design is
/// re-optimized with the current merit function and every other variable
/// (<see cref="LocalOptimizer"/> as it is). The lowest merit is kept; ties keep the earlier
/// surface; a design that meets the exact constraints beats one that does not.</item>
/// </list>
/// The even polynomial is optimized as an Extended Asphere normalized at the surface's clear
/// semi-diameter R (coefficients A_i on (r/R)^(2i), all lengths, so the optimizer's
/// derivative steps suit every order) and written back as the Even Asphere α_i = A_i / R^(2i), the
/// same sag; the merit is evaluated again on the Even Asphere. Trials run one after another;
/// cancellation keeps the trials so far.
/// </summary>
public static class FindBestAsphere
{
    public const string Tool = "Find Best Asphere";

    /// <summary>Why surface <paramref name="j"/> is not a candidate, or null when it is.</summary>
    public static string? WhyNotCandidate(OpticsSettings optics, int j)
    {
        ArgumentNullException.ThrowIfNull(optics);
        var surfaces = optics.Surfaces ?? throw new ArgumentException("The lens has no surfaces.");
        if (j < 1 || j > surfaces.Count - 2) return "not a lens surface.";
        var s = surfaces[j];
        if (!SavedSurfaces.IsStandard(s)) return $"its type is {s.Type}, not Standard.";
        if (s.Conic != 0) return "it has a conic value.";
        if (s.IsIgnored) return "it is ignored.";
        if (SavedSurfaces.IsMirror(s) || SavedSurfaces.IsMirror(surfaces[j - 1])) return "it is a mirror or follows one.";
        bool airBefore = SavedSurfaces.IsAir(surfaces[j - 1]), airAfter = SavedSurfaces.IsAir(s);
        if (airBefore == airAfter) return airBefore ? "air on both sides." : "glass on both sides (a cemented surface).";
        var radius = SurfaceFlags.Of(s, SavedSurfaces.RadiusColumn);
        bool ok = radius is not null && (radius.Kind == SavedSurfaces.VariableFlag
            || (radius.Kind == SavedSurfaces.SolveFlag && radius.Solve is "Marginal ray angle" or "F/#"));
        if (!ok) return "its curvature is neither variable nor held by a marginal ray angle or F/# solve.";
        if (optics.MultiConfiguration?.Operands?.Any(r => string.Equals(r?.Type, "CRVT", StringComparison.OrdinalIgnoreCase) && r!.Number1 == j) == true)
            return "its curvature is controlled by the Multi-Configuration Editor (a CRVT row).";
        return null;
    }

    /// <exception cref="ArgumentException">An empty merit function, a range outside the lens, an
    /// order other than 4 … 16 even, a variable cell that cannot be varied.</exception>
    /// <exception cref="InvalidOperationException">The start cannot be evaluated.</exception>
    public static FindBestAsphereResult Run(OpticsSettings optics, IReadOnlyList<OpticalCatalog> catalogs,
        FindBestAsphereOptions? options = null, MeritFunction? function = null, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new FindBestAsphereOptions();
        registry ??= MeritOperandRegistry.CreateDefault();
        var surfaces = optics.Surfaces ?? throw new ArgumentException($"{Tool}: the lens has no surfaces.");
        int first = options.FirstSurface ?? 1, last = options.LastSurface ?? surfaces.Count - 2;
        if (first < 1 || last > surfaces.Count - 2 || first > last)
            throw new ArgumentException($"{Tool}: the surface range {first} to {last} is not within 1 to {surfaces.Count - 2} with the first not after the last.");
        if (options.Kind == BestAsphereKind.EvenAsphere && (options.MaxOrder < 4 || options.MaxOrder > 16 || options.MaxOrder % 2 != 0))
            throw new ArgumentException($"{Tool}: the maximum order is 4, 6, … 16 (the Even Asphere's α2 … α8), not {options.MaxOrder}.");
        var token = options.CancellationToken;
        var log = new List<string>();
        function = DesignToolSupport.Function(optics, function, registry, Tool, log);
        var local = options.Optimization ?? new LocalOptimizationOptions();
        var unit = DesignToolSupport.Unit(optics);

        double startMerit = DesignToolSupport.EvaluateOrThrow(optics, catalogs, function, registry, local);
        var skipped = new List<(int, string)>();
        var candidates = new List<int>();
        for (int j = first; j <= last; j++)
        {
            if (WhyNotCandidate(optics, j) is { } why) skipped.Add((j, why));
            else candidates.Add(j);
        }
        int total = candidates.Count + 1, done = 0;
        var trials = new List<FindBestAsphereTrial>();
        bool cancelled = false;
        var reoptimized = DesignToolSupport.Optimize(optics, catalogs, function, registry, local, token);
        if (reoptimized is null)
            return new FindBestAsphereResult
            {
                Start = optics, Best = optics, StartMerit = startMerit, ReoptimizedStartMerit = startMerit, BestMerit = startMerit,
                Kind = options.Kind, MaxOrder = options.MaxOrder, Trials = trials, Skipped = skipped, Cancelled = true,
            };
        double reference = reoptimized.Value.Merit;
        options.Progress?.Invoke(new DesignToolProgress(Tool, ++done, total, reference, "the start re-optimized"));

        double[]? clear = null;
        OpticsSettings best = optics;
        double bestMerit = double.PositiveInfinity;
        bool bestMet = false;
        int? bestSurface = null;
        foreach (int j in candidates)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            try
            {
                var trial = optics.Surfaces!.ToList();
                double normalization = 0;
                int n = options.MaxOrder / 2;
                if (options.Kind == BestAsphereKind.Conic)
                    trial[j] = SurfaceFlags.With(trial[j], SavedSurfaces.ConicColumn, SurfaceFlags.Variable(SavedSurfaces.ConicColumn));
                else
                {
                    clear ??= ApertureTools.ClearSemiDiameters(surfaces, new LensEditContext(optics.SystemData, catalogs));
                    normalization = clear[j];
                    if (!(normalization > 0) || !double.IsFinite(normalization))
                        throw new InvalidOperationException($"surface {j} has no clear semi-diameter to normalize the asphere by.");
                    var s = trial[j] with { Type = "Extended Asphere", Parameters = null, ExtraData = null };
                    s = DesignToolSupport.WithParameter(s, 13, n);
                    s = DesignToolSupport.WithParameter(s, 14, normalization);
                    for (int i = 2; i <= n; i++)
                        s = SurfaceFlags.With(s, SavedSurfaces.ParameterColumn(14 + i), SurfaceFlags.Variable(SavedSurfaces.ParameterColumn(14 + i)));
                    trial[j] = s;
                }
                var outcome = DesignToolSupport.Optimize(optics with { Surfaces = trial }, catalogs, function, registry, local, token);
                if (outcome is null) { cancelled = true; break; }
                var design = outcome.Value.Design;
                double merit = outcome.Value.Merit;
                if (options.Kind == BestAsphereKind.EvenAsphere)
                {
                    design = ToEvenAsphere(design, j, n, normalization);
                    merit = DesignToolSupport.EvaluateOrThrow(design, catalogs, function, registry, local);
                }
                bool met = outcome.Value.Met;
                trials.Add(new FindBestAsphereTrial(j, merit, met, met ? null : "the exact (negative-weight) constraints are not met"));
                if (bestSurface is null || DesignToolSupport.Better(merit, met, bestMerit, bestMet))
                {
                    best = design; bestMerit = merit; bestMet = met; bestSurface = j;
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                trials.Add(new FindBestAsphereTrial(j, null, false, e.Message));
            }
            options.Progress?.Invoke(new DesignToolProgress(Tool, ++done, total, Math.Min(reference, bestMerit), $"surface {j}"));
        }
        if (bestSurface is null) bestMerit = reference;
        return new FindBestAsphereResult
        {
            Start = optics, Best = bestSurface is null ? optics : best, BestSurface = bestSurface,
            StartMerit = startMerit, ReoptimizedStartMerit = reference, BestMerit = bestMerit,
            Kind = options.Kind, MaxOrder = options.MaxOrder, Trials = trials, Skipped = skipped, Cancelled = cancelled,
        };
    }

    /// <summary>Surface <paramref name="j"/>'s Extended Asphere (normalization R, terms on ρ⁴ …
    /// ρ^(2n) variable) written as the Even Asphere of the same sag: α_i = A_i / R^(2i), with
    /// α2 … αn flagged Variable.</summary>
    private static OpticsSettings ToEvenAsphere(OpticsSettings design, int j, int n, double normalization)
    {
        var list = design.Surfaces!.ToList();
        var s = list[j];
        var alpha = new double[n];
        for (int i = 1; i <= n; i++) alpha[i - 1] = SavedSurfaces.Value(s, 14 + i) / Math.Pow(normalization, 2 * i);
        var flags = s.Flags?.Where(f => SavedSurfaces.ParameterNumber(f.Column) is null).ToList() ?? new();
        var even = s with { Type = "Even Asphere", Parameters = null, ExtraData = null, Flags = flags.Count > 0 ? flags : null };
        for (int i = 1; i <= n; i++)
            if (alpha[i - 1] != 0) even = DesignToolSupport.WithParameter(even, i, alpha[i - 1]);
        for (int i = 2; i <= n; i++)
            even = SurfaceFlags.With(even, SavedSurfaces.ParameterColumn(i), SurfaceFlags.Variable(SavedSurfaces.ParameterColumn(i)));
        list[j] = even;
        return design with { Surfaces = list };
    }
}
