using System.Globalization;
using System.Text;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: the keyword and function library and its published table (docs/Optics-Conventions.md
// §56, "What is bound"; docs/Optics-User-Guide.md, "ZPL macros").

/// <summary>What a name of OpticStudio's ZPL is.</summary>
public enum ZplNameKind { Keyword, NumericFunction, StringFunction }

/// <summary>One row of the published table: a keyword or function of OpticStudio's ZPL (help,
/// KEYWORDS (about the zpl), Numeric Functions, String Functions), whether this version binds it,
/// and what the binding does or why it is not bound.</summary>
public sealed record ZplLibraryEntry(string Name, ZplNameKind Kind, bool Bound, string Note);

internal delegate void ZplKeywordHandler(ZplInterpreter m, ZplArgs a);
internal delegate ZplValue ZplFunctionHandler(ZplInterpreter m, ZplValue[] v);

internal sealed record ZplFunctionDefinition(int Min, int Max, ZplFunctionHandler Handler);

/// <summary>
/// The keywords and functions of OpticStudio's ZPL: every name the help lists, each bound to this
/// program's engines or listed with the reason it is not (<see cref="Table"/>). A macro that
/// reaches a name that is not bound fails with <see cref="ZplNotBoundException"/>, naming it and
/// the line.
/// </summary>
public static partial class ZplLibrary
{
    private static readonly Dictionary<string, ZplKeywordHandler> Keywords = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> KeywordNotes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ZplFunctionDefinition> Functions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> FunctionNotes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> NotBoundNotes = new(StringComparer.Ordinal);

    // ---------------------------------------------------------------- The help's names

    /// <summary>The keywords of the help's KEYWORDS (about the zpl) page and its language pages
    /// (FOR, NEXT, IF, THEN, ELSE, ENDIF, GOTO, LABEL, GOSUB, SUB, RETURN, END, DECLARE, RELEASE,
    /// PRINT, FORMAT, REM), in the help's order.</summary>
    private static readonly string[] HelpKeywords =
    {
        "APMN", "APMX", "APTP", "APXD", "APYD", "ATYP", "AVAL", "BEEP", "BROWSE", "CALLMACRO", "CALLSETDBL", "CALLSETSTR", "COAT",
        "COMPOSITEOFFAXISAPERTUREON", "COMPOSITEOFF", "COMPOSITEON", "CLOSE", "CLOSEWINDOW", "COLOR", "COMMAND", "COMMENT", "CONI",
        "CONVERTFILEFORMAT", "CONVERTIMAGETOGRID", "COPYFILE", "CURV", "DECLARE", "DEFAULTMERIT", "DELETE", "DELETECONFIG",
        "DELETEFILE", "DELETEMCO", "DELETEMFO", "DELETEOBJECT", "DELETETOL", "EDVA", "END", "EXPORTBMP", "EXPORTCAD", "EXPORTJPG",
        "FINDFILE", "FLDX", "FLDY", "FWGT", "FVDX", "FVDY", "FVCX", "FVCY", "FVAN", "FOR", "NEXT", "FORMAT", "FTYP", "GCRS", "GDATE",
        "GETDENCUSER1D", "GETEXTRADATA", "GETGLASSDATA", "GETLSF", "GETMTF", "GETMTFUSER1D", "GETNSCMTF", "GETPSF", "GETSYSTEMDATA",
        "GETTEXTFILE", "GETVARDATA", "GETZERNIKE", "GLAS", "GLASSTEMPLATE", "GLENSNAME", "GLOBALTOLOCAL", "GOSUB", "SUB", "RETURN",
        "GOTO", "GRAPHICS", "GTEXT", "GTEXTCENT", "GTITLE", "HAMMER", "IF", "THEN", "ELSE", "ENDIF", "IMA", "IMAGECOMBINE", "IMAGEEXTRACT",
        "IMASHOW", "IMASUM", "IMPORTEXTRADATA", "INPUT", "INSERT", "INSERTCONFIG", "INSERTMCO", "INSERTMFO", "INSERTOBJECT",
        "INSERTTOL", "LABEL", "LINE", "LOADARCHIVE", "LOADCATALOG", "LOADDETECTOR", "LOADLENS", "LOADMERIT", "LOADTOLERANCE",
        "LOCALTOGLOBAL", "LOCKWINDOW", "MAKEFACETLIST", "MAKEFOLDER", "MODIFYSETTINGS", "NSLT", "NSTR", "NSTR2", "NUMFIELD",
        "NUMWAVE", "OPEN", "OPENANALYSISWINDOW", "OPTIMIZE", "OPTRETURN", "OUTPUT", "PARM", "PARAXIAL", "PAUSE", "PIXEL", "PLOT",
        "PLOT2D", "POLDEFINE", "POLTRACE", "POP", "PRINT", "PRINTFILE", "PRINTWINDOW", "PWAV", "QUICKFOCUS", "QUICKSENSITIVITY",
        "RADI", "RANDOMIZE", "RAYTRACE", "RAYTRACEX", "READ", "READ_LOCALE", "READNEXT_LOCALE", "READNEXT", "READSKIP", "READSTRING",
        "RELEASE", "RELOADOBJECTS", "REM", "REMOVEVARIABLES", "RESUMEUPDATES", "RENAMEFILE", "REWIND", "SAVEARCHIVE", "SAVEDETECTOR",
        "SAVELENS", "SAVEMERIT", "SAVETOLERANCE", "SAVEWINDOW", "SCATTER", "SDIA", "SETAIM", "SETAIMDATA", "SETAPODIZATION",
        "SETCONFIG", "SETDETECTOR", "SETMCOPERAND", "SETNSCPARAMETER", "SETNSCPOSITION", "SETNSCPROPERTY", "SETOPERAND", "SETSTDD",
        "SETSURFACEPROPERTY", "SURP", "SETSYSTEMPROPERTY", "SYSP", "SETTEXTSIZE", "SETTITLE", "SETTOL", "SETUNITS", "SETVAR",
        "SETVECSIZE", "SETVIG", "SHOWBITMAP", "SHOWFILE", "SOLVEBEFORESTOP", "SOLVERETURN", "SOLVETYPE", "STOPSURF", "SURFTYPE",
        "SUSPENDUPDATES", "TELECENTRIC", "TESTPLATEFIT", "THIC", "TIMER", "TOLERANCE", "UNLOCKWINDOW", "UPDATE", "VEC1", "VEC2",
        "VEC3", "VEC4", "WAVL", "WWGT", "XDIFFIA", "ZBFCLR", "ZBFMULT", "ZBFPROPERTIES", "ZBFREAD", "ZBFRESAMPLE", "ZBFSHOW",
        "ZBFSUM", "ZBFTILT", "ZBFWRITE", "ZRDAPPEND", "ZRDFILTER", "ZRDPLAYBACK", "ZRDSAVERAYS", "ZRDSUM",
    };

    /// <summary>The help's keyword pages that say only "This command is obsolete" or point to
    /// SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function, with no syntax of their own.</summary>
    private static readonly HashSet<string> ObsoleteKeywords = new(StringComparer.Ordinal)
    {
        "APMN", "APMX", "APTP", "APXD", "APYD", "ATYP", "AVAL", "COAT", "COMMENT", "CONI", "CURV", "EDVA", "FLDX", "FLDY", "FWGT",
        "FVDX", "FVDY", "FVCX", "FVCY", "FVAN", "FTYP", "GCRS", "GLAS", "NUMFIELD", "NUMWAVE", "PARM", "RADI", "SDIA", "SETAPODIZATION",
        "SETSTDD", "SETTITLE", "SETUNITS", "SURFTYPE", "TELECENTRIC", "THIC", "WAVL", "WWGT",
    };

    /// <summary>The help's ZPL Function Listing (Numeric Functions), in its order.</summary>
    private static readonly string[] HelpNumericFunctions =
    {
        "ABSO", "ACOS", "APMN", "APMX", "APOI", "APXD", "APYD", "APTP", "ASIN", "ASPR", "ATAN", "ATYP", "AVAL", "CALD", "CHZN", "CONF",
        "CONI", "COSI", "CURV", "EDGE", "EOFF", "ETIM", "EXPE", "EXPT", "FICL", "FLDX", "FLDY", "FTAN", "FTYP", "FVAN", "FVCX", "FVCY",
        "FVDX", "FVDY", "FWGT", "GABB", "GAUS", "GETT", "GETL", "GIND", "GLCA", "GLCB", "GLCC", "GLCM", "GLCX", "GLCY", "GLCZ", "GNUM",
        "GPAR", "GRIN", "GTEM", "IMAE", "INDX", "INTE", "ISCS", "ISMS", "LOGE", "LOGT", "LOST", "LVAL", "MAGN", "MAXF", "MAXG", "MCON",
        "MCOP", "MCSD", "MFCN", "NCON", "NFLD", "NOBJ", "NPAR", "NPOS", "NPRO", "NSDC", "NSDD", "NSDE", "NSDP", "NSUR", "NWAV", "OBJC",
        "OCOD", "ONUM", "OPDC", "OPER", "OPEV", "OPEW", "OPTH", "PARM", "PIXX", "PIXY", "PMOD", "POWR", "PVEX", "PVEY", "PVHX", "PVHY",
        "PVPX", "PVPY", "PWAV", "RADI", "RAGL", "RAGM", "RAGN", "RAGX", "RAGY", "RAGZ", "RAND", "RANX", "RANY", "RANZ", "RAYE", "RAYL",
        "RAYM", "RAYN", "RAYO", "RAYT", "RAYV", "RAYX", "RAYY", "RAYZ", "RELI", "SAGG", "SCOM", "SDIA", "SIGN", "SINE", "SLEN", "SOLM",
        "SOLV", "SOSO", "SPRO", "SPRX", "SRCN", "SQRT", "STDD", "SURC", "SVAL", "SYPR", "TANG", "TMAS", "THIC", "TOLV", "UNIT", "VEC1",
        "VEC2", "VEC3", "VEC4", "VERS", "WAVL", "WINL", "WINN", "WWGT", "XMIN", "XMAX", "YMIN", "YMAX",
    };

    /// <summary>The help's string functions (String Functions), and $GETARG (Running Macros from
    /// the Command Line).</summary>
    private static readonly string[] HelpStringFunctions =
    {
        "$BUFFER", "$CALLSTR", "$COAT", "$COATINGPATH", "$COMMENT", "$DATAPATH", "$DATE", "$EXTENSIONPATH", "$FILENAME", "$FILEPATH",
        "$GETSTRING", "$GETSTRINGC", "$GLASS", "$GLASSCATALOG", "$GLASSPATH", "$LEFTSTRING", "$LENSNAME", "$MACROPATH", "$NEWLINE",
        "$NOTE", "$OBJECTPATH", "$PATHNAME", "$PROGRAMPATH", "$QUOTE", "$RIGHTSTRING", "$STR", "$TAB", "$TEMPFILENAME",
        "$TOLCOMMENT", "$TOLOPERAND", "$UNITS", "$GETARG",
    };

    private static readonly HashSet<string> KeywordSet = new(HelpKeywords, StringComparer.Ordinal);
    private static readonly HashSet<string> FunctionSet = new(HelpNumericFunctions.Concat(HelpStringFunctions), StringComparer.Ordinal);

    static ZplLibrary()
    {
        RegisterCore();
        RegisterLens();
    }

    // ---------------------------------------------------------------- Queries

    internal static bool IsKeyword(string upper) => KeywordSet.Contains(upper);

    internal static bool IsKnownFunction(string upper) => FunctionSet.Contains(upper);

    internal static string Canonical(string upper) => upper;

    /// <summary>True when the keyword runs in this version (the language's own keywords included).</summary>
    public static bool IsKeywordBound(string name) => Keywords.ContainsKey(name.ToUpperInvariant())
        || LanguageKeywords.Contains(name.ToUpperInvariant());

    /// <summary>True when the function runs in this version.</summary>
    public static bool IsFunctionBound(string name) => Functions.ContainsKey(name.ToUpperInvariant());

    /// <summary>The keywords the parser runs itself.</summary>
    private static readonly HashSet<string> LanguageKeywords = new(StringComparer.Ordinal)
    {
        "IF", "THEN", "ELSE", "ENDIF", "FOR", "NEXT", "GOTO", "LABEL", "GOSUB", "SUB", "RETURN", "END", "PRINT", "FORMAT", "REM",
        "DECLARE", "RELEASE",
    };

    /// <summary>
    /// The published table: every keyword and function the help lists, whether it is bound, and
    /// what the binding does or why it is not bound, in the help's order (keywords, numeric
    /// functions, string functions).
    /// </summary>
    public static IReadOnlyList<ZplLibraryEntry> Table()
    {
        var rows = new List<ZplLibraryEntry>();
        foreach (var k in HelpKeywords)
        {
            if (LanguageKeywords.Contains(k)) rows.Add(new(k, ZplNameKind.Keyword, true, "Language core (Conventions §56)."));
            else if (Keywords.ContainsKey(k)) rows.Add(new(k, ZplNameKind.Keyword, true, KeywordNotes[k]));
            else rows.Add(new(k, ZplNameKind.Keyword, false, KeywordReason(k) is { Length: > 0 } why ? why : "Not bound in this version."));
        }
        foreach (var f in HelpNumericFunctions)
            rows.Add(Functions.ContainsKey(f) ? new(f, ZplNameKind.NumericFunction, true, FunctionNotes[f]) : new(f, ZplNameKind.NumericFunction, false, FunctionReason(f) is { Length: > 0 } why ? why : "Not bound in this version."));
        foreach (var f in HelpStringFunctions)
            rows.Add(Functions.ContainsKey(f) ? new(f, ZplNameKind.StringFunction, true, FunctionNotes[f]) : new(f, ZplNameKind.StringFunction, false, FunctionReason(f) is { Length: > 0 } why ? why : "Not bound in this version."));
        return rows;
    }

    /// <summary>The table as Markdown (name, kind, bound, note), for the documentation.</summary>
    public static string TableMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Name | Kind | Bound | What it does here, or why not |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var r in Table())
            sb.AppendLine($"| {r.Name} | {r.Kind switch { ZplNameKind.Keyword => "keyword", ZplNameKind.NumericFunction => "numeric function", _ => "string function" }} | {(r.Bound ? "yes" : "no")} | {r.Note.Replace("|", "\\|")} |");
        return sb.ToString();
    }

    private static string KeywordReason(string k)
    {
        if (NotBoundNotes.TryGetValue(k, out var note)) return note;
        if (ObsoleteKeywords.Contains(k)) return "Obsolete in the help (no syntax given; it points to SETSURFACEPROPERTY, SETSYSTEMPROPERTY or a function).";
        return "";
    }

    private static string Reason(string note)
    {
        string r = note.StartsWith("Not bound: ", StringComparison.Ordinal) ? note["Not bound: ".Length..] : note;
        r = r.TrimEnd('.');
        if (r is "" or "Not bound") return "";
        return ": " + (r.Length > 1 && char.IsLower(r[1]) ? char.ToLowerInvariant(r[0]) + r[1..] : r);
    }

    private static string FunctionReason(string f) => NotBoundNotes.TryGetValue("f:" + f, out var note) ? note : "";

    // ---------------------------------------------------------------- Dispatch

    internal static ZplKeywordHandler Keyword(string name)
    {
        if (Keywords.TryGetValue(name, out var h)) return h;
        return (m, _) => throw new ZplNotBoundException(m.MacroName, m.Line, name,
            $"the keyword {name} is not bound in this version{Reason(KeywordReason(name))} (docs/Optics-User-Guide.md, \"ZPL macros\", lists what is bound).");
    }

    internal static ZplValue CallFunction(ZplInterpreter m, string name, ZplValue[] values)
    {
        if (!Functions.TryGetValue(name, out var f))
        {
            if (FunctionSet.Contains(name))
                throw new ZplNotBoundException(m.MacroName, m.Line, name,
                    $"the function {name}() is not bound in this version{Reason(FunctionReason(name))} (docs/Optics-User-Guide.md, \"ZPL macros\", lists what is bound).");
            throw new ZplRuntimeException($"{name}(…) is neither a ZPL function nor an array declared with DECLARE.");
        }
        if (values.Length < f.Min || values.Length > f.Max)
            throw new ZplRuntimeException(f.Min == f.Max
                ? $"{name}() takes {f.Min} argument{(f.Min == 1 ? "" : "s")}; {values.Length} given."
                : $"{name}() takes {f.Min} to {f.Max} arguments; {values.Length} given.");
        ZplValue result;
        try { result = f.Handler(m, values); }
        catch (ZplRuntimeException e) when (!e.Message.StartsWith(name, StringComparison.Ordinal))
        {
            throw new ZplRuntimeException($"{name}(): {e.Message}", e);
        }
        if (!result.IsString && !double.IsFinite(result.Number))
            throw new ZplRuntimeException($"{name}() gave {result.Number.ToString(CultureInfo.InvariantCulture)}, not a finite number.");
        return result;
    }

    // ---------------------------------------------------------------- Registration helpers

    private static void Kw(string name, string note, ZplKeywordHandler handler)
    {
        if (!KeywordSet.Contains(name)) throw new InvalidOperationException($"{name} is not a keyword of the help.");
        Keywords[name] = handler;
        KeywordNotes[name] = note;
    }

    private static void Fn(string name, int min, int max, string note, ZplFunctionHandler handler)
    {
        if (!FunctionSet.Contains(name)) throw new InvalidOperationException($"{name} is not a function of the help.");
        Functions[name] = new ZplFunctionDefinition(min, max, handler);
        FunctionNotes[name] = note;
    }

    /// <summary>A function of no arguments: "it is not required to provide one. The parentheses
    /// however, are still required" (Numeric Functions); one argument is accepted and ignored.</summary>
    private static void Fn0(string name, string note, Func<ZplInterpreter, ZplValue> handler) =>
        Fn(name, 0, 1, note, (m, _) => handler(m));

    private static void NotBound(string name, string reason) => NotBoundNotes[name] = reason;
    private static void FunctionNotBound(string name, string reason) => NotBoundNotes["f:" + name] = reason;

    private static ZplValue N(double v) => ZplValue.Of(v);
    private static ZplValue S(string s) => ZplValue.Of(s);

    internal static double Num(ZplValue[] v, int i, string what)
    {
        if (v[i].IsString) throw new ZplRuntimeException($"{what} must be a number, not the string \"{v[i].Text}\".");
        return v[i].Number;
    }

    internal static string Str(ZplValue[] v, int i, string what)
    {
        if (!v[i].IsString) throw new ZplRuntimeException($"{what} must be a string (a quoted text or a string variable), not a number.");
        return v[i].Text!;
    }

    internal static int Int(double value, string what)
    {
        double r = Math.Round(value);
        if (!double.IsFinite(value) || Math.Abs(value - r) > 1e-9 * Math.Max(1, Math.Abs(r)) || Math.Abs(r) > int.MaxValue)
            throw new ZplRuntimeException($"{what} must be an integer, not {ZplInterpreter.Num(value)}.");
        return (int)r;
    }

    internal static int Int(ZplValue[] v, int i, string what) => Int(Num(v, i, what), what);

    internal static string F(double v) => v.ToString("G10", CultureInfo.InvariantCulture);
}

/// <summary>Reading keyword arguments.</summary>
internal static class ZplArgsReading
{
    public static double Num(this ZplArgs a, ZplInterpreter m, int i, string what) => a.Expr(i, what).Number(m);

    public static double Num(this ZplArgs a, ZplInterpreter m, int i, string what, double absent) => a.Has(i) ? a.Num(m, i, what) : absent;

    public static int Int(this ZplArgs a, ZplInterpreter m, int i, string what) => ZplLibrary.Int(a.Num(m, i, what), what);

    public static int Int(this ZplArgs a, ZplInterpreter m, int i, string what, int absent) => a.Has(i) ? a.Int(m, i, what) : absent;

    public static string Str(this ZplArgs a, ZplInterpreter m, int i, string what) => a.Expr(i, what).Text(m);

    /// <summary>A value that may be a string or a number (SETSURFACEPROPERTY's value1).</summary>
    public static ZplValue Value(this ZplArgs a, ZplInterpreter m, int i, string what) => a.Expr(i, what).Evaluate(m);

    /// <summary>A file name: a quoted text or string expression, or the bare text as written
    /// (OPEN TEST.DAT, CALLMACRO CHILD.ZPL, the help's own examples).</summary>
    public static string FileName(this ZplArgs a, ZplInterpreter m, int i, string what)
    {
        if (!a.Has(i)) throw new ZplRuntimeException($"{what} is missing.");
        string t = a.Text(i);
        ZplExpr? e = null;
        try { e = ZplExpressionParser.ParseAll(t); } catch (ZplRuntimeException) { }
        if (e is not null && e.IsStringForm) return e.Text(m);
        if (t.StartsWith('"')) throw new ZplRuntimeException($"{what}: '{t}' is not a valid string.");
        return t;
    }

    public static void Max(this ZplArgs a, int count, string keyword)
    {
        if (a.Count > count)
            throw new ZplRuntimeException($"{keyword} takes {(count == 0 ? "no arguments" : $"at most {count} argument{(count == 1 ? "" : "s")}")}; {a.Count} given.");
    }

    public static void Exactly(this ZplArgs a, int count, string keyword)
    {
        if (a.Count != count)
            throw new ZplRuntimeException($"{keyword} takes {count} argument{(count == 1 ? "" : "s")}; {a.Count} given.");
    }
}
