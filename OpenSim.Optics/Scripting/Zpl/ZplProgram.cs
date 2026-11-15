using System.Globalization;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: statements and the program structure (docs/Optics-Conventions.md §56, "Statements").

internal abstract record ZplStatement
{
    public int Line { get; init; }
}

internal sealed record ZplAssign(string Name, IReadOnlyList<ZplExpr>? Indices, ZplExpr Value) : ZplStatement;
internal sealed record ZplPrint(IReadOnlyList<ZplExpr> Items, bool NoNewLine) : ZplStatement;
internal sealed record ZplFormatStatement(ZplFormatSpec Spec) : ZplStatement;
internal sealed record ZplIf(ZplExpr Condition) : ZplStatement { public int ElseIndex { get; set; } = -1; public int EndIndex { get; set; } = -1; }
internal sealed record ZplIfThen(ZplExpr Condition, ZplStatement Then) : ZplStatement;
internal sealed record ZplElse : ZplStatement { public int EndIndex { get; set; } = -1; }
internal sealed record ZplEndIf : ZplStatement;
internal sealed record ZplFor(string Variable, ZplExpr Start, ZplExpr Stop, ZplExpr Step) : ZplStatement { public int NextIndex { get; set; } = -1; }
internal sealed record ZplNext(string? Variable) : ZplStatement { public int ForIndex { get; set; } = -1; }
internal sealed record ZplGoto(string Label) : ZplStatement;
internal sealed record ZplLabel(string Label) : ZplStatement;
internal sealed record ZplGosub(string Name) : ZplStatement;
internal sealed record ZplSub(string Name) : ZplStatement;
internal sealed record ZplReturn : ZplStatement;
internal sealed record ZplEnd : ZplStatement;
internal sealed record ZplDeclare(string Name, bool IsInteger, IReadOnlyList<ZplExpr> Dimensions) : ZplStatement;
internal sealed record ZplRelease(string Name) : ZplStatement;
internal sealed record ZplKeywordStatement(string Name, ZplArgs Args) : ZplStatement;

/// <summary>
/// A keyword's arguments as written: the text between the commas (outside strings and
/// parentheses), each parsed as an expression only when the keyword asks for one, because some
/// arguments are words (OUTPUT SCREEN, UPDATE ALL, SURP 7, GLAS, …) or bare file names
/// (CALLMACRO CHILD.ZPL, OPEN TEST.DAT).
/// </summary>
internal sealed class ZplArgs
{
    private readonly List<(string Text, int Start)> _parts;
    private readonly ZplExpr?[] _parsed;

    public ZplArgs(string text, int offset)
    {
        Raw = text.Trim();
        _parts = ZplLexer.SplitArguments(text, offset);
        _parsed = new ZplExpr?[_parts.Count];
    }

    /// <summary>The whole argument text, trimmed.</summary>
    public string Raw { get; }

    public int Count => _parts.Count;

    public string Text(int i) => _parts[i].Text.Trim();

    public bool Has(int i) => i < Count && Text(i).Length > 0;

    /// <summary>Argument <paramref name="i"/> as an expression.</summary>
    public ZplExpr Expr(int i, string what)
    {
        if (!Has(i)) throw new ZplRuntimeException($"{what} is missing (argument {i + 1}).");
        try { return _parsed[i] ??= ZplExpressionParser.ParseAll(_parts[i].Text); }
        catch (ZplRuntimeException e) { throw new ZplRuntimeException($"{what} (argument {i + 1}, '{Text(i)}'): {e.Message}"); }
    }

    /// <summary>The upper-case word when argument <paramref name="i"/> is one bare identifier
    /// (without parentheses), else null.</summary>
    public string? Word(int i)
    {
        if (!Has(i)) return null;
        string t = Text(i);
        if (t.Length == 0 || !(char.IsLetter(t[0]) || t[0] == '_')) return null;
        for (int k = 0; k < t.Length; k++)
        {
            char c = t[k];
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || (c == '$' && k == t.Length - 1))) return null;
        }
        return t.ToUpperInvariant();
    }
}

/// <summary>
/// A parsed macro: its statements in order with their lines, and the jumps of IF, ELSE, FOR,
/// NEXT, LABEL and SUB resolved before anything runs, so a missing ENDIF, NEXT, LABEL or SUB is
/// reported by line before the first statement executes.
/// </summary>
public sealed class ZplProgram
{
    internal ZplProgram(string name, IReadOnlyList<ZplStatement> statements, IReadOnlyDictionary<string, int> labels,
        IReadOnlyDictionary<string, int> subs)
    {
        Name = name;
        Statements = statements;
        Labels = labels;
        Subs = subs;
    }

    /// <summary>The macro's name (its file name), used in every message.</summary>
    public string Name { get; }

    internal IReadOnlyList<ZplStatement> Statements { get; }
    internal IReadOnlyDictionary<string, int> Labels { get; }
    internal IReadOnlyDictionary<string, int> Subs { get; }

    // OP-67
    /// <summary>The context keywords the macro was parsed with (a tolerance script's commands); null for a plain macro.</summary>
    internal ZplDialect? Dialect { get; private set; }

    /// <summary>The keywords and functions the macro names that this version does not bind
    /// (<see cref="ZplLibrary"/>), each with the first line naming it, for an editor to warn
    /// before a run. A run fails only when it reaches one.</summary>
    public IReadOnlyList<(string Name, int Line)> NotBound()
    {
        var found = new List<(string, int)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string n, int line) { if (seen.Add(n)) found.Add((n, line)); }
        void Walk(ZplStatement s)
        {
            switch (s)
            {
                case ZplKeywordStatement k when !ZplLibrary.IsKeywordBound(k.Name) && Dialect?.Binds(k.Name) != true: Add(k.Name, k.Line); break;   // OP-67: context keywords are bound
                case ZplIfThen t: Walk(t.Then); break;
            }
            foreach (var e in Expressions(s)) Calls(e, s.Line);
        }
        void Calls(ZplExpr e, int line)
        {
            switch (e)
            {
                case ZplCall c:
                    if (ZplLibrary.IsKnownFunction(c.Name) && !ZplLibrary.IsFunctionBound(c.Name)) Add(c.Name, line);
                    foreach (var a in c.Arguments) Calls(a, line);
                    break;
                case ZplUnary u: Calls(u.Operand, line); break;
                case ZplBinary b: Calls(b.Left, line); Calls(b.Right, line); break;
            }
        }
        foreach (var s in Statements) Walk(s);
        return found;
    }

    private static IEnumerable<ZplExpr> Expressions(ZplStatement s) => s switch
    {
        ZplAssign a => (a.Indices ?? Array.Empty<ZplExpr>()).Append(a.Value),
        ZplPrint p => p.Items,
        ZplIf i => new[] { i.Condition },
        ZplIfThen t => new[] { t.Condition },
        ZplFor f => new[] { f.Start, f.Stop, f.Step },
        ZplDeclare d => d.Dimensions,
        _ => Array.Empty<ZplExpr>(),
    };

    /// <summary>The limits the help sets: "a limit of 300 label commands in a macro" (LABEL) and
    /// "no more than 100 defined subroutines per ZPL macro file" (GOSUB, SUB, RETURN, and END).</summary>
    internal const int MaxLabels = 300, MaxSubs = 100;

    /// <summary>Parses macro text; throws <see cref="ZplException"/> naming the line of the first
    /// error.</summary>
    public static ZplProgram Parse(string text, string name = "macro") => Parse(text, name, null);

    // OP-67
    /// <summary>Parses macro text with the keywords of a context added (<see cref="ZplDialect"/>).</summary>
    internal static ZplProgram Parse(string text, string name, ZplDialect? dialect)
    {
        ArgumentNullException.ThrowIfNull(text);
        var statements = new List<ZplStatement>();
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = ZplLexer.StripComment(lines[n]);
            if (line.Trim().Length == 0) continue;
            try { statements.Add(ParseStatement(line, n + 1, allowBlock: true, dialect)); }
            catch (ZplRuntimeException e) { throw new ZplException(name, n + 1, e.Message, e); }
        }
        var program = Resolve(name, statements);
        program.Dialect = dialect;   // OP-67
        return program;
    }

    private static ZplProgram Resolve(string name, List<ZplStatement> st)
    {
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var blocks = new Stack<int>();
        for (int i = 0; i < st.Count; i++)
        {
            var s = st[i];
            switch (s)
            {
                case ZplIf: blocks.Push(i); break;
                case ZplElse e:
                    if (blocks.Count == 0 || st[blocks.Peek()] is not ZplIf open)
                        throw new ZplException(name, s.Line, "ELSE without an IF block open (an IF ... THEN line takes no ELSE).");
                    if (open.ElseIndex >= 0) throw new ZplException(name, s.Line, $"a second ELSE for the IF of line {open.Line}.");
                    open.ElseIndex = i;
                    break;
                case ZplEndIf:
                    if (blocks.Count == 0 || st[blocks.Peek()] is not ZplIf openIf)
                        throw new ZplException(name, s.Line, blocks.Count == 0 ? "ENDIF without an IF." : $"ENDIF inside the FOR loop of line {st[blocks.Peek()].Line}, which has no NEXT yet.");
                    blocks.Pop();
                    openIf.EndIndex = i;
                    if (openIf.ElseIndex >= 0) ((ZplElse)st[openIf.ElseIndex]).EndIndex = i;
                    break;
                case ZplFor: blocks.Push(i); break;
                case ZplNext next:
                    if (blocks.Count == 0 || st[blocks.Peek()] is not ZplFor f)
                        throw new ZplException(name, s.Line, blocks.Count == 0 ? "NEXT without a FOR." : $"NEXT inside the IF block of line {st[blocks.Peek()].Line}, which has no ENDIF yet.");
                    if (next.Variable is { } v && !string.Equals(v, f.Variable, StringComparison.OrdinalIgnoreCase))
                        throw new ZplException(name, s.Line, $"NEXT {v} closes the FOR {f.Variable} of line {f.Line}.");
                    next.ForIndex = blocks.Pop();
                    f.NextIndex = i;
                    break;
                case ZplLabel l:
                    if (!labels.TryAdd(l.Label, i)) throw new ZplException(name, s.Line, $"LABEL {l.Label} is defined twice (first at line {st[labels[l.Label]].Line}).");
                    if (labels.Count > MaxLabels) throw new ZplException(name, s.Line, $"more than {MaxLabels} labels (help, LABEL).");
                    break;
                case ZplSub sub:
                    if (!subs.TryAdd(sub.Name, i)) throw new ZplException(name, s.Line, $"SUB {sub.Name} is defined twice (first at line {st[subs[sub.Name]].Line}).");
                    if (subs.Count > MaxSubs) throw new ZplException(name, s.Line, $"more than {MaxSubs} subroutines (help, GOSUB).");
                    break;
            }
        }
        if (blocks.Count > 0)
        {
            var open = st[blocks.Peek()];
            throw new ZplException(name, open.Line, open is ZplFor f ? $"FOR {f.Variable} has no NEXT." : "IF has no ENDIF.");
        }
        foreach (var s in st) CheckTargets(name, s, labels, subs);
        return new ZplProgram(name, st, labels, subs);
    }

    private static void CheckTargets(string name, ZplStatement s, Dictionary<string, int> labels, Dictionary<string, int> subs)
    {
        switch (s)
        {
            case ZplGoto g when !labels.ContainsKey(g.Label):
                throw new ZplException(name, s.Line, $"GOTO {g.Label}: there is no LABEL {g.Label} in the macro.");
            case ZplGosub g when !subs.ContainsKey(g.Name):
                throw new ZplException(name, s.Line, $"GOSUB {g.Name}: there is no SUB {g.Name} in the macro.");
            case ZplIfThen t:
                CheckTargets(name, t.Then, labels, subs);
                break;
        }
    }

    private static readonly HashSet<string> BlockWords = new(StringComparer.Ordinal)
    {
        "IF", "ELSE", "ENDIF", "FOR", "NEXT", "LABEL", "SUB", "DECLARE",
    };

    internal static ZplStatement ParseStatement(string line, int number, bool allowBlock, ZplDialect? dialect = null)
    {
        // OP-67: a context keyword takes the rest of the line as its arguments.
        if (dialect?.Match(line) is { } d) return new ZplKeywordStatement(d.Name, new ZplArgs(d.Tail, d.Offset)) { Line = number };

        var tokens = ZplLexer.Tokenize(line);
        var first = tokens[0];
        if (first.Kind != ZplTokenKind.Identifier)
            throw new ZplRuntimeException($"a line starts with an assignment, a keyword or a remark, not '{first.Text}'.");
        string word = first.Text.ToUpperInvariant();
        string rest = line[first.End..];

        // Assignment: name = value, or name(indices) = value.
        if (tokens[1].Is("=")) return Assignment(word, null, tokens, 2, number);
        if (tokens[1].Is("(") && !ZplLibrary.IsKeyword(word))
        {
            var p = new ZplExpressionParser(tokens, 1);
            p.Expect("(");
            var indices = new List<ZplExpr>();
            while (true)
            {
                indices.Add(p.Parse());
                if (p.Peek.Is(",")) { p.Next(); continue; }
                break;
            }
            p.Expect(")");
            if (p.Peek.Is("=")) return Assignment(word, indices, tokens, p.Position + 1, number);
            throw new ZplRuntimeException($"{word}(…) at the start of a line must be followed by '=' (an array element assignment); {word} is not a keyword.");
        }

        if (!allowBlock && BlockWords.Contains(word))
            throw new ZplRuntimeException($"{word} cannot follow THEN; an IF ... THEN line takes one simple command.");

        switch (word)
        {
            case "IF":
            {
                var p = new ZplExpressionParser(tokens, 1);
                if (p.AtEnd) throw new ZplRuntimeException("IF needs a condition.");
                var condition = p.Parse();
                if (p.AtEnd) return allowBlock ? new ZplIf(condition) { Line = number }
                    : throw new ZplRuntimeException("an IF block cannot follow THEN.");
                if (!p.Peek.IsWord("THEN")) throw new ZplRuntimeException($"THEN or the end of the line expected after the IF condition{p.Found()}.");
                var thenToken = p.Next();
                string after = line[thenToken.End..];
                if (after.Trim().Length == 0) throw new ZplRuntimeException("THEN needs a command after it.");
                return new ZplIfThen(condition, ParseStatement(after, number, allowBlock: false, dialect)) { Line = number };
            }
            case "ELSE":
                NoArguments(tokens, "ELSE", "put the commands of the ELSE branch on the following lines");
                return new ZplElse { Line = number };
            case "ENDIF":
                NoArguments(tokens, "ENDIF", null);
                return new ZplEndIf { Line = number };
            case "FOR":
            {
                // FOR variable, start_value, stop_value, increment (help, FOR, NEXT); the help's
                // own examples also write "FOR i = 1, n, 1" (Optimizing with ZPL Macros,
                // SUSPENDUPDATES), so '=' is taken after the variable as well.
                if (tokens[1].Kind != ZplTokenKind.Identifier || tokens[1].Text.EndsWith('$'))
                    throw new ZplRuntimeException("FOR needs a numeric variable name.");
                string v = tokens[1].Text.ToUpperInvariant();
                CheckName(v);
                if (!(tokens[2].Is(",") || tokens[2].Is("="))) throw new ZplRuntimeException($"FOR {v}: a comma expected after the variable.");
                var p = new ZplExpressionParser(tokens, 3);
                var start = p.Parse(); p.Expect(",");
                var stop = p.Parse();
                if (!p.Peek.Is(",")) throw new ZplRuntimeException($"FOR {v}: the increment is missing (FOR variable, start_value, stop_value, increment).");
                p.Next();
                var step = p.Parse();
                if (!p.AtEnd) throw new ZplRuntimeException($"FOR {v}: unexpected '{p.Peek.Text}' after the increment.");
                return new ZplFor(v, start, stop, step) { Line = number };
            }
            case "NEXT":
                if (tokens[1].Kind == ZplTokenKind.End) return new ZplNext(null) { Line = number };
                if (tokens[1].Kind == ZplTokenKind.Identifier && tokens[2].Kind == ZplTokenKind.End)
                    return new ZplNext(tokens[1].Text.ToUpperInvariant()) { Line = number };
                throw new ZplRuntimeException("NEXT takes at most the loop variable's name.");
            case "GOTO":
                return new ZplGoto(Label(rest, "GOTO")) { Line = number };
            case "LABEL":
                return new ZplLabel(Label(rest, "LABEL")) { Line = number };
            case "GOSUB":
                return new ZplGosub(Label(rest, "GOSUB")) { Line = number };
            case "SUB":
                return new ZplSub(Label(rest, "SUB")) { Line = number };
            case "RETURN":
                NoArguments(tokens, "RETURN", null);
                return new ZplReturn { Line = number };
            case "END":
                NoArguments(tokens, "END", null);
                return new ZplEnd { Line = number };
            case "PRINT":
                return ParsePrint(tokens, number);
            case "FORMAT":
                return new ZplFormatStatement(ZplFormatSpec.Parse(rest)) { Line = number };
            case "DECLARE":
                return ParseDeclare(rest, first.End, number);
            case "RELEASE":
            {
                var a = new ZplArgs(rest, first.End);
                if (a.Count != 1 || a.Word(0) is not { } arrayName) throw new ZplRuntimeException("RELEASE takes the name of an array declared with DECLARE.");
                return new ZplRelease(arrayName) { Line = number };
            }
        }
        if (!ZplLibrary.IsKeyword(word))
        {
            if (ZplLibrary.IsKnownFunction(word))
                throw new ZplRuntimeException($"{word} is a function, not a keyword: use it in an expression ({word}(...)), or assign it to a variable.");
            throw new ZplRuntimeException($"'{first.Text}' is not a ZPL keyword, and an assignment needs '=' after the variable name.");
        }
        return new ZplKeywordStatement(ZplLibrary.Canonical(word), new ZplArgs(rest, first.End)) { Line = number };
    }

    private static ZplStatement Assignment(string name, IReadOnlyList<ZplExpr>? indices, List<ZplToken> tokens, int start, int number)
    {
        CheckName(name);
        var p = new ZplExpressionParser(tokens, start);
        if (p.AtEnd) throw new ZplRuntimeException($"{name} = needs a value.");
        var value = p.Parse();
        if (!p.AtEnd) throw new ZplRuntimeException($"unexpected '{p.Peek.Text}' after the value assigned to {name} (one command per line).");
        return new ZplAssign(name, indices, value) { Line = number };
    }

    /// <summary>"A variable cannot take on the same name as a keyword or function" and "Each
    /// variable name is limited to 28 characters" (Numeric Variables).</summary>
    internal static void CheckName(string name)
    {
        string bare = name.TrimEnd('$');
        if (name.StartsWith('$')) throw new ZplRuntimeException($"{name} is a string function and cannot be assigned.");
        if (ZplLibrary.IsKeyword(bare) || ZplLibrary.IsKnownFunction(bare) || ZplLibrary.IsKeyword(name))
            throw new ZplRuntimeException($"{name} is the name of a ZPL keyword or function and cannot be a variable (help, Numeric Variables).");
        if (name.Length > 28) throw new ZplRuntimeException($"the variable name {name} is longer than 28 characters (help, Numeric Variables).");
    }

    private static void NoArguments(List<ZplToken> tokens, string word, string? hint)
    {
        if (tokens[1].Kind != ZplTokenKind.End)
            throw new ZplRuntimeException($"{word} takes nothing after it{(hint is null ? "" : "; " + hint)}.");
    }

    private static string Label(string rest, string word)
    {
        string t = rest.Trim();
        if (t.Length == 0) throw new ZplRuntimeException($"{word} needs a label (a number greater than zero or a word).");
        foreach (char c in t)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.'))
                throw new ZplRuntimeException($"{word} {t}: a label must not contain spaces or delimiters (help, LABEL).");
        if (char.IsAsciiDigit(t[0]))
        {
            if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n <= 0)
                throw new ZplRuntimeException($"{word} {t}: a numeric label must be an integer greater than zero (help, LABEL).");
            return n.ToString(CultureInfo.InvariantCulture);
        }
        return t.ToUpperInvariant();
    }

    private static ZplPrint ParsePrint(List<ZplToken> tokens, int number)
    {
        // PRINT [item {, item}] [,]: a trailing comma suppresses the end of the line (help,
        // PRINT). Items written side by side without a comma (as in the help's own
        // PRINT "Surface " $STR(n), Running Macros from the Command Line) are read as two items.
        var items = new List<ZplExpr>();
        var p = new ZplExpressionParser(tokens, 1);
        bool trailing = false;
        while (!p.AtEnd)
        {
            items.Add(p.Parse());
            trailing = false;
            if (p.Peek.Is(",")) { p.Next(); trailing = true; }
        }
        return new ZplPrint(items, trailing) { Line = number };
    }

    private static ZplDeclare ParseDeclare(string rest, int offset, int number)
    {
        // DECLARE name, type, num_dimensions, dimension1 [, dimension 2 ...] (Array Variables).
        var a = new ZplArgs(rest, offset);
        if (a.Count < 4 || a.Word(0) is not { } name || name.EndsWith('$'))
            throw new ZplRuntimeException("DECLARE name, type, num_dimensions, dimension1 [, dimension2 ...]: the name, type, number of dimensions and sizes are needed.");
        CheckName(name);
        bool isInteger = a.Word(1) switch
        {
            "DOUBLE" => false,
            "INTEGER" => true,
            _ => throw new ZplRuntimeException($"DECLARE {name}: the type must be DOUBLE or INTEGER, not '{a.Text(1)}'."),
        };
        string countText = a.Text(2);
        if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1 || count > 4)
            throw new ZplRuntimeException($"DECLARE {name}: the number of dimensions must be an integer from 1 to 4, not '{countText}'.");
        if (a.Count != 3 + count)
            throw new ZplRuntimeException($"DECLARE {name}: {count} dimension{(count == 1 ? "" : "s")} declared but {a.Count - 3} size{(a.Count - 3 == 1 ? "" : "s")} given.");
        var dims = Enumerable.Range(3, count).Select(i => a.Expr(i, $"DECLARE {name} size {i - 2}")).ToArray();
        return new ZplDeclare(name, isInteger, dims) { Line = number };
    }
}
