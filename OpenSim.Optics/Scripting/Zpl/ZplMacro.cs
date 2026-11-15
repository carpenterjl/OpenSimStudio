using System.Diagnostics;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Scripting.Zpl;

// OP-99: running macros (docs/Optics-Conventions.md §56; docs/Scripting-from-JGraph.md, "ZPL macros").

/// <summary>What a macro asks of the person running it: INPUT and PAUSE with a message. A run
/// without a host fails at those keywords, naming them.</summary>
public interface IZplHost
{
    /// <summary>INPUT: the text typed for <paramref name="prompt"/>, or null when cancelled.</summary>
    string? Input(string prompt);

    /// <summary>PAUSE with a message: returns when the person presses OK.</summary>
    void Pause(string message);
}

/// <summary>How to run a macro.</summary>
public sealed record ZplRunOptions
{
    /// <summary>The lens the macro works on (the "current lens"); null is a system with no
    /// surfaces. The macro's changes are returned in <see cref="ZplRunResult.System"/>; this
    /// record is not changed.</summary>
    public OpticsSettings? System { get; init; }

    /// <summary>The glass catalogs the lens draws from (<see cref="OpticsScripting.Catalogs"/>);
    /// null is none, and a glass name then fails where it is needed.</summary>
    public IReadOnlyList<OpticalCatalog>? Catalogs { get; init; }

    /// <summary>The folder of ZPL macros: CALLMACRO's macros, OUTPUT and OPEN file names given
    /// without a path, $MACROPATH(); null fails where it is needed.</summary>
    public string? MacroFolder { get; init; }

    /// <summary>The folder LOADLENS and SAVELENS use for a file name without a path; null fails
    /// where it is needed.</summary>
    public string? LensFolder { get; init; }

    /// <summary>The lens file the system came from ($FILENAME, $FILEPATH, $PATHNAME, SAVELENS
    /// without a name), or null.</summary>
    public string? LensPath { get; init; }

    /// <summary>INPUT and PAUSE; null fails at those keywords.</summary>
    public IZplHost? Host { get; init; }

    /// <summary>The command-line arguments $GETARG reads, by name (case is ignored).
    /// "IsAutomated" is always set: "True" when <see cref="Automated"/>.</summary>
    public IReadOnlyDictionary<string, string>? Arguments { get; init; }

    /// <summary>True when run from the command line ($GETARG("IsAutomated") is "True").</summary>
    public bool Automated { get; init; }

    /// <summary>ZPLM's Data1 to Data6 (PVHX, PVHY, PVPX, PVPY, PVEX, PVEY), or null outside an
    /// operand.</summary>
    public IReadOnlyList<double>? PassValues { get; init; }

    /// <summary>The surface of the macro solve being computed (SOSO(0)), or null outside a solve.</summary>
    public int? SolveSurface { get; init; }

    /// <summary>Text written to the screen, as it is written (for a live output window).</summary>
    public Action<string>? Output { get; init; }

    /// <summary>The whole screen text after REWIND erased a line.</summary>
    public Action<string>? ScreenReplaced { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>What a macro run produced.</summary>
public sealed record ZplRunResult
{
    /// <summary>The text written to the screen (the macro's output window).</summary>
    public required string Output { get; init; }

    /// <summary>The lens after the macro (its SETSURFACEPROPERTY, OPTIMIZE, LOADLENS … changes).</summary>
    public required OpticsSettings System { get; init; }

    /// <summary>The lens file the macro last loaded or saved, or the one it was given.</summary>
    public string? LensPath { get; init; }

    /// <summary>OPTRETURN's 51 data fields (0 to 50); null where the macro returned nothing.</summary>
    public required IReadOnlyList<double?> OptReturn { get; init; }

    /// <summary>The last SOLVERETURN value, or null when the macro called none ("The absence of
    /// the SOLVERETURN call will indicate … that the solve cannot be computed", SOLVERETURN).</summary>
    public double? SolveReturn { get; init; }

    /// <summary>True when the macro ran CLOSEWINDOW without an argument ("quiet" mode).</summary>
    public bool Quiet { get; init; }

    /// <summary>The number of statements executed (the parent and every child).</summary>
    public long Steps { get; init; }
}

/// <summary>
/// The interpreter of OpticStudio's ZPL macro language (OP-99): <see cref="Parse"/> checks a
/// macro and <see cref="Run(string, ZplRunOptions?, string)"/> runs one on a copy of a lens.
/// The language core is complete; the keywords and functions bound to the optics engines are
/// listed in <see cref="ZplLibrary"/>, and a keyword or function that is not bound fails by name
/// and line when the macro reaches it.
/// </summary>
public static class ZplMacro
{
    /// <summary>The file extension of macros.</summary>
    public const string Extension = ".zpl";

    /// <summary>Parses macro text; throws <see cref="ZplException"/> naming the first bad line.</summary>
    public static ZplProgram Parse(string text, string name = "macro") => ZplProgram.Parse(text, name);

    /// <summary>Runs macro text. Throws <see cref="ZplException"/> naming the macro, the line and
    /// the problem; <see cref="OperationCanceledException"/> when cancelled.</summary>
    public static ZplRunResult Run(string text, ZplRunOptions? options = null, string name = "macro") =>
        Run(Parse(text, name), options);

    /// <summary>Runs the macro file at <paramref name="path"/>.</summary>
    public static ZplRunResult RunFile(string path, ZplRunOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new ZplRunOptions();
        if (options.MacroFolder is null) options = options with { MacroFolder = Path.GetDirectoryName(Path.GetFullPath(path)) };
        return Run(File.ReadAllText(path), options, Path.GetFileName(path));
    }

    /// <summary>Runs a parsed macro.</summary>
    public static ZplRunResult Run(ZplProgram program, ZplRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        var session = new ZplSession(options ?? new ZplRunOptions());
        var interpreter = new ZplInterpreter(program, session);
        session.Depth = 1;
        interpreter.Run();
        return new ZplRunResult
        {
            Output = interpreter.ScreenText,
            System = session.System,
            LensPath = session.LensPath,
            OptReturn = session.OptReturn.ToArray(),
            SolveReturn = session.SolveReturn,
            Quiet = interpreter.Quiet,
            Steps = session.Steps,
        };
    }
}

/// <summary>
/// What a parent macro and the macros it calls share: the lens (settings in lens units, as the
/// editors hold it), the call buffers, OPTRETURN and SOLVERETURN, the vectors VEC1..4, the
/// random numbers, the timer and the last traced ray.
/// </summary>
internal sealed class ZplSession
{
    private readonly ZplRunOptions _options;
    private OpticsSettings _system;
    private int _version;
    private (int Version, OpticsSettings Settings)? _current;
    private (int Version, SequentialLens? Lens)? _lens;

    public ZplSession(ZplRunOptions options)
    {
        _options = options;
        _system = options.System ?? new OpticsSettings();
        Catalogs = options.Catalogs ?? Array.Empty<OpticalCatalog>();
        LensPath = options.LensPath;
        Array.Fill(CallStrings, "");
        for (int k = 0; k < 4; k++) Vectors[k] = new double[VectorSize + 1];
        if (options.PassValues is { } pv)
        {
            if (pv.Count > 6) throw new ArgumentException("A macro takes at most six pass values (ZPLM's Data1 to Data6).");
            for (int k = 0; k < pv.Count; k++) PassValues[k] = pv[k];
        }
        Timer.Start();
    }

    public IReadOnlyList<OpticalCatalog> Catalogs { get; }
    public string? MacroFolder => _options.MacroFolder;
    public string? LensFolder => _options.LensFolder;
    public string? LensPath { get; set; }
    public IZplHost? Host => _options.Host;
    public CancellationToken CancellationToken => _options.CancellationToken;
    public Action<string>? Output => _options.Output;
    public Action<string>? ScreenReplaced => _options.ScreenReplaced;
    public IReadOnlyDictionary<string, string>? Arguments => _options.Arguments;
    public bool Automated => _options.Automated;
    public int? SolveSurface => _options.SolveSurface;

    /// <summary>The call buffers: "a buffer of 51 numeric and 51 string values" shared by the
    /// parent and its children (Calling a Macro from within a Macro).</summary>
    public double[] CallNumbers { get; } = new double[51];
    public string[] CallStrings { get; } = new string[51];

    /// <summary>OPTRETURN's data fields 0 to 50.</summary>
    public double?[] OptReturn { get; } = new double?[51];

    public double? SolveReturn { get; set; }
    public double[] PassValues { get; } = new double[6];

    /// <summary>VEC1..4; "The initial size of the vectors is 1000" and subscripts run from 0 to
    /// the size (SETVECSIZE, VEC1, VEC2, VEC3, VEC4).</summary>
    public int VectorSize { get; private set; } = 1000;
    public double[][] Vectors { get; } = new double[4][];

    public void ResizeVectors(int size)
    {
        VectorSize = size;
        for (int k = 0; k < 4; k++) Vectors[k] = new double[size + 1];
    }

    public Random Random { get; set; } = new();
    public Stopwatch Timer { get; } = new();

    /// <summary>The string buffer $BUFFER() reads.</summary>
    public string Buffer { get; set; } = "";

    /// <summary>The last RAYTRACE, with the lens unit it was traced in.</summary>
    public ScriptRay? Ray { get; set; }
    public LensUnit RayUnit { get; set; }

    /// <summary>FINDFILE's filter and the files left to report.</summary>
    public string? FindFilter { get; set; }
    public Queue<string>? FindFiles { get; set; }

    /// <summary>The merit evaluation of the last MFCN, with the lens version it was made on.</summary>
    public (int Version, MeritEvaluation Evaluation)? LastMerit { get; set; }

    /// <summary>The CALLMACRO depth (1 for the parent).</summary>
    public int Depth { get; set; }
    public int SuspendCount { get; set; }

    public long Steps { get; private set; }
    public void CountStep() => Steps++;

    // OP-67
    /// <summary>The context keywords this run binds (a tolerance script's commands); null for a plain macro.</summary>
    public ZplDialect? Dialect { get; set; }

    /// <summary>The lens as entered (settings), changed by the keywords.</summary>
    public OpticsSettings System
    {
        get => _system;
        set { _system = value ?? throw new ArgumentNullException(nameof(value)); _version++; }
    }

    /// <summary>A number that changes whenever <see cref="System"/> does.</summary>
    public int Version => _version;

    public LensUnit Unit => ProjectLens.LensUnitOf(_system.SystemData);

    /// <summary>The current configuration's settings (the multi-configuration table applied),
    /// which the lens-data functions read.</summary>
    public OpticsSettings Current
    {
        get
        {
            if (_current is { } c && c.Version == _version) return c.Settings;
            var s = _system.MultiConfiguration is null ? _system : MultiConfiguration.CurrentSettings(_system, Catalogs);
            _current = (_version, s);
            return s;
        }
    }

    /// <summary>The current configuration's solved lens (meters).</summary>
    public SequentialLens Lens
    {
        get
        {
            if (_lens is { } l && l.Version == _version && l.Lens is not null) return l.Lens;
            var lens = ProjectLens.From(Current, Catalogs)
                ?? throw new ZplRuntimeException("the lens has no surfaces.");
            _lens = (_version, lens);
            return lens;
        }
    }

    /// <summary>The current configuration's surfaces with every solved and picked-up cell
    /// holding its solved value (in lens units), as the editor shows them; the lens-data
    /// functions read these. A solve that fails is an error naming it.</summary>
    public List<OpticsSurfaceSettings> Surfaces => Solved.Surfaces is { Count: > 0 } s ? s
        : throw new ZplRuntimeException("the lens has no surfaces.");

    private (int Version, OpticsSettings Settings)? _solved;

    public OpticsSettings Solved
    {
        get
        {
            if (_solved is { } c && c.Version == _version) return c.Settings;
            var cur = Current;
            var result = cur;
            if (cur.Surfaces is { Count: > 0 } surfaces && surfaces.Any(x => x.Flags?.Any(f => f.Kind is SavedSurfaces.SolveFlag or SavedSurfaces.PickupFlag) == true))
            {
                var solution = ProjectLens.Solve(cur, Catalogs)
                    ?? throw new ZplRuntimeException("the lens has no surfaces.");
                if (!solution.Succeeded)
                    throw new ZplRuntimeException("the lens's solves cannot be evaluated: " + string.Join("; ", solution.Errors));
                double perUnit = OpticsConventions.MetersPer(solution.Unit);
                var list = surfaces.ToList();
                foreach (var cell in solution.Solves)
                {
                    var saved = list[cell.Surface];
                    if (SavedSolves.SavedValue(solution.Lens, saved, cell, perUnit) is not double x) continue;
                    list[cell.Surface] = cell.Solve.Column switch
                    {
                        OpenSim.Optics.Sequential.Solves.SolveColumn.Curvature => saved with { Radius = double.IsInfinity(x) ? null : x },
                        OpenSim.Optics.Sequential.Solves.SolveColumn.Thickness => saved with { Thickness = x, ThicknessIsInfinite = false },
                        OpenSim.Optics.Sequential.Solves.SolveColumn.SemiDiameter => saved with { SemiDiameter = x },
                        OpenSim.Optics.Sequential.Solves.SolveColumn.Conic => saved with { Conic = x },
                        OpenSim.Optics.Sequential.Solves.SolveColumn.Tce => saved with { Tce = x },
                        OpenSim.Optics.Sequential.Solves.SolveColumn.Parameter when SavedSurfaces.ParameterNumber(cell.Column) is int n => ZplLibrary.WithParameter(saved, n, x),
                        _ => saved,
                    };
                }
                result = cur with { Surfaces = list };
            }
            _solved = (_version, result);
            return result;
        }
    }

    /// <summary>The image surface's number (surfaces are 0 to this).</summary>
    public int ImageSurface => (Current.Surfaces is { Count: > 0 } s ? s.Count
        : throw new ZplRuntimeException("the lens has no surfaces.")) - 1;

    public OpticsSurfaceSettings Surface(double number, string who)
    {
        int s = SurfaceNumber(number, who);
        return Surfaces[s];
    }

    public int SurfaceNumber(double number, string who)
    {
        int last = ImageSurface;
        if (number != Math.Floor(number) || number < 0 || number > last)
            throw new ZplRuntimeException($"{who}: surface {ZplInterpreter.Num(number)} does not exist; the lens has surfaces 0 (object) to {last} (image).");
        return (int)number;
    }

    /// <summary>Replaces surface <paramref name="number"/> of the lens as entered. Refused on a
    /// system with a multi-configuration table, where the help changes the current
    /// configuration's table entry as well (SETSURFACEPROPERTY), which this version does not.</summary>
    public void SetSurface(int number, OpticsSurfaceSettings surface, string who)
    {
        RequireSingleConfiguration(who);
        var list = new List<OpticsSurfaceSettings>(System.Surfaces!);
        list[number] = surface;
        System = System with { Surfaces = list };
    }

    public void RequireSingleConfiguration(string who)
    {
        if (System.MultiConfiguration is { Operands.Count: > 0 })
            throw new ZplRuntimeException($"{who} on a lens with a multi-configuration table is not bound in this version (the help also changes the current configuration's table entry).");
    }

    /// <summary>A path for a file name: unchanged when it has a folder, else in
    /// <paramref name="folder"/> (named <paramref name="what"/> in the message when it is not set).</summary>
    public static string Resolve(string name, string? folder, string what)
    {
        string n = name.Trim();
        if (n.Length == 0) throw new ZplRuntimeException("a file name is empty.");
        if (Path.IsPathRooted(n)) return n;
        if (folder is null) throw new ZplRuntimeException($"'{n}' has no folder and no {what} is set; give the full path.");
        return Path.Combine(folder, n);
    }
}
