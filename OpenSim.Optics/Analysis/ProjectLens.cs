using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

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

    /// <summary>The lens of an optics record with the given active catalogs, or null when the
    /// record has no surfaces.</summary>
    public static SequentialLens? From(OpticsSettings system, IReadOnlyList<OpticalCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(catalogs);
        if (system.Surfaces is not { Count: > 0 } surfaces) return null;
        var unit = LensUnitOf(system.SystemData);
        return SavedSurfaces.ToLens(surfaces, unit, name => FindGlass(name, catalogs),
            surfaces.Any(s => s?.ModelGlass is not null) ? ModelGlassMaker(catalogs) : null);
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
