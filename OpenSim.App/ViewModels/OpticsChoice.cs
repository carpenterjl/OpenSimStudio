using OpenSim.Optics;

namespace OpenSim.App.ViewModels;

/// <summary>One entry of an optics picker: the label shown and the value behind it.</summary>
public sealed record OpticsChoice<T>(string Label, T Value) where T : struct, Enum
{
    /// <summary>The picker's entries from a saved-name table, in its order.</summary>
    public static IReadOnlyList<OpticsChoice<T>> From(SavedNameTable<T> names) =>
        names.All.Select(v => new OpticsChoice<T>(names.Label(v), v)).ToArray();
}
