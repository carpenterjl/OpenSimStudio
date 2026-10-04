namespace OpenSim.Core.Model;

/// <summary>
/// Turns a body's per-region material NAMES into materials for a multi-material solve.
/// Every named region must resolve: a solve input falls back to its single body material
/// for a region with no entry, so dropping an unresolved name would solve that region
/// with the wrong material and say nothing.
/// </summary>
public static class RegionMaterialResolver
{
    /// <param name="bodyName">For the failure message.</param>
    /// <param name="regionMaterialNames">Mesh region id → material name; null or empty for
    /// a single-material body.</param>
    /// <param name="lookup">The material of a name, or null when there is none.</param>
    /// <returns>Region id → material, or null for a single-material body.</returns>
    public static IReadOnlyDictionary<int, Material>? Resolve(string bodyName,
        IReadOnlyDictionary<int, string>? regionMaterialNames, Func<string, Material?> lookup)
    {
        if (regionMaterialNames is not { Count: > 0 }) return null;
        var map = new Dictionary<int, Material>();
        var missing = new List<string>();
        foreach (var (region, name) in regionMaterialNames.OrderBy(p => p.Key))
        {
            if (lookup(name) is { } material) map[region] = material;
            else missing.Add($"region {region} → '{name}'");
        }
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Body '{bodyName}' names material(s) that are not in the material library: " +
                string.Join(", ", missing) + ". Add the material back (or re-import the board) — " +
                "solving those regions with another material would give a wrong answer silently.");
        return map;
    }
}
