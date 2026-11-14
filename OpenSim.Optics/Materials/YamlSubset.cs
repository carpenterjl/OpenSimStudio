using System.Globalization;
using System.Text;

namespace OpenSim.Optics.Materials;

/// <summary>A node of a parsed YAML document: a scalar, a mapping or a sequence.</summary>
internal abstract record YamlNode(int Line);

/// <summary>A scalar as text (plain, quoted or block); YAML's typing of it is not applied.</summary>
internal sealed record YamlScalar(int Line, string Value) : YamlNode(Line);

/// <summary>A block mapping, keys in file order.</summary>
internal sealed record YamlMapping(int Line, IReadOnlyList<KeyValuePair<string, YamlNode>> Entries) : YamlNode(Line)
{
    /// <summary>The value of <paramref name="key"/> (exact case), or null.</summary>
    public YamlNode? this[string key] => Entries.FirstOrDefault(e => e.Key == key).Value;
}

/// <summary>A block sequence.</summary>
internal sealed record YamlSequence(int Line, IReadOnlyList<YamlNode> Items) : YamlNode(Line);

/// <summary>
/// A reader for the part of YAML the refractiveindex.info database files use, written for
/// OP-12 instead of taking a package. What it reads: block mappings and block sequences by
/// indentation (spaces only), the compact "- key: value" sequence item, plain scalars with an
/// optional trailing " # comment", single- and double-quoted scalars on one line, literal
/// ("|", "|-", "|+") and folded ("&gt;") block scalars, full-line comments, and a leading
/// "---". What it refuses, naming the line: flow collections ([…], {…}), anchors, aliases and
/// tags, quoted scalars that continue onto another line, a tab in indentation, duplicate keys,
/// and anything else it cannot place. Scalars stay text; the caller converts numbers.
/// <para>
/// Every construct in the shipped files is covered by this list; the database's own schema
/// test (tests/test_yaml.py in the database repository) describes the same top-level keys.
/// </para>
/// </summary>
internal sealed class YamlSubset
{
    private readonly string[] _lines;
    private readonly string _source;
    private int _pos;

    private YamlSubset(string text, string source)
    {
        _lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        _source = source;
    }

    /// <summary>Parses <paramref name="text"/>; <paramref name="source"/> names it in errors.
    /// Throws <see cref="InvalidDataException"/> naming the line for anything outside the subset.</summary>
    public static YamlNode? Parse(string text, string source)
    {
        var parser = new YamlSubset(text, source);
        parser.SkipIgnorable();
        if (parser._pos < parser._lines.Length && parser._lines[parser._pos].TrimEnd() == "---")
        {
            parser._pos++;
            parser.SkipIgnorable();
        }
        if (parser._pos >= parser._lines.Length) return null;
        int indent = parser.Indent(parser._pos);
        if (indent != 0) throw parser.Error(parser._pos, "the document does not start at column 1.");
        var root = parser.ParseNode(indent);
        parser.SkipIgnorable();
        if (parser._pos < parser._lines.Length && parser._lines[parser._pos].TrimEnd() != "...")
            throw parser.Error(parser._pos, "this line is not part of the document's structure (check its indentation).");
        return root;
    }

    private InvalidDataException Error(int lineIndex, string what) =>
        new($"{_source} line {lineIndex + 1}: {what}");

    /// <summary>Moves past blank lines and full-line comments.</summary>
    private void SkipIgnorable()
    {
        while (_pos < _lines.Length)
        {
            string t = _lines[_pos].Trim();
            if (t.Length == 0 || t.StartsWith('#')) _pos++;
            else break;
        }
    }

    private int Indent(int lineIndex)
    {
        string line = _lines[lineIndex];
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        if (i < line.Length && line[i] == '\t') throw Error(lineIndex, "a tab in the indentation; YAML indents with spaces only.");
        return i;
    }

    private static bool IsSequenceItem(string content) => content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

    /// <summary>The block (mapping or sequence) whose first line is at <see cref="_pos"/>, indented by <paramref name="indent"/>.</summary>
    private YamlNode ParseNode(int indent) =>
        IsSequenceItem(_lines[_pos][indent..].TrimEnd()) ? ParseSequence(indent) : ParseMapping(indent);

    private YamlSequence ParseSequence(int indent)
    {
        int first = _pos;
        var items = new List<YamlNode>();
        while (true)
        {
            SkipIgnorable();
            if (_pos >= _lines.Length) break;
            int ind = Indent(_pos);
            if (ind < indent) break;
            if (ind > indent) throw Error(_pos, "unexpected indentation inside a sequence.");
            string content = _lines[_pos][ind..].TrimEnd();
            if (!IsSequenceItem(content)) break;
            int line = _pos;
            string rest = content.Length > 1 ? content[2..].TrimStart() : "";
            if (rest.Length == 0 || rest.StartsWith('#'))
            {
                _pos++;
                SkipIgnorable();
                if (_pos < _lines.Length && Indent(_pos) > indent) items.Add(ParseNode(Indent(_pos)));
                else items.Add(new YamlScalar(line, ""));
            }
            else if (SplitKey(rest) is not null)
            {
                // "- key: value": a mapping whose column is where the key starts. The dash is
                // blanked so the mapping parser sees an ordinary indented line.
                int column = _lines[_pos].Length - _lines[_pos].TrimStart().Length + (content.Length - rest.Length);
                _lines[_pos] = new string(' ', column) + rest;
                items.Add(ParseMapping(column));
            }
            else
            {
                items.Add(new YamlScalar(line, ParseInlineScalar(rest, line)));
                _pos++;
            }
        }
        return new YamlSequence(first, items);
    }

    private YamlMapping ParseMapping(int indent)
    {
        int first = _pos;
        var entries = new List<KeyValuePair<string, YamlNode>>();
        while (true)
        {
            SkipIgnorable();
            if (_pos >= _lines.Length) break;
            int ind = Indent(_pos);
            if (ind < indent) break;
            if (ind > indent) throw Error(_pos, "unexpected indentation inside a mapping.");
            string content = _lines[_pos][ind..].TrimEnd();
            if (IsSequenceItem(content)) break;
            int line = _pos;
            if (SplitKey(content) is not (string key, string rest))
                throw Error(_pos, $"expected 'key: value', found '{content}'.");
            if (entries.Any(e => e.Key == key)) throw Error(_pos, $"the key '{key}' appears twice in one mapping.");
            _pos++;
            YamlNode value;
            if (rest.Length == 0 || rest.StartsWith('#'))
            {
                SkipIgnorable();
                if (_pos < _lines.Length && Indent(_pos) > indent) value = ParseNode(Indent(_pos));
                else if (_pos < _lines.Length && Indent(_pos) == indent && IsSequenceItem(_lines[_pos][indent..].TrimEnd()))
                    value = ParseSequence(indent);                  // a sequence may sit at its key's indentation
                else value = new YamlScalar(line, "");
            }
            else if (rest[0] is '|' or '>')
            {
                value = new YamlScalar(line, ParseBlockScalar(rest, indent, line));
            }
            else
            {
                value = new YamlScalar(line, ParseInlineScalar(rest, line));
            }
            entries.Add(new KeyValuePair<string, YamlNode>(key, value));
        }
        return new YamlMapping(first, entries);
    }

    /// <summary>"key: rest" or "key:" split at the first ": " (or a final ':'); null when the
    /// text is not a key line. Keys are plain or quoted.</summary>
    private static (string Key, string Value)? SplitKey(string content)
    {
        if (content.Length == 0) return null;
        if (content[0] is '"' or '\'')
        {
            char q = content[0];
            int close = content.IndexOf(q, 1);
            if (close < 0 || close + 1 >= content.Length || content[close + 1] != ':') return null;
            string after = content[(close + 2)..];
            if (after.Length > 0 && after[0] != ' ') return null;
            return (content[1..close], after.Trim());
        }
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '#' && i > 0 && content[i - 1] == ' ') return null;
            if (content[i] == ':' && (i + 1 == content.Length || content[i + 1] == ' '))
            {
                string key = content[..i].TrimEnd();
                return key.Length == 0 ? null : (key, content[(i + 1)..].Trim());
            }
        }
        return null;
    }

    private string ParseInlineScalar(string text, int line)
    {
        if (text[0] is '[' or '{') throw Error(line, "flow collections ([…], {…}) are not read.");
        if (text[0] is '&' or '*' or '!') throw Error(line, "anchors, aliases and tags are not read.");
        if (text[0] == '"') return DoubleQuoted(text, line);
        if (text[0] == '\'') return SingleQuoted(text, line);
        int comment = text.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? text[..comment] : text).Trim();
    }

    private string SingleQuoted(string text, int line)
    {
        var sb = new StringBuilder();
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] != '\'') { sb.Append(text[i]); continue; }
            if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i++; continue; }
            CheckAfterQuote(text, i, line);
            return sb.ToString();
        }
        throw Error(line, "a single-quoted scalar is not closed on its line.");
    }

    private string DoubleQuoted(string text, int line)
    {
        var sb = new StringBuilder();
        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                CheckAfterQuote(text, i, line);
                return sb.ToString();
            }
            if (c != '\\') { sb.Append(c); continue; }
            if (++i >= text.Length) break;
            switch (text[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'u' when i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: throw Error(line, $"the escape '\\{text[i]}' is not read.");
            }
        }
        throw Error(line, "a double-quoted scalar is not closed on its line.");
    }

    private void CheckAfterQuote(string text, int close, int line)
    {
        string after = text[(close + 1)..].Trim();
        if (after.Length > 0 && !after.StartsWith('#')) throw Error(line, $"text after a closing quote: '{after}'.");
    }

    /// <summary>A literal or folded block scalar: the lines below, indented more than the key.</summary>
    private string ParseBlockScalar(string header, int parentIndent, int line)
    {
        string h = header;
        int hash = h.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0) h = h[..hash];
        h = h.Trim();
        if (h is not ("|" or "|-" or "|+" or ">" or ">-" or ">+"))
            throw Error(line, $"the block scalar header '{h}' is not read (indentation indicators are not supported).");
        bool folded = h[0] == '>';
        char chomp = h.Length > 1 ? h[1] : ' ';

        var raw = new List<string>();
        int contentIndent = -1;
        while (_pos < _lines.Length)
        {
            string l = _lines[_pos];
            if (l.Trim().Length == 0) { raw.Add(""); _pos++; continue; }
            int ind = Indent(_pos);
            if (ind <= parentIndent) break;
            if (contentIndent < 0) contentIndent = ind;
            if (ind < contentIndent) throw Error(_pos, "a block scalar line is indented less than its first line.");
            raw.Add(l[contentIndent..].TrimEnd());
            _pos++;
        }
        // Trailing blank lines belong to no one; YAML's chomping decides how many newlines stay.
        int trailing = 0;
        while (raw.Count > 0 && raw[^1].Length == 0) { raw.RemoveAt(raw.Count - 1); trailing++; }
        // Leave the trailing blank lines for the next construct (they are ignorable anyway).
        _pos -= trailing;

        string body;
        if (!folded) body = string.Join("\n", raw);
        else
        {
            var sb = new StringBuilder();
            // Folding: adjacent lines join with a space; each blank line is one newline.
            for (int i = 0; i < raw.Count; i++)
            {
                if (raw[i].Length == 0) { sb.Append('\n'); continue; }
                if (i > 0 && raw[i - 1].Length > 0) sb.Append(' ');
                sb.Append(raw[i]);
            }
            body = sb.ToString();
        }
        return chomp switch
        {
            '-' => body,
            '+' => body + new string('\n', trailing + (raw.Count > 0 ? 1 : 0)),
            _ => raw.Count > 0 ? body + "\n" : ""
        };
    }
}
