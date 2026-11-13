namespace OpenSim.Optics;

/// <summary>
/// How an optical system is described and traced. One Optics workspace carries both, chosen by
/// a mode switch (plan item OP-1, owner decision OQ-1).
/// </summary>
public enum OpticsMode
{
    /// <summary>Surfaces in a fixed order: every ray meets surface 1, then 2, and so on. The
    /// description lens design uses (imaging quality, optimization, tolerancing).</summary>
    Sequential = 0,

    /// <summary>Objects in space with no set order: a ray meets whatever lies in its path, and
    /// may split and scatter. The description illumination and stray-light work uses.</summary>
    NonSequential = 1
}

/// <summary>The names an <see cref="OpticsMode"/> is saved under and shown as.</summary>
public static class OpticsModes
{
    public static IReadOnlyList<OpticsMode> All { get; } = new[] { OpticsMode.Sequential, OpticsMode.NonSequential };

    /// <summary>The name written to a project file. Fixed: a file saved today must read the
    /// same way after the enum's members are renamed.</summary>
    public static string SavedName(OpticsMode mode) => mode switch
    {
        OpticsMode.Sequential => "Sequential",
        OpticsMode.NonSequential => "NonSequential",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "No saved name for this optics mode.")
    };

    /// <summary>The label a picker shows.</summary>
    public static string Label(OpticsMode mode) => mode switch
    {
        OpticsMode.Sequential => "Sequential",
        OpticsMode.NonSequential => "Non-sequential",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "No label for this optics mode.")
    };

    /// <summary>Reads a saved name back. False for a name this build does not know (a
    /// hand-edited file, or one from a later build), so the caller can say so instead of
    /// quietly opening the system in another mode.</summary>
    public static bool TryParseSavedName(string? name, out OpticsMode mode)
    {
        foreach (var candidate in All)
        {
            if (SavedName(candidate) != name) continue;
            mode = candidate;
            return true;
        }
        mode = OpticsMode.Sequential;
        return false;
    }
}
