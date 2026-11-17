// OP-88
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;

namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// What the non-sequential rows of one merit function evaluation share in one configuration
/// (plan item OP-88; docs/Optics-Conventions.md §76): the configuration's optics record and
/// lens unit, its scene (built on first use), the data each detector object holds (NSTR rows add
/// to it, NSDD, NSDE and NSDP rows with Det# 0 or −n clear it, the detector rows read it), whether
/// every detector has been cleared, the last NSTR ledger, and caches for the rows that read a
/// group of neighbours (NSDD Data 3, NSDE Data 14–16) or one traced ray (NSRA). Lives in the
/// evaluation's <see cref="MeritEvaluationState"/>, so each evaluation starts empty: the
/// detectors never carry data from one evaluation into the next.
/// </summary>
internal sealed class NscMeritSession
{
    private NscScene? _scene;

    private NscMeritSession(int configuration, OpticsSettings settings, LensUnit unit, OpticsAnalysisContext context)
    {
        Configuration = configuration;
        Settings = settings;
        Unit = unit;
        Context = context;
    }

    public int Configuration { get; }

    public OpticsSettings Settings { get; }

    public LensUnit Unit { get; }

    public OpticsAnalysisContext Context { get; }

    /// <summary>The data on each detector object, by object number.</summary>
    public Dictionary<int, NscDetectorData> Detectors { get; } = new();

    /// <summary>True once a row with Det# 0 has cleared every detector in this configuration.</summary>
    public bool ClearedAll { get; set; }

    /// <summary>The energy ledger of the most recent NSTR row, or null.</summary>
    public NscEnergyLedger? Ledger { get; set; }

    /// <summary>The normaliser of each group of consecutive NSDD Data 3 or NSDE Data 14–16 rows,
    /// by the group's first row number.</summary>
    public Dictionary<int, double> GroupPeaks { get; } = new();

    /// <summary>NSRA's traced rays, by (source, split, polarization, element).</summary>
    public Dictionary<(int Source, bool Split, bool Polar, int Element), NscRayTree> SingleRays { get; } = new();

    /// <summary>The objects as saved (object 1 first).</summary>
    public IReadOnlyList<OpticsNscObjectSettings> Rows => Settings.NonSequential?.Objects ?? (IReadOnlyList<OpticsNscObjectSettings>)Array.Empty<OpticsNscObjectSettings>();

    /// <summary>The scene, built once. Throws <see cref="MeritOperandException"/> when the system
    /// has no objects or an object cannot be built (naming it).</summary>
    public NscScene Scene
    {
        get
        {
            if (_scene is not null) return _scene;
            NscScene? scene;
            try { scene = ProjectNonSequential.Scene(Context, Unit); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                throw new MeritOperandException(OneLine(e.Message));
            }
            return _scene = scene ?? throw new MeritOperandException("the system has no non-sequential objects (add them in the Non-Sequential Component Editor).");
        }
    }

    /// <summary>The detector data the rows read on object <paramref name="number"/>: what traces
    /// left, or the detector's empty data (never traced, or cleared, reads zero).</summary>
    public NscDetectorData DataOf(int number, string who)
    {
        CheckObject(number, who);
        if (Detectors.TryGetValue(number, out var held)) return held;
        double primary = SystemDataSettings.FromSettings(Settings.SystemData, new List<string>()).Wavelengths.Primary.Micrometers;
        return NscDetectorQueries.Empty(Scene, number, primary)
            ?? throw new MeritOperandException($"{who} {number} is a {Scene.Objects[number - 1].Geometry.TypeName}, not a detector object (Detector Rectangle, Color or Polar).");
    }

    /// <summary>Checks that object <paramref name="number"/> exists in the scene.</summary>
    public void CheckObject(int number, string who)
    {
        int count = Scene.Objects.Count;
        if (number < 1 || number > count)
            throw new MeritOperandException($"{who} {number} is not an object; the system has objects 1 to {count}.");
    }

    /// <summary>Adds a trace's detector data to what the detectors hold.</summary>
    public void Add(IReadOnlyList<NscDetectorData>? data)
    {
        foreach (var d in data ?? Array.Empty<NscDetectorData>())
        {
            if (Detectors.TryGetValue(d.ObjectNumber, out var held))
            {
                try { Detectors[d.ObjectNumber] = held.Combine(d); }
                catch (ArgumentException e)
                {
                    throw new MeritOperandException($"detector object {d.ObjectNumber}'s new data cannot be added to the data it holds ({OneLine(e.Message)}); clear it first (an NSDD row with Det# −{d.ObjectNumber} or 0).");
                }
            }
            else Detectors[d.ObjectNumber] = d;
        }
    }

    /// <summary>The session of the row's configuration in the row's evaluation, made on first use.</summary>
    public static NscMeritSession Of(MeritRowContext c)
    {
        var state = c.State ?? throw new MeritOperandException("non-sequential rows keep their detector data in the evaluation's shared state, and this evaluation has none (evaluate the merit function with MeritEvaluator).");
        return state.GetOrAdd($"nsc:{c.Configuration}", () => Create(c));
    }

    /// <summary>The 0-based count of NSTR rows evaluated before this one in the evaluation (any
    /// configuration), and one more counted: NSTR's random stream number.</summary>
    public static long NextTrace(MeritRowContext c)
    {
        var counter = c.State!.GetOrAdd("nsc:traces", () => new Counter());
        return counter.Value++;
    }

    private sealed class Counter
    {
        public long Value;
    }

    private static NscMeritSession Create(MeritRowContext c)
    {
        OpticsSettings? settings;
        try { settings = c.Configurations?.SettingsFor(c.Configuration); }
        catch (ArgumentException e) { throw new MeritOperandException($"configuration {c.Configuration} cannot be built: {OneLine(e.Message)}"); }
        if (settings is null)
            throw new MeritOperandException("these configurations carry no project, so there is no non-sequential system to read (the evaluation was given a lens; non-sequential rows need the project's configurations).");
        var unit = SystemDataSettings.FromSettings(settings.SystemData, new List<string>()).Units.LensUnit;
        var context = new OpticsAnalysisContext("Merit function", settings) { Catalogs = c.Catalogs ?? Array.Empty<Materials.OpticalCatalog>() };
        return new NscMeritSession(c.Configuration, settings, unit, context);
    }

    internal static string OneLine(string message)
    {
        var lines = message.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToArray();
        return lines.Length switch
        {
            0 => message,
            1 => lines[0],
            _ => lines[0].TrimEnd(':') + ": " + string.Join("; ", lines.Skip(1)),
        };
    }
}

/// <summary>
/// The fixed random streams of the non-sequential merit rows (plan item OP-88;
/// docs/Optics-Conventions.md §76). OpticStudio's help: "when launching rays with the NSTR
/// operand, OpticStudio seeds the random number generator with the identical value every time the
/// merit function is evaluated. This means the identical set of random numbers are used on every
/// evaluation of the merit function" (help, "NSC Operands", "Comments about Random Numbers and
/// NSTR"). Here the k-th NSTR row evaluated in an evaluation (k = 0, 1, ...; counted over every
/// configuration in evaluation order) traces with the seed SplitMix64.ForStream(<see cref="BaseSeed"/>, k),
/// whatever the variables are: so two evaluations at the same design give the same bits, and
/// evaluations at nearby designs use the same random numbers (common random numbers), which keeps
/// the Monte Carlo noise from showing as merit-function noise between designs.
/// </summary>
public static class NscMeritRandom
{
    /// <summary>The base seed: the trace settings' default seed (<see cref="NscTraceSettings.Seed"/>).</summary>
    public static long BaseSeed { get; } = new NscTraceSettings().Seed;

    /// <summary>The seed of the <paramref name="trace"/>-th NSTR row (0-based) of an evaluation.</summary>
    public static long SeedOf(long trace) => unchecked((long)SplitMix64.ForStream(BaseSeed, trace).Next());
}
