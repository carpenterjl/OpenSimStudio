using System.Globalization;
using System.Text;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: the ZPL macro language (docs/Optics-Conventions.md §56). The language is OpticStudio's,
// read from its 2025 R2 help, "About the ZPL" chapter (saved under
// plans/sources/optics/op-99-help/): Numeric Variables, Array Variables, Numeric Operations,
// Numeric Logical Operators, String Variables, String Operations, String Logical Operators, REM.

/// <summary>
/// A macro failed: the message names the macro, the line and what is wrong. Every failure of the
/// interpreter (a syntax error, a keyword or function that is not bound, a value out of range, a
/// keyword that cannot do what it was asked) is one of these; nothing is skipped silently.
/// </summary>
public class ZplException : Exception
{
    public ZplException(string macro, int line, string message, Exception? inner = null)
        : base($"{macro}, line {line}: {message}", inner)
    {
        Macro = macro;
        Line = line;
        Problem = message;
    }

    /// <summary>The macro's name.</summary>
    public string Macro { get; }

    /// <summary>The 1-based line of the macro file.</summary>
    public int Line { get; }

    /// <summary>The message without the macro and line.</summary>
    public string Problem { get; }
}

/// <summary>A keyword or function of OpticStudio's ZPL that this version does not bind.</summary>
public sealed class ZplNotBoundException : ZplException
{
    public ZplNotBoundException(string macro, int line, string name, string message)
        : base(macro, line, message) => Name = name;

    /// <summary>The keyword or function name, upper case.</summary>
    public string Name { get; }
}

/// <summary>A failure inside a keyword or function, before the interpreter adds the macro and
/// line (<see cref="ZplException"/>).</summary>
public class ZplRuntimeException : Exception
{
    public ZplRuntimeException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A ZPL value: a 64-bit number ("All ZPL numeric variables are stored internally as
/// 64-bit double precision numbers", Numeric Variables) or a string.</summary>
public readonly struct ZplValue
{
    private ZplValue(double number, string? text) { Number = number; Text = text; }

    public double Number { get; }

    /// <summary>The string, or null for a number.</summary>
    public string? Text { get; }

    public bool IsString => Text is not null;

    public static ZplValue Of(double number) => new(number, null);

    public static ZplValue Of(string text) => new(0, text ?? "");

    public override string ToString() => Text ?? Number.ToString("R", CultureInfo.InvariantCulture);
}

internal enum ZplTokenKind { Number, String, Identifier, Operator, End }

/// <summary>One token: <see cref="Text"/> is the identifier (as written), the operator, or the
/// string literal's content; <see cref="Start"/> the offset in the line.</summary>
internal readonly record struct ZplToken(ZplTokenKind Kind, string Text, double Number, int Start, int End)
{
    public bool Is(string op) => Kind == ZplTokenKind.Operator && Text == op;

    public bool IsWord(string word) => Kind == ZplTokenKind.Identifier && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Splits one macro line into tokens. Identifiers start with a letter or underscore and continue
/// with letters, digits, underscores and periods; a trailing <c>$</c> makes a string variable
/// ("string variables are distinguished from numeric variables by the presence of the $ character
/// at the end", String Variables), a leading <c>$</c> a string function ($LENSNAME). The help
/// lists the characters a name must not contain ("(, ), =, +, -, *, /, !, >, <, ^, &amp;, |, #, ",
/// and the space character", Numeric Variables); the period is not among them, so file names
/// such as CHILD.ZPL read as one word. Operators: + − * / ( ) , and the numeric logical operators
/// &amp; | ^ ! == != &lt; &gt; &lt;= &gt;= and their string forms $== $!= $&lt; $&gt; $&lt;= $&gt;=. A single
/// <c>=</c> is assignment.
/// </summary>
internal static class ZplLexer
{
    public static List<ZplToken> Tokenize(string text, int offset = 0)
    {
        var tokens = new List<ZplToken>();
        int i = 0;
        while (true)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            char c = text[i];
            int start = i;
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                if (i < text.Length && text[i] == '.') { i++; while (i < text.Length && char.IsAsciiDigit(text[i])) i++; }
                if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
                {
                    int j = i + 1;
                    if (j < text.Length && (text[j] == '+' || text[j] == '-')) j++;
                    if (j < text.Length && char.IsAsciiDigit(text[j]))
                    {
                        i = j;
                        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                    }
                }
                string s = text[start..i];
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new ZplRuntimeException($"'{s}' is not a number.");
                tokens.Add(new ZplToken(ZplTokenKind.Number, s, v, start + offset, i + offset));
                continue;
            }
            if (c == '"')
            {
                int close = text.IndexOf('"', i + 1);
                if (close < 0) throw new ZplRuntimeException("a string is not closed: a double quote is missing.");
                tokens.Add(new ZplToken(ZplTokenKind.String, text[(i + 1)..close], 0, start + offset, close + 1 + offset));
                i = close + 1;
                continue;
            }
            if (char.IsLetter(c) || c == '_' || (c == '$' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_')))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.')) i++;
                if (c != '$' && i < text.Length && text[i] == '$') i++;
                tokens.Add(new ZplToken(ZplTokenKind.Identifier, text[start..i], 0, start + offset, i + offset));
                continue;
            }
            string? op = null;
            string two = i + 1 < text.Length ? text.Substring(i, 2) : "";
            string three = i + 2 < text.Length ? text.Substring(i, 3) : "";
            if (c == '$')
            {
                if (three is "$==" or "$!=" or "$<=" or "$>=") op = three;
                else if (two is "$<" or "$>") op = two;
            }
            else if (two is "==" or "!=" or "<=" or ">=") op = two;
            else if ("+-*/(),=<>&|^!".IndexOf(c) >= 0) op = c.ToString();
            if (op is null)
                throw new ZplRuntimeException($"the character '{c}' is not part of the ZPL language here.");
            tokens.Add(new ZplToken(ZplTokenKind.Operator, op, 0, start + offset, start + op.Length + offset));
            i += op.Length;
        }
        tokens.Add(new ZplToken(ZplTokenKind.End, "", 0, text.Length + offset, text.Length + offset));
        return tokens;
    }

    /// <summary>
    /// The line without its comment: "REM" or "!" at the very beginning of the line make the whole
    /// line a remark, and "#" anywhere outside a string starts one (help, "REM, !, #").
    /// </summary>
    public static string StripComment(string line)
    {
        string t = line.TrimStart();
        if (t.StartsWith('!')) return "";
        if (t.Length >= 3 && string.Equals(t[..3], "REM", StringComparison.OrdinalIgnoreCase) && (t.Length == 3 || char.IsWhiteSpace(t[3])))
            return "";
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inString = !inString;
            else if (line[i] == '#' && !inString) return line[..i];
        }
        return line;
    }

    /// <summary>Splits keyword arguments at the commas outside strings and parentheses.</summary>
    public static List<(string Text, int Start)> SplitArguments(string text, int offset)
    {
        var parts = new List<(string, int)>();
        if (text.Trim().Length == 0) return parts;
        int depth = 0, start = 0;
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') inString = !inString;
            else if (inString) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add((text[start..i], start + offset));
                start = i + 1;
            }
        }
        parts.Add((text[start..], start + offset));
        return parts;
    }

    public static string Upper(string s) => s.ToUpperInvariant();

    internal static string Describe(IEnumerable<ZplToken> tokens)
    {
        var sb = new StringBuilder();
        foreach (var t in tokens) { if (t.Kind == ZplTokenKind.End) break; sb.Append(t.Text).Append(' '); }
        return sb.ToString().Trim();
    }
}
