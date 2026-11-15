using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: FORMAT (help, "FORMAT"; docs/Optics-Conventions.md §56, "Number output").

internal enum ZplFormatKind { Fixed, Exponential, Integer, Literal }

/// <summary>
/// The numeric output format of PRINT and $STR (help, "FORMAT"): <c>FORMAT m.n</c> prints m
/// characters with n after the decimal point ("If a number is too large to fit within the m
/// decimal places, then the m portion of the FORMAT command will be ignored"; <c>FORMAT .5</c>
/// "as many total places as needed"); <c>EXP</c> exponential notation; <c>m INT</c> the value
/// first converted to an integer, in m places; <c>"C_format_string" LIT</c> the C format.
/// </summary>
internal sealed record ZplFormatSpec(ZplFormatKind Kind, int Width, int Decimals, string? Literal)
{
    /// <summary>
    /// The format before any FORMAT: four decimals, no width. The help gives no default; its
    /// example output for a macro that prints 3.5 and 7.11 without a FORMAT is "3.5000" and
    /// "7.1100" (Calling a Macro from within a Macro), which is <c>FORMAT .4</c>.
    /// </summary>
    public static ZplFormatSpec Default { get; } = new(ZplFormatKind.Fixed, 0, 4, null);

    private static readonly Regex MDotN = new(@"^(\d*)\.(\d+)\s*(EXP)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MInt = new(@"^(\d+)\s*(INT)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Lit = new("^\"([^\"]*)\"\\s*(LIT)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Parses the text after FORMAT. "The values for m and n used must be explicit, i.e.
    /// values stored as variables cannot be used." <c>FORMAT m</c> without INT is read as
    /// <c>FORMAT m INT</c>, the help's syntax line writing INT as optional ("FORMAT m [INT]").</summary>
    public static ZplFormatSpec Parse(string text)
    {
        string t = text.Trim();
        var m = MDotN.Match(t);
        if (m.Success)
        {
            int width = m.Groups[1].Value.Length > 0 ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            int decimals = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return new ZplFormatSpec(m.Groups[3].Success ? ZplFormatKind.Exponential : ZplFormatKind.Fixed, width, decimals, null);
        }
        m = MInt.Match(t);
        if (m.Success) return new ZplFormatSpec(ZplFormatKind.Integer, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0, null);
        m = Lit.Match(t);
        if (m.Success)
        {
            CFormat.Check(m.Groups[1].Value);
            return new ZplFormatSpec(ZplFormatKind.Literal, 0, 0, m.Groups[1].Value);
        }
        throw new ZplRuntimeException($"FORMAT '{t}' is not FORMAT m.n, FORMAT m.n EXP, FORMAT m INT or FORMAT \"C format\" LIT, with explicit numbers (help, FORMAT).");
    }

    public string Format(double value)
    {
        switch (Kind)
        {
            case ZplFormatKind.Fixed:
                return Pad(value.ToString("F" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            case ZplFormatKind.Exponential:
                // .NET's "E" format writes at least three exponent digits, as the help's example
                // "FORMAT 12.2 EXP" output 5.00E+000 shows.
                return Pad(value.ToString("E" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            case ZplFormatKind.Integer:
                return Pad(ToInteger(value).ToString(CultureInfo.InvariantCulture));
            default:
                return CFormat.Format(Literal!, value);
        }
    }

    private string Pad(string s) => s.Length >= Width ? s : new string(' ', Width - s.Length) + s;

    /// <summary>"converted to an integer": truncated toward zero, as C converts a double.</summary>
    internal static long ToInteger(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > 9.2e18)
            throw new ZplRuntimeException($"{value.ToString("R", CultureInfo.InvariantCulture)} cannot be converted to an integer.");
        return (long)Math.Truncate(value);
    }
}

/// <summary>
/// The C format of <c>FORMAT "…" LIT</c>: literal text with exactly one conversion
/// <c>%[flags][width][.precision]conversion</c> (flags - + space # 0; conversions d i u o x X f F e
/// E g G; <c>%%</c> is a percent sign), following the C standard's printf. d i u o x X convert
/// the value to an integer first (truncation). The exponent of e E g G has at least two digits,
/// as the C standard says.
/// </summary>
internal static class CFormat
{
    private static readonly Regex Spec = new(@"%([-+ #0]*)(\d*)(?:\.(\d*))?(h|l|ll|L)?([diuoxXfFeEgG%])", RegexOptions.CultureInvariant);

    public static void Check(string format)
    {
        int conversions = Spec.Matches(format).Count(m => m.Groups[5].Value != "%");
        int percents = format.Count(c => c == '%');
        int matchedPercents = Spec.Matches(format).Sum(m => m.Groups[5].Value == "%" ? 2 : 1);
        if (conversions != 1 || percents != matchedPercents)
            throw new ZplRuntimeException($"the C format \"{format}\" must hold exactly one numeric conversion such as %f, %e, %g or %d (and %% for a percent sign).");
    }

    public static string Format(string format, double value)
    {
        var sb = new StringBuilder();
        int last = 0;
        foreach (Match m in Spec.Matches(format))
        {
            sb.Append(format, last, m.Index - last);
            last = m.Index + m.Length;
            char conv = m.Groups[5].Value[0];
            if (conv == '%') { sb.Append('%'); continue; }
            string flags = m.Groups[1].Value;
            int width = m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            int? precision = m.Groups[3].Success ? (m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0) : null;
            sb.Append(One(flags, width, precision, conv, value));
        }
        sb.Append(format, last, format.Length - last);
        return sb.ToString();
    }

    private static string One(string flags, int width, int? precision, char conv, double value)
    {
        bool left = flags.Contains('-'), plus = flags.Contains('+'), space = flags.Contains(' '), alt = flags.Contains('#'), zero = flags.Contains('0');
        string sign = "", body;
        bool numericZeroPad = zero && !left;
        switch (conv)
        {
            case 'd' or 'i' or 'u' or 'o' or 'x' or 'X':
            {
                long n = ZplFormatSpec.ToInteger(value);
                if (conv is 'd' or 'i')
                {
                    if (n < 0) sign = "-"; else if (plus) sign = "+"; else if (space) sign = " ";
                    body = Math.Abs((decimal)n).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    ulong u = unchecked((ulong)n);
                    body = conv switch
                    {
                        'u' => u.ToString(CultureInfo.InvariantCulture),
                        'o' => Convert.ToString(unchecked((long)u), 8),
                        'x' => u.ToString("x", CultureInfo.InvariantCulture),
                        _ => u.ToString("X", CultureInfo.InvariantCulture),
                    };
                    if (alt && conv == 'o' && !body.StartsWith('0')) body = "0" + body;
                    if (alt && conv is 'x' or 'X' && u != 0) sign = conv == 'x' ? "0x" : "0X";
                }
                if (precision is int p)
                {
                    numericZeroPad = false;   // C: the 0 flag is ignored when a precision is given
                    if (p == 0 && n == 0) body = "";
                    else if (body.Length < p) body = new string('0', p - body.Length) + body;
                }
                break;
            }
            default:
            {
                if (double.IsNaN(value)) { body = char.IsUpper(conv) ? "NAN" : "nan"; numericZeroPad = false; }
                else
                {
                    if (value < 0 || (value == 0 && double.IsNegative(value))) sign = "-"; else if (plus) sign = "+"; else if (space) sign = " ";
                    double a = Math.Abs(value);
                    if (double.IsInfinity(a)) { body = char.IsUpper(conv) ? "INF" : "inf"; numericZeroPad = false; }
                    else body = Real(conv, a, precision ?? 6, alt);
                }
                break;
            }
        }
        int len = sign.Length + body.Length;
        if (len >= width) return sign + body;
        if (left) return sign + body + new string(' ', width - len);
        if (numericZeroPad) return sign + new string('0', width - len) + body;
        return new string(' ', width - len) + sign + body;
    }

    private static string Real(char conv, double a, int precision, bool alt)
    {
        switch (conv)
        {
            case 'f' or 'F':
            {
                string s = a.ToString("F" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                return alt && precision == 0 ? s + "." : s;
            }
            case 'e' or 'E':
                return Exp(a, precision, conv == 'E', alt);
            default:
            {
                // %g: P = precision (6 if absent, 1 if zero); with X the exponent of the %e form,
                // %f with precision P − 1 − X when P > X ≥ −4, else %e with precision P − 1;
                // trailing zeros (and a trailing point) removed unless '#'.
                int p = precision == 0 ? 1 : precision;
                int x = a == 0 ? 0 : ExponentOf(a, p - 1);
                string s = p > x && x >= -4
                    ? a.ToString("F" + (p - 1 - x).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                    : Exp(a, p - 1, conv == 'G', alt);
                if (!alt) s = TrimZeros(s);
                return s;
            }
        }
    }

    private static int ExponentOf(double a, int decimals)
    {
        string e = a.ToString("E" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return int.Parse(e[(e.IndexOf('E') + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    private static string Exp(double a, int precision, bool upper, bool alt)
    {
        string e = a.ToString("E" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int k = e.IndexOf('E');
        string mantissa = e[..k];
        if (alt && precision == 0) mantissa += ".";
        int exponent = int.Parse(e[(k + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        string digits = Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        return mantissa + (upper ? "E" : "e") + (exponent < 0 ? "-" : "+") + digits;
    }

    private static string TrimZeros(string s)
    {
        int e = s.IndexOfAny(new[] { 'e', 'E' });
        string mantissa = e >= 0 ? s[..e] : s, tail = e >= 0 ? s[e..] : "";
        if (mantissa.Contains('.')) mantissa = mantissa.TrimEnd('0').TrimEnd('.');
        return mantissa + tail;
    }
}
