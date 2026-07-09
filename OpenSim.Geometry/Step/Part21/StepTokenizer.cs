using System.Globalization;
using System.Text;

namespace OpenSim.Geometry.Step.Part21;

/// <summary>
/// Character-level tokenizer for ISO 10303-21 exchange structures. Single forward pass,
/// line-tracked so every error is addressable in a text editor. String literals decode
/// the Part 21 escape directives (<c>''</c>, <c>\\</c>, <c>\S\</c>, <c>\X\</c>,
/// <c>\X2\…\X0\</c>, <c>\X4\…\X0\</c>); an unknown directive fails loudly rather than
/// passing mojibake downstream. Raw newlines inside strings are tolerated (some real
/// exporters hard-wrap long strings) — spec-strictness there would reject valid CAD data.
/// </summary>
internal sealed class StepTokenizer
{
    private readonly string _s;
    private int _i;
    private int _line = 1;

    public StepTokenizer(string source) => _s = source;

    public StepToken Next()
    {
        SkipWhitespaceAndComments();
        int line = _line;
        if (_i >= _s.Length) return new StepToken(StepTokenKind.EndOfFile, "", 0, 0, line);

        char c = _s[_i];
        switch (c)
        {
            case '(': _i++; return Punct(StepTokenKind.LParen, line);
            case ')': _i++; return Punct(StepTokenKind.RParen, line);
            case ',': _i++; return Punct(StepTokenKind.Comma, line);
            case '=': _i++; return Punct(StepTokenKind.Equals, line);
            case ';': _i++; return Punct(StepTokenKind.Semicolon, line);
            case '$': _i++; return Punct(StepTokenKind.Dollar, line);
            case '*': _i++; return Punct(StepTokenKind.Star, line);
            case '#': return ReadHash(line);
            case '\'': return ReadString(line);
            case '.': return ReadEnumeration(line);
        }

        if (c is '+' or '-' || char.IsAsciiDigit(c)) return ReadNumber(line);
        if (char.IsAsciiLetter(c) || c == '_') return ReadKeyword(line);

        throw new StepParseException(line, $"unexpected character '{c}'");
    }

    private static StepToken Punct(StepTokenKind kind, int line) => new(kind, "", 0, 0, line);

    private void SkipWhitespaceAndComments()
    {
        while (_i < _s.Length)
        {
            char c = _s[_i];
            if (c == '\n') { _line++; _i++; }
            else if (c is ' ' or '\t' or '\r' or '\f') _i++;
            else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
            {
                int start = _line;
                _i += 2;
                while (true)
                {
                    if (_i >= _s.Length)
                        throw new StepParseException(start, "unterminated /* comment");
                    if (_s[_i] == '\n') _line++;
                    if (_s[_i] == '*' && _i + 1 < _s.Length && _s[_i + 1] == '/') { _i += 2; break; }
                    _i++;
                }
            }
            else break;
        }
    }

    private StepToken ReadHash(int line)
    {
        _i++; // '#'
        int start = _i;
        while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
        if (_i == start) throw new StepParseException(line, "'#' must be followed by an instance number");
        long id = long.Parse(_s.AsSpan(start, _i - start), CultureInfo.InvariantCulture);
        return new StepToken(StepTokenKind.Hash, "", id, 0, line);
    }

    private StepToken ReadKeyword(int line)
    {
        int start = _i;
        // '-' is admitted so the section markers ISO-10303-21 / END-ISO-10303-21 lex as
        // single keywords; Part 21 has no arithmetic, so a hyphen is unambiguous here.
        while (_i < _s.Length && (char.IsAsciiLetterOrDigit(_s[_i]) || _s[_i] is '_' or '-')) _i++;
        return new StepToken(StepTokenKind.Keyword, _s[start.._i], 0, 0, line);
    }

    private StepToken ReadEnumeration(int line)
    {
        _i++; // '.'
        int start = _i;
        while (_i < _s.Length && (char.IsAsciiLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
        if (_i == start || _i >= _s.Length || _s[_i] != '.')
            throw new StepParseException(line, "malformed enumeration literal (expected .NAME.)");
        string name = _s[start.._i];
        _i++; // closing '.'
        return new StepToken(StepTokenKind.Enumeration, name, 0, 0, line);
    }

    private StepToken ReadNumber(int line)
    {
        int start = _i;
        if (_s[_i] is '+' or '-') _i++;
        int digits = _i;
        while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
        if (_i == digits) throw new StepParseException(line, "sign without digits");

        bool isReal = false;
        if (_i < _s.Length && _s[_i] == '.')
        {
            isReal = true;
            _i++;
            while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
        }
        if (_i < _s.Length && _s[_i] is 'E' or 'e')
        {
            isReal = true;
            _i++;
            if (_i < _s.Length && _s[_i] is '+' or '-') _i++;
            int expDigits = _i;
            while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
            if (_i == expDigits) throw new StepParseException(line, "exponent without digits");
        }

        var span = _s.AsSpan(start, _i - start);
        return isReal
            ? new StepToken(StepTokenKind.Real, "", 0, double.Parse(span, CultureInfo.InvariantCulture), line)
            : new StepToken(StepTokenKind.Integer, "", long.Parse(span, CultureInfo.InvariantCulture), 0, line);
    }

    private StepToken ReadString(int line)
    {
        _i++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= _s.Length) throw new StepParseException(line, "unterminated string literal");
            char c = _s[_i];
            if (c == '\'')
            {
                if (_i + 1 < _s.Length && _s[_i + 1] == '\'') { sb.Append('\''); _i += 2; continue; }
                _i++;
                break;
            }
            if (c == '\n') _line++; // tolerated: exporter-wrapped string
            if (c == '\\') { DecodeDirective(sb, line); continue; }
            sb.Append(c);
            _i++;
        }
        return new StepToken(StepTokenKind.Text, sb.ToString(), 0, 0, line);
    }

    /// <summary>Decodes one backslash directive starting at <c>_i</c> (which points at '\').</summary>
    private void DecodeDirective(StringBuilder sb, int line)
    {
        // Directive grammar: \\, \S\c, \X\hh\, \X2\(hhhh)+\X0\, \X4\(hhhhhhhh)+\X0\, \P?\.
        if (_i + 1 >= _s.Length) throw new StepParseException(line, "dangling '\\' in string literal");
        char d = _s[_i + 1];
        switch (d)
        {
            case '\\':
                sb.Append('\\');
                _i += 2;
                return;
            case 'S' when At(_i + 2, '\\'):
                if (_i + 3 >= _s.Length) throw new StepParseException(line, "dangling \\S\\ directive");
                // Upper half of the current 8-bit page; decoded as Latin-1 (pages differ
                // only in exotic glyphs and never affect geometry).
                sb.Append((char)(_s[_i + 3] + 128));
                _i += 4;
                return;
            case 'P' when _i + 3 < _s.Length && _s[_i + 3] == '\\':
                _i += 4; // code-page selector for \S\ — accepted, Latin-1 is used regardless
                return;
            case 'X' when At(_i + 2, '\\'):
                sb.Append((char)ReadHex(_i + 3, 2, line));
                _i += 3 + 2 + 1; // \X\hh\  (trailing backslash)
                return;
            case 'X' when At(_i + 2, '2') && At(_i + 3, '\\'):
                DecodeHexRun(sb, 4, line);
                return;
            case 'X' when At(_i + 2, '4') && At(_i + 3, '\\'):
                DecodeHexRun(sb, 8, line);
                return;
            default:
                throw new StepParseException(line, $"unknown string escape directive '\\{d}'");
        }
    }

    /// <summary>Decodes <c>\X2\…\X0\</c> (4-digit UTF-16 units) or <c>\X4\…\X0\</c> (8-digit code points).</summary>
    private void DecodeHexRun(StringBuilder sb, int digitsPerUnit, int line)
    {
        _i += 4; // past \X2\ or \X4\
        while (true)
        {
            if (At(_i, '\\') && At(_i + 1, 'X') && At(_i + 2, '0') && At(_i + 3, '\\')) { _i += 4; return; }
            int code = ReadHex(_i, digitsPerUnit, line);
            if (digitsPerUnit == 4) sb.Append((char)code);
            else sb.Append(char.ConvertFromUtf32(code));
            _i += digitsPerUnit;
        }
    }

    private int ReadHex(int at, int count, int line)
    {
        if (at + count > _s.Length)
            throw new StepParseException(line, "truncated hex digits in \\X directive");
        int value = 0;
        for (int k = 0; k < count; k++)
        {
            int digit = HexDigit(_s[at + k]);
            if (digit < 0)
                throw new StepParseException(line, $"invalid hex digit '{_s[at + k]}' in \\X directive");
            value = (value << 4) | digit;
        }
        return value;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1
    };

    private bool At(int index, char c) => index < _s.Length && _s[index] == c;
}
