using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.DesignTools;

// OP-61: Stock Lens Matching (docs/Optics-Conventions.md §59).

/// <summary>The help's Surfaces: "If All is chosen, all glass elements with glass surfaces are
/// matched. If Variables is chosen, all glass elements that have variable radius status are
/// matched" (OpticStudio 2025 R2 help, "Stock Lens Matching";
/// plans/sources/optics/op-61-help/Stock_Lens_Matching.html).</summary>
public enum StockLensSurfaces
{
    All,
    Variables,
}

/// <summary>Settings of <see cref="StockLensMatching.Run"/>. The defaults of Show Matches, the two
/// tolerances and # Opt. Cycles are the help's dialog picture (5, 25 %, 25 %, Automatic); Match
/// Shape and Match Orientation are on, as its text says ("This option is selected by default").</summary>
public sealed class StockLensMatchingOptions
{
    public StockLensSurfaces Surfaces { get; init; } = StockLensSurfaces.All;

    /// <summary>"The maximum number of stock lens matches to show" per component.</summary>
    public int ShowMatches { get; init; } = 5;

    /// <summary>"The EFL difference divided by the EFL of the defined lens expressed as a percent."</summary>
    public double EflTolerancePercent { get; init; } = 25;

    /// <summary>"The EPD difference divided by the diameter of the defined lens expressed as a
    /// percent"; "The EPD of matched lenses will not be smaller than the diameter of the defined lens."</summary>
    public double EpdTolerancePercent { get; init; } = 25;

    /// <summary>"Air thicknesses allocated as variables will be reoptimized after the stock lens
    /// is in place" (every other variable is held for the trial).</summary>
    public bool AirThicknessCompensation { get; init; }

    /// <summary>The optimization of the air thicknesses (the help's # Opt. Cycles: its
    /// <see cref="LocalOptimizationOptions.Cycles"/> and <see cref="LocalOptimizationOptions.CycleCount"/>).</summary>
    public LocalOptimizationOptions? Optimization { get; init; }

    /// <summary>"Use a lens that has a shape factor close to the original design": within
    /// <see cref="ShapeTolerance"/>.</summary>
    public bool MatchShape { get; init; } = true;

    /// <summary>Close shape factors differ by at most this (this program's choice; the help does
    /// not say how close). S runs from −1 to 1 between the two plano forms.</summary>
    public double ShapeTolerance { get; init; } = 1.0;

    /// <summary>On: one orientation, the stock lens reversed when its shape factor and the
    /// design's have opposite signs. Off: "check replacements lenses in both orientations".</summary>
    public bool MatchOrientation { get; init; } = true;

    /// <summary>Every combination of the components' shown matches is evaluated while there are
    /// at most this many; beyond, a greedy search (this program's).</summary>
    public int MaxCombinations { get; init; } = 1000;

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Called on the search's thread after every trial; null for none.</summary>
    public Action<DesignToolProgress>? Progress { get; init; }
}

/// <summary>A component of the design: lens surfaces <see cref="First"/> to <see cref="Last"/>
/// with air before the first and after the last.</summary>
/// <param name="EffectiveFocalLengthMm">The group's EFL in air (Lens Catalogs' rule,
/// <see cref="StockLensProperties.OfGroup"/>); null when afocal.</param>
/// <param name="DiameterMm">Twice the largest clear semi-diameter of the group.</param>
/// <param name="ShapeFactor">S = (C_front + C_back)/(C_front − C_back) (help).</param>
/// <param name="Skipped">Why it is not matched, or null.</param>
public sealed record StockLensComponent(int Number, int First, int Last, int Elements, double? EffectiveFocalLengthMm,
    double DiameterMm, double ShapeFactor, string? Skipped);

/// <summary>A stock lens tried in one component.</summary>
/// <param name="Catalog">The catalog (vendor) name.</param>
/// <param name="Reversed">Inserted turned round (the report's "Reverse").</param>
/// <param name="Merit">The merit with the lens in place (air thicknesses re-optimized when
/// asked); null when the trial failed.</param>
public sealed record StockLensMatch(int Component, string Catalog, StockLens Lens, bool Reversed, double? Merit, bool ConstraintsMet, string? Error);

/// <summary>Stock lenses in several components at once and the merit.</summary>
public sealed record StockLensCombination(IReadOnlyList<StockLensMatch> Lenses, double Merit, bool ConstraintsMet);

/// <summary>The outcome of <see cref="StockLensMatching.Run"/>.</summary>
public sealed record StockLensMatchingResult
{
    public required OpticsSettings Start { get; init; }

    /// <summary>The help's "Nominal Criterion": the merit of the design as given.</summary>
    public required double NominalMerit { get; init; }

    public required IReadOnlyList<StockLensComponent> Components { get; init; }

    /// <summary>Per component number, its best matches (at most Show Matches), best first.</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<StockLensMatch>> Matches { get; init; }

    /// <summary>The trials that failed, with their reasons.</summary>
    public required IReadOnlyList<StockLensMatch> Failed { get; init; }

    /// <summary>The best combinations (two or more components), best first.</summary>
    public required IReadOnlyList<StockLensCombination> Combinations { get; init; }

    /// <summary>The best design: the best combination, or the best single match when only one
    /// component has matches; null when nothing matched.</summary>
    public OpticsSettings? Best { get; init; }

    public StockLensCombination? BestChoice { get; init; }

    public int Trials { get; init; }

    public bool Cancelled { get; init; }

    public required StockLensMatchingOptions Options { get; init; }

    /// <summary>The text report, laid out as the help's example.</summary>
    public IReadOnlyList<string> ReportLines()
    {
        var ci = CultureInfo.InvariantCulture;
        string Row(string left, string merit, string change, string orientation) =>
            string.Format(ci, "{0,-48} {1,16} {2,16}  {3}", left, merit, change, orientation);
        var lines = new List<string>
        {
            "Stock lens data is the vendors' files; check availability with the vendor.",
            "",
            $"Surfaces            : {Options.Surfaces}",
            $"Show Matches        : {Options.ShowMatches}",
            string.Create(ci, $"EFL Tolerance (%)   : {Options.EflTolerancePercent:G6}"),
            string.Create(ci, $"EPD Tolerance (%)   : {Options.EpdTolerancePercent:G6}"),
            $"Air compensation    : {(Options.AirThicknessCompensation ? "on" : "off")}",
            $"Match shape         : {(Options.MatchShape ? "on" : "off")}; match orientation: {(Options.MatchOrientation ? "on" : "off")}",
            string.Create(ci, $"Nominal Criterion   : {NominalMerit:G10}"),
        };
        foreach (var c in Components)
        {
            lines.Add("");
            lines.Add(Row($"Component {c.Number} (Surfaces {c.First}-{c.Last})", "MF Value", "MF Change", "Orientation"));
            if (c.Skipped is not null) { lines.Add($"Not matched: {c.Skipped}"); continue; }
            if (!Matches.TryGetValue(c.Number, out var list) || list.Count == 0) { lines.Add("No suitable lenses found."); continue; }
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                lines.Add(Row($"{i + 1,2}) {m.Lens.PartName} ({m.Catalog})", m.Merit!.Value.ToString("G10", ci),
                    (m.Merit.Value - NominalMerit).ToString("G6", ci), m.Reversed ? "Reverse" : ""));
            }
        }
        lines.Add("");
        lines.Add(Row("Best Combinations", "MF Value", "MF Change", ""));
        if (Combinations.Count == 0) lines.Add("No additional combinations found.");
        for (int i = 0; i < Combinations.Count; i++)
        {
            var c = Combinations[i];
            lines.Add(Row($"{i + 1,2}) " + string.Join(", ", c.Lenses.Select(m => $"{m.Component}: {m.Lens.PartName}{(m.Reversed ? " (rev.)" : "")}")),
                c.Merit.ToString("G10", ci), (c.Merit - NominalMerit).ToString("G6", ci), c.ConstraintsMet ? "" : "constraints not met"));
        }
        if (Failed.Count > 0)
        {
            lines.Add("");
            lines.Add($"{Failed.Count} trial{(Failed.Count == 1 ? "" : "s")} failed:");
            foreach (var f in Failed) lines.Add($"Component {f.Component}, {f.Lens.PartName} ({f.Catalog}){(f.Reversed ? " reversed" : "")}: {f.Error}");
        }
        if (Cancelled) lines.Add("Stopped: the trials so far are listed.");
        return lines;
    }
}

/// <summary>
/// Stock Lens Matching (plan item OP-61; OpticStudio 2025 R2 help, "Stock Lens Matching"): "finds
/// stock lens matches for lenses defined in the current system".
/// <list type="number">
/// <item>Components: runs of Standard lens surfaces with air before the first and after the last
/// ("Only OpticStudio Standard Surface Type is supported"); one with a mirror, a tilt or
/// decenter is reported and skipped. With Surfaces = Variables only components with a variable
/// radius are matched.</item>
/// <item>Candidates: the lenses of the given catalogs with the component's number of elements,
/// an EFL within the EFL tolerance and an EPD not smaller than the component's diameter and
/// within the EPD tolerance above it (the vendor index's EFL and EPD, OP-16). The orientation and
/// shape rules follow the help: with Match Orientation the lens is reversed when its shape factor
/// and the design's have opposite signs (not when either is zero), otherwise both orientations
/// are tried; with Match Shape the oriented shape factor must be within
/// <see cref="StockLensMatchingOptions.ShapeTolerance"/> of the design's.</item>
/// <item>Trial: the lens file is put into the component's surfaces with Insert Lens
/// (<see cref="StockLensInsertion"/>, its unit conversion and Reverse), surface for surface, so
/// no surface is renumbered: the lens's radii, conics, glasses, center thicknesses, apertures and
/// coatings replace the component's, the component keeps its stop flag and the air thickness
/// after its last surface (with that cell's flag). A lens file with a different number of
/// surfaces fails the trial by name. The merit is evaluated, or, with Air Thickness
/// Compensation, the variable air thicknesses alone are re-optimized first
/// (<see cref="LocalOptimizer"/> as it is).</item>
/// <item>The matches of a component are its successful trials ranked by merit (meeting the
/// exact constraints first); Show Matches are kept. Then every combination of the shown matches
/// of the components that have any (two or more) is evaluated the same way, or, beyond
/// <see cref="StockLensMatchingOptions.MaxCombinations"/>, a greedy search takes the components
/// in order of their best match and keeps the best match of each in the presence of the
/// earlier choices.</item>
/// </list>
/// Trials run one after another. Cancellation keeps the trials so far and their best.
/// </summary>
public static class StockLensMatching
{
    public const string Tool = "Stock Lens Matching";

    /// <summary>The shape factor (C_front + C_back)/(C_front − C_back) of surfaces
    /// <paramref name="first"/> and <paramref name="last"/>, C = 1/R (0 for a plane); a plane
    /// window is 0, two equal non-zero curvatures ±∞.</summary>
    public static double ShapeFactor(IReadOnlyList<OpticsSurfaceSettings> surfaces, int first, int last)
    {
        double C(OpticsSurfaceSettings s) => SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value;
        double c1 = C(surfaces[first]), c2 = C(surfaces[last]);
        if (c1 == c2) return c1 == 0 ? 0 : (c1 > 0 ? double.PositiveInfinity : double.NegativeInfinity);
        return (c1 + c2) / (c1 - c2);
    }

    /// <summary>The components of the lens (numbered from 1 in surface order).</summary>
    public static IReadOnlyList<StockLensComponent> Components(OpticsSettings optics, IReadOnlyList<OpticalCatalog> glassCatalogs, StockLensSurfaces which)
    {
        ArgumentNullException.ThrowIfNull(optics);
        var surfaces = optics.Surfaces ?? throw new ArgumentException($"{Tool}: the lens has no surfaces.");
        var context = new LensEditContext(optics.SystemData, glassCatalogs);
        var list = new List<StockLensComponent>();
        int j = 1;
        while (j <= surfaces.Count - 2)
        {
            var s = surfaces[j];
            if (SavedSurfaces.IsAir(s) || !SavedSurfaces.IsAir(surfaces[j - 1]) || SavedSurfaces.IsMirror(surfaces[j - 1])) { j++; continue; }
            int k = j;
            while (k <= surfaces.Count - 2 && !SavedSurfaces.IsAir(surfaces[k])) k++;
            int number = list.Count + 1;
            if (k > surfaces.Count - 2)
            {
                list.Add(new StockLensComponent(number, j, surfaces.Count - 2, k - j, null, 0, 0, "the glass runs to the image surface."));
                break;
            }
            string? why = null;
            for (int i = j; i <= k && why is null; i++)
            {
                var t = surfaces[i];
                if (SavedSurfaces.IsMirror(t)) why = $"surface {i} is a mirror.";
                else if (!SavedSurfaces.IsStandard(t)) why = $"surface {i} is a {t.Type} surface; only Standard surfaces are matched.";
                else if (t.TiltBefore is not null || t.TiltAfter is not null || t.CoordinateReturn is { Mode: not (null or "None") }) why = $"surface {i} is tilted or decentered.";
            }
            if (why is null && which == StockLensSurfaces.Variables
                && !Enumerable.Range(j, k - j + 1).Any(i => SurfaceFlags.Of(surfaces[i], SavedSurfaces.RadiusColumn) is { Kind: SavedSurfaces.VariableFlag }))
                why = "no radius is variable (Surfaces: Variables).";
            double? efl = null;
            double diameter = 0;
            if (why is null)
            {
                try
                {
                    var group = StockLensProperties.OfGroup(surfaces, j, k, context);
                    efl = group.EffectiveFocalLengthMm;
                    diameter = group.DiameterMm;
                    if (efl is null) why = "it is afocal.";
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { why = e.Message; }
            }
            list.Add(new StockLensComponent(number, j, k, k - j, efl, diameter, ShapeFactor(surfaces, j, k), why));
            j = k + 1;
        }
        return list;
    }

    /// <summary>True when the stock lens's index passes the EFL, EPD and element rules for the component.</summary>
    public static bool InRange(StockLensComponent component, StockLens lens, double eflPercent, double epdPercent)
    {
        if (lens.Elements != component.Elements) return false;
        if (component.EffectiveFocalLengthMm is not double f || lens.EffectiveFocalLengthMm is not double fs) return false;
        if (Math.Abs(fs - f) > eflPercent / 100 * Math.Abs(f)) return false;
        if (lens.EntrancePupilDiameterMm is not double d) return false;
        double dd = component.DiameterMm;
        return d >= dd && d - dd <= epdPercent / 100 * dd;
    }

    /// <summary>
    /// The design with component surfaces <paramref name="component"/> replaced by the lens file
    /// <paramref name="lens"/> (the rules in the class summary). Throws naming the lens when the
    /// file's surfaces do not correspond one for one.
    /// </summary>
    public static OpticsSettings Replace(OpticsSettings design, StockLensComponent component, OpticsSettings lens, bool reversed, string label)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(lens);
        var inserted = StockLensInsertion.Insert(design, lens, component.First, new InsertLensOptions { IgnoreObject = true, Reverse = reversed });
        int count = inserted.Last - inserted.First + 1, wanted = component.Last - component.First + 1;
        if (count != wanted)
            throw new InvalidOperationException($"the lens file '{label}' has {count} surfaces between its object and image; component {component.Number} has {wanted}.");
        var block = inserted.Settings.Surfaces!;
        var list = design.Surfaces!.ToList();
        for (int i = 0; i < wanted; i++)
        {
            var d = list[component.First + i];
            var b = block[inserted.First + i];
            bool last = i == wanted - 1;
            var flags = last ? d.Flags?.Where(f => f.Column == SavedSurfaces.ThicknessColumn).ToList() : null;
            list[component.First + i] = b with
            {
                Thickness = last ? d.Thickness : b.Thickness,
                ThicknessIsInfinite = last ? d.ThicknessIsInfinite : b.ThicknessIsInfinite,
                Flags = flags is { Count: > 0 } ? flags : null,
                IsStop = d.IsStop, IsIgnored = d.IsIgnored, SkipRaysTo = d.SkipRaysTo, HideRaysTo = d.HideRaysTo,
                DoNotDraw = d.DoNotDraw, RowColor = d.RowColor,
                TiltBefore = null, TiltAfter = null, CoordinateReturn = null,
                Comment = i == 0 ? label : b.Comment,
            };
        }
        return design with { Surfaces = list };
    }

    /// <exception cref="ArgumentException">An empty merit function, settings out of range.</exception>
    /// <exception cref="InvalidOperationException">The design cannot be evaluated.</exception>
    public static StockLensMatchingResult Run(OpticsSettings optics, IReadOnlyList<LensCatalog> vendors, IReadOnlyList<OpticalCatalog> glassCatalogs,
        StockLensMatchingOptions? options = null, MeritFunction? function = null, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(vendors);
        ArgumentNullException.ThrowIfNull(glassCatalogs);
        options ??= new StockLensMatchingOptions();
        registry ??= MeritOperandRegistry.CreateDefault();
        if (options.ShowMatches < 1) throw new ArgumentException($"{Tool}: Show Matches must be at least 1, not {options.ShowMatches}.");
        if (!(options.EflTolerancePercent >= 0) || !(options.EpdTolerancePercent >= 0))
            throw new ArgumentException($"{Tool}: the EFL and EPD tolerances are percentages of at least 0.");
        if (!(options.ShapeTolerance >= 0)) throw new ArgumentException($"{Tool}: the shape tolerance must be at least 0.");
        if (vendors.Count == 0) throw new ArgumentException($"{Tool}: no stock lens catalog is selected.");
        var token = options.CancellationToken;
        var log = new List<string>();
        function = DesignToolSupport.Function(optics, function, registry, Tool, log);
        var local = options.Optimization ?? new LocalOptimizationOptions();
        double nominal = DesignToolSupport.EvaluateOrThrow(optics, glassCatalogs, function, registry, local);
        var components = Components(optics, glassCatalogs, options.Surfaces);

        // The candidate trials: (component, catalog, lens, file, reversed), the files read once.
        var files = new Dictionary<StockLens, (OpticsSettings? Optics, string? Error)>(ReferenceEqualityComparer.Instance);
        var failed = new List<StockLensMatch>();
        var plan = new List<(StockLensComponent C, LensCatalog Catalog, StockLens Lens, OpticsSettings File, bool Reversed)>();
        foreach (var c in components.Where(c => c.Skipped is null))
            foreach (var catalog in vendors)
                foreach (var lens in catalog.Lenses)
                {
                    if (!InRange(c, lens, options.EflTolerancePercent, options.EpdTolerancePercent)) continue;
                    if (!files.TryGetValue(lens, out var file))
                    {
                        try
                        {
                            var read = catalog.Import(lens, glassCatalogs);
                            file = (read.Optics, null);
                        }
                        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
                        {
                            file = (null, e.Message);
                        }
                        files[lens] = file;
                    }
                    var lensOptics = file.Optics;
                    var fs = lensOptics?.Surfaces;
                    if (lensOptics is null || fs is null || fs.Count < 3)
                    {
                        failed.Add(new StockLensMatch(c.Number, catalog.Name, lens, false, null, false, file.Error ?? "the lens file has no surfaces."));
                        continue;
                    }
                    double stock = ShapeFactor(fs, 1, fs.Count - 2);
                    var orientations = options.MatchOrientation ? new[] { c.ShapeFactor * stock < 0 } : new[] { false, true };
                    foreach (bool reversed in orientations)
                    {
                        double oriented = reversed ? -stock : stock;
                        if (options.MatchShape && !ShapeClose(oriented, c.ShapeFactor, options.ShapeTolerance)) continue;
                        plan.Add((c, catalog, lens, lensOptics, reversed));
                    }
                }

        int total = plan.Count, done = 0;
        double best = double.PositiveInfinity;
        bool cancelled = false;
        var successes = new List<StockLensMatch>();

        foreach (var (c, catalog, lens, file, reversed) in plan)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            string label = $"{lens.PartName} ({catalog.Name})";
            try
            {
                var design = Replace(optics, c, file, reversed, label);
                var outcome = Settle(design, glassCatalogs, function, registry, local, options, token);
                if (outcome is null) { cancelled = true; break; }
                var m = new StockLensMatch(c.Number, catalog.Name, lens, reversed, outcome.Value.Merit, outcome.Value.Met, null);
                successes.Add(m);
                best = Math.Min(best, outcome.Value.Merit);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                failed.Add(new StockLensMatch(c.Number, catalog.Name, lens, reversed, null, false, e.Message));
            }
            options.Progress?.Invoke(new DesignToolProgress(Tool, ++done, total, best, $"component {c.Number}: {label}"));
        }

        var matches = new Dictionary<int, IReadOnlyList<StockLensMatch>>();
        foreach (var g in successes.GroupBy(m => m.Component))
            matches[g.Key] = Ranked(g).Take(options.ShowMatches).ToArray();

        // Combinations of the shown matches.
        var combinations = new List<StockLensCombination>();
        var combinationDesigns = new Dictionary<StockLensCombination, OpticsSettings>(ReferenceEqualityComparer.Instance);
        var withMatches = components.Where(c => matches.ContainsKey(c.Number)).ToList();
        StockLensCombination? TryChoice(IReadOnlyList<StockLensMatch> choice, ref bool stop)
        {
            try
            {
                var design = optics;
                foreach (var m in choice)
                    design = Replace(design, components[m.Component - 1], files[m.Lens].Optics!, m.Reversed, $"{m.Lens.PartName} ({m.Catalog})");
                var outcome = Settle(design, glassCatalogs, function!, registry!, local, options!, token);
                if (outcome is null) { stop = true; return null; }
                var combination = new StockLensCombination(choice, outcome.Value.Merit, outcome.Value.Met);
                combinations.Add(combination);
                combinationDesigns[combination] = outcome.Value.Design;
                best = Math.Min(best, combination.Merit);
                return combination;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                log.Add($"Combination {string.Join(", ", choice.Select(m => m.Lens.PartName))}: {e.Message}");
                return null;
            }
        }

        int full = withMatches.Count;
        if (!cancelled && full == 1)
        {
            bool stop = false;
            TryChoice(new[] { matches[withMatches[0].Number][0] }, ref stop);
            cancelled = stop;
        }
        else if (!cancelled && full >= 2)
        {
            var lists = withMatches.Select(c => matches[c.Number]).ToList();
            double product = lists.Aggregate(1.0, (p, l) => p * l.Count);
            bool stop = false;
            if (product <= options.MaxCombinations)
            {
                int count = (int)product;
                var index = new int[lists.Count];
                for (int n = 0; n < count && !stop; n++)
                {
                    if (token.IsCancellationRequested) { stop = true; break; }
                    var choice = lists.Select((l, i) => l[index[i]]).ToArray();
                    TryChoice(choice, ref stop);
                    options.Progress?.Invoke(new DesignToolProgress(Tool, done + n + 1, total + count, best, "combinations"));
                    for (int i = lists.Count - 1; i >= 0; i--)
                    {
                        if (++index[i] < lists[i].Count) break;
                        index[i] = 0;
                    }
                }
            }
            else
            {
                log.Add(string.Create(CultureInfo.InvariantCulture, $"{product:G6} combinations exceed {options.MaxCombinations}; a greedy search was used."));
                var order = lists.OrderBy(l => l[0].ConstraintsMet ? 0 : 1).ThenBy(l => l[0].Merit).ToList();
                var chosen = new List<StockLensMatch>();
                int step = 0, steps = order.Sum(l => l.Count);
                foreach (var l in order)
                {
                    StockLensCombination? winner = null;
                    foreach (var m in l)
                    {
                        if (token.IsCancellationRequested) { stop = true; break; }
                        var trial = TryChoice(chosen.Append(m).ToArray(), ref stop);
                        options.Progress?.Invoke(new DesignToolProgress(Tool, done + ++step, total + steps, best, "combinations (greedy)"));
                        if (stop) break;
                        if (trial is not null && (winner is null || DesignToolSupport.Better(trial.Merit, trial.ConstraintsMet, winner.Merit, winner.ConstraintsMet)))
                            winner = trial;
                    }
                    if (stop || winner is null) break;
                    chosen.Add(winner.Lenses[^1]);
                }
            }
            cancelled |= stop;
        }
        // Only full combinations (a lens in every component that has matches) compete.
        StockLensCombination? bestChoice = null;
        foreach (var c in combinations.Where(c => c.Lenses.Count == full))
            if (bestChoice is null || DesignToolSupport.Better(c.Merit, c.ConstraintsMet, bestChoice.Merit, bestChoice.ConstraintsMet))
                bestChoice = c;
        var bestDesign = bestChoice is null ? null : combinationDesigns[bestChoice];
        if (full < 2) combinations.Clear();   // one component: no combination to report
        else combinations.RemoveAll(c => c.Lenses.Count < full);
        var ranked = combinations
            .GroupBy(c => string.Join("|", c.Lenses.Select(m => $"{m.Component}:{m.Catalog}:{m.Lens.Source}:{m.Reversed}")))
            .Select(g => g.First())
            .OrderBy(c => c.ConstraintsMet ? 0 : 1).ThenBy(c => c.Merit)
            .Take(options.ShowMatches).ToArray();
        return new StockLensMatchingResult
        {
            Start = optics, NominalMerit = nominal, Components = components, Matches = matches, Failed = failed,
            Combinations = ranked, Best = bestDesign, BestChoice = bestChoice,
            Trials = done, Cancelled = cancelled, Options = options,
        };
    }

    private static IEnumerable<StockLensMatch> Ranked(IEnumerable<StockLensMatch> matches) =>
        matches.OrderBy(m => m.ConstraintsMet ? 0 : 1).ThenBy(m => m.Merit);

    private static bool ShapeClose(double a, double b, double tolerance)
    {
        if (double.IsInfinity(a) || double.IsInfinity(b)) return double.IsInfinity(a) && double.IsInfinity(b) && Math.Sign(a) == Math.Sign(b);
        return Math.Abs(a - b) <= tolerance;
    }

    /// <summary>The merit of a trial design: evaluated, or with Air Thickness Compensation the
    /// variable air thicknesses alone re-optimized (other variables held), the result written back
    /// onto the design with its own flags. Null when cancelled.</summary>
    private static DesignOutcome? Settle(OpticsSettings design, IReadOnlyList<OpticalCatalog> catalogs, MeritFunction function,
        MeritOperandRegistry registry, LocalOptimizationOptions local, StockLensMatchingOptions options, CancellationToken token)
    {
        if (!options.AirThicknessCompensation)
        {
            if (token.IsCancellationRequested) return null;
            var e = DesignToolSupport.Evaluate(design, catalogs, function, registry, local);
            if (e.HasFailures) throw new InvalidOperationException(string.Join(Environment.NewLine, e.Errors));
            return new DesignOutcome(design, e.Value, true);
        }
        var surfaces = design.Surfaces!;
        var held = DesignToolSupport.KeepVariables(design, (j, column) => column == SavedSurfaces.ThicknessColumn
            && j < surfaces.Count - 1 && SavedSurfaces.IsAir(surfaces[j]) && !SavedSurfaces.IsMirror(surfaces[j]));
        var variables = LensVariables.Find(held);
        var outcome = DesignToolSupport.Optimize(held, catalogs, function, registry, local, token);
        if (outcome is null) return null;
        var written = variables.Count == 0 ? design : LensVariables.CopyCells(outcome.Value.Design, design, variables);
        return outcome.Value with { Design = written };
    }
}
