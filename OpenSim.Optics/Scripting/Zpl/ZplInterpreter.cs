using System.Globalization;
using System.Text;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: executing a parsed macro (docs/Optics-Conventions.md §56, "Execution").

/// <summary>
/// Runs one macro: its variables, arrays, numeric format, text output and open file are its own
/// (a macro called with CALLMACRO is a separate macro with its own variables); the lens, the call
/// buffers, the OPTRETURN and SOLVERETURN values, the vectors and the last ray belong to the
/// <see cref="ZplSession"/> the parent and its children share.
/// </summary>
internal sealed class ZplInterpreter : IZplEvaluator
{
    private readonly ZplProgram _program;
    private readonly Dictionary<string, double> _numbers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZplArray> _arrays = new(StringComparer.Ordinal);
    private readonly Dictionary<int, LoopState> _loops = new();
    private readonly Stack<int> _calls = new();
    private readonly StringBuilder _screen = new();
    private StreamWriter? _file;

    /// <summary>"There can be no more than 100 'nesting levels'" (help, GOSUB, SUB, RETURN, and END).</summary>
    internal const int MaxNesting = 100;

    private readonly bool _stream;

    /// <param name="stream">True for the parent macro, whose screen text is passed to
    /// <see cref="ZplRunOptions.Output"/> as it is written; a child's text reaches it when the
    /// parent copies it in.</param>
    public ZplInterpreter(ZplProgram program, ZplSession session, bool stream = true)
    {
        _program = program;
        Session = session;
        _stream = stream;
    }

    public ZplSession Session { get; }
    public ZplFormatSpec Format { get; set; } = ZplFormatSpec.Default;
    public string MacroName => _program.Name;

    /// <summary>The line being executed (for messages).</summary>
    public int Line { get; private set; }

    /// <summary>The text written to the screen (OUTPUT SCREEN), in order.</summary>
    public string ScreenText => _screen.ToString();

    /// <summary>The file opened by OPEN for READ, or null.</summary>
    public ZplReader? Reader { get; set; }

    /// <summary>True after CLOSEWINDOW: run "in quiet mode".</summary>
    public bool Quiet { get; set; }

    private sealed record LoopState(double Stop, double Step, int Direction);

    public void Run()
    {
        var st = _program.Statements;
        int pc = 0;
        try
        {
            while (pc >= 0 && pc < st.Count)
            {
                Session.CancellationToken.ThrowIfCancellationRequested();
                Session.CountStep();
                var s = st[pc];
                Line = s.Line;
                try { pc = Execute(s, pc); }
                catch (ZplException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (ZplRuntimeException e) { throw new ZplException(MacroName, s.Line, e.Message, e); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or ArithmeticException or FormatException)
                {
                    throw new ZplException(MacroName, s.Line, e.Message, e);
                }
            }
            if (pc >= st.Count && _calls.Count > 0)
                throw new ZplException(MacroName, Line, "the macro ended inside a subroutine: each subroutine must be terminated by a RETURN command (help, GOSUB).");
        }
        finally
        {
            CloseOutputFile();
            Reader?.Dispose();
            Reader = null;
        }
    }

    private int Execute(ZplStatement s, int pc)
    {
        switch (s)
        {
            case ZplAssign a: Assign(a); return pc + 1;
            case ZplPrint p: Print(p); return pc + 1;
            case ZplFormatStatement f: Format = f.Spec; return pc + 1;
            case ZplIf i:
                if (Truth(i.Condition)) return pc + 1;
                return i.ElseIndex >= 0 ? i.ElseIndex + 1 : i.EndIndex + 1;
            case ZplIfThen t:
                return Truth(t.Condition) ? Execute(t.Then, pc) : pc + 1;
            case ZplElse e: return e.EndIndex + 1;
            case ZplEndIf: return pc + 1;
            case ZplFor f: return StartLoop(f, pc);
            case ZplNext n: return NextLoop(n, pc);
            case ZplGoto g: return _program.Labels[g.Label];
            case ZplLabel: return pc + 1;
            case ZplGosub g:
                if (_calls.Count >= MaxNesting) throw new ZplRuntimeException($"GOSUB {g.Name}: more than {MaxNesting} nesting levels (help, GOSUB).");
                _calls.Push(pc + 1);
                return _program.Subs[g.Name] + 1;
            case ZplSub sub:
                throw new ZplRuntimeException($"execution reached SUB {sub.Name} without a GOSUB: end the main body of the macro with END before the subroutines (help, GOSUB).");
            case ZplReturn:
                if (_calls.Count == 0) throw new ZplRuntimeException("RETURN without a GOSUB.");
                return _calls.Pop();
            case ZplEnd: return -1;
            case ZplDeclare d: Declare(d); return pc + 1;
            case ZplRelease r:
                if (!_arrays.Remove(r.Name)) throw new ZplRuntimeException($"RELEASE {r.Name}: there is no array {r.Name} (declare it with DECLARE).");
                return pc + 1;
            case ZplKeywordStatement k:
                (Session.Dialect?.Handler(k.Name) ?? ZplLibrary.Keyword(k.Name))(this, k.Args);   // OP-67: context keywords first
                return pc + 1;
            default:
                throw new InvalidOperationException(s.GetType().Name);
        }
    }

    private bool Truth(ZplExpr e) => e.Number(this) != 0;

    // ---------------------------------------------------------------- FOR / NEXT

    private int StartLoop(ZplFor f, int pc)
    {
        // "Upon reaching a FOR command, the expressions for the start, stop, and increment values
        // are evaluated and saved" (help, FOR, NEXT).
        double start = f.Start.Number(this), stop = f.Stop.Number(this), step = f.Step.Number(this);
        int direction = start == stop ? 0 : start < stop ? 1 : -1;
        if (direction != 0 && !(step * direction > 0))
            throw new ZplRuntimeException($"FOR {f.Variable}: the increment {Num(step)} does not move from {Num(start)} toward {Num(stop)}, so the loop would not end.");
        SetNumber(f.Variable, start);
        _loops[pc] = new LoopState(stop, step, direction);
        return pc + 1;
    }

    private int NextLoop(ZplNext n, int pc)
    {
        var f = (ZplFor)_program.Statements[n.ForIndex];
        if (!_loops.TryGetValue(n.ForIndex, out var loop))
            throw new ZplRuntimeException($"NEXT reached without the FOR {f.Variable} of line {f.Line} running (a GOTO into the loop?).");
        // "If the start value and stop value are the same, the loop executes exactly once. If the
        // start value is less than the stop value, then the loop continues until the counter
        // variable is greater than the stop value", and the reverse for a decreasing loop.
        double v = Number(f.Variable) + loop.Step;
        SetNumber(f.Variable, v);
        bool done = loop.Direction == 0 || (loop.Direction > 0 ? v > loop.Stop : v < loop.Stop);
        if (done) { _loops.Remove(n.ForIndex); return pc + 1; }
        return n.ForIndex + 1;
    }

    // ---------------------------------------------------------------- Variables and arrays

    private void Assign(ZplAssign a)
    {
        var value = a.Value.Evaluate(this);
        if (a.Indices is { } indices)
        {
            if (!_arrays.TryGetValue(a.Name, out var array))
                throw new ZplRuntimeException($"{a.Name}(…) = : there is no array {a.Name}; declare it first (DECLARE {a.Name}, DOUBLE, 1, size).");
            if (value.IsString) throw new ZplRuntimeException($"the array {a.Name} holds numbers, not strings.");
            array.Set(Indices(a.Name, array, indices), value.Number);
            return;
        }
        if (a.Name.EndsWith('$'))
        {
            if (!value.IsString) throw new ZplRuntimeException($"{a.Name} is a string variable; assign it a string (use $STR to convert a number).");
            SetString(a.Name, value.Text!);
        }
        else
        {
            if (value.IsString) throw new ZplRuntimeException($"{a.Name} is a numeric variable; a string variable's name ends in $ (help, String Variables).");
            if (_arrays.ContainsKey(a.Name)) throw new ZplRuntimeException($"{a.Name} is an array; assign its elements as {a.Name}(i) = value.");
            SetNumber(a.Name, value.Number);
        }
    }

    /// <summary>"String variables can hold a maximum of 360 characters" (String Variables).</summary>
    internal const int MaxStringLength = 360;

    public void SetString(string name, string value)
    {
        if (value.Length > MaxStringLength)
            throw new ZplRuntimeException($"{name}: a string variable holds at most {MaxStringLength} characters; this one would have {value.Length} (help, String Variables).");
        _strings[name] = value;
    }

    public void SetNumber(string name, double value)
    {
        if (_arrays.ContainsKey(name)) throw new ZplRuntimeException($"{name} is an array, not a scalar variable.");
        _numbers[name] = value;
    }

    public double Number(string name) => _numbers.TryGetValue(name, out double v) ? v
        : throw new ZplRuntimeException($"the variable {name} is used before a value is assigned to it.");

    public bool HasString(string name) => _strings.ContainsKey(name);

    public ZplValue Variable(string name)
    {
        if (name.EndsWith('$'))
            return _strings.TryGetValue(name, out var s) ? ZplValue.Of(s)
                : throw new ZplRuntimeException($"the string variable {name} is used before a value is assigned to it.");
        if (_arrays.ContainsKey(name)) throw new ZplRuntimeException($"{name} is an array; read its elements as {name}(i).");
        if (ZplLibrary.IsKnownFunction(name)) throw new ZplRuntimeException($"{name} is a function and needs parentheses: {name}().");
        return ZplValue.Of(Number(name));
    }

    public ZplValue Call(string name, IReadOnlyList<ZplExpr> arguments)
    {
        if (_arrays.TryGetValue(name, out var array))
            return ZplValue.Of(array.Get(Indices(name, array, arguments)));
        var values = new ZplValue[arguments.Count];
        for (int i = 0; i < values.Length; i++) values[i] = arguments[i].Evaluate(this);
        return ZplLibrary.CallFunction(this, name, values);
    }

    // wire-13: ZBFREAD and ZBFWRITE take arrays by name.
    /// <summary>The array DECLAREd as <paramref name="name"/> (upper case), or an error naming
    /// <paramref name="who"/>.</summary>
    internal ZplArray ArrayNamed(string name, string who) => _arrays.TryGetValue(name, out var array) ? array
        : throw new ZplRuntimeException($"{who}: there is no array {name}; declare it first with DECLARE.");

    private int[] Indices(string name, ZplArray array, IReadOnlyList<ZplExpr> expressions)
    {
        if (expressions.Count != array.Sizes.Length)
            throw new ZplRuntimeException($"the array {name} has {array.Sizes.Length} dimension{(array.Sizes.Length == 1 ? "" : "s")}; {expressions.Count} indices given.");
        var idx = new int[expressions.Count];
        for (int k = 0; k < idx.Length; k++)
        {
            // The help does not say how a non-integer index is taken; it is rounded down, as VEC1..4
            // take their subscripts ("rounded down to an integer", VEC1, VEC2, VEC3, VEC4).
            double v = expressions[k].Number(this);
            idx[k] = (int)Math.Floor(v);
            if (idx[k] < 1 || idx[k] > array.Sizes[k])
                throw new ZplRuntimeException($"{name}: index {k + 1} is {Num(v)}; it must be from 1 to {array.Sizes[k]} (array variables start at index 1, help, Array Variables).");
        }
        return idx;
    }

    private void Declare(ZplDeclare d)
    {
        if (_numbers.ContainsKey(d.Name)) throw new ZplRuntimeException($"DECLARE {d.Name}: {d.Name} is already a numeric variable.");
        var sizes = new int[d.Dimensions.Count];
        long total = 1;
        for (int k = 0; k < sizes.Length; k++)
        {
            double v = d.Dimensions[k].Number(this);
            if (v != Math.Floor(v) || v < 1 || v > int.MaxValue)
                throw new ZplRuntimeException($"DECLARE {d.Name}: size {k + 1} must be a positive integer, not {Num(v)}.");
            sizes[k] = (int)v;
            total *= sizes[k];
            if (total > 100_000_000) throw new ZplRuntimeException($"DECLARE {d.Name}: {total} elements is more than this program allocates (100,000,000).");
        }
        _arrays[d.Name] = new ZplArray(sizes, d.IsInteger);
    }

    // ---------------------------------------------------------------- Output

    private void Print(ZplPrint p)
    {
        var sb = new StringBuilder();
        foreach (var item in p.Items)
        {
            var v = item.Evaluate(this);
            sb.Append(v.IsString ? v.Text : Format.Format(v.Number));
        }
        if (!p.NoNewLine) sb.Append(Environment.NewLine);
        Write(sb.ToString());
    }

    /// <summary>Writes to the current destination (OUTPUT): the screen or the output file.</summary>
    public void Write(string text)
    {
        if (_file is not null) { _file.Write(text); return; }
        _screen.Append(text);
        if (_stream) Session.Output?.Invoke(text);
    }

    /// <summary>REWIND: "erases the last line printed by the PRINT command, up to the previous
    /// end of line" (screen output only in this version).</summary>
    public void Rewind()
    {
        if (_file is not null) throw new ZplRuntimeException("REWIND on output sent to a file (OUTPUT filename) is not bound in this version; it erases screen output only.");
        string s = _screen.ToString();
        int end = s.Length;
        if (end > 0 && s[end - 1] == '\n') { end--; if (end > 0 && s[end - 1] == '\r') end--; }
        int from = end == 0 ? 0 : s.LastIndexOf('\n', end - 1) + 1;
        _screen.Length = from;
        if (_stream) Session.ScreenReplaced?.Invoke(_screen.ToString());
    }

    public void OutputToFile(string path, bool append)
    {
        CloseOutputFile();
        // "when sending PRINT commands to an output file, the file will be in Unicode format" (help, OUTPUT).
        _file = new StreamWriter(path, append, Encoding.Unicode);
    }

    public void OutputToScreen() => CloseOutputFile();

    public void CloseOutputFile()
    {
        _file?.Dispose();
        _file = null;
    }

    /// <summary>The text of a child macro's screen output, copied in when it returns (help,
    /// Calling a Macro from within a Macro).</summary>
    public void AppendChildOutput(string text) { if (text.Length > 0) Write(text); }

    internal static string Num(double v) => v.ToString("G10", CultureInfo.InvariantCulture);
}

/// <summary>A DECLAREd array of 1 to 4 dimensions, indices from 1.</summary>
internal sealed class ZplArray
{
    private readonly double[] _data;

    public ZplArray(int[] sizes, bool isInteger)
    {
        Sizes = sizes;
        IsInteger = isInteger;
        long n = 1;
        foreach (int s in sizes) n *= s;
        _data = new double[n];
    }

    public int[] Sizes { get; }
    public bool IsInteger { get; }

    private int Offset(int[] idx)
    {
        int o = 0;
        for (int k = 0; k < idx.Length; k++) o = o * Sizes[k] + (idx[k] - 1);
        return o;
    }

    public double Get(int[] idx) => _data[Offset(idx)];

    /// <summary>An INTEGER array keeps the value converted to an integer (truncated toward zero,
    /// as C converts; the help does not say).</summary>
    public void Set(int[] idx, double value) => _data[Offset(idx)] = IsInteger ? ZplFormatSpec.ToInteger(value) : value;
}

/// <summary>A text file opened by OPEN and read by READ, READNEXT, READSTRING and READSKIP.</summary>
internal sealed class ZplReader : IDisposable
{
    private readonly StreamReader _reader;

    public ZplReader(string path) { Path = path; _reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true); }

    public string Path { get; }

    /// <summary>EOFF(): set when a read found the end of the file.</summary>
    public bool EndOfFile { get; private set; }

    public string? ReadLine()
    {
        var line = _reader.ReadLine();
        if (line is null) EndOfFile = true;
        return line;
    }

    /// <summary>The next whitespace-delimited field, across line ends; null at the end.</summary>
    public string? ReadField()
    {
        int c;
        while ((c = _reader.Peek()) >= 0 && char.IsWhiteSpace((char)c)) _reader.Read();
        if (c < 0) { EndOfFile = true; return null; }
        var sb = new StringBuilder();
        while ((c = _reader.Peek()) >= 0 && !char.IsWhiteSpace((char)c)) sb.Append((char)_reader.Read());
        return sb.ToString();
    }

    public void Skip(int count)
    {
        if (count < 0) { ReadLine(); return; }
        for (int i = 0; i < count; i++) if (_reader.Read() < 0) { EndOfFile = true; return; }
    }

    public void Dispose() => _reader.Dispose();
}
