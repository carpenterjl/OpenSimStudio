using System.Globalization;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: the keywords and functions of the language core: control of the run, text output and
// files, the call buffers, vectors, random numbers, the timer, and the math and string functions.
// Each is as its OpticStudio 2025 R2 help page defines it (pages saved under
// plans/sources/optics/op-99-help/, named after the keyword).

public static partial class ZplLibrary
{
    /// <summary>CALLMACRO nesting this program allows ("there is no hard limit as to how many
    /// macros may be called or nested", Calling a Macro from within a Macro; a limit keeps a macro
    /// that calls itself from exhausting the stack).</summary>
    internal const int MaxCallDepth = 64;

    private static void RegisterCore()
    {
        // ---- Calling macros (CALLMACRO, CALLSETDBL, CALLSETSTR; Calling a Macro from within a Macro).
        Kw("CALLMACRO", "Runs a macro of the macro folder as a child; its screen output is copied into the caller's when it returns. The call buffers, lens, vectors and OPTRETURN values are shared.", (m, a) =>
        {
            a.Exactly(1, "CALLMACRO");
            string name = a.FileName(m, 0, "CALLMACRO's macro name");
            string path = ZplSession.Resolve(name, m.Session.MacroFolder, "macro folder");
            if (!File.Exists(path)) throw new ZplRuntimeException($"CALLMACRO: the macro '{path}' does not exist.");
            if (m.Session.Depth >= MaxCallDepth) throw new ZplRuntimeException($"CALLMACRO {name}: macros nested more than {MaxCallDepth} deep (a macro calling itself?).");
            var program = ZplProgram.Parse(File.ReadAllText(path), Path.GetFileName(path));
            var child = new ZplInterpreter(program, m.Session, stream: false);
            m.Session.Depth++;
            try { child.Run(); }
            finally { m.Session.Depth--; }
            m.AppendChildOutput(child.ScreenText);
        });
        Kw("CALLSETDBL", "Sets numeric call buffer entry index (0 to 50).", (m, a) =>
        {
            a.Exactly(2, "CALLSETDBL");
            m.Session.CallNumbers[BufferIndex(a.Int(m, 0, "CALLSETDBL index"), "CALLSETDBL")] = a.Num(m, 1, "CALLSETDBL value");
        });
        Kw("CALLSETSTR", "Sets string call buffer entry index (0 to 50).", (m, a) =>
        {
            a.Exactly(2, "CALLSETSTR");
            m.Session.CallStrings[BufferIndex(a.Int(m, 0, "CALLSETSTR index"), "CALLSETSTR")] = a.Str(m, 1, "CALLSETSTR text");
        });
        Fn("CALD", 1, 1, "Numeric call buffer entry (0 to 50).", (m, v) => N(m.Session.CallNumbers[BufferIndex(Int(v, 0, "the index"), "CALD")]));
        Fn("$CALLSTR", 1, 1, "String call buffer entry (0 to 50).", (m, v) => S(m.Session.CallStrings[BufferIndex(Int(v, 0, "the index"), "$CALLSTR")]));

        // ---- Values returned to the optimizer and to solves.
        Kw("OPTRETURN", "Stores a value in data field 0 to 50 for the ZPLM operand.", (m, a) =>
        {
            a.Exactly(2, "OPTRETURN");
            int field = a.Int(m, 0, "OPTRETURN data field");
            if (field < 0 || field > 50) throw new ZplRuntimeException($"OPTRETURN: the data field must be from 0 to 50, not {field}.");
            m.Session.OptReturn[field] = a.Num(m, 1, "OPTRETURN result");
        });
        Kw("SOLVERETURN", "Returns the value of a macro solve; only the last call counts.", (m, a) =>
        {
            a.Exactly(1, "SOLVERETURN");
            m.Session.SolveReturn = a.Num(m, 0, "SOLVERETURN value");
        });
        Kw("SOLVEBEFORESTOP", "Accepted and does nothing: this program does not refuse macro solves before the stop.", (m, a) => a.Max(0, "SOLVEBEFORESTOP"));
        Fn0("PVHX", "ZPLM's Data1 (0 outside an operand).", m => N(m.Session.PassValues[0]));
        Fn0("PVHY", "ZPLM's Data2 (0 outside an operand).", m => N(m.Session.PassValues[1]));
        Fn0("PVPX", "ZPLM's Data3 (0 outside an operand).", m => N(m.Session.PassValues[2]));
        Fn0("PVPY", "ZPLM's Data4 (0 outside an operand).", m => N(m.Session.PassValues[3]));
        Fn0("PVEX", "ZPLM's Data5 (0 outside an operand).", m => N(m.Session.PassValues[4]));
        Fn0("PVEY", "ZPLM's Data6 (0 outside an operand).", m => N(m.Session.PassValues[5]));
        Fn("SOSO", 0, 1, "Code 0: the surface of the macro solve being computed; fails outside a solve. Code 1 (the NSC object) is not bound: solves on non-sequential object data belong to the Non-Sequential Component Editor, plan item OP-71.", (m, v) =>
        {
            int code = v.Length == 0 ? 0 : Int(v, 0, "the code");
            if (code != 0) throw new ZplRuntimeException(code == 1 ? "code 1 (the non-sequential object of a solve) is not bound: solves on object data belong to the Non-Sequential Component Editor, plan item OP-71 (code 0, the solve's surface, is bound)." : $"code {code} is not bound in this version (code 0, the solve's surface, is).");
            return N(m.Session.SolveSurface ?? throw new ZplRuntimeException("the macro is not running as a solve, so there is no solve surface."));
        });

        // ---- Text output (OUTPUT, PRINT and FORMAT are the language's own; REWIND, CLOSEWINDOW).
        Kw("OUTPUT", "OUTPUT SCREEN, or OUTPUT filename [, APPEND]; the file is written in Unicode (UTF-16) as the help says; a name without a folder goes to the macro folder.", (m, a) =>
        {
            if (a.Count == 1 && a.Word(0) == "SCREEN") { m.OutputToScreen(); return; }
            if (a.Count is < 1 or > 2) throw new ZplRuntimeException("OUTPUT takes SCREEN, or a file name and optionally APPEND.");
            bool append = false;
            if (a.Count == 2)
            {
                if (a.Word(1) != "APPEND") throw new ZplRuntimeException($"OUTPUT: the second argument must be APPEND, not '{a.Text(1)}'.");
                append = true;
            }
            m.OutputToFile(ZplSession.Resolve(a.FileName(m, 0, "OUTPUT's file name"), m.Session.MacroFolder, "macro folder"), append);
        });
        Kw("REWIND", "Erases the last line printed to the screen; on output sent to a file it fails (not bound there).", (m, a) => { a.Max(0, "REWIND"); m.Rewind(); });
        Kw("CLOSEWINDOW", "Without an argument: sets quiet mode (the result says so). With a window number it fails: there are no numbered analysis windows.", (m, a) =>
        {
            if (a.Count > 0) throw new ZplRuntimeException("CLOSEWINDOW n (closing analysis window n) is not bound: macros here open no numbered analysis windows.");
            m.Quiet = true;
        });
        Kw("SUSPENDUPDATES", "Accepted; counted. Editors are not open during a run, so there is nothing to suspend.", (m, a) => { a.Max(0, "SUSPENDUPDATES"); m.Session.SuspendCount++; });
        Kw("RESUMEUPDATES", "Accepted; counted (see SUSPENDUPDATES).", (m, a) => { a.Max(0, "RESUMEUPDATES"); m.Session.SuspendCount = Math.Max(0, m.Session.SuspendCount - 1); });

        // ---- The person running the macro.
        Kw("INPUT", "Asks the host (the macro window) for a number or a string; fails when the run has no host (the command line).", (m, a) =>
        {
            if (a.Count is < 1 or > 2) throw new ZplRuntimeException("INPUT takes an optional prompt string and a variable.");
            string prompt = a.Count == 2 ? a.Str(m, 0, "INPUT's prompt") : "?";
            string? name = a.Word(a.Count - 1);
            if (name is null) throw new ZplRuntimeException($"INPUT: '{a.Text(a.Count - 1)}' is not a variable name.");
            ZplProgram.CheckName(name);
            var host = m.Session.Host ?? throw new ZplRuntimeException("INPUT needs someone to type a value, and this run has no host (it runs from the command line or as an operand or solve).");
            string? typed = host.Input(prompt) ?? throw new OperationCanceledException("INPUT was cancelled.");
            if (name.EndsWith('$')) m.SetString(name, typed);
            else if (double.TryParse(typed.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) m.SetNumber(name, x);
            else throw new ZplRuntimeException($"INPUT: '{typed}' is not a number (for {name}).");
        });
        Kw("PAUSE", "PAUSE [message] asks the host to show the message and wait (fails without a host); PAUSE TIME, ms sleeps (at most 1,000,000 ms); PAUSE THREADS returns at once (no analysis windows compute during a run).", (m, a) =>
        {
            if (a.Word(0) == "TIME")
            {
                a.Exactly(2, "PAUSE TIME");
                double ms = a.Num(m, 1, "PAUSE TIME's milliseconds");
                if (!(ms >= 0) || ms > 1_000_000) throw new ZplRuntimeException($"PAUSE TIME: the interval must be from 0 to 1,000,000 milliseconds, not {F(ms)}.");
                m.Session.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(ms));
                m.Session.CancellationToken.ThrowIfCancellationRequested();
                return;
            }
            if (a.Count == 1 && a.Word(0) == "THREADS") return;
            a.Max(1, "PAUSE");
            string message = "";
            if (a.Count == 1)
            {
                var v = a.Value(m, 0, "PAUSE's message");
                message = v.IsString ? v.Text! : m.Format.Format(v.Number);
            }
            var host = m.Session.Host ?? throw new ZplRuntimeException("PAUSE waits for someone to press OK, and this run has no host (use PAUSE TIME, ms to wait without one).");
            host.Pause(message);
        });

        // ---- Reading files (OPEN, CLOSE, READ, READNEXT, READSTRING, READSKIP, EOFF).
        Kw("OPEN", "Opens a text file for READ; a name without a folder is taken in the macro folder.", (m, a) =>
        {
            a.Exactly(1, "OPEN");
            string path = ZplSession.Resolve(a.FileName(m, 0, "OPEN's file name"), m.Session.MacroFolder, "macro folder");
            if (!File.Exists(path)) throw new ZplRuntimeException($"OPEN: the file '{path}' does not exist.");
            m.Reader?.Dispose();
            m.Reader = new ZplReader(path);
        });
        Kw("CLOSE", "Closes the file opened by OPEN.", (m, a) =>
        {
            a.Max(0, "CLOSE");
            m.Reader?.Dispose();
            m.Reader = null;
        });
        Kw("READ", "Reads one line into numeric variables, one space-delimited field each (period decimal separator). At the end of the file EOFF() becomes 1 and the variables are left as they were.", (m, a) =>
        {
            var reader = OpenReader(m, "READ");
            var names = ReadNames(a, "READ");
            string? line = reader.ReadLine();
            if (line is null) return;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < names.Count)
                throw new ZplRuntimeException($"READ: the line \"{line}\" of {reader.Path} has {fields.Length} field{(fields.Length == 1 ? "" : "s")}; {names.Count} variables were listed.");
            for (int k = 0; k < names.Count; k++) m.SetNumber(names[k], ParseField(fields[k], reader.Path, "READ"));
        });
        Kw("READNEXT", "Reads as many space-delimited numbers as variables listed, across line ends. At the end of the file EOFF() becomes 1 and the remaining variables are left as they were.", (m, a) =>
        {
            var reader = OpenReader(m, "READNEXT");
            foreach (var name in ReadNames(a, "READNEXT"))
            {
                string? field = reader.ReadField();
                if (field is null) return;
                m.SetNumber(name, ParseField(field, reader.Path, "READNEXT"));
            }
        });
        Kw("READSTRING", "Reads a whole line into a string variable (at most 359 characters, as the help says); at the end of the file EOFF() becomes 1 and the variable is set to the empty string.", (m, a) =>
        {
            var reader = OpenReader(m, "READSTRING");
            a.Exactly(1, "READSTRING");
            string? name = a.Word(0);
            if (name is null || !name.EndsWith('$')) throw new ZplRuntimeException($"READSTRING needs a string variable name (ending in $), not '{a.Text(0)}'.");
            ZplProgram.CheckName(name);
            string line = reader.ReadLine() ?? "";
            if (line.Length > 359) throw new ZplRuntimeException($"READSTRING: a line of {reader.Path} has {line.Length} characters; at most 359 can be read into a string (help, READSTRING).");
            m.SetString(name, line);
        });
        Kw("READSKIP", "Skips n characters, or to the end of the line when n is negative.", (m, a) =>
        {
            var reader = OpenReader(m, "READSKIP");
            a.Exactly(1, "READSKIP");
            reader.Skip(a.Int(m, 0, "READSKIP's count"));
        });
        Fn0("EOFF", "1 when a read reached the end of the file opened by OPEN, else 0.", m => N((m.Reader ?? throw new ZplRuntimeException("no file is open (OPEN).")).EndOfFile ? 1 : 0));

        // ---- Files.
        Kw("DELETEFILE", "Deletes a file (a name without a folder is in the macro folder); a file that does not exist is an error.", (m, a) =>
        {
            a.Exactly(1, "DELETEFILE");
            string path = ZplSession.Resolve(a.FileName(m, 0, "DELETEFILE's file name"), m.Session.MacroFolder, "macro folder");
            if (!File.Exists(path)) throw new ZplRuntimeException($"DELETEFILE: the file '{path}' does not exist.");
            File.Delete(path);
        });
        Kw("COPYFILE", "Copies a file, overwriting the target.", (m, a) =>
        {
            a.Exactly(2, "COPYFILE");
            string from = ZplSession.Resolve(a.FileName(m, 0, "COPYFILE's source"), m.Session.MacroFolder, "macro folder");
            string to = ZplSession.Resolve(a.FileName(m, 1, "COPYFILE's new file"), m.Session.MacroFolder, "macro folder");
            if (!File.Exists(from)) throw new ZplRuntimeException($"COPYFILE: the file '{from}' does not exist.");
            File.Copy(from, to, overwrite: true);
        });
        Kw("RENAMEFILE", "Renames a file; an existing target is an error.", (m, a) =>
        {
            a.Exactly(2, "RENAMEFILE");
            string from = ZplSession.Resolve(a.FileName(m, 0, "RENAMEFILE's old name"), m.Session.MacroFolder, "macro folder");
            string to = ZplSession.Resolve(a.FileName(m, 1, "RENAMEFILE's new name"), m.Session.MacroFolder, "macro folder");
            if (!File.Exists(from)) throw new ZplRuntimeException($"RENAMEFILE: the file '{from}' does not exist.");
            File.Move(from, to);
        });
        Kw("MAKEFOLDER", "Creates a folder; an existing folder is left alone.", (m, a) =>
        {
            a.Exactly(1, "MAKEFOLDER");
            Directory.CreateDirectory(ZplSession.Resolve(a.FileName(m, 0, "MAKEFOLDER's path"), m.Session.MacroFolder, "macro folder"));
        });
        Kw("FINDFILE", "Puts the next file name (without its folder) matching the filter into the string variable, in ordinal order; the empty string when there are no more. A new filter starts again.", (m, a) =>
        {
            a.Exactly(2, "FINDFILE");
            string? name = a.Word(0);
            if (name is null || !name.EndsWith('$')) throw new ZplRuntimeException($"FINDFILE needs a string variable name (ending in $) first, not '{a.Text(0)}'.");
            ZplProgram.CheckName(name);
            string filter = a.Str(m, 1, "FINDFILE's filter");
            var s = m.Session;
            if (!string.Equals(filter, s.FindFilter, StringComparison.Ordinal) || s.FindFiles is null)
            {
                string full = ZplSession.Resolve(filter, s.MacroFolder, "macro folder");
                string folder = Path.GetDirectoryName(full) ?? ".";
                string pattern = Path.GetFileName(full);
                if (!Directory.Exists(folder)) throw new ZplRuntimeException($"FINDFILE: the folder '{folder}' does not exist.");
                var files = Directory.GetFiles(folder, pattern).Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal);
                s.FindFiles = new Queue<string>(files);
                s.FindFilter = filter;
            }
            m.SetString(name, s.FindFiles.Count > 0 ? s.FindFiles.Dequeue() : "");
        });

        // ---- Vectors (SETVECSIZE, VEC1..4).
        Kw("SETVECSIZE", "Sets the size of VEC1..4 (1 to 18,000,000; the data is cleared).", (m, a) =>
        {
            a.Exactly(1, "SETVECSIZE");
            int n = a.Int(m, 0, "SETVECSIZE's size");
            if (n < 1 || n > 18_000_000) throw new ZplRuntimeException($"SETVECSIZE: the size must be from 1 to 18,000,000, not {n}.");
            m.Session.ResizeVectors(n);
        });
        for (int k = 1; k <= 4; k++)
        {
            int vec = k - 1;
            string name = "VEC" + k.ToString(CultureInfo.InvariantCulture);
            Kw(name, $"Sets element subscript (rounded down; 0 to the vector size) of {name}.", (m, a) =>
            {
                a.Exactly(2, name);
                int i = VectorIndex(m, a.Num(m, 0, $"{name} subscript"), name);
                m.Session.Vectors[vec][i] = a.Num(m, 1, $"{name} value");
            });
            Fn(name, 1, 1, $"Element of {name}.", (m, v) => N(m.Session.Vectors[vec][VectorIndex(m, Num(v, 0, "the subscript"), name)]));
        }

        // ---- Random numbers and the timer.
        Kw("RANDOMIZE", "Seeds RAND and GAUS: the same seed repeats the series; 0 (or no seed) seeds from the clock.", (m, a) =>
        {
            a.Max(1, "RANDOMIZE");
            double seed = a.Num(m, 0, "RANDOMIZE's seed", 0);
            m.Session.Random = seed == 0 ? new Random() : new Random(SeedOf(seed));
        });
        Fn("RAND", 1, 1, "Uniform random number from 0 to x (System.Random).", (m, v) => N(m.Session.Random.NextDouble() * Num(v, 0, "x")));
        Fn("GAUS", 1, 1, "Gaussian random number, zero mean, standard deviation x (Box–Muller on RAND's generator).", (m, v) =>
        {
            double sd = Num(v, 0, "the standard deviation");
            double u1 = 1.0 - m.Session.Random.NextDouble(), u2 = m.Session.Random.NextDouble();
            return N(sd * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        });
        Kw("TIMER", "Resets the clock ETIM() reads.", (m, a) => { a.Max(0, "TIMER"); m.Session.Timer.Restart(); });
        Fn0("ETIM", "Seconds since the last TIMER (or the start of the run).", m => N(m.Session.Timer.Elapsed.TotalSeconds));

        // ---- Numeric functions (Numeric Functions).
        Fn("ABSO", 1, 1, "Absolute value.", (m, v) => N(Math.Abs(Num(v, 0, "x"))));
        Fn("ACOS", 1, 1, "Arc cosine in radians; |x| > 1 fails.", (m, v) => N(Math.Acos(Domain(Num(v, 0, "x"), -1, 1, "ACOS"))));
        Fn("ASIN", 1, 1, "Arc sine in radians; |x| > 1 fails.", (m, v) => N(Math.Asin(Domain(Num(v, 0, "x"), -1, 1, "ASIN"))));
        Fn("ATAN", 1, 1, "Arc tangent in radians.", (m, v) => N(Math.Atan(Num(v, 0, "x"))));
        Fn("COSI", 1, 1, "Cosine of radians.", (m, v) => N(Math.Cos(Num(v, 0, "x"))));
        Fn("SINE", 1, 1, "Sine of radians.", (m, v) => N(Math.Sin(Num(v, 0, "x"))));
        Fn("TANG", 1, 1, "Tangent of radians.", (m, v) => N(Math.Tan(Num(v, 0, "x"))));
        Fn("EXPE", 1, 1, "e to the power x.", (m, v) => N(Math.Exp(Num(v, 0, "x"))));
        Fn("EXPT", 1, 1, "10 to the power x.", (m, v) => N(Math.Pow(10, Num(v, 0, "x"))));
        Fn("LOGE", 1, 1, "Natural logarithm; x ≤ 0 fails.", (m, v) => N(Math.Log(Positive(Num(v, 0, "x"), "LOGE"))));
        Fn("LOGT", 1, 1, "Base-10 logarithm; x ≤ 0 fails.", (m, v) => N(Math.Log10(Positive(Num(v, 0, "x"), "LOGT"))));
        Fn("SQRT", 1, 1, "Square root; x < 0 fails.", (m, v) =>
        {
            double x = Num(v, 0, "x");
            if (x < 0) throw new ZplRuntimeException($"the square root of {F(x)} is not real.");
            return N(Math.Sqrt(x));
        });
        Fn("INTE", 1, 1, "Largest integer not greater than x.", (m, v) => N(Math.Floor(Num(v, 0, "x"))));
        Fn("SIGN", 1, 1, "−1, 0 or +1.", (m, v) => N(Math.Sign(Num(v, 0, "x"))));
        Fn("MAGN", 2, 2, "√(x² + y²).", (m, v) => N(Math.Sqrt(Num(v, 0, "x") * Num(v, 0, "x") + Num(v, 1, "y") * Num(v, 1, "y"))));
        Fn("POWR", 2, 2, "|x| to the power y.", (m, v) => N(Math.Pow(Math.Abs(Num(v, 0, "x")), Num(v, 1, "y"))));
        Fn("SLEN", 1, 1, "Number of characters of a string.", (m, v) => N(Str(v, 0, "the argument").Length));
        Fn("SVAL", 1, 1, "Number of a string, period decimal separator; text that is not a number fails.", (m, v) => N(ParseNumber(Str(v, 0, "the argument"), CultureInfo.InvariantCulture, "SVAL")));
        Fn("LVAL", 1, 1, "Number of a string in the current culture's decimal separator; text that is not a number fails.", (m, v) => N(ParseNumber(Str(v, 0, "the argument"), CultureInfo.CurrentCulture, "LVAL")));
        Fn("SCOM", 2, 2, "−1, 0 or +1 as A$ is less than, equal to or greater than B$ (ordinal comparison).", (m, v) => N(Math.Sign(string.CompareOrdinal(Str(v, 0, "A$"), Str(v, 1, "B$")))));

        // ---- String functions (String Functions).
        Fn0("$BUFFER", "The string buffer that OPER, SPRO, SYPR … fill.", m => S(m.Session.Buffer));
        Fn0("$DATE", "The date and time, yyyy-MM-dd HH:mm:ss (the help's format comes from OpticStudio's preferences).", m => S(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        Fn0("$FILENAME", "The lens file's name without its folder (fails when the lens has no file).", m => S(Path.GetFileName(LensFile(m))));
        Fn0("$FILEPATH", "The lens file's full path (fails when the lens has no file).", m => S(Path.GetFullPath(LensFile(m))));
        Fn0("$PATHNAME", "The folder of the lens file (fails when the lens has no file).", m => S(Path.GetDirectoryName(Path.GetFullPath(LensFile(m))) ?? ""));
        Fn0("$MACROPATH", "The macro folder (fails when none is set).", m => S(m.Session.MacroFolder ?? throw new ZplRuntimeException("no macro folder is set.")));
        Fn("$GETSTRING", 2, 2, "The n-th space-delimited word of A$; the empty string past the last.", (m, v) => S(Word(Str(v, 0, "A$"), Int(v, 1, "n"), ' ')));
        Fn("$GETSTRINGC", 2, 2, "The n-th comma-delimited part of A$; the empty string past the last.", (m, v) => S(Word(Str(v, 0, "A$"), Int(v, 1, "n"), ',')));
        Fn("$LEFTSTRING", 2, 2, "The leftmost n characters, padded with blanks on the right.", (m, v) =>
        {
            string s = Str(v, 0, "A$");
            int n = Count(Int(v, 1, "n"));
            return S(s.Length >= n ? s[..n] : s.PadRight(n));
        });
        Fn("$RIGHTSTRING", 2, 2, "The rightmost n characters, padded with blanks on the left (the help does not say which side).", (m, v) =>
        {
            string s = Str(v, 0, "A$");
            int n = Count(Int(v, 1, "n"));
            return S(s.Length >= n ? s[^n..] : s.PadLeft(n));
        });
        Fn0("$NEWLINE", "A newline. The help says \"the tab character (\\n)\"; \\n is the newline, which this returns.", m => S("\n"));
        Fn0("$QUOTE", "The double quote character.", m => S("\""));
        Fn0("$TAB", "The tab character.", m => S("\t"));
        Fn("$STR", 1, 1, "The number formatted by the current FORMAT.", (m, v) => S(m.Format.Format(Num(v, 0, "the expression"))));
        Fn0("$TEMPFILENAME", "A new empty temporary file's full path (not deleted automatically; use DELETEFILE).", m => S(Path.GetTempFileName()));
        Fn("$GETARG", 1, 1, "A command-line argument by name (case ignored); \"IsAutomated\" is True from the command line, else False. An argument not given fails.", (m, v) =>
        {
            string name = Str(v, 0, "the argument name");
            if (string.Equals(name, "IsAutomated", StringComparison.OrdinalIgnoreCase)) return S(m.Session.Automated ? "True" : "False");
            if (m.Session.Arguments is { } args)
                foreach (var kv in args)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return S(kv.Value);
            throw new ZplRuntimeException($"no argument '{name}' was given (on the command line: --arg {name}=value).");
        });

        // ---- Not bound, with the reason.
        NotBound("BEEP", "Not bound: no sound is made by a library run.");
        NotBound("BROWSE", "Not bound: needs a file dialog.");
        NotBound("COMMAND", "Not bound deliberately: it would run a shell command.");
        NotBound("READ_LOCALE", "Not bound: READ (period decimal separator) is.");
        NotBound("READNEXT_LOCALE", "Not bound: READNEXT (period decimal separator) is.");
        NotBound("PRINTFILE", "Not bound: no printer is driven.");
        NotBound("PRINTWINDOW", "Not bound: no printer is driven.");
        NotBound("SHOWFILE", "Not bound: no file viewer is opened.");
        NotBound("CONVERTFILEFORMAT", "Not bound.");
        foreach (var g in new[] { "COLOR", "GDATE", "GLENSNAME", "GRAPHICS", "GTEXT", "GTEXTCENT", "GTITLE", "LINE", "PIXEL", "PLOT", "PLOT2D", "SETTEXTSIZE", "EXPORTBMP", "EXPORTJPG", "SHOWBITMAP", "IMAGECOMBINE", "IMAGEEXTRACT", "CONVERTIMAGETOGRID" })
            NotBound(g, "Not bound: macro graphics and image files are not built.");
        foreach (var w in new[] { "LOCKWINDOW", "UNLOCKWINDOW", "OPENANALYSISWINDOW", "SAVEWINDOW", "MODIFYSETTINGS" })
            NotBound(w, "Not bound: macros here do not drive analysis windows or their settings files.");
        FunctionNotBound("$DATAPATH", "Not bound: the program has no single data folder.");
        FunctionNotBound("$COATINGPATH", "Not bound: the program has no coating file folder.");
        FunctionNotBound("$EXTENSIONPATH", "Not bound.");
        FunctionNotBound("$GLASSPATH", "Not bound.");
        FunctionNotBound("$OBJECTPATH", "Not bound: there is no objects folder; object files (polygon objects, CAD parts) are plan items OP-74 and OP-75.");
        FunctionNotBound("$PROGRAMPATH", "Not bound.");
        foreach (var g in new[] { "ASPR", "XMIN", "XMAX", "YMIN", "YMAX", "PIXX", "PIXY" })
            FunctionNotBound(g, "Not bound: macro graphics are not built.");
        foreach (var w in new[] { "GETT", "GETL", "WINL", "WINN" })
            FunctionNotBound(w, "Not bound: macros here open no analysis windows (GETTEXTFILE writes an analysis's text to a file instead).");
        FunctionNotBound("VERS", "Not bound: OpticStudio's version number has no meaning here.");
    }

    // ---------------------------------------------------------------- Core helpers

    private static int BufferIndex(int i, string who) => i is >= 0 and <= 50 ? i
        : throw new ZplRuntimeException($"{who}: the buffer index must be from 0 to 50, not {i}.");

    private static int VectorIndex(ZplInterpreter m, double subscript, string who)
    {
        int i = (int)Math.Floor(subscript);
        if (i < 0 || i > m.Session.VectorSize)
            throw new ZplRuntimeException($"{who}: the subscript {F(subscript)} must be from 0 to the vector size {m.Session.VectorSize} (SETVECSIZE changes it).");
        return i;
    }

    private static int SeedOf(double seed)
    {
        if (seed == Math.Floor(seed) && Math.Abs(seed) <= int.MaxValue) return (int)seed;
        long bits = BitConverter.DoubleToInt64Bits(seed);
        return unchecked((int)bits ^ (int)(bits >> 32));
    }

    private static double Domain(double x, double lo, double hi, string who) => x >= lo && x <= hi ? x
        : throw new ZplRuntimeException($"{F(x)} is outside {F(lo)} to {F(hi)}.");

    private static double Positive(double x, string who) => x > 0 ? x
        : throw new ZplRuntimeException($"the logarithm of {F(x)} is not defined (it must be positive).");

    private static int Count(int n) => n >= 0 ? n : throw new ZplRuntimeException($"the character count must not be negative ({n}).");

    private static double ParseNumber(string s, CultureInfo culture, string who) =>
        double.TryParse(s.Trim(), NumberStyles.Float, culture, out double x) ? x
            : throw new ZplRuntimeException($"\"{s}\" is not a number.");

    private static string Word(string s, int n, char separator)
    {
        var parts = separator == ' '
            ? s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            : s.Split(separator);
        return n >= 1 && n <= parts.Length ? (separator == ' ' ? parts[n - 1] : parts[n - 1].Trim()) : "";
    }

    private static string LensFile(ZplInterpreter m) => m.Session.LensPath
        ?? throw new ZplRuntimeException("the lens has no file (it was not loaded from or saved to one).");

    private static ZplReader OpenReader(ZplInterpreter m, string who) =>
        m.Reader ?? throw new ZplRuntimeException($"{who}: no file is open; open one with OPEN first.");

    private static List<string> ReadNames(ZplArgs a, string who)
    {
        if (a.Count == 0) throw new ZplRuntimeException($"{who} needs at least one variable.");
        if (a.Count > 199) throw new ZplRuntimeException($"{who}: at most 199 variables (help, READ).");
        var names = new List<string>();
        for (int i = 0; i < a.Count; i++)
        {
            string? name = a.Word(i);
            if (name is null || name.EndsWith('$'))
                throw new ZplRuntimeException($"{who}: '{a.Text(i)}' is not a numeric variable name (array elements are not read; read into a variable and assign it, help, READ).");
            ZplProgram.CheckName(name);
            names.Add(name);
        }
        return names;
    }

    private static double ParseField(string field, string path, string who) =>
        double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x
            : throw new ZplRuntimeException($"{who}: '{field}' in {path} is not a number.");
}
