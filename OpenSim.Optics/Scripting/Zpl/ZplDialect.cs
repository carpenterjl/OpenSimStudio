namespace OpenSim.Optics.Scripting.Zpl;

// OP-67: keywords that exist only in one context of the macro language (docs/Optics-Conventions.md
// §60). A tolerance script (OpticStudio 2025 R2 help, "The Tolerance Script Commands") is a macro
// with the tolerance-script commands added; those names are keywords there and nowhere else.

/// <summary>
/// A set of keywords added to ZPL in one context. The parser reads a line whose first word is
/// one of <see cref="Names"/> as that keyword with the rest of the line as its argument text (a
/// name may contain '-', as OPTIMIZE-OD does); the interpreter runs it with <see cref="Handler"/>
/// before the library's own keywords, so a context keyword of the same name as a ZPL keyword
/// (LOADMERIT, OPTIMIZE, UPDATE) takes its place in that context.
/// </summary>
internal sealed class ZplDialect
{
    private readonly Dictionary<string, ZplKeywordHandler> _handlers;

    /// <param name="name">What the context is, for messages ("tolerance script").</param>
    /// <param name="names">The keywords, upper case.</param>
    /// <param name="handlers">The handlers by keyword; null at parse time, when only the names matter.</param>
    public ZplDialect(string name, IReadOnlyCollection<string> names, IReadOnlyDictionary<string, ZplKeywordHandler>? handlers = null)
    {
        Name = name;
        Names = new HashSet<string>(names, StringComparer.Ordinal);
        _handlers = handlers is null ? new(StringComparer.Ordinal) : new Dictionary<string, ZplKeywordHandler>(handlers, StringComparer.Ordinal);
    }

    public string Name { get; }

    public IReadOnlySet<string> Names { get; }

    public bool Binds(string upper) => Names.Contains(upper);

    /// <summary>The handler of a keyword of this context; null when the name is not one.</summary>
    public ZplKeywordHandler? Handler(string upper)
    {
        if (!Names.Contains(upper)) return null;
        return _handlers.TryGetValue(upper, out var h) ? h
            : (m, _) => throw new ZplRuntimeException($"{upper} is a {Name} command; it runs only inside a {Name} run.");
    }

    /// <summary>The keyword a line starts with, the text after it and that text's offset in the
    /// line; null when the line does not start with one of <see cref="Names"/> followed by a
    /// space, a tab or the end of the line.</summary>
    public (string Name, string Tail, int Offset)? Match(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        int start = i;
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '-')) i++;
        if (i == start || (i < line.Length && !char.IsWhiteSpace(line[i]))) return null;
        string word = line[start..i].ToUpperInvariant();
        return Names.Contains(word) ? (word, line[i..], i) : null;
    }
}
