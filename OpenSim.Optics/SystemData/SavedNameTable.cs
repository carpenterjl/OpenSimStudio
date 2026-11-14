namespace OpenSim.Optics;

/// <summary>
/// The fixed names an enum's values are saved under in project and lens files, and the labels
/// a picker shows (the pattern of <see cref="OpticsModes"/>). A saved name never follows a
/// rename of the enum member, so a file written today reads the same way later; a name this
/// build does not know is refused, so the caller can say so instead of quietly opening the
/// system with another choice.
/// </summary>
public sealed class SavedNameTable<T> where T : struct, Enum
{
    private readonly (T Value, string SavedName, string Label)[] _entries;

    /// <summary>One entry per enum value, in the order a picker lists them. Throws when a
    /// value has no entry or a saved name repeats, so a new member cannot ship without a
    /// name.</summary>
    public SavedNameTable(params (T Value, string SavedName, string Label)[] entries)
    {
        foreach (var value in Enum.GetValues<T>())
            if (!entries.Any(e => EqualityComparer<T>.Default.Equals(e.Value, value)))
                throw new ArgumentException($"{typeof(T).Name}.{value} has no saved name.", nameof(entries));
        if (entries.Select(e => e.SavedName).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new ArgumentException($"Two {typeof(T).Name} values share a saved name.", nameof(entries));
        if (entries.Select(e => e.Value).Distinct().Count() != entries.Length)
            throw new ArgumentException($"A {typeof(T).Name} value is listed twice.", nameof(entries));
        _entries = entries;
        All = entries.Select(e => e.Value).ToArray();
    }

    /// <summary>Every value, in picker order.</summary>
    public IReadOnlyList<T> All { get; }

    /// <summary>The name written to a file.</summary>
    public string SavedName(T value) => Find(value).SavedName;

    /// <summary>The text a picker shows.</summary>
    public string Label(T value) => Find(value).Label;

    /// <summary>Reads a saved name back; names are exact (a near miss is a hand edit and is
    /// refused). False, with <paramref name="value"/> the first entry, for an unknown name.</summary>
    public bool TryParse(string? savedName, out T value)
    {
        foreach (var entry in _entries)
        {
            if (!string.Equals(entry.SavedName, savedName, StringComparison.Ordinal)) continue;
            value = entry.Value;
            return true;
        }
        value = _entries[0].Value;
        return false;
    }

    private (T Value, string SavedName, string Label) Find(T value)
    {
        foreach (var entry in _entries)
            if (EqualityComparer<T>.Default.Equals(entry.Value, value)) return entry;
        throw new ArgumentOutOfRangeException(nameof(value), value, $"No saved name for this {typeof(T).Name}.");
    }
}
