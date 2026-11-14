using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Sequential.LensEditing;

/// <summary>
/// What the editing tools that evaluate the lens (Make Focal, the aperture conversions that
/// size semi-diameters, System Check) need besides the surfaces: the system data as the file
/// stores it, the active glass catalogs and the names of the coatings in the lens's catalog.
/// </summary>
public sealed record LensEditContext(OpticsSystemSettings? SystemData, IReadOnlyList<OpticalCatalog> Catalogs)
{
    /// <summary>The coating names of the lens's coating catalog, for System Check; null skips
    /// the coating check.</summary>
    public IReadOnlyCollection<string>? CoatingNames { get; init; }

    /// <summary>The lens unit the saved lengths are in.</summary>
    public LensUnit Unit => ProjectLens.LensUnitOf(SystemData);

    /// <summary>The system data in meters; notes on names this build does not know go to
    /// <paramref name="notes"/> when given.</summary>
    public OpticalSystemData System(ICollection<string>? notes = null) =>
        SystemDataSettings.FromSettings(SystemData, notes ?? new List<string>());

    /// <summary>The traceable lens (<see cref="SavedSurfaces.ToLens"/>); throws with every problem.</summary>
    public SequentialLens ToLens(IReadOnlyList<OpticsSurfaceSettings> surfaces) =>
        ProjectLens.From(new OpticsSettings { SystemData = SystemData, Surfaces = surfaces.ToList() }, Catalogs)
        ?? throw new ArgumentException("Saved lens: there are no surfaces.");
}

/// <summary>The surfaces an editing tool produced and what it wants the user to know.</summary>
public sealed record LensEditResult(IReadOnlyList<OpticsSurfaceSettings> Surfaces, IReadOnlyList<string> Notes)
{
    public LensEditResult(IReadOnlyList<OpticsSurfaceSettings> surfaces) : this(surfaces, Array.Empty<string>()) { }
}

/// <summary>Reading and changing the per-cell flags of a saved surface.</summary>
public static class SurfaceFlags
{
    /// <summary>The flag on <paramref name="column"/>, or null for a fixed cell.</summary>
    public static OpticsCellFlagSettings? Of(OpticsSurfaceSettings surface, string column) =>
        surface.Flags?.LastOrDefault(f => string.Equals(f.Column, column, StringComparison.Ordinal));

    /// <summary>The surface with the flags matching <paramref name="drop"/> removed (those cells fixed).</summary>
    public static OpticsSurfaceSettings Without(OpticsSurfaceSettings surface, Func<OpticsCellFlagSettings, bool> drop)
    {
        if (surface.Flags is not { Count: > 0 } flags || !flags.Any(drop)) return surface;
        var kept = flags.Where(f => !drop(f)).ToList();
        return surface with { Flags = kept.Count > 0 ? kept : null };
    }

    /// <summary>The surface with <paramref name="flag"/> on its column, replacing any flag there;
    /// a null flag fixes the cell.</summary>
    public static OpticsSurfaceSettings With(OpticsSurfaceSettings surface, string column, OpticsCellFlagSettings? flag)
    {
        var s = Without(surface, f => string.Equals(f.Column, column, StringComparison.Ordinal));
        if (flag is null) return s;
        var list = s.Flags?.ToList() ?? new List<OpticsCellFlagSettings>();
        list.Add(flag with { Column = column });
        return s with { Flags = list };
    }

    /// <summary>A variable flag.</summary>
    public static OpticsCellFlagSettings Variable(string column) => new() { Column = column, Kind = SavedSurfaces.VariableFlag };

    /// <summary>A pickup from (<paramref name="surface"/>, <paramref name="column"/>).</summary>
    public static OpticsCellFlagSettings Pickup(string column, int surface, string sourceColumn, double scale = 1, double offset = 0) => new()
    {
        Column = column, Kind = SavedSurfaces.PickupFlag, PickupSurface = surface, PickupColumn = sourceColumn,
        PickupScale = scale, PickupOffset = offset
    };

    public static bool IsKind(OpticsCellFlagSettings flag, string kind) => string.Equals(flag.Kind, kind, StringComparison.Ordinal);

    /// <summary>True for the columns whose values are lengths (scaled by Scale Lens).</summary>
    public static bool IsLengthColumn(string? column) => column is SavedSurfaces.RadiusColumn or SavedSurfaces.ThicknessColumn
        or SavedSurfaces.SemiDiameterColumn or SavedSurfaces.ChipZoneColumn or SavedSurfaces.MechanicalSemiDiameterColumn;

    /// <summary>Checks a surface range [first, last] against the list, naming the tool.</summary>
    internal static void CheckRange(string tool, IReadOnlyList<OpticsSurfaceSettings> surfaces, int first, int last, int lowest, int highest)
    {
        if (surfaces.Count < 3) throw new ArgumentException($"{tool}: the lens needs the object, at least one surface and the image.");
        if (first < lowest || last > highest || first > last)
            throw new ArgumentException($"{tool}: the surface range must lie within {lowest} to {highest} with the first not after the last (it is {first} to {last}).");
    }
}
