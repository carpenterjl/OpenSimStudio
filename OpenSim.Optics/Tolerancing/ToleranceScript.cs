using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Scripting.Zpl;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Optics.Tolerancing;

// OP-67: tolerance scripts (docs/Optics-Conventions.md §60). The command set is OpticStudio 2025 R2
// help, "The Tolerance Script Commands", "Using Tolerance Scripts" and "Tolerance Script Example"
// (saved under plans/sources/optics/op-67-help/). Decided by the owner 2026-11-12 (OQ-3): scripts
// are written in the macro language (ZPL, OP-99); the commands are ZPL keywords that exist only in
// a tolerance script (ZplDialect).

/// <summary>A tolerance script and where its files are (help, "Tolerancing", Criterion tab,
/// "Script": "The name of the script, if using the User Script criterion").</summary>
public sealed record ToleranceScriptOptions
{
    /// <summary>The script's text: ZPL with the tolerance-script commands added.</summary>
    public string Text { get; init; } = "";

    /// <summary>The script's name in every message ("ALIGN.TSC").</summary>
    public string Name { get; init; } = "script.tsc";

    /// <summary>The folder LOADMERIT reads a merit function file from when the name has no folder.</summary>
    public string? MeritFolder { get; init; }

    /// <summary>The folder of OPENFILE names without a folder and of SAVE's TSAVnnnn.ZMX files.</summary>
    public string? OutputFolder { get; init; }

    /// <summary>The folder of the macros CALLMACRO runs.</summary>
    public string? MacroFolder { get; init; }

    /// <summary>Merit functions LOADMERIT finds by name (case ignored, with or without the
    /// extension) before it looks for a file; not saved.</summary>
    public IReadOnlyDictionary<string, OpticsMeritFunctionSettings>? MeritFunctions { get; init; }

    /// <summary>Reads a merit function file (path to text); null reads the disk. Not saved.</summary>
    public Func<string, string>? ReadFile { get; init; }

    /// <summary>Writes an output file (path, bytes); null writes the disk. Not saved.</summary>
    public Action<string, byte[]>? WriteFile { get; init; }
}

/// <summary>One REPORT of a script run: its text and its value (null for REPORT "text" -1).</summary>
public sealed record ToleranceScriptReport(string Text, double? Value);

/// <summary>What one run of a script produced besides the criterion: the text it sent to the
/// tolerance output (REPORT unless QUIET ON, and PRINT), every REPORT in order, and the file data
/// and lenses it asked to write (written when the whole run is done, in trial order).</summary>
public sealed record ToleranceScriptOutput
{
    public string Text { get; init; } = "";

    public IReadOnlyList<ToleranceScriptReport> Reports { get; init; } = Array.Empty<ToleranceScriptReport>();

    /// <summary>OPENFILE data: each file's bytes from this run, in the order the files were first opened.</summary>
    internal IReadOnlyList<(string Path, byte[] Bytes)> Files { get; init; } = Array.Empty<(string, byte[])>();

    /// <summary>SAVE n: the lens at that point.</summary>
    internal IReadOnlyList<(int Number, OpticsSettings Lens)> Saves { get; init; } = Array.Empty<(int, OpticsSettings)>();
}

/// <summary>A tolerance script failed in a trial: the message names the script, the line and the
/// problem (<see cref="ZplException"/>).</summary>
public sealed class ToleranceScriptException : InvalidOperationException
{
    public ToleranceScriptException(string script, int line, string message, Exception? inner = null)
        : base($"Tolerance script {script}, line {line}: {message}", inner)
    {
        Script = script;
        Line = line;
    }

    public string Script { get; }

    /// <summary>The 1-based line of the script; 0 when the failure is not at a line (no criterion returned).</summary>
    public int Line { get; }
}

/// <summary>What a script run is: the nominal, a sensitivity trial (row and side; Inverse in the
/// inverse modes) or Monte Carlo trial k of the run with seed <see cref="Seed"/>.</summary>
internal readonly record struct ToleranceScriptTrialId(ToleranceScriptPhase Phase, int Row, ToleranceSide Side, int MonteCarlo, int Seed, bool Inverse)
{
    public static ToleranceScriptTrialId Nominal(bool inverse) => new(ToleranceScriptPhase.Nominal, 0, ToleranceSide.Min, 0, 1, inverse);
    public static ToleranceScriptTrialId Sensitivity(int row, ToleranceSide side, bool inverse) => new(ToleranceScriptPhase.Sensitivity, row, side, 0, 1, inverse);
    public static ToleranceScriptTrialId OfMonteCarlo(int k, int seed) => new(ToleranceScriptPhase.MonteCarlo, 0, ToleranceSide.Min, k, seed, false);

    /// <summary>The random stream of the run: trial k of a Monte Carlo run is stream −k of its
    /// seed (the row draws use stream k); a sensitivity trial is stream −2³² − (2·row + side) of
    /// seed 1; the nominal stream 0 of seed 1. So a trial's numbers depend on what it is, never on
    /// the thread or the order trials ran in.</summary>
    public SplitMix64 Stream() => Phase switch
    {
        ToleranceScriptPhase.MonteCarlo => SplitMix64.ForStream(Seed, -(long)MonteCarlo),
        ToleranceScriptPhase.Sensitivity => SplitMix64.ForStream(1, -(1L << 32) - (2L * Row + (int)Side)),
        _ => SplitMix64.ForStream(1, 0),
    };
}

internal enum ToleranceScriptPhase { Nominal, Sensitivity, MonteCarlo }

/// <summary>
/// The tolerance-script commands as ZPL keywords (help, "The Tolerance Script Commands"), and the
/// check of a script before a run.
/// </summary>
public static class ToleranceScript
{
    /// <summary>The extension of tolerance scripts (help, "Using Tolerance Scripts": "text files ending in the extension TSC").</summary>
    public const string Extension = ".tsc";

    /// <summary>Every command of the help's page, in its order, whether this version runs it, and
    /// what it does here or why not. "!" (a comment) is ZPL's own comment.</summary>
    public static IReadOnlyList<ZplLibraryEntry> Commands { get; } = new[]
    {
        new ZplLibraryEntry("CEDV", ZplNameKind.Keyword, false, "Refused: \"CEDV is deprecated and has been replaced by CPAR\" (help); use CPAR."),
        new ZplLibraryEntry("CLEARCOMP", ZplNameKind.Keyword, true, "Removes all current compensators."),
        new ZplLibraryEntry("CLOSEFILE", ZplNameKind.Keyword, true, "Closes the current output file."),
        new ZplLibraryEntry("CMCO", ZplNameKind.Keyword, true, "CMCO operand config: a multi-configuration cell compensator."),
        new ZplLibraryEntry("CNPA", ZplNameKind.Keyword, false, "Refused: non-sequential compensators are not built (the OP-70 core has no tolerancing of objects)."),
        new ZplLibraryEntry("CNPS", ZplNameKind.Keyword, false, "Refused: non-sequential compensators are not built (the OP-70 core has no tolerancing of objects)."),
        new ZplLibraryEntry("COMP", ZplNameKind.Keyword, true, "COMP surf code: thickness (0), curvature (1) or conic (2) of a surface of the original lens."),
        new ZplLibraryEntry("CPAR", ZplNameKind.Keyword, true, "CPAR surf param: a parameter of a surface of the original lens."),
        new ZplLibraryEntry("FORMAT", ZplNameKind.Keyword, true, "ZPL's FORMAT; the default in a script is 16.8 EXP (help)."),
        new ZplLibraryEntry("GETMERIT", ZplNameKind.Keyword, true, "Evaluates the current merit function; its value becomes the criterion."),
        new ZplLibraryEntry("HEADER", ZplNameKind.Keyword, true, "HEADER \"text\": a line in the current TXT output file, followed by the row, the Monte Carlo trial or Nominal."),
        new ZplLibraryEntry("LOADMERIT", ZplNameKind.Keyword, true, "Loads a merit function (by name from the run, or this program's JSON file; OpticStudio's .MF layout is not published), renumbers its surfaces, evaluates it; its value becomes the criterion."),
        new ZplLibraryEntry("NOMINAL", ZplNameKind.Keyword, true, "NOMINAL OPT_OFF | OPT_ON: whether OPTIMIZE runs while the nominal is computed."),
        new ZplLibraryEntry("OPENFILE", ZplNameKind.Keyword, true, "OPENFILE \"name\" TXT|BIN: REPORT values go to the file (written in trial order when the run is done); refused in the inverse modes (help)."),
        new ZplLibraryEntry("OPTIMIZE", ZplNameKind.Keyword, true, "OPTIMIZE n: damped least squares on the current compensators (n = 0 or none: automatic); the merit function value becomes the criterion."),
        new ZplLibraryEntry("OPTIMIZE-OD", ZplNameKind.Keyword, true, "OPTIMIZE-OD n: Orthogonal Descent on the current compensators."),
        new ZplLibraryEntry("PERTURB", ZplNameKind.Keyword, true, "PERTURB type int1 int2 int3 stat nstd min max: types 0 (surface data), 1 (parameter) and 3 (multi-configuration); ignored on the nominal (help). Types 2 (extra data), 4 and 5 (non-sequential) are refused."),
        new ZplLibraryEntry("QUIET", ZplNameKind.Keyword, true, "QUIET ON | OFF: whether REPORT also writes to the tolerance output."),
        new ZplLibraryEntry("REPORT", ZplNameKind.Keyword, true, "REPORT \"text\" operand (0: the merit function) and REPORT \"text\" -1. REPORT \"text\" SZERNIKE is refused: the Zernike Standard Coefficients analysis is not built."),
        new ZplLibraryEntry("REPORT_NB", ZplNameKind.Keyword, true, "REPORT without the line break."),
        new ZplLibraryEntry("SAVE", ZplNameKind.Keyword, true, "SAVE n: TSAVnnnn.ZMX in the output folder (OP-94's writer), written when the run is done; the last trial in run order wins."),
        new ZplLibraryEntry("UPDATE", ZplNameKind.Keyword, true, "Checks that the lens builds (its solves and pickups evaluate)."),
    };

    /// <summary>The tolerance-script commands, upper case.</summary>
    internal static readonly string[] CommandNames =
    {
        "CEDV", "CLEARCOMP", "CLOSEFILE", "CMCO", "CNPA", "CNPS", "COMP", "CPAR", "GETMERIT", "HEADER", "LOADMERIT", "NOMINAL",
        "OPENFILE", "OPTIMIZE", "OPTIMIZE-OD", "PERTURB", "QUIET", "REPORT", "REPORT_NB", "SAVE", "UPDATE",
    };

    /// <summary>ZPL keywords a script may not run: they would make a trial depend on files, other
    /// trials or a person, or replace the trial lens.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Refused = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OUTPUT"] = "OUTPUT to a file is not allowed in a tolerance script (trials run in parallel); use OPENFILE and REPORT. OUTPUT SCREEN is allowed.",
        ["DELETEFILE"] = "files cannot be changed from a tolerance script (trials run in parallel).",
        ["COPYFILE"] = "files cannot be changed from a tolerance script (trials run in parallel).",
        ["RENAMEFILE"] = "files cannot be changed from a tolerance script (trials run in parallel).",
        ["MAKEFOLDER"] = "files cannot be changed from a tolerance script (trials run in parallel).",
        ["SAVELENS"] = "use SAVE n in a tolerance script.",
        ["SAVEARCHIVE"] = "use SAVE n in a tolerance script.",
        ["LOADLENS"] = "a tolerance script works on the trial lens; it cannot load another.",
        ["LOADARCHIVE"] = "a tolerance script works on the trial lens; it cannot load another.",
        ["TOLERANCE"] = "a tolerance run cannot be started inside a tolerance trial.",
        ["QUICKSENSITIVITY"] = "a tolerance run cannot be started inside a tolerance trial.",
        ["INPUT"] = "a tolerance script runs without a person to answer.",
        ["PAUSE"] = "a tolerance script runs without a person to answer.",
    };

    internal static readonly string[] CompensatorCommands = { "COMP", "CPAR", "CMCO", "CEDV", "CNPA", "CNPS" };

    internal static ZplDialect ParseDialect { get; } = new("tolerance script", CommandNames.Concat(Refused.Keys).ToArray());

    /// <summary>Parses a script; throws <see cref="ZplException"/> naming the first bad line.</summary>
    internal static ZplProgram Parse(ToleranceScriptOptions o) => ZplProgram.Parse(o.Text ?? "", o.Name, ParseDialect);

    /// <summary>
    /// Checks a script before a run: the parse (a syntax error names its line) and the commands,
    /// keywords and functions that this version does not run, each with its first line. Empty when
    /// the script would run as far as can be known before running it.
    /// </summary>
    public static IReadOnlyList<string> Check(ToleranceScriptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ZplProgram program;
        try { program = Parse(options); }
        catch (ZplException e) { return new[] { $"Tolerance script {e.Macro}, line {e.Line}: {e.Problem}" }; }
        var problems = new List<string>();
        foreach (var (name, line) in program.NotBound())
            problems.Add($"Tolerance script {options.Name}, line {line}: {name} is not bound in this version.");
        foreach (var s in program.Statements)
        {
            var k = s as ZplKeywordStatement ?? (s as ZplIfThen)?.Then as ZplKeywordStatement;
            if (k is null) continue;
            if (Refused.TryGetValue(k.Name, out var why) && !(k.Name == "OUTPUT" && k.Args.Word(0) == "SCREEN"))
                problems.Add($"Tolerance script {options.Name}, line {k.Line}: {k.Name}: {why}");
            else if (Commands.FirstOrDefault(c => c.Name == k.Name) is { Bound: false } c)
                problems.Add($"Tolerance script {options.Name}, line {k.Line}: {c.Note}");
        }
        return problems;
    }

    /// <summary>True when the script names COMP, CPAR, CMCO (or the refused CEDV, CNPA, CNPS) anywhere.</summary>
    internal static bool DefinesCompensators(ZplProgram program) => program.Statements.Any(s =>
        (s as ZplKeywordStatement ?? (s as ZplIfThen)?.Then as ZplKeywordStatement) is { } k && CompensatorCommands.Contains(k.Name));

    /// <summary>The arguments of a command: split at the commas when the text has a comma outside
    /// strings and parentheses (ZPL's form), else at blanks outside strings and parentheses (the
    /// help's form, "CPAR 3 1"). In the blank form an argument cannot contain a blank, so write an
    /// expression with spaces in the comma form.</summary>
    internal static List<string> Items(string raw)
    {
        string t = raw.Trim();
        var items = new List<string>();
        if (t.Length == 0) return items;
        bool comma = false;
        {
            bool inString = false; int depth = 0;
            foreach (char c in t)
            {
                if (c == '"') inString = !inString;
                else if (!inString && c == '(') depth++;
                else if (!inString && c == ')') depth--;
                else if (!inString && depth == 0 && c == ',') { comma = true; break; }
            }
        }
        var sb = new StringBuilder();
        bool str = false; int d = 0;
        foreach (char c in t)
        {
            if (c == '"') str = !str;
            else if (!str && c == '(') d++;
            else if (!str && c == ')') d--;
            bool split = !str && d == 0 && (comma ? c == ',' : char.IsWhiteSpace(c));
            if (split)
            {
                if (comma || sb.Length > 0) items.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (comma || sb.Length > 0) items.Add(sb.ToString().Trim());
        return items;
    }
}

/// <summary>
/// Runs a tolerance script in each trial of a tolerance run (help, "Using Tolerance Scripts":
/// "The script is executed many times during the evaluation of the tolerance analysis": once for
/// the nominal, twice per row in the sensitivity analysis, repeatedly in the inverse modes, once per
/// Monte Carlo lens). Each run parses the script afresh and has its own ZPL session, variables,
/// compensators, merit function, files and random stream, so a trial depends only on what it is.
/// </summary>
internal sealed class ToleranceScriptRunner
{
    private readonly ToleranceScriptOptions _script;
    private readonly IReadOnlyList<OpticalCatalog> _catalogs;
    private readonly IReadOnlyList<ToleranceCompensator> _tableCompensators;
    private readonly int _surfaceCount;
    private readonly OpticsMeritFunctionSettings? _nominalMerit;
    private readonly Dictionary<string, OpticsMeritFunctionSettings> _merits = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _meritLock = new();

    /// <summary>The help's default format of REPORT: "The default format is 16.8 EXP".</summary>
    private static readonly ZplFormatSpec ReportFormat = ZplFormatSpec.Parse("16.8 EXP");

    public ToleranceScriptRunner(ToleranceScriptOptions script, ToleranceRunOptions options, ToleranceTable table, OpticsSettings nominal,
        IReadOnlyList<OpticalCatalog> catalogs)
    {
        _script = script;
        _catalogs = catalogs;
        _surfaceCount = nominal.Surfaces?.Count ?? 0;
        _nominalMerit = nominal.MeritFunction;
        ZplProgram program;
        try { program = ToleranceScript.Parse(script); }
        catch (ZplException e) { throw new ArgumentException($"Tolerance script {e.Macro}, line {e.Line}: {e.Problem}", e); }

        // The table's compensators are the script's to begin with; "If any compensators are defined
        // in the script, then no compensators are allowed in the Tolerance Data Editor" (help).
        var all = table.Compensators();
        var chosen = all;
        if (options.CompensatorRows is { } pick)
        {
            foreach (int r in pick)
                if (!all.Any(c => c.Row == r))
                    throw new ArgumentException($"Tolerance run: row {r} is not a COMP, CPAR or CMCO row of the tolerance data, so it cannot be a chosen compensator.");
            chosen = all.Where(c => pick.Contains(c.Row)).ToArray();
        }
        foreach (var c in chosen)
            if (c.Code == "CEDV") throw new ArgumentException($"Tolerance row {c.Row} (CEDV): deprecated and replaced by CPAR (help); use CPAR.");
        if (all.Count > 0 && ToleranceScript.DefinesCompensators(program))
            throw new ArgumentException($"Tolerance script {script.Name} defines compensators and the tolerance data has COMP, CPAR or CMCO rows "
                + $"({string.Join(", ", all.Select(c => c.Row))}); the help: \"If any compensators are defined in the script, then no compensators are allowed in the Tolerance Data Editor.\" Remove the rows or the script's compensators.");
        _tableCompensators = chosen.ToArray();
    }

    public IReadOnlyList<string> Assumptions => new[]
    {
        $"Criterion: the user script {_script.Name}: the merit function value of its last LOADMERIT, GETMERIT, OPTIMIZE or OPTIMIZE-OD (help, \"Tolerance Script Example\"); the Comp setting is not used, and compensator bounds are not enforced (help).",
        "The script runs in each trial in place of the compensation, on the perturbed lens: the nominal first (PERTURB ignored, NOMINAL OPT_OFF honoured), then every trial starting from the lens the nominal run left, with that run's variable flags removed and the lens's own merit function.",
        "Its commands take the original lens's surface numbers (help) and are mapped through the surfaces the tolerancing inserted; ZPL's own keywords and functions see the trial lens as it is.",
        $"Compensators to begin with: {(_tableCompensators.Count == 0 ? "none" : string.Join("; ", _tableCompensators.Select(c => $"row {c.Row} {c.Code} {c.Int1} {c.Int2}")))}.",
        "OPTIMIZE and OPTIMIZE-OD run OP-57's local optimizer on one thread; every evaluation of a trial lens is aimed by its twin with the tolerance decenters and the coordinate-break decenter and tilt compensators at their start values (TC-15).",
        "Output files (OPENFILE) and SAVE lenses are written when the run is done, the nominal first and then the trials in order, so the files do not depend on the thread count.",
    };

    /// <summary>Runs the script on <paramref name="lens"/> (map: the original lens's surface numbers
    /// to the lens's; twin: the trial with its decenters and tilts at zero, or null).</summary>
    /// <exception cref="ToleranceScriptException">The script failed, naming the line.</exception>
    public (OpticsSettings Lens, double Criterion, ToleranceScriptOutput Output) Run(OpticsSettings lens, Func<int, int> map, OpticsSettings? twin,
        ToleranceScriptTrialId id, CancellationToken token)
    {
        ZplProgram program;
        try { program = ToleranceScript.Parse(_script); }
        catch (ZplException e) { throw new ToleranceScriptException(e.Macro, e.Line, e.Problem, e); }
        var trial = new Trial(this, lens, map, twin ?? lens, id, token);
        var session = new ZplSession(new ZplRunOptions { System = lens, Catalogs = _catalogs, MacroFolder = _script.MacroFolder, CancellationToken = token })
        {
            Dialect = trial.Dialect(),
            Depth = 1,
        };
        session.Random = new Random(unchecked((int)trial.Random.Next()));
        var m = new ZplInterpreter(program, session, stream: false) { Format = ReportFormat };
        try { m.Run(); }
        catch (ZplException e) { throw new ToleranceScriptException(e.Macro, e.Line, e.Problem, e); }
        if (trial.Criterion is not double criterion)
            throw new ToleranceScriptException(_script.Name, 0, "the script returned no criterion: it ran none of LOADMERIT, GETMERIT, OPTIMIZE or OPTIMIZE-OD (help: the criterion is the merit function value of the last of these).");
        var output = new ToleranceScriptOutput
        {
            Text = m.ScreenText, Reports = trial.Reports.ToArray(),
            Files = trial.FileOrder.Select(p => (p, trial.Files[p].ToArray())).ToArray(), Saves = trial.Saves.ToArray(),
        };
        return (session.System, criterion, output);
    }

    /// <summary>The start of every trial after the nominal run: its lens without variable flags and
    /// with the lens's own merit function.</summary>
    public OpticsSettings Start(OpticsSettings afterNominal) =>
        WithoutVariables(afterNominal) with { MeritFunction = _nominalMerit };

    private static OpticsSettings WithoutVariables(OpticsSettings s) =>
        ToleranceCompensators.WithVariables(s, Array.Empty<ToleranceCompensator>(), k => k, _ => "");

    private OpticsMeritFunctionSettings LoadMerit(string name)
    {
        lock (_meritLock)
        {
            if (_merits.TryGetValue(name, out var cached)) return cached;
            var merit = ReadMerit(name);
            _merits[name] = merit;
            return merit;
        }
    }

    private OpticsMeritFunctionSettings ReadMerit(string name)
    {
        if (_script.MeritFunctions is { } named)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            foreach (var (key, value) in named)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) || string.Equals(key, stem, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(key), stem, StringComparison.OrdinalIgnoreCase))
                    return value;
        }
        string ext = Path.GetExtension(name);
        if (ext.Equals(".mf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".zmx", StringComparison.OrdinalIgnoreCase))
            throw new ZplRuntimeException($"LOADMERIT {name}: OpticStudio's .MF and .ZMX merit function layouts are not published, so they are not read; save the merit function as this program's merit function file (.json, the Tolerance Data Editor's \"Save merit function\") and load that.");
        string file = ext.Length == 0 ? name + ".json" : name;
        string path = ZplSession.Resolve(file, _script.MeritFolder, "merit function folder");
        string text;
        try { text = _script.ReadFile is { } read ? read(path) : File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ZplRuntimeException($"LOADMERIT: the merit function file '{path}' cannot be read: {e.Message}");
        }
        try
        {
            return JsonSerializer.Deserialize<OpticsMeritFunctionSettings>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ZplRuntimeException($"LOADMERIT: the merit function file '{path}' is empty.");
        }
        catch (JsonException e)
        {
            throw new ZplRuntimeException($"LOADMERIT: the merit function file '{path}' is not a merit function of this program (JSON, as the Tolerance Data Editor saves it): {e.Message}");
        }
    }

    // ------------------------------------------------------------------ one run

    private sealed class Trial
    {
        private readonly ToleranceScriptRunner _r;
        private readonly Func<int, int> _map;
        private readonly OpticsSettings _twinBase;
        private readonly ToleranceScriptTrialId _id;
        private readonly CancellationToken _token;
        private readonly List<ToleranceCompensator> _compensators;
        private readonly List<ToleranceCompensator> _ever;
        private readonly MeritOperandRegistry _registry = MeritOperandRegistry.CreateDefault();
        private bool _optimizeOnNominal = true;
        private bool _quiet;
        private string? _file;
        private bool _binary;
        private MeritEvaluation? _last;

        public Trial(ToleranceScriptRunner runner, OpticsSettings lens, Func<int, int> map, OpticsSettings twinBase, ToleranceScriptTrialId id, CancellationToken token)
        {
            _r = runner;
            _map = map;
            _twinBase = twinBase;
            _id = id;
            _token = token;
            _compensators = runner._tableCompensators.ToList();
            _ever = _compensators.ToList();
            Random = id.Stream();
        }

        public SplitMix64 Random { get; }
        public double? Criterion { get; private set; }
        public List<ToleranceScriptReport> Reports { get; } = new();
        public Dictionary<string, List<byte>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> FileOrder { get; } = new();
        public List<(int, OpticsSettings)> Saves { get; } = new();

        private bool IsNominal => _id.Phase == ToleranceScriptPhase.Nominal;

        public ZplDialect Dialect()
        {
            var h = new Dictionary<string, ZplKeywordHandler>(StringComparer.Ordinal)
            {
                ["CLEARCOMP"] = (m, a) => { NoArguments(a, "CLEARCOMP"); _compensators.Clear(); },
                ["CEDV"] = (m, a) => throw new ZplRuntimeException("CEDV is deprecated and has been replaced by CPAR (help); use CPAR surf param."),
                ["CNPA"] = (m, a) => throw new ZplRuntimeException("CNPA: non-sequential compensators are not built (the OP-70 core has no tolerancing of objects)."),
                ["CNPS"] = (m, a) => throw new ZplRuntimeException("CNPS: non-sequential compensators are not built (the OP-70 core has no tolerancing of objects)."),
                ["COMP"] = (m, a) => AddCompensator(m, a, "COMP"),
                ["CPAR"] = (m, a) => AddCompensator(m, a, "CPAR"),
                ["CMCO"] = (m, a) => AddCompensator(m, a, "CMCO"),
                ["GETMERIT"] = (m, a) => { NoArguments(a, "GETMERIT"); Evaluate(m, "GETMERIT"); },
                ["LOADMERIT"] = LoadMerit,
                ["OPTIMIZE"] = (m, a) => Optimize(m, a, LocalOptimizationAlgorithm.DampedLeastSquares, "OPTIMIZE"),
                ["OPTIMIZE-OD"] = (m, a) => Optimize(m, a, LocalOptimizationAlgorithm.OrthogonalDescent, "OPTIMIZE-OD"),
                ["NOMINAL"] = Nominal,
                ["QUIET"] = Quiet,
                ["REPORT"] = (m, a) => Report(m, a, newLine: true),
                ["REPORT_NB"] = (m, a) => Report(m, a, newLine: false),
                ["HEADER"] = Header,
                ["OPENFILE"] = OpenFile,
                ["CLOSEFILE"] = (m, a) => { NoArguments(a, "CLOSEFILE"); _file = null; },
                ["PERTURB"] = Perturb,
                ["UPDATE"] = (m, a) => { NoArguments(a, "UPDATE"); if (m.Session.System.Surfaces is { Count: > 0 }) _ = m.Session.Lens; },
                ["SAVE"] = Save,
            };
            foreach (var (name, why) in ToleranceScript.Refused)
            {
                string reason = why;
                h[name] = name == "OUTPUT"
                    ? (m, a) =>
                    {
                        if (a.Count == 1 && a.Word(0) == "SCREEN") ZplLibrary.Keyword("OUTPUT")(m, a);
                        else throw new ZplRuntimeException("OUTPUT: " + reason);
                    }
                    : (m, a) => throw new ZplRuntimeException($"{name}: {reason}");
            }
            return new ZplDialect("tolerance script", ToleranceScript.ParseDialect.Names, h);
        }

        // -------------------------------------------------------------- arguments

        private static void NoArguments(ZplArgs a, string keyword)
        {
            if (a.Raw.Length > 0) throw new ZplRuntimeException($"{keyword} takes no arguments.");
        }

        private static List<string> Items(ZplArgs a, string keyword, int min, int max)
        {
            var items = ToleranceScript.Items(a.Raw);
            if (items.Count < min || items.Count > max || items.Any(i => i.Length == 0))
                throw new ZplRuntimeException(min == max
                    ? $"{keyword} takes {min} argument{(min == 1 ? "" : "s")}, separated by blanks or commas; {items.Count} given."
                    : $"{keyword} takes {min} to {max} arguments, separated by blanks or commas; {items.Count} given.");
            return items;
        }

        private static ZplValue Value(ZplInterpreter m, string item, string what)
        {
            try { return ZplExpressionParser.ParseAll(item).Evaluate(m); }
            catch (ZplRuntimeException e) { throw new ZplRuntimeException($"{what} ('{item}'): {e.Message}", e); }
        }

        private static double Number(ZplInterpreter m, string item, string what)
        {
            var v = Value(m, item, what);
            if (v.IsString) throw new ZplRuntimeException($"{what} must be a number, not the string \"{v.Text}\".");
            if (!double.IsFinite(v.Number)) throw new ZplRuntimeException($"{what} is not a finite number.");
            return v.Number;
        }

        private static int Integer(ZplInterpreter m, string item, string what) => ZplLibrary.Int(Number(m, item, what), what);

        private static string Text(ZplInterpreter m, string item, string what)
        {
            var v = Value(m, item, what);
            if (!v.IsString) throw new ZplRuntimeException($"{what} must be a string (a quoted text or a string variable).");
            return v.Text!;
        }

        // -------------------------------------------------------------- compensators

        private void AddCompensator(ZplInterpreter m, ZplArgs a, string code)
        {
            var it = Items(a, code, 2, 2);
            int i1 = Integer(m, it[0], code == "CMCO" ? "the operand" : "the surface");
            int i2 = Integer(m, it[1], code switch { "COMP" => "the code", "CPAR" => "the parameter", _ => "the configuration" });
            if (code != "CMCO" && (i1 < 0 || i1 >= _r._surfaceCount))
                throw new ZplRuntimeException($"{code}: surface {i1} is not a surface of the original lens (0 to {_r._surfaceCount - 1}).");
            if (code == "COMP" && i2 is not (0 or 1 or 2))
                throw new ZplRuntimeException($"COMP: the code is 0 for thickness, 1 for curvature, 2 for conic (help), not {i2}.");
            if (code == "CPAR" && i2 < 1) throw new ZplRuntimeException($"CPAR: the parameter number must be 1 or more, not {i2}.");
            var c = new ToleranceCompensator(0, code, i1, i2, 0, 0);
            if (_compensators.Any(x => x.Code == c.Code && x.Int1 == c.Int1 && x.Int2 == c.Int2))
                throw new ZplRuntimeException($"{code} {i1} {i2} is already a compensator.");
            _compensators.Add(c);
            _ever.Add(c);
        }

        private static string Who(ToleranceCompensator c) => c.Row > 0 ? $"Tolerance row {c.Row} ({c.Code})" : $"{c.Code} {c.Int1} {c.Int2}";

        /// <summary>True for a compensator that decenters or tilts: a coordinate break's parameters 1 to 5.</summary>
        private bool MovesOffAxis(ToleranceCompensator c, OpticsSettings s)
        {
            if (c.Code != "CPAR" || c.Int2 < 1 || c.Int2 > 5) return false;
            int j = _map(c.Int1);
            return j >= 0 && j < (s.Surfaces?.Count ?? 0)
                && string.Equals(s.Surfaces![j].Type, SurfaceTypes.CoordinateBreak, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The aim twin of <paramref name="s"/>: the trial's twin with every compensator cell
        /// the script has used, other than a coordinate break's decenter or tilt, copied from <paramref name="s"/>.</summary>
        private OpticsSettings TwinFor(OpticsSettings s)
        {
            var twin = _twinBase;
            if (twin.Surfaces is null || s.Surfaces is null || twin.Surfaces.Count != s.Surfaces.Count) return twin;
            List<OpticsSurfaceSettings>? list = null;
            foreach (var c in _ever)
            {
                if (c.Code == "CMCO" || MovesOffAxis(c, s)) continue;
                int j = _map(c.Int1);
                if (j < 0 || j >= s.Surfaces.Count) continue;
                list ??= twin.Surfaces.ToList();
                var from = s.Surfaces[j];
                var to = list[j];
                list[j] = c.Code == "CPAR"
                    ? ZplLibrary.WithParameter(to, c.Int2, SavedSurfaces.Value(from, c.Int2))
                    : c.Int2 switch
                    {
                        0 => to with { Thickness = from.Thickness, ThicknessIsInfinite = from.ThicknessIsInfinite },
                        1 => to with { Radius = from.Radius },
                        _ => to with { Conic = from.Conic },
                    };
            }
            return list is null ? twin : twin with { Surfaces = list };
        }

        private IMeritConfigurations Configurations(OpticsSettings s) =>
            new Aimed(new ProjectMeritConfigurations(s, _r._catalogs), new ProjectMeritConfigurations(TwinFor(s), _r._catalogs));

        /// <summary>TC-15's <see cref="AimedConfigurations"/>, with the catalogs passed on (the glass operands read them).</summary>
        private sealed class Aimed : IMeritConfigurations
        {
            private readonly ProjectMeritConfigurations _inner, _twin;

            public Aimed(ProjectMeritConfigurations inner, ProjectMeritConfigurations twin) { _inner = inner; _twin = twin; }

            public int Count => _inner.Count;

            public SequentialLens LensFor(int configuration) =>
                ToleranceAim.Attach(_inner.LensFor(configuration), _twin.LensFor(configuration), $"configuration {configuration}");

            public OpticalSystemData SystemFor(int configuration) => _inner.SystemFor(configuration);

            public IReadOnlyList<OpticalCatalog>? Catalogs => _inner.Catalogs;

            // wire-16: the non-sequential rows (OP-88) build their scene from the trial project.
            public OpticsSettings? SettingsFor(int configuration) => _inner.SettingsFor(configuration);
        }

        // -------------------------------------------------------------- merit function

        private MeritFunction Function(ZplInterpreter m, string keyword)
        {
            var notes = new List<string>();
            var function = MeritFunctionSettings.FromSettings(m.Session.System.MeritFunction, _registry, notes);
            if (notes.Count > 0) throw new ZplRuntimeException($"{keyword}: the merit function cannot be read: {string.Join(" ", notes)}");
            if (function.Rows.Count == 0)
                throw new ZplRuntimeException($"{keyword}: the current merit function is empty; LOADMERIT one, or build it with INSERTMFO and SETOPERAND.");
            return function;
        }

        private void Evaluate(ZplInterpreter m, string keyword)
        {
            var e = MeritEvaluator.Evaluate(Function(m, keyword), Configurations(m.Session.System), _registry,
                new MeritEvaluationOptions { MaxDegreeOfParallelism = 1 }, _token);
            if (e.HasFailures) throw new ZplRuntimeException($"{keyword}: the merit function cannot be evaluated: {string.Join(" ", e.Errors)}");
            _last = e;
            Criterion = e.Value;
        }

        private void LoadMerit(ZplInterpreter m, ZplArgs a)
        {
            string raw = a.Raw.Trim();
            if (raw.Length == 0) throw new ZplRuntimeException("LOADMERIT needs a merit function file name.");
            string name = raw.StartsWith('"') ? Text(m, raw, "LOADMERIT's file name") : raw;
            var merit = _r.LoadMerit(name);
            // "Any operands in the merit function which reference surface numbers should be the
            // surface numbers for the original, unaltered lens. OpticStudio automatically adjusts
            // the surface numbers" (help, LOADMERIT).
            var renumbered = SavedLensSurgery.RenumberMerit(merit, _map, _r._surfaceCount);
            m.Session.System = m.Session.System with { MeritFunction = renumbered };
            Evaluate(m, "LOADMERIT");
        }

        private void Optimize(ZplInterpreter m, ZplArgs a, LocalOptimizationAlgorithm algorithm, string keyword)
        {
            var it = Items(a, keyword, 0, 1);
            int n = it.Count == 0 ? 0 : Integer(m, it[0], "the number of cycles");
            if (n < 0) throw new ZplRuntimeException($"{keyword}: the number of cycles must be 0 (automatic) or more, not {n}.");
            if (IsNominal && !_optimizeOnNominal) return;   // NOMINAL OPT_OFF
            if (_compensators.Count == 0)
                throw new ZplRuntimeException($"{keyword}: there are no compensators; define them with COMP, CPAR or CMCO first.");
            var function = Function(m, keyword);
            var start = ToleranceCompensators.WithVariables(m.Session.System, _compensators, _map, Who);
            var result = LocalOptimizer.Run(start, _r._catalogs, new LocalOptimizationOptions
            {
                Algorithm = algorithm,
                Cycles = n > 0 ? LocalOptimizationCycles.Fixed : LocalOptimizationCycles.Automatic,
                CycleCount = Math.Max(1, n),
                MaxDegreeOfParallelism = 1,
                CancellationToken = _token,
                Configurations = Configurations,
            }, function, _registry);
            if (result.Cancelled) throw new OperationCanceledException(_token);
            if (result.Evaluation.HasFailures)
                throw new ZplRuntimeException($"{keyword}: the merit function cannot be evaluated on the optimized lens: {string.Join(" ", result.Evaluation.Errors)}");
            m.Session.System = result.Optimized;
            _last = result.Evaluation;
            Criterion = result.Evaluation.Value;
        }

        // -------------------------------------------------------------- control and output

        private static string Word(ZplArgs a, string keyword, params string[] choices)
        {
            string w = a.Raw.Trim().ToUpperInvariant();
            if (!choices.Contains(w)) throw new ZplRuntimeException($"{keyword} takes {string.Join(" or ", choices)}, not '{a.Raw.Trim()}'.");
            return w;
        }

        private void Nominal(ZplInterpreter m, ZplArgs a)
        {
            string w = Word(a, "NOMINAL", "OPT_OFF", "OPT_ON");
            // "The NOMINAL command is only executed during computation of the nominal system performance".
            if (IsNominal) _optimizeOnNominal = w == "OPT_ON";
        }

        private void Quiet(ZplInterpreter m, ZplArgs a) => _quiet = Word(a, "QUIET", "ON", "OFF") == "ON";

        private void Report(ZplInterpreter m, ZplArgs a, bool newLine)
        {
            string keyword = newLine ? "REPORT" : "REPORT_NB";
            var it = Items(a, keyword, 2, 6);
            string text = Text(m, it[0], $"{keyword}'s text");
            if (string.Equals(it[1], "SZERNIKE", StringComparison.OrdinalIgnoreCase))
                throw new ZplRuntimeException($"{keyword} \"…\" SZERNIKE: the Zernike Standard Coefficients analysis is not built in this version.");
            if (it.Count > 2) throw new ZplRuntimeException($"{keyword} takes the text and an operand number; {it.Count} arguments given.");
            int operand = Integer(m, it[1], "the operand number");
            double? value;
            if (operand == -1) value = null;
            else
            {
                var e = _last ?? throw new ZplRuntimeException($"{keyword}: the merit function has not been evaluated; run GETMERIT, LOADMERIT or OPTIMIZE first (help, GETMERIT).");
                if (operand == 0) value = e.Value;
                else
                {
                    if (operand < 1 || operand > e.Rows.Count)
                        throw new ZplRuntimeException($"{keyword}: operand {operand} is not a row of the current merit function (1 to {e.Rows.Count}; 0 is the whole merit function, -1 text only).");
                    double v = e.Rows[operand - 1].Value;
                    if (double.IsNaN(v)) throw new ZplRuntimeException($"{keyword}: merit row {operand} ({e.Rows[operand - 1].Row.Code}) has no value.");
                    value = v;
                }
            }
            Reports.Add(new ToleranceScriptReport(text, value));
            if (!_quiet) m.Write(text + (value is double x ? m.Format.Format(x) : "") + (newLine ? Environment.NewLine : ""));
            // To a file only the number: "when REPORT follows an OPENFILE command, all content
            // within the "text" portion will be omitted from the output".
            if (_file is not null && value is double y)
            {
                var bytes = Files[_file];
                if (_binary)
                {
                    var b = new byte[8];
                    BinaryPrimitives.WriteDoubleLittleEndian(b, y);
                    bytes.AddRange(b);
                }
                else bytes.AddRange(Encoding.UTF8.GetBytes(m.Format.Format(y).Trim() + "\r\n"));
            }
        }

        private void Header(ZplInterpreter m, ZplArgs a)
        {
            var it = Items(a, "HEADER", 1, 1);
            string text = Text(m, it[0], "HEADER's text");
            if (_file is null || _binary) return;   // "adds a text line to the output file when using OPENFILE in TXT mode"
            string what = _id.Phase switch
            {
                ToleranceScriptPhase.Nominal => "Nominal",
                ToleranceScriptPhase.Sensitivity => _id.Row.ToString(CultureInfo.InvariantCulture),
                _ => _id.MonteCarlo.ToString(CultureInfo.InvariantCulture),
            };
            Files[_file].AddRange(Encoding.UTF8.GetBytes(text + " " + what + "\r\n"));
        }

        private void OpenFile(ZplInterpreter m, ZplArgs a)
        {
            if (_id.Inverse)
                throw new ZplRuntimeException("OPENFILE in an inverse sensitivity run: the help says \"OPENFILE should not be used during an inverse sensitivity run, because there are multiple calls to execute the script and no practical way to discern which data belongs to which operand\".");
            var it = Items(a, "OPENFILE", 2, 2);
            string name = Text(m, it[0], "OPENFILE's file name");
            string mode = it[1].ToUpperInvariant();
            if (mode is not ("BIN" or "TXT")) throw new ZplRuntimeException($"OPENFILE: the mode is BIN or TXT (help), not '{it[1]}'.");
            string path = Path.GetFullPath(ZplSession.Resolve(name, _r._script.OutputFolder, "output folder"));
            if (!Files.ContainsKey(path)) { Files[path] = new List<byte>(); FileOrder.Add(path); }
            _file = path;
            _binary = mode == "BIN";
        }

        private void Save(ZplInterpreter m, ZplArgs a)
        {
            var it = Items(a, "SAVE", 1, 1);
            int n = Integer(m, it[0], "the file number");
            if (n < 0 || n > 9999) throw new ZplRuntimeException($"SAVE: the file number must be 0 to 9999 (TSAVnnnn.ZMX), not {n}.");
            if (_r._script.OutputFolder is null) throw new ZplRuntimeException("SAVE: no output folder is set for the script's files.");
            Saves.Add((n, m.Session.System));
        }

        // -------------------------------------------------------------- PERTURB

        private void Perturb(ZplInterpreter m, ZplArgs a)
        {
            var it = Items(a, "PERTURB", 8, 8);
            int type = Integer(m, it[0], "the type");
            int i1 = Integer(m, it[1], "int1"), i2 = Integer(m, it[2], "int2"), i3 = Integer(m, it[3], "int3");
            int stat = Integer(m, it[4], "the statistics");
            double nstd = Number(m, it[5], "nstd"), min = Number(m, it[6], "min"), max = Number(m, it[7], "max");
            if (type is 4 or 5) throw new ZplRuntimeException("PERTURB of non-sequential data: tolerancing of non-sequential objects is not built (the OP-70 core has none).");
            if (type == 2) throw new ZplRuntimeException("PERTURB of extra data (type 2) is not bound in this version.");
            if (type is not (0 or 1 or 3)) throw new ZplRuntimeException($"PERTURB: the type is 0 to 5 (help), not {type}.");
            if (stat is not (0 or 1 or 2)) throw new ZplRuntimeException($"PERTURB: the statistics are 0 (normal), 1 (uniform) or 2 (parabolic) (help), not {stat}.");
            if (min > max) throw new ZplRuntimeException($"PERTURB: min {ZplInterpreter.Num(min)} lies above max {ZplInterpreter.Num(max)}.");
            if (stat == 0 && !(nstd > 0)) throw new ZplRuntimeException($"PERTURB: the normal distribution needs nstd above zero, not {ZplInterpreter.Num(nstd)}.");
            if (type != 3 && i3 != 0) throw new ZplRuntimeException($"PERTURB: int3 must be zero for type {type} (help), not {i3}.");
            // "The PERTURB command is ignored while evaluating the nominal criterion."
            if (IsNominal) return;
            double v = Draw(stat, nstd, min, max);
            var s = m.Session.System;
            switch (type)
            {
                case 0:
                case 1:
                {
                    if (i1 < 0 || i1 >= _r._surfaceCount) throw new ZplRuntimeException($"PERTURB: surface {i1} is not a surface of the original lens (0 to {_r._surfaceCount - 1}).");
                    int j = _map(i1);
                    var list = s.Surfaces!.ToList();
                    var surface = list[j];
                    string column = type == 1 ? SavedSurfaces.ParameterColumn(i2) : i2 switch
                    {
                        0 => SavedSurfaces.ThicknessColumn, 1 => SavedSurfaces.RadiusColumn, 2 => SavedSurfaces.ConicColumn, 3 => SavedSurfaces.SemiDiameterColumn,
                        _ => throw new ZplRuntimeException($"PERTURB type 0: int2 is 0 (thickness), 1 (radius), 2 (conic) or 3 (semi-diameter) (help), not {i2}."),
                    };
                    if (SurfaceFlags.Of(surface, column) is { } f && !SurfaceFlags.IsKind(f, SavedSurfaces.VariableFlag))
                        throw new ZplRuntimeException($"PERTURB: surface {i1}'s {column} is set by a {(SurfaceFlags.IsKind(f, SavedSurfaces.PickupFlag) ? "pickup" : "solve")}, which would overwrite the perturbation.");
                    if (type == 1)
                    {
                        if (i2 < 1) throw new ZplRuntimeException($"PERTURB type 1: the parameter number must be 1 or more, not {i2}.");
                        list[j] = ZplLibrary.WithParameter(surface, i2, SavedSurfaces.Value(surface, i2) + v);
                    }
                    else list[j] = i2 switch
                    {
                        0 => surface.ThicknessIsInfinite ? throw new ZplRuntimeException($"PERTURB: surface {i1}'s thickness is infinite.") : surface with { Thickness = surface.Thickness + v },
                        1 => SavedSurfaces.IsPlane(surface) ? throw new ZplRuntimeException($"PERTURB: surface {i1} is a plane; its radius is infinite.") : surface with { Radius = surface.Radius!.Value + v },
                        2 => surface with { Conic = surface.Conic + v },
                        _ => surface.SemiDiameter is double sd ? surface with { SemiDiameter = sd + v }
                            : throw new ZplRuntimeException($"PERTURB: surface {i1}'s semi-diameter is automatic; give it a fixed value to perturb it."),
                    };
                    m.Session.System = s with { Surfaces = list };
                    break;
                }
                default:
                {
                    // "int1 is the configuration number, int2 is the operand number".
                    var cell = new MultiConfigCell(i2, i1);
                    var values = s.MultiConfiguration?.Operands is { } ops && i2 >= 1 && i2 <= ops.Count ? ops[i2 - 1]?.Values : null;
                    var old = values is not null && i1 >= 1 && i1 <= values.Count ? values[i1 - 1] : null;
                    if (old?.Solve is { } solve && !string.Equals(solve, "Variable", StringComparison.Ordinal))
                        throw new ZplRuntimeException($"PERTURB: the multi-configuration cell (operand {i2}, configuration {i1}) has a {solve}, which would overwrite the perturbation.");
                    try { m.Session.System = MultiConfiguration.WithValue(s, cell, MultiConfiguration.ValueOf(s, cell) + v); }
                    catch (ArgumentException e) { throw new ZplRuntimeException($"PERTURB: {e.Message}"); }
                    break;
                }
            }
        }

        /// <summary>A value over [min, max]: normal with σ = (max − min)/(2·nstd) about the middle,
        /// truncated at the ends ("the number of standard deviations from the center of the
        /// distribution to either edge"), uniform, or parabolic (OP-65's distributions).</summary>
        private double Draw(int stat, double nstd, double min, double max)
        {
            double mid = (min + max) / 2, half = (max - min) / 2;
            switch (stat)
            {
                case 0:
                {
                    if (half == 0) return mid;
                    double sigma = half / nstd;
                    while (true)
                    {
                        double x = sigma * Random.NextNormal();
                        if (Math.Abs(x) <= half) return mid + x;
                    }
                }
                case 1: return ToleranceDistribution.Uniform(min, max).Draw(Random);
                default: return ToleranceDistribution.Parabolic(min, max).Draw(Random);
            }
        }
    }
}

/// <summary>Writes a run's script files when the run is done: OPENFILE data and SAVE lenses, the
/// nominal first and then the trials in order.</summary>
internal static class ToleranceScriptFiles
{
    /// <summary>The report's "Script output" section: each run's text (REPORT unless QUIET ON,
    /// and PRINT), and the files written.</summary>
    public static void AppendOutput(List<string> lines, IEnumerable<(string Who, ToleranceScriptOutput? Output)> runs, IReadOnlyList<string> files)
    {
        var shown = runs.Where(r => r.Output is { Text.Length: > 0 }).ToArray();
        if (shown.Length > 0)
        {
            lines.Add("");
            lines.Add("Script output:");
            foreach (var (who, output) in shown)
            {
                lines.Add("  " + who + ":");
                lines.AddRange(output!.Text.TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n').Select(l => "    " + l));
            }
        }
        if (files.Count > 0) lines.Add("Script files written: " + string.Join(", ", files) + ".");
    }

    public static IReadOnlyList<string> Write(IEnumerable<ToleranceScriptOutput?> inOrder, ToleranceScriptOptions options, List<string> notes)
    {
        var files = new Dictionary<string, List<byte>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var saves = new SortedDictionary<int, OpticsSettings>();
        foreach (var o in inOrder)
        {
            if (o is null) continue;
            foreach (var (path, bytes) in o.Files)
            {
                if (!files.TryGetValue(path, out var list)) { files[path] = list = new List<byte>(); order.Add(path); }
                list.AddRange(bytes);
            }
            foreach (var (n, lens) in o.Saves) saves[n] = lens;
        }
        var write = options.WriteFile ?? ((p, b) => File.WriteAllBytes(p, b));
        var written = new List<string>();
        void Put(string path, byte[] bytes)
        {
            try { write(path, bytes); written.Add(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                notes.Add($"Tolerance script {options.Name}: the file '{path}' could not be written: {e.Message}");
            }
        }
        foreach (var path in order) Put(path, files[path].ToArray());
        foreach (var (n, lens) in saves)
        {
            string path = Path.Combine(options.OutputFolder!, $"TSAV{n.ToString("D4", CultureInfo.InvariantCulture)}.ZMX");
            ZemaxExportResult result;
            try { result = ZemaxLensWriter.Write(lens, new ZemaxExportOptions { UnsupportedAsWarnings = true }); }
            catch (Exception e) when (e is ZemaxFileException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                notes.Add($"Tolerance script {options.Name}: SAVE {n}: the lens cannot be written as a .zmx file: {e.Message}");
                continue;
            }
            foreach (var w in result.Warnings) notes.Add($"Tolerance script {options.Name}: SAVE {n} ({Path.GetFileName(path)}): {w}");
            Put(path, ZemaxText.Encode(result.Text));
        }
        return written;
    }
}
