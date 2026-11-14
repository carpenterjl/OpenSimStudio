using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Solves;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Optics.Analysis;

/// <summary>
/// A project's lens as entered and as solved (<see cref="ProjectLens.Solve"/>), with each
/// translated solve's status and every error.
/// </summary>
public sealed record ProjectLensSolution
{
    /// <summary>The lens as the saved cells give it.</summary>
    public required SequentialLens Entered { get; init; }

    /// <summary>The lens with every solve that worked applied; the solved lens when
    /// <see cref="Succeeded"/>.</summary>
    public required SequentialLens Lens { get; init; }

    /// <summary>The lens unit the saved values are in.</summary>
    public LensUnit Unit { get; init; }

    /// <summary>The translated solves and pickups, one per driven saved cell.</summary>
    public IReadOnlyList<SavedSolveCell> Solves { get; init; } = Array.Empty<SavedSolveCell>();

    /// <summary>The engine's result, or null when it did not run.</summary>
    public SolveResult? Result { get; init; }

    /// <summary>Translation problems, solve failures and non-convergence, one line each naming
    /// the surface and the cell.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Flags kept but not evaluated by design (<see cref="SavedSolveSet.NotEvaluated"/>).</summary>
    public IReadOnlyList<string> NotEvaluated { get; init; } = Array.Empty<string>();

    public bool Succeeded => Errors.Count == 0;

    /// <summary>The error of the solve on a saved cell, or null when it worked or the cell has none.</summary>
    public string? ErrorOf(int surface, string column)
    {
        if (Result is null) return null;
        foreach (var cell in Solves)
        {
            if (cell.Surface != surface || cell.Column != column) continue;
            var error = Result.Statuses.FirstOrDefault(st => ReferenceEquals(st.Solve, cell.Solve))?.Error;
            if (error is null) return null;
            // A solve set refused before any pass marks every status "not applied"; the reason
            // for this cell is the error line the engine names it in.
            var s = cell.Solve;
            string prefix = $"Surface {s.Surface}, {SolveColumns.Label(s.Column, s.ParameterIndex)}, {s.Name.ToLowerInvariant()} solve";
            return Result.Errors.FirstOrDefault(e => e.StartsWith(prefix, StringComparison.Ordinal)) ?? error;
        }
        return null;
    }

    /// <summary>The solved lens, or <see cref="ArgumentException"/> listing every error.</summary>
    public SequentialLens LensOrThrow() => Succeeded ? Lens
        : throw new ArgumentException(string.Join(Environment.NewLine, Errors));
}

/// <summary>
/// The lens an analysis reads from the project (OP-31): the saved surfaces of the snapshot's
/// optics record, converted with its lens unit and its active glass catalogs
/// (<see cref="SavedSurfaces.ToLens"/>). Null when the project has no surfaces, which the
/// analyses report as "No surfaces"; a lens that cannot be converted throws with every problem
/// named, which the analysis window shows.
/// </summary>
public static class ProjectLens
{
    /// <summary>The real-trace lens of the snapshot, or null when it has no surfaces.</summary>
    public static SequentialLens? Sequential(OpticsAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return From(context.System, context.Catalogs);
    }

    /// <summary>The paraxial lens of the snapshot, or null when it has no surfaces.</summary>
    public static ParaxialLens? Paraxial(OpticsAnalysisContext context) => Sequential(context)?.ToParaxialLens();

    /// <summary>The solved lens of an optics record with the given active catalogs, or null when
    /// the record has no surfaces; throws <see cref="ArgumentException"/> with every problem
    /// when the lens cannot be converted or a solve fails (<see cref="Solve"/>).</summary>
    public static SequentialLens? From(OpticsSettings system, IReadOnlyList<OpticalCatalog> catalogs) =>
        Solve(system, catalogs)?.LensOrThrow();

    /// <summary>
    /// The lens of an optics record as entered, with its solves and pickups evaluated (TC-6): the
    /// saved surfaces are converted (<see cref="SavedSurfaces.ToLens"/>, which throws
    /// <see cref="ArgumentException"/> with every conversion problem), their flags translated
    /// (<see cref="SavedSolves.Translate"/>), and <see cref="SolveEngine.Apply"/> run with the
    /// record's system data and active catalogs. Paraxial surfaces in OPD mode 3 then get their
    /// conjugates from the solved lens (<see cref="ParaxialShape.ResolveConjugates"/>). A lens
    /// without solves, pickups or such surfaces is returned as converted, without reading the
    /// system data. Null when the record has no surfaces.
    /// </summary>
    public static ProjectLensSolution? Solve(OpticsSettings system, IReadOnlyList<OpticalCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(catalogs);
        if (system.Surfaces is not { Count: > 0 } surfaces) return null;
        var unit = LensUnitOf(system.SystemData);
        var entered = SavedSurfaces.ToLens(surfaces, unit, name => FindGlass(name, catalogs),
            surfaces.Any(s => s?.ModelGlass is not null) ? ModelGlassMaker(catalogs) : null);
        var set = SavedSolves.Translate(surfaces, unit);
        var mode3 = Enumerable.Range(0, surfaces.Count)
            .Where(j => surfaces[j] is { } s && string.Equals(s.Type, "Paraxial", StringComparison.OrdinalIgnoreCase) && SavedSurfaces.Value(s, 2) == 3)
            .ToArray();
        if (set.Cells.Count == 0 && set.Problems.Count == 0 && mode3.Length == 0)
            return new ProjectLensSolution { Entered = entered, Lens = entered, Unit = unit, NotEvaluated = set.NotEvaluated };

        var errors = new List<string>(set.Problems);
        SolveResult? result = null;
        var lens = entered;
        if (errors.Count == 0)
        {
            var notes = new List<string>();
            var data = SystemDataSettings.FromSettings(system.SystemData, notes);
            if (data.Validate() is { Count: > 0 } invalid)
                errors.AddRange(invalid.Select(p => $"The solves need valid system data: {p}"));
            else
            {
                if (set.Cells.Count > 0)
                {
                    result = SolveEngine.Apply(entered, data, set.Solves, new SolveOptions { Catalogs = catalogs });
                    lens = result.Lens;
                    errors.AddRange(result.Errors);
                }
                if (errors.Count == 0 && mode3.Length > 0)
                {
                    try { lens = ParaxialShape.ResolveConjugates(lens, data, mode3); }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { errors.Add(ex.Message); }
                }
            }
        }
        return new ProjectLensSolution
        {
            Entered = entered, Lens = lens, Unit = unit, Solves = set.Cells, Result = result, Errors = errors, NotEvaluated = set.NotEvaluated,
        };
    }

    /// <summary>The lens unit the saved lengths are in (millimeters when not given or unknown;
    /// the system data's own reader names an unknown unit).</summary>
    public static LensUnit LensUnitOf(OpticsSystemSettings? systemData) =>
        SystemDataSettings.Pick(ConventionNames.LensUnits, systemData?.LensUnit, OpticalSystemData.Default.Units.LensUnit, "Lens unit", new List<string>());

    /// <summary>A glass by name in the catalogs, searched in order; "catalog:glass" picks a catalog.</summary>
    public static OpticalMaterial? FindGlass(string name, IReadOnlyList<OpticalCatalog> catalogs)
    {
        int colon = name.IndexOf(':');
        if (colon > 0)
        {
            string catalog = name[..colon].Trim(), glass = name[(colon + 1)..].Trim();
            return catalogs.FirstOrDefault(c => string.Equals(c.Name, catalog, StringComparison.OrdinalIgnoreCase))?.Find(glass);
        }
        foreach (var catalog in catalogs)
            if (catalog.Find(name.Trim()) is { } found) return found;
        return null;
    }

    private static readonly object CacheLock = new();
    private static (OpticalCatalog[] Catalogs, ModelGlassFit Fit)? _cache;

    /// <summary>Model glasses trained on the catalogs (<see cref="ModelGlassFit"/>); the last fit
    /// is kept, as training reads every glass.</summary>
    public static Func<OpticsModelGlassSettings, OpticalMaterial> ModelGlassMaker(IReadOnlyList<OpticalCatalog> catalogs)
    {
        ModelGlassFit? fit = null;
        return m =>
        {
            fit ??= Train(catalogs);
            return fit.Create(m.Nd, m.Vd, m.DeltaPgF, FormattableString.Invariant($"MODEL {m.Nd:R}/{m.Vd:R}"));
        };
    }

    private static ModelGlassFit Train(IReadOnlyList<OpticalCatalog> catalogs)
    {
        if (catalogs.Count == 0)
            throw new InvalidOperationException("there are no active glass catalogs to train the model on.");
        lock (CacheLock)
        {
            if (_cache is { } c && c.Catalogs.Length == catalogs.Count && c.Catalogs.Zip(catalogs).All(p => ReferenceEquals(p.First, p.Second)))
                return c.Fit;
            var fit = ModelGlassFit.Train(catalogs.SelectMany(k => k.Materials));
            _cache = (catalogs.ToArray(), fit);
            return fit;
        }
    }
}
