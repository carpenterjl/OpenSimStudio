namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: expressions (docs/Optics-Conventions.md §56, "Expressions").

/// <summary>What an expression reads while it is evaluated: variables, arrays and functions.</summary>
internal interface IZplEvaluator
{
    ZplValue Variable(string name);
    ZplValue Call(string name, IReadOnlyList<ZplExpr> arguments);
}

internal abstract record ZplExpr
{
    public abstract ZplValue Evaluate(IZplEvaluator e);

    /// <summary>True when the expression is a string by its form (a string literal, a string
    /// variable, a $ function or a concatenation of them); used where a keyword argument may be a
    /// bare file name.</summary>
    public virtual bool IsStringForm => false;

    public double Number(IZplEvaluator e)
    {
        var v = Evaluate(e);
        if (v.IsString) throw new ZplRuntimeException($"a number is needed here, not the string \"{v.Text}\".");
        return v.Number;
    }

    public string Text(IZplEvaluator e)
    {
        var v = Evaluate(e);
        if (!v.IsString) throw new ZplRuntimeException("a string is needed here (a quoted text, a string variable ending in $, or a $ function), not a number.");
        return v.Text!;
    }
}

internal sealed record ZplNumber(double Value) : ZplExpr
{
    public override ZplValue Evaluate(IZplEvaluator e) => ZplValue.Of(Value);
}

internal sealed record ZplString(string Value) : ZplExpr
{
    public override ZplValue Evaluate(IZplEvaluator e) => ZplValue.Of(Value);
    public override bool IsStringForm => true;
}

/// <summary>A scalar variable (numeric, or string when the name ends in $).</summary>
internal sealed record ZplVariable(string Name) : ZplExpr
{
    public override ZplValue Evaluate(IZplEvaluator e) => e.Variable(Name);
    public override bool IsStringForm => Name.EndsWith('$');
}

/// <summary>NAME(arguments): a function, or an element of an array declared with DECLARE.</summary>
internal sealed record ZplCall(string Name, IReadOnlyList<ZplExpr> Arguments) : ZplExpr
{
    public override ZplValue Evaluate(IZplEvaluator e) => e.Call(Name, Arguments);
    public override bool IsStringForm => Name.StartsWith('$');
}

internal sealed record ZplUnary(string Op, ZplExpr Operand) : ZplExpr
{
    public override ZplValue Evaluate(IZplEvaluator e)
    {
        double v = Operand.Number(e);
        return ZplValue.Of(Op switch
        {
            "-" => -v,
            "+" => v,
            "!" => v == 0 ? 1 : 0,
            _ => throw new InvalidOperationException(Op),
        });
    }
}

internal sealed record ZplBinary(string Op, ZplExpr Left, ZplExpr Right) : ZplExpr
{
    public override bool IsStringForm => Op == "+" && (Left.IsStringForm || Right.IsStringForm);

    public override ZplValue Evaluate(IZplEvaluator e)
    {
        if (Op[0] == '$')
        {
            string a = Left.Text(e), b = Right.Text(e);
            int c = string.CompareOrdinal(a, b);
            bool r = Op switch
            {
                "$==" => c == 0,
                "$!=" => c != 0,
                "$<" => c < 0,
                "$>" => c > 0,
                "$<=" => c <= 0,
                "$>=" => c >= 0,
                _ => throw new InvalidOperationException(Op),
            };
            return ZplValue.Of(r ? 1 : 0);
        }
        if (Op == "+")
        {
            var l = Left.Evaluate(e);
            var rv = Right.Evaluate(e);
            if (l.IsString && rv.IsString) return ZplValue.Of(l.Text + rv.Text);
            if (l.IsString || rv.IsString)
                throw new ZplRuntimeException("+ joins two strings or adds two numbers; it cannot add a number to a string (use $STR to make a number a string).");
            return ZplValue.Of(l.Number + rv.Number);
        }
        double x = Left.Number(e);
        double y = Right.Number(e);   // both operands are always evaluated (no short circuit)
        return ZplValue.Of(Op switch
        {
            "-" => x - y,
            "*" => x * y,
            "/" => y != 0 ? x / y : throw new ZplRuntimeException($"division by zero ({x.ToString(System.Globalization.CultureInfo.InvariantCulture)} / 0)."),
            "&" => x != 0 && y != 0 ? 1 : 0,
            "|" => x != 0 || y != 0 ? 1 : 0,
            "^" => (x != 0) != (y != 0) ? 1 : 0,
            "==" => x == y ? 1 : 0,
            "!=" => x != y ? 1 : 0,
            "<" => x < y ? 1 : 0,
            ">" => x > y ? 1 : 0,
            "<=" => x <= y ? 1 : 0,
            ">=" => x >= y ? 1 : 0,
            _ => throw new InvalidOperationException(Op),
        });
    }
}

/// <summary>
/// Parses expressions. The help defines the operators but not their precedence (it says only
/// that "parentheses are used to force precedence", Numeric Logical Operators); this program's
/// reading, from tightest to loosest: unary − + !; * /; + −; the comparisons == != &lt; &gt; &lt;= &gt;=
/// and their string forms; then &amp; | ^ at one level, left to right. Note ^ is exclusive or, not
/// a power (POWR is the power function).
/// </summary>
internal sealed class ZplExpressionParser
{
    private readonly List<ZplToken> _t;
    private int _i;

    public ZplExpressionParser(List<ZplToken> tokens, int start = 0) { _t = tokens; _i = start; }

    public int Position => _i;
    public ZplToken Peek => _t[_i];
    public ZplToken PeekAt(int k) => _t[Math.Min(_i + k, _t.Count - 1)];
    public bool AtEnd => _t[_i].Kind == ZplTokenKind.End;
    public ZplToken Next() => _t[_i++];

    public void Expect(string op)
    {
        if (!Peek.Is(op)) throw new ZplRuntimeException($"'{op}' expected{Found()}.");
        _i++;
    }

    public string Found() => AtEnd ? " at the end of the line" : $" before '{Peek.Text}'";

    /// <summary>Parses one whole expression and requires the end of the tokens.</summary>
    public static ZplExpr ParseAll(string text)
    {
        var p = new ZplExpressionParser(ZplLexer.Tokenize(text));
        if (p.AtEnd) throw new ZplRuntimeException("an expression is missing.");
        var e = p.Parse();
        if (!p.AtEnd) throw new ZplRuntimeException($"unexpected '{p.Peek.Text}' after the expression.");
        return e;
    }

    public ZplExpr Parse()
    {
        var left = Comparison();
        while (Peek.Is("&") || Peek.Is("|") || Peek.Is("^"))
        {
            string op = Next().Text;
            left = new ZplBinary(op, left, Comparison());
        }
        return left;
    }

    private ZplExpr Comparison()
    {
        var left = Additive();
        while (Peek.Kind == ZplTokenKind.Operator && Peek.Text is "==" or "!=" or "<" or ">" or "<=" or ">=" or "$==" or "$!=" or "$<" or "$>" or "$<=" or "$>=")
        {
            string op = Next().Text;
            left = new ZplBinary(op, left, Additive());
        }
        if (Peek.Is("="))
            throw new ZplRuntimeException("'=' assigns a value; compare with '==' (Numeric Logical Operators).");
        return left;
    }

    private ZplExpr Additive()
    {
        var left = Multiplicative();
        while (Peek.Is("+") || Peek.Is("-"))
        {
            string op = Next().Text;
            left = new ZplBinary(op, left, Multiplicative());
        }
        return left;
    }

    private ZplExpr Multiplicative()
    {
        var left = Unary();
        while (Peek.Is("*") || Peek.Is("/"))
        {
            string op = Next().Text;
            left = new ZplBinary(op, left, Unary());
        }
        return left;
    }

    private ZplExpr Unary()
    {
        if (Peek.Is("-") || Peek.Is("+") || Peek.Is("!"))
        {
            string op = Next().Text;
            return new ZplUnary(op, Unary());
        }
        return Primary();
    }

    private ZplExpr Primary()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case ZplTokenKind.Number:
                _i++;
                return new ZplNumber(t.Number);
            case ZplTokenKind.String:
                _i++;
                return new ZplString(t.Text);
            case ZplTokenKind.Identifier:
                _i++;
                string name = t.Text.ToUpperInvariant();
                if (Peek.Is("("))
                {
                    _i++;
                    var args = new List<ZplExpr>();
                    if (!Peek.Is(")"))
                    {
                        while (true)
                        {
                            args.Add(Parse());
                            if (Peek.Is(",")) { _i++; continue; }
                            break;
                        }
                    }
                    Expect(")");
                    return new ZplCall(name, args);
                }
                if (name.StartsWith('$'))
                    throw new ZplRuntimeException($"the string function {name} needs parentheses: {name}().");
                return new ZplVariable(name);
            case ZplTokenKind.Operator when t.Text == "(":
                _i++;
                var inner = Parse();
                Expect(")");
                return inner;
            default:
                throw new ZplRuntimeException(t.Kind == ZplTokenKind.End ? "an expression is incomplete at the end of the line." : $"'{t.Text}' cannot start an expression.");
        }
    }
}
