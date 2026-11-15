using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.Optimization.TestPlates;

// OP-17: fitting variable radii to a fabricator's test plate list.

/// <summary>The order in which the radii are fitted (OpticStudio 2025 R2 help, "Test Plate
/// Fitting", "Method of Fit"; the integers are the ZPL keyword TESTPLATEFIT's "method").</summary>
public enum TestPlateFitMethod
{
    /// <summary>"Try All Methods": the four orders below, keeping the one with the lowest merit function.</summary>
    TryAll = 0,
    /// <summary>"Best To Worst": the radius with the closest plate match (in fringes) first.</summary>
    BestToWorst = 1,
    /// <summary>"Worst To Best": the worst-fitting radius first.</summary>
    WorstToBest = 2,
    /// <summary>"Long To Short": the longest radius first.</summary>
    LongToShort = 3,
    /// <summary>"Short To Long": the shortest radius first.</summary>
    ShortToLong = 4,
}

/// <summary>Progress after each fitted radius: the help's "the number of radii remaining and the
/// current merit function".</summary>
public sealed record TestPlateFitProgress(TestPlateFitMethod Method, int Remaining, double Merit);

/// <summary>Settings of <see cref="TestPlateFitting.Run"/>. Every member has a usable default.</summary>
public sealed class TestPlateFitOptions
{
    /// <summary>The help's "edge sag no more than 0.2 mm different than the test plate".</summary>
    public const double EdgeSagLimitMillimeters = 0.2;

    /// <summary>The help's "the diameter is at least 3/4 of the clear aperture diameter".</summary>
    public const double DiameterFraction = 0.75;

    public TestPlateFitMethod Method { get; init; } = TestPlateFitMethod.TryAll;

    /// <summary>The local optimization run before fitting and after each fitted radius (its
    /// cancellation token is replaced by this run's; infinite cycles run as automatic). The help's
    /// "# Opt Cycles" is its <see cref="LocalOptimizationOptions.Cycles"/> (Automatic) or Fixed with
    /// <see cref="LocalOptimizationOptions.CycleCount"/>. Null takes the defaults.</summary>
    public LocalOptimizationOptions? Optimization { get; init; }

    /// <summary>The wavelength the fringes are reported at, µm. The help does not name one for
    /// fitting; the default is the 0.6328 µm HeNe test wavelength OpticStudio's default tolerances
    /// use (help, "TWAV: Test Wavelength"). The fitting order does not depend on it (every
    /// surface's fringes scale by the same 1/λ).</summary>
    public double TestWavelengthMicrometers { get; init; } = 0.6328;

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Called on the fitting thread after each radius; null for none.</summary>
    public Action<TestPlateFitProgress>? Progress { get; init; }
}

/// <summary>One radius fitted, or found to have no plate.</summary>
public sealed record TestPlateFitStep
{
    /// <summary>1-based position in the fitting order.</summary>
    public required int Order { get; init; }

    public required int Surface { get; init; }

    /// <summary>The radius just before it was fitted (after the preceding optimization), lens
    /// units; null for a plane.</summary>
    public double? RadiusBefore { get; init; }

    /// <summary>The plate's radius with the surface's sign, lens units; null when no plate matched.</summary>
    public double? RadiusAfter { get; init; }

    /// <summary>The plate chosen; null for "NO MATCH FOUND!".</summary>
    public TestPlate? Plate { get; init; }

    /// <summary>The power difference between the radius and the plate in fringes at
    /// <see cref="TestPlateFitOptions.TestWavelengthMicrometers"/>; null when no plate matched.</summary>
    public double? Fringes { get; init; }

    /// <summary>The clear semi-diameter the fringes, diameter and sag tests used, lens units.</summary>
    public double SemiDiameter { get; init; }

    /// <summary>The merit function after the re-optimization that followed this step (for a step
    /// with no match, the merit at that point).</summary>
    public required double Merit { get; init; }

    /// <summary>Why no plate matched (the radius stays variable and is not fitted); null when one did.</summary>
    public string? NoMatch { get; init; }

    /// <summary>False when the re-optimization after this step left negative-weight rows off target.</summary>
    public bool ConstraintsMet { get; init; } = true;

    /// <summary>RadiusAfter − RadiusBefore, lens units; null when no plate matched or the surface was a plane.</summary>
    public double? RadiusChange => RadiusAfter is double a && RadiusBefore is double b ? a - b : null;
}

/// <summary>One fitting order run to the end (or to its failure).</summary>
public sealed record TestPlateFitRun
{
    public required TestPlateFitMethod Method { get; init; }

    /// <summary>The fitted design; null when the order failed.</summary>
    public OpticsSettings? Design { get; init; }

    /// <summary>The final merit function; NaN when the order failed.</summary>
    public double Merit { get; init; } = double.NaN;

    public IReadOnlyList<TestPlateFitStep> Steps { get; init; } = Array.Empty<TestPlateFitStep>();

    /// <summary>Why the order failed (named by surface and plate); null when it finished.</summary>
    public string? Error { get; init; }

    public bool Cancelled { get; init; }
}

/// <summary>The outcome of <see cref="TestPlateFitting.Run"/>.</summary>
public sealed record TestPlateFitResult
{
    public required OpticsSettings Start { get; init; }

    /// <summary>The fitted design: each matched radius set to its plate with its Variable flag
    /// removed, every other variable re-optimized; radii with no match are still variable. When
    /// cancelled, the design so far.</summary>
    public required OpticsSettings Fitted { get; init; }

    public required TestPlateList List { get; init; }

    /// <summary>The order whose design was kept.</summary>
    public required TestPlateFitMethod Method { get; init; }

    public required double StartMerit { get; init; }

    /// <summary>The merit after the local optimization before fitting.</summary>
    public required double PreOptimizedMerit { get; init; }

    public required double Merit { get; init; }

    /// <summary>The kept order's steps, in fitting order.</summary>
    public required IReadOnlyList<TestPlateFitStep> Steps { get; init; }

    /// <summary>Every order run (one, or four for Try All), in the help's order.</summary>
    public required IReadOnlyList<TestPlateFitRun> Runs { get; init; }

    public double TestWavelengthMicrometers { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>Notes on the run (cells not fitted, constraints not met), one per line.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>The surfaces for which no plate matched ("NO MATCH FOUND!").</summary>
    public IReadOnlyList<int> Unmatched => Steps.Where(s => s.Plate is null).Select(s => s.Surface).ToArray();

    /// <summary>The report the help describes: the vendor identification, the order in which the
    /// radii were fitted, each radius's plate and change, and the merit after each step.</summary>
    public IReadOnlyList<string> ReportLines()
    {
        var lines = new List<string> { "Test plate fitting" };
        if (List.Source is not null) lines.Add($"Test plate list: {List.Source}");
        foreach (string h in List.Header) lines.Add(" " + h);
        lines.Add($"Method of fit: {TestPlateFitting.Label(Method)}{(Runs.Count > 1 ? " (kept from Try All Methods)" : "")}.");
        if (Runs.Count > 1)
            foreach (var r in Runs)
                lines.Add($"  {TestPlateFitting.Label(r.Method)}: {(r.Error is null ? $"merit {F(r.Merit)}{(r.Cancelled ? " (stopped)" : "")}" : $"failed: {r.Error}")}");
        lines.Add($"Merit function: {F(StartMerit)} at the start, {F(PreOptimizedMerit)} after the optimization before fitting, {F(Merit)} after fitting.");
        lines.Add($"Fringes at {F(TestWavelengthMicrometers)} µm over each surface's clear semi-diameter. Radii in lens units.");
        foreach (var s in Steps)
        {
            string before = s.RadiusBefore is double b ? F(b) : "plane";
            lines.Add(s.Plate is { } p
                ? $"{s.Order}. Surface {s.Surface}: radius {before} → {F(s.RadiusAfter!.Value)} (change {(s.RadiusChange is double d ? F(d) : "from a plane")}), plate {p.Name} "
                  + $"({F(p.Radius)} mm, diameter {F(p.Diameter)} mm), {F(s.Fringes!.Value)} fringes; merit after {F(s.Merit)}{(s.ConstraintsMet ? "" : " (exact constraints not met)")}."
                : $"{s.Order}. Surface {s.Surface}: radius {before}: NO MATCH FOUND! {s.NoMatch} The radius stays variable; merit {F(s.Merit)}.");
        }
        lines.AddRange(Notes);
        if (Cancelled) lines.Add("Stopped: the design so far is kept.");
        return lines;
    }

    private static string F(double v) => v.ToString("G8", CultureInfo.InvariantCulture);
}

/// <summary>
/// Test plate fitting (plan item OP-17; OpticStudio 2025 R2 help, "Test Plate Fitting";
/// docs/Optics-Conventions.md §48). Only radii flagged Variable in the Lens Data Editor are fitted.
/// <list type="number">
/// <item>The design is optimized locally (<see cref="LocalOptimizer"/>) against the current merit
/// function before any fitting ("Before any fitting is performed, OpticStudio will do a local
/// optimization on the starting system").</item>
/// <item>For every remaining radius the closest admissible plate in fringes is found. A plate is
/// admissible when it has the shape the surface needs (a convex surface is tested by a concave
/// plate, a concave surface by a convex one, a cemented surface needs both; §48), when its
/// diameter is at least 3/4 of the clear aperture diameter (a diameter of 0 skips this test), and
/// when its sag at the clear semi-diameter differs from the surface's by at most 0.2 mm. The
/// fringes are N = r²·|c_plate − c|/λ, r the clear semi-diameter (from the TFRN help page's
/// relations ΔZ = (λ/2)·N and ΔC = 2ΔZ/r²). A radius with no admissible plate is reported
/// "NO MATCH FOUND!" and is not fitted (it stays variable).</item>
/// <item>The method picks one radius (closest or worst match, longest or shortest radius); its
/// plate's radius is substituted with the surface's sign, its Variable flag removed, and every
/// remaining variable re-optimized. This repeats until no variable radius is left.</item>
/// </list>
/// Try All runs the four orders from the same pre-optimized design and keeps the lowest merit
/// (the first in the help's order on a tie). Ties within an order go to the lower surface, ties
/// between plates to the earlier line of the list. Everything runs one step after another, so the
/// result does not depend on threads beyond the local optimizer's own guarantee.
/// </summary>
public static class TestPlateFitting
{
    /// <summary>Fits the variable radii of <paramref name="optics"/> to <paramref name="list"/>.</summary>
    /// <exception cref="ArgumentException">No variable radius; a fitted surface with air on both
    /// sides; an empty or invalid merit function; a non-positive test wavelength.</exception>
    /// <exception cref="InvalidOperationException">The merit function fails on the start, a clear
    /// semi-diameter cannot be found, or (for a single method, or every method of Try All) a
    /// re-optimization fails; the message names the surface and plate.</exception>
    public static TestPlateFitResult Run(OpticsSettings optics, TestPlateList list, IReadOnlyList<OpticalCatalog> catalogs,
        TestPlateFitOptions? options = null, MeritFunction? function = null, MeritOperandRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(optics);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new TestPlateFitOptions();
        registry ??= MeritOperandRegistry.CreateDefault();
        if (!(options.TestWavelengthMicrometers > 0) || !double.IsFinite(options.TestWavelengthMicrometers))
            throw new ArgumentException($"The test wavelength must be a positive number of micrometers, not {options.TestWavelengthMicrometers.ToString(CultureInfo.InvariantCulture)}.");
        if (!Enum.IsDefined(options.Method)) throw new ArgumentException($"Unknown method of fit {(int)options.Method}.");
        if (list.Plates.Count == 0) throw new ArgumentException("The test plate list has no plates.");
        var notes = new List<string>();
        if (function is null)
        {
            var read = new List<string>();
            function = MeritFunctionSettings.FromSettings(optics.MeritFunction, registry, read);
            notes.AddRange(read);
        }
        if (function.Rows.Count == 0)
            throw new ArgumentException("The merit function has no rows: build one in the Merit Function Editor before fitting test plates.");

        var surfaces = optics.Surfaces ?? new List<OpticsSurfaceSettings>();
        var variables = LensVariables.Find(optics);
        var radii = variables.Where(v => v.Kind == LensVariableKind.Curvature && v.Cell is null).Select(v => v.Surface).Distinct().OrderBy(j => j).ToList();
        if (radii.Count == 0)
            throw new ArgumentException("No radius is variable: flag the radii to fit Variable in the Lens Data Editor (only variable radii are fitted).");
        var table = optics.MultiConfiguration;
        foreach (var cell in variables.Where(v => v.Cell is not null).Select(v => v.Cell!.Value))
            if (string.Equals(table?.Operands?[cell.Operand - 1]?.Type, "CRVT", StringComparison.OrdinalIgnoreCase))
                notes.Add($"Configuration {cell.Configuration}, operand {cell.Operand} (CRVT) is variable; fitting multi-configuration curvature cells is not built, so it is re-optimized as a compensator only.");
        var problems = new List<string>();
        var sides = new Dictionary<int, SurfaceSide>();
        foreach (int j in radii)
        {
            if (j <= 0 || j >= surfaces.Count - 1) { problems.Add($"{SavedSurfaces.Where(j, surfaces.Count)}: the object and image radii are not fitted; remove the Variable flag."); continue; }
            if (Side(surfaces, j) is { } side) sides[j] = side;
            else problems.Add($"Surface {j} has air on both sides, so it is not a surface of any element and no plate tests it; remove its Variable flag before fitting.");
        }
        if (problems.Count > 0) throw new ArgumentException("These radii cannot be fitted to test plates:" + Environment.NewLine + string.Join(Environment.NewLine, problems));

        var context = new Context(list, catalogs, function, registry, options, sides,
            OpticsConventions.MetersPer(ProjectLens.LensUnitOf(optics.SystemData)) / OpticsConventions.MetersPer(LensUnit.Millimeter));
        var start = context.Evaluate(optics);
        if (start.HasFailures)
            throw new InvalidOperationException("The merit function cannot be evaluated on the starting design, so the fitting cannot begin:"
                + Environment.NewLine + string.Join(Environment.NewLine, start.Errors));

        var pre = context.Reoptimize(optics);
        if (pre is null)
            return new TestPlateFitResult
            {
                Start = optics, Fitted = optics, List = list, Method = options.Method, StartMerit = start.Value, PreOptimizedMerit = start.Value,
                Merit = start.Value, Steps = Array.Empty<TestPlateFitStep>(), Runs = Array.Empty<TestPlateFitRun>(),
                TestWavelengthMicrometers = options.TestWavelengthMicrometers, Cancelled = true, Notes = notes,
            };
        if (!pre.Value.Met) notes.Add("The optimization before fitting left exact (negative-weight) constraints off target.");

        var methods = options.Method == TestPlateFitMethod.TryAll
            ? new[] { TestPlateFitMethod.BestToWorst, TestPlateFitMethod.WorstToBest, TestPlateFitMethod.LongToShort, TestPlateFitMethod.ShortToLong }
            : new[] { options.Method };
        var runs = new List<TestPlateFitRun>();
        foreach (var method in methods)
        {
            TestPlateFitRun run;
            try { run = context.Fit(pre.Value.Design, pre.Value.Merit, radii, method); }
            catch (Exception e) when (methods.Length > 1 && e is ArgumentException or InvalidOperationException)
            {
                run = new TestPlateFitRun { Method = method, Error = e.Message };
            }
            runs.Add(run);
            if (run.Cancelled) break;
        }
        TestPlateFitRun? best = null;
        foreach (var r in runs)
            if (r.Error is null && (best is null || r.Merit < best.Merit)) best = r;
        if (best is null)
            throw new InvalidOperationException("Every method of fit failed:" + Environment.NewLine
                + string.Join(Environment.NewLine, runs.Select(r => $"{Label(r.Method)}: {r.Error}")));
        return new TestPlateFitResult
        {
            Start = optics, Fitted = best.Design!, List = list, Method = best.Method, StartMerit = start.Value,
            PreOptimizedMerit = pre.Value.Merit, Merit = best.Merit, Steps = best.Steps, Runs = runs,
            TestWavelengthMicrometers = options.TestWavelengthMicrometers, Cancelled = runs.Any(r => r.Cancelled), Notes = notes,
        };
    }

    /// <summary>The help's name of a method.</summary>
    public static string Label(TestPlateFitMethod method) => method switch
    {
        TestPlateFitMethod.TryAll => "Try All Methods",
        TestPlateFitMethod.BestToWorst => "Best To Worst",
        TestPlateFitMethod.WorstToBest => "Worst To Best",
        TestPlateFitMethod.LongToShort => "Long To Short",
        TestPlateFitMethod.ShortToLong => "Short To Long",
        _ => method.ToString(),
    };

    /// <summary>Which side of a surface the element's material is on, in the surface's local z
    /// (+1 the side the z axis points to); <see cref="Cemented"/> when both sides are solid.</summary>
    internal sealed record SurfaceSide(int SolidSide, bool Cemented);

    /// <summary>
    /// The side of surface <paramref name="j"/> its element's material lies on (§48), or null when
    /// both sides are air. Light runs along +z until a mirror and reverses at each mirror. A
    /// refracting surface's material is the solid medium after it (+direction) or before it
    /// (−direction); a mirror's is its substrate behind the reflecting face (+direction), or the
    /// glass in front of it when the light arrives through glass (a Mangin mirror). The medium after
    /// a mirror is the one the light arrived through. A constant index of exactly 1 counts as air.
    /// </summary>
    internal static SurfaceSide? Side(IReadOnlyList<OpticsSurfaceSettings> surfaces, int j)
    {
        static bool Solid(OpticsSurfaceSettings s) => !SavedSurfaces.IsAir(s) && !SavedSurfaces.IsMirror(s)
            && !(s.Index == 1.0 && string.IsNullOrWhiteSpace(s.Material) && s.ModelGlass is null);
        bool solidBefore = false;
        int direction = 1;
        for (int i = 0; i < j; i++)
        {
            var s = surfaces[i] ?? new OpticsSurfaceSettings();
            if (SavedSurfaces.IsMirror(s)) direction = -direction;   // the medium after a mirror is the one before it
            else solidBefore = Solid(s);
        }
        var here = surfaces[j] ?? new OpticsSurfaceSettings();
        if (SavedSurfaces.IsMirror(here)) return new SurfaceSide(solidBefore ? -direction : direction, false);
        bool solidAfter = Solid(here);
        if (solidAfter && solidBefore) return new SurfaceSide(direction, true);
        if (solidAfter) return new SurfaceSide(direction, false);
        if (solidBefore) return new SurfaceSide(-direction, false);
        return null;
    }

    /// <summary>The sag of a conic of curvature <paramref name="c"/> and conic constant
    /// <paramref name="k"/> at height <paramref name="r"/> (Conventions §3), or NaN beyond its edge.</summary>
    internal static double Sag(double c, double k, double r)
    {
        double q = 1 - (1 + k) * c * c * r * r;
        return q < 0 ? double.NaN : c * r * r / (1 + Math.Sqrt(q));
    }

    /// <summary>The closest admissible plate for one surface, or the reason none is.</summary>
    internal readonly record struct Match(TestPlate? Plate, int Sign, double Fringes, string? Reason);

    internal static Match FindPlate(TestPlateList list, double curvatureMm, double conic, double semiDiameterMm, SurfaceSide side, double wavelengthMm)
    {
        int[] signs = curvatureMm > 0 ? new[] { 1 } : curvatureMm < 0 ? new[] { -1 } : new[] { 1, -1 };
        double r = semiDiameterMm;
        double surfaceSag = Sag(curvatureMm, conic, r);
        if (double.IsNaN(surfaceSag))
            return new Match(null, 0, 0, $"The surface (curvature {F(curvatureMm)} /mm, conic {F(conic)}) does not reach its clear semi-diameter {F(r)} mm, so its edge sag is undefined.");
        int wrongShape = 0, tooSmall = 0, sagOff = 0;
        TestPlate? best = null;
        int bestSign = 0;
        double bestFringes = double.PositiveInfinity;
        double minimumDiameter = TestPlateFitOptions.DiameterFraction * 2 * r;
        foreach (var plate in list.Plates)
        {
            int reason = 0;
            foreach (int sign in signs)
            {
                bool shapeOk = side.Cemented ? plate.Shape == TestPlateShape.Both
                    : sign == side.SolidSide ? plate.HasConcave   // a convex surface is tested by a concave plate
                    : plate.HasConvex;
                if (!shapeOk) { reason = 1; continue; }
                if (plate.Diameter > 0 && plate.Diameter < minimumDiameter) { reason = 2; continue; }
                double cp = sign / plate.Radius;
                double plateSag = Sag(cp, 0, r);
                if (double.IsNaN(plateSag) || Math.Abs(plateSag - surfaceSag) > TestPlateFitOptions.EdgeSagLimitMillimeters) { reason = 3; continue; }
                reason = 0;
                double n = r * r * Math.Abs(cp - curvatureMm) / wavelengthMm;
                if (n < bestFringes) { best = plate; bestSign = sign; bestFringes = n; }
            }
            if (reason == 1) wrongShape++;
            else if (reason == 2) tooSmall++;
            else if (reason == 3) sagOff++;
        }
        if (best is not null) return new Match(best, bestSign, bestFringes, null);
        string need = side.Cemented ? "both a concave and a convex plate (a cemented surface)"
            : curvatureMm == 0 ? "a plate of either shape" : signs[0] == side.SolidSide ? "a concave plate (a convex surface)" : "a convex plate (a concave surface)";
        return new Match(null, 0, 0, $"None of the {list.Plates.Count} plates fits: the surface needs {need}; {wrongShape} have the wrong shape, "
            + $"{tooSmall} are smaller than {F(minimumDiameter)} mm (3/4 of the clear aperture), {sagOff} differ from the surface's edge sag by more than "
            + $"{F(TestPlateFitOptions.EdgeSagLimitMillimeters)} mm at the clear semi-diameter {F(r)} mm (or are shorter than it).");
    }

    private sealed class Context
    {
        private readonly TestPlateList _list;
        private readonly IReadOnlyList<OpticalCatalog> _catalogs;
        private readonly MeritFunction _function;
        private readonly MeritOperandRegistry _registry;
        private readonly TestPlateFitOptions _options;
        private readonly IReadOnlyDictionary<int, SurfaceSide> _sides;
        private readonly double _mmPerUnit;

        public Context(TestPlateList list, IReadOnlyList<OpticalCatalog> catalogs, MeritFunction function, MeritOperandRegistry registry,
            TestPlateFitOptions options, IReadOnlyDictionary<int, SurfaceSide> sides, double mmPerUnit)
        {
            _list = list; _catalogs = catalogs; _function = function; _registry = registry; _options = options; _sides = sides; _mmPerUnit = mmPerUnit;
        }

        private LocalOptimizationOptions Local => _options.Optimization ?? new LocalOptimizationOptions();

        public MeritEvaluation Evaluate(OpticsSettings design) =>
            MeritEvaluator.Evaluate(_function, new ProjectMeritConfigurations(design, _catalogs), _registry,
                (Local.Merit ?? new MeritEvaluationOptions()) with { MaxDegreeOfParallelism = Local.MaxDegreeOfParallelism });

        /// <summary>The design re-optimized (only evaluated when nothing is variable); null when cancelled.</summary>
        public (OpticsSettings Design, double Merit, bool Met)? Reoptimize(OpticsSettings design)
        {
            var token = _options.CancellationToken;
            if (token.IsCancellationRequested) return null;
            if (LensVariables.Find(design).Count == 0)
            {
                var e = Evaluate(design);
                if (e.HasFailures) throw new InvalidOperationException(string.Join(Environment.NewLine, e.Errors));
                return (design, e.Value, true);
            }
            var local = Local;
            var result = LocalOptimizer.Run(design, _catalogs, new LocalOptimizationOptions
            {
                Algorithm = local.Algorithm, Cycles = local.Cycles == LocalOptimizationCycles.Infinite ? LocalOptimizationCycles.Automatic : local.Cycles,
                CycleCount = local.CycleCount, Kinds = local.Kinds, MaxDegreeOfParallelism = local.MaxDegreeOfParallelism,
                MaxEvaluations = local.MaxEvaluations, ConstraintTolerance = local.ConstraintTolerance, MaxConstraintRounds = local.MaxConstraintRounds,
                Merit = local.Merit, CancellationToken = token,
            }, _function, _registry);
            if (result.Cancelled) return null;
            if (result.Evaluation.HasFailures) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Evaluation.Errors));
            return (result.Optimized, result.Merit, result.Stop != LocalOptimizationStop.ConstraintsNotMet);
        }

        /// <summary>One method of fit from the pre-optimized design.</summary>
        public TestPlateFitRun Fit(OpticsSettings design, double merit, IReadOnlyList<int> radii, TestPlateFitMethod method)
        {
            var remaining = radii.ToList();
            var steps = new List<TestPlateFitStep>();
            double wavelengthMm = _options.TestWavelengthMicrometers * 1e-3;
            while (remaining.Count > 0)
            {
                if (_options.CancellationToken.IsCancellationRequested)
                    return new TestPlateFitRun { Method = method, Design = design, Merit = merit, Steps = steps, Cancelled = true };
                var semi = SemiDiameters(design, remaining);
                int chosen = -1;
                Match chosenMatch = default;
                double chosenKey = 0;
                foreach (int j in remaining.ToArray())
                {
                    var s = design.Surfaces![j];
                    double c = SavedSurfaces.IsPlane(s) ? 0 : 1 / s.Radius!.Value;
                    var m = FindPlate(_list, c / _mmPerUnit, s.Conic, semi[j] * _mmPerUnit, _sides[j], wavelengthMm);
                    if (m.Plate is null)
                    {
                        remaining.Remove(j);
                        steps.Add(new TestPlateFitStep
                        {
                            Order = steps.Count + 1, Surface = j, RadiusBefore = SavedSurfaces.IsPlane(s) ? null : s.Radius,
                            SemiDiameter = semi[j], Merit = merit, NoMatch = m.Reason,
                        });
                        continue;
                    }
                    // Smaller key first: fewest fringes, most fringes, longest radius, shortest radius.
                    double key = method switch
                    {
                        TestPlateFitMethod.BestToWorst => m.Fringes,
                        TestPlateFitMethod.WorstToBest => -m.Fringes,
                        TestPlateFitMethod.LongToShort => Math.Abs(c),
                        _ => -Math.Abs(c),
                    };
                    if (chosen < 0 || key < chosenKey) { chosen = j; chosenMatch = m; chosenKey = key; }
                }
                if (chosen < 0) break;
                remaining.Remove(chosen);
                var before = design.Surfaces![chosen];
                double radius = chosenMatch.Sign * chosenMatch.Plate!.Radius / _mmPerUnit;
                var list = design.Surfaces!.ToList();
                list[chosen] = SurfaceFlags.Without(before with { Radius = radius },
                    f => string.Equals(f.Column, SavedSurfaces.RadiusColumn, StringComparison.Ordinal) && SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag));
                var snapped = design with { Surfaces = list };
                (OpticsSettings Design, double Merit, bool Met)? next;
                try { next = Reoptimize(snapped); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    throw new InvalidOperationException($"{Label(method)}: after fitting surface {chosen} to plate {chosenMatch.Plate.Name} "
                        + $"(line {chosenMatch.Plate.Line}), the design could not be re-optimized: {e.Message}", e);
                }
                if (next is null)
                {
                    var partial = Evaluate(snapped);
                    steps.Add(Step(steps.Count + 1, chosen, before, radius, chosenMatch, semi[chosen], partial.Value, true));
                    return new TestPlateFitRun { Method = method, Design = snapped, Merit = partial.Value, Steps = steps, Cancelled = true };
                }
                design = next.Value.Design;
                merit = next.Value.Merit;
                steps.Add(Step(steps.Count + 1, chosen, before, radius, chosenMatch, semi[chosen], merit, next.Value.Met));
                _options.Progress?.Invoke(new TestPlateFitProgress(method, remaining.Count, merit));
            }
            return new TestPlateFitRun { Method = method, Design = design, Merit = merit, Steps = steps };
        }

        private static TestPlateFitStep Step(int order, int surface, OpticsSurfaceSettings before, double radius, Match m, double semi, double merit, bool met) => new()
        {
            Order = order, Surface = surface, RadiusBefore = SavedSurfaces.IsPlane(before) ? null : before.Radius,
            RadiusAfter = radius, Plate = m.Plate, Fringes = m.Fringes, SemiDiameter = semi, Merit = merit, ConstraintsMet = met,
        };

        /// <summary>The clear semi-diameters (lens units) of the given surfaces: a fixed one as
        /// entered, automatic ones sized as the editor sizes them (<see cref="ApertureTools.ClearSemiDiameters"/>).</summary>
        private Dictionary<int, double> SemiDiameters(OpticsSettings design, IReadOnlyList<int> which)
        {
            var result = new Dictionary<int, double>();
            double[]? sized = null;
            foreach (int j in which)
            {
                var s = design.Surfaces![j];
                double value;
                if (s.SemiDiameter is double fixedValue) value = fixedValue;
                else
                {
                    if (sized is null)
                    {
                        try { sized = ApertureTools.ClearSemiDiameters(design.Surfaces!, new LensEditContext(design.SystemData, _catalogs)); }
                        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                        {
                            throw new InvalidOperationException($"Surface {j} has an automatic semi-diameter, and the automatic semi-diameters cannot be sized: {e.Message} "
                                + "Enter fixed semi-diameters on the fitted surfaces, or correct the lens.", e);
                        }
                    }
                    value = sized[j];
                }
                if (!(value > 0) || !double.IsFinite(value))
                    throw new InvalidOperationException($"Surface {j}: the clear semi-diameter is {value.ToString("G8", CultureInfo.InvariantCulture)}, so the plate's fringes, diameter and edge sag cannot be checked; enter a positive semi-diameter.");
                result[j] = value;
            }
            return result;
        }
    }

    private static string F(double v) => v.ToString("G8", CultureInfo.InvariantCulture);
}
