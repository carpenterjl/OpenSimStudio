using System.Globalization;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Gerber;

/// <summary>
/// Evaluates an aperture macro against one %ADD%'s parameters into concrete
/// <see cref="MacroPrimitive"/>s. Implements the spec 4.5 subset KiCad/Altium emit:
/// primitives 0 (comment), 1, 4, 5, 20, 21, 7, moiré 6 as a warned circle
/// approximation, in-macro variable assignments ($n=expr), and arithmetic
/// expressions with + − x/X / (unary ±, parentheses, $n substitution).
/// An undefined $n or a division by zero throws — a parameter silently becoming 0
/// would be a silent misrender.
/// </summary>
public static class MacroEvaluator
{
    public static IReadOnlyList<MacroPrimitive> Evaluate(
        MacroDefinition macro, IReadOnlyList<double> parameters, double unitScale, List<string> warnings)
    {
        var variables = new Dictionary<int, double>();
        for (int i = 0; i < parameters.Count; i++)
            variables[i + 1] = parameters[i];                     // $1 is the first AD parameter

        var primitives = new List<MacroPrimitive>();
        foreach (var statement in macro.BodyStatements)
        {
            if (statement.Length == 0) continue;

            if (statement[0] == '$')                              // variable assignment: $4=$1x0.75
            {
                int eq = statement.IndexOf('=');
                if (eq < 2 || !int.TryParse(statement[1..eq], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    throw Error(macro, statement, "malformed variable assignment");
                variables[index] = MacroExpression.Evaluate(statement[(eq + 1)..], variables,
                    reason => Error(macro, statement, reason));
                continue;
            }

            var fields = statement.Split(',');
            if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            {
                // Comment text without the leading "0," separator (Altium strips spaces).
                if (fields[0].StartsWith('0')) continue;
                throw Error(macro, statement, "unknown primitive statement");
            }
            if (code == 0) continue;                              // comment

            double[] p = new double[fields.Length - 1];
            for (int i = 1; i < fields.Length; i++)
                p[i - 1] = MacroExpression.Evaluate(fields[i], variables,
                    reason => Error(macro, statement, reason));

            primitives.Add(BuildPrimitive(macro, statement, code, p, unitScale, warnings));
        }
        return primitives;
    }

    private static MacroPrimitive BuildPrimitive(MacroDefinition macro, string statement,
        int code, double[] p, double s, List<string> warnings)
    {
        switch (code)
        {
            case 1:                                               // exposure, diameter, X, Y, [rot]
            {
                Require(macro, statement, p.Length >= 4, "circle needs exposure, diameter, X, Y");
                double rot = p.Length >= 5 ? p[4] : 0;
                return new MacroCircle(Exposure(macro, statement, p[0]),
                    Rotate(new Point2(p[2], p[3]), rot) * s, p[1] * s);
            }
            case 20:                                              // exposure, width, X1, Y1, X2, Y2, [rot]
            {
                Require(macro, statement, p.Length >= 6, "vector line needs exposure, width, X1, Y1, X2, Y2");
                double rot = p.Length >= 7 ? p[6] : 0;
                var a = new Point2(p[2], p[3]);
                var b = new Point2(p[4], p[5]);
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                // Flat-ended rectangle along the segment (spec: line ends are square).
                var n = len < 1e-12 ? new Point2(0, 0) : new Point2(-dy / len, dx / len) * (p[1] / 2);
                var ring = new[] { a - n, b - n, b + n, a + n }
                    .Select(v => Rotate(v, rot) * s).ToArray();
                return new MacroRing(Exposure(macro, statement, p[0]), ring);
            }
            case 21:                                              // exposure, width, height, X, Y, [rot]
            {
                Require(macro, statement, p.Length >= 5, "center line needs exposure, width, height, X, Y");
                double rot = p.Length >= 6 ? p[5] : 0;
                var c = new Point2(p[3], p[4]);
                double w = p[1] / 2, h = p[2] / 2;
                var ring = new[]
                {
                    new Point2(c.X - w, c.Y - h), new Point2(c.X + w, c.Y - h),
                    new Point2(c.X + w, c.Y + h), new Point2(c.X - w, c.Y + h)
                }.Select(v => Rotate(v, rot) * s).ToArray();
                return new MacroRing(Exposure(macro, statement, p[0]), ring);
            }
            case 4:                                               // exposure, n, X1,Y1 … Xn+1,Yn+1, [rot]
            {
                Require(macro, statement, p.Length >= 2, "outline needs exposure and a vertex count");
                int n = RoundToInt(macro, statement, p[1], "outline vertex count");
                Require(macro, statement, n >= 3, "outline needs at least 3 vertices");
                Require(macro, statement, p.Length >= 2 + 2 * (n + 1),
                    $"outline declares {n} vertices but carries too few coordinates");
                double rot = p.Length >= 3 + 2 * (n + 1) ? p[2 + 2 * (n + 1)] : 0;
                var ring = new Point2[n];                         // the (n+1)-th point closes the ring — drop it
                for (int i = 0; i < n; i++)
                    ring[i] = Rotate(new Point2(p[2 + 2 * i], p[3 + 2 * i]), rot) * s;
                return new MacroRing(Exposure(macro, statement, p[0]), ring);
            }
            case 5:                                               // exposure, n, X, Y, diameter, [rot]
            {
                Require(macro, statement, p.Length >= 5, "polygon needs exposure, vertices, X, Y, diameter");
                int n = RoundToInt(macro, statement, p[1], "polygon vertex count");
                Require(macro, statement, n is >= 3 and <= 12, "polygon vertex count must be 3–12");
                double rot = p.Length >= 6 ? p[5] : 0;
                var c = new Point2(p[2], p[3]);
                double r = p[4] / 2;
                var ring = new Point2[n];                         // first vertex on +X pre-rotation, per spec
                for (int i = 0; i < n; i++)
                {
                    double a = 2 * Math.PI * i / n;
                    ring[i] = Rotate(new Point2(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)), rot) * s;
                }
                return new MacroRing(Exposure(macro, statement, p[0]), ring);
            }
            case 7:                                               // X, Y, outer, inner, gap, [rot]
            {
                Require(macro, statement, p.Length >= 5, "thermal needs X, Y, outer, inner, gap");
                double rot = p.Length >= 6 ? p[5] : 0;
                return new MacroThermal(Rotate(new Point2(p[0], p[1]), rot) * s,
                    p[2] * s, p[3] * s, p[4] * s, rot);
            }
            case 6:                                               // moiré: X, Y, outer, … (fiducial-only, deprecated)
            {
                Require(macro, statement, p.Length >= 3, "moiré needs X, Y and an outer diameter");
                double rot = p.Length >= 9 ? p[8] : 0;
                warnings.Add($"Macro '{macro.Name}': moiré primitive approximated by its " +
                             $"{p[2]:g3} outer circle (moiré is a fiducial pattern, not copper).");
                return new MacroCircle(true, Rotate(new Point2(p[0], p[1]), rot) * s, p[2] * s);
            }
            default:
                throw Error(macro, statement, $"unsupported macro primitive code {code}");
        }
    }

    private static bool Exposure(MacroDefinition macro, string statement, double value) => value switch
    {
        1 => true,
        0 => false,
        _ => throw Error(macro, statement, $"exposure must be 0 or 1, got {value}")
    };

    private static int RoundToInt(MacroDefinition macro, string statement, double value, string what)
    {
        double rounded = Math.Round(value);
        if (Math.Abs(value - rounded) > 1e-9)
            throw Error(macro, statement, $"{what} must be an integer, got {value}");
        return (int)rounded;
    }

    private static void Require(MacroDefinition macro, string statement, bool condition, string reason)
    {
        if (!condition) throw Error(macro, statement, reason);
    }

    private static InvalidDataException Error(MacroDefinition macro, string statement, string reason) =>
        new($"Aperture macro '{macro.Name}', statement '{statement}*': {reason}.");

    /// <summary>Rotation is about the MACRO ORIGIN (spec 4.5.3), not the primitive's own center.</summary>
    private static Point2 Rotate(Point2 p, double degrees)
    {
        if (degrees == 0) return p;
        double a = degrees * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        return new Point2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
    }
}

/// <summary>
/// Recursive-descent evaluator for macro arithmetic (spec 4.5.4): decimals, $n,
/// binary + − x X / with the usual precedence, unary ±, parentheses.
/// </summary>
internal static class MacroExpression
{
    public static double Evaluate(string text, IReadOnlyDictionary<int, double> variables,
        Func<string, Exception> error)
    {
        int pos = 0;
        double value = ParseSum(text, ref pos, variables, error);
        if (pos != text.Length)
            throw error($"unexpected '{text[pos]}' in expression '{text}'");
        return value;
    }

    private static double ParseSum(string s, ref int pos, IReadOnlyDictionary<int, double> vars,
        Func<string, Exception> error)
    {
        double value = ParseProduct(s, ref pos, vars, error);
        while (pos < s.Length && (s[pos] == '+' || s[pos] == '-'))
        {
            char op = s[pos++];
            double rhs = ParseProduct(s, ref pos, vars, error);
            value = op == '+' ? value + rhs : value - rhs;
        }
        return value;
    }

    private static double ParseProduct(string s, ref int pos, IReadOnlyDictionary<int, double> vars,
        Func<string, Exception> error)
    {
        double value = ParseFactor(s, ref pos, vars, error);
        while (pos < s.Length && (s[pos] is 'x' or 'X' or '/'))
        {
            char op = s[pos++];
            double rhs = ParseFactor(s, ref pos, vars, error);
            if (op == '/')
            {
                if (rhs == 0) throw error($"division by zero in expression '{s}'");
                value /= rhs;
            }
            else
            {
                value *= rhs;
            }
        }
        return value;
    }

    private static double ParseFactor(string s, ref int pos, IReadOnlyDictionary<int, double> vars,
        Func<string, Exception> error)
    {
        if (pos >= s.Length) throw error($"expression '{s}' ended unexpectedly");
        char c = s[pos];
        if (c == '+') { pos++; return ParseFactor(s, ref pos, vars, error); }
        if (c == '-') { pos++; return -ParseFactor(s, ref pos, vars, error); }
        if (c == '(')
        {
            pos++;
            double inner = ParseSum(s, ref pos, vars, error);
            if (pos >= s.Length || s[pos] != ')') throw error($"missing ')' in expression '{s}'");
            pos++;
            return inner;
        }
        if (c == '$')
        {
            pos++;
            int start = pos;
            while (pos < s.Length && char.IsDigit(s[pos])) pos++;
            if (pos == start) throw error($"'$' without a variable number in expression '{s}'");
            int index = int.Parse(s[start..pos], System.Globalization.CultureInfo.InvariantCulture);
            if (!vars.TryGetValue(index, out double value))
                throw error($"undefined macro variable ${index}");
            return value;
        }
        if (char.IsDigit(c) || c == '.')
        {
            int start = pos;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
            return double.Parse(s[start..pos], System.Globalization.CultureInfo.InvariantCulture);
        }
        throw error($"unexpected '{c}' in expression '{s}'");
    }
}
