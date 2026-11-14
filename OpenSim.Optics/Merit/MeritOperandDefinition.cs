using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Merit;

/// <summary>What a control or configuration row does to the evaluation (OpticStudio 2025 R2
/// help, Merit Function Control Operands; Changing System Data for CONF).</summary>
public enum MeritControl
{
    /// <summary>An operand with a value.</summary>
    None,

    /// <summary>BLNK: does nothing; a comment line.</summary>
    Blank,

    /// <summary>DMFS: marks where a default merit function would be appended (OP-56); does
    /// nothing in the evaluation.</summary>
    DefaultStart,

    /// <summary>ENDX: ends the evaluation; the rows after it are not evaluated.</summary>
    End,

    /// <summary>GOTO: evaluation continues at row Op# (Int1).</summary>
    GoTo,

    /// <summary>OOFF: an unused row (a code that was not recognised); evaluated as BLNK.</summary>
    Off,

    /// <summary>SKIN: when the lens is not rotationally symmetric, evaluation continues at row Op#.</summary>
    SkipIfNotSymmetric,

    /// <summary>SKIS: when the lens is rotationally symmetric, evaluation continues at row Op#.</summary>
    SkipIfSymmetric,

    /// <summary>USYM: present anywhere, the lens is taken as rotationally symmetric.</summary>
    AssumeSymmetry,

    /// <summary>CONF: the rows after it are evaluated in configuration Cfg# (Int1).</summary>
    Configuration,
}

/// <summary>The one real ray a row reads: wavelength number (1 = the first row of the
/// wavelength table) and normalised field and pupil coordinates (docs/Optics-Conventions.md §7).</summary>
public readonly record struct MeritRayRequest(int Wavelength, double Hx, double Hy, double Px, double Py);

/// <summary>
/// One operand type of the registry: OpenSim's descriptive name and OpticStudio's four-letter
/// code (OQ-13: own names, the codes alongside, searchable and used on import), what each of the
/// eight parameter columns means for it (null = unused), and how its value is computed.
/// </summary>
public sealed class MeritOperandDefinition
{
    /// <summary>OpticStudio's four-letter code.</summary>
    public required string Code { get; init; }

    /// <summary>OpenSim's descriptive name.</summary>
    public required string Name { get; init; }

    /// <summary>What the value is, one or two sentences, from the help's definition.</summary>
    public required string Description { get; init; }

    /// <summary>The help's category ("First-order optical properties", "General math operands"...).</summary>
    public required string Category { get; init; }

    /// <summary>The OpticStudio help page the definition was read from.</summary>
    public required string Source { get; init; }

    /// <summary>The Int1 column's meaning ("Surf", "Op#"...), or null when unused.</summary>
    public string? Int1 { get; init; }

    /// <summary>The Int2 column's meaning, or null when unused.</summary>
    public string? Int2 { get; init; }

    /// <summary>Data1 to Data6's meanings, null where unused; shorter lists leave the rest unused.</summary>
    public IReadOnlyList<string?> Data { get; init; } = Array.Empty<string?>();

    /// <summary>False for rows that do not use the target and weight columns (the controls and CONF).</summary>
    public bool UsesTargetAndWeight { get; init; } = true;

    /// <summary>What the row does to the evaluation; <see cref="MeritControl.None"/> for operands with a value.</summary>
    public MeritControl Control { get; init; }

    /// <summary>True for a one-sided bound: its value is the target while the bound holds.</summary>
    public bool IsBound { get; init; }

    /// <summary>The real ray the row reads, or null for none. Rays are traced once per
    /// configuration, wavelength, field and pupil point and shared between rows.</summary>
    public Func<MeritOperandRow, MeritRayRequest>? Ray { get; init; }

    /// <summary>The rows (numbers) whose values the row reads; null for none. Each must come
    /// before the row and be an operand with a value.</summary>
    public Func<MeritOperandRow, IEnumerable<int>>? References { get; init; }

    /// <summary>The value. Throws <see cref="MeritOperandException"/> where it cannot be computed,
    /// naming why; null for control rows.</summary>
    public Func<MeritRowContext, double>? Compute { get; init; }

    /// <summary>The label of Data<paramref name="k"/> (1 to 6), or null when unused.</summary>
    public string? DataLabel(int k) => k >= 1 && k <= Data.Count ? Data[k - 1] : null;

    /// <summary>"Effective focal length (EFFL)".</summary>
    public string DisplayName => $"{Name} ({Code})";

    public override string ToString() => DisplayName;
}

/// <summary>A row's value cannot be computed; the message says why (the evaluator adds the row).</summary>
public sealed class MeritOperandException : Exception
{
    public MeritOperandException(string message) : base(message) { }
}

/// <summary>
/// What an operand's <see cref="MeritOperandDefinition.Compute"/> reads: its row, the lens and
/// system data of the configuration the row is evaluated in, the rays traced for it, the
/// paraxial model at a wavelength, and the values of earlier rows.
/// </summary>
public sealed class MeritRowContext
{
    private readonly Func<int, double> _valueOf;
    private readonly Func<MeritRayRequest, RayPath> _ray;
    private readonly Func<int, ParaxialSystem> _paraxial;

    internal MeritRowContext(MeritOperandRow row, int number, int configuration, SequentialLens lens, OpticalSystemData system,
        Func<int, double> valueOf, Func<MeritRayRequest, RayPath> ray, Func<int, ParaxialSystem> paraxial)
    {
        Row = row;
        Number = number;
        Configuration = configuration;
        Lens = lens;
        System = system;
        _valueOf = valueOf;
        _ray = ray;
        _paraxial = paraxial;
    }

    public MeritOperandRow Row { get; }

    /// <summary>The row's number (1 = the first).</summary>
    public int Number { get; }

    /// <summary>The configuration (1 = the first) the row is evaluated in.</summary>
    public int Configuration { get; }

    public SequentialLens Lens { get; }

    public OpticalSystemData System { get; }

    public LensUnit Unit => System.Units.LensUnit;

    /// <summary>A length in meters, in lens units.</summary>
    public double Length(double meters) => OpticsConventions.FromMeters(meters, Unit);

    /// <summary>A curvature in 1/m, in 1/lens unit.</summary>
    public double Curvature(double perMeter) => perMeter / OpticsConventions.FromMeters(1.0, Unit);

    /// <summary>The value of earlier row <paramref name="number"/>; throws when that row was
    /// skipped or failed.</summary>
    public double ValueOf(int number) => _valueOf(number);

    /// <summary>The traced path of the row's ray (<see cref="MeritOperandDefinition.Ray"/>).</summary>
    public RayPath Ray(MeritRayRequest request) => _ray(request);

    /// <summary>The paraxial model at wavelength number <paramref name="wavelength"/> (1-based).</summary>
    public ParaxialSystem Paraxial(int wavelength) => _paraxial(wavelength);

    /// <summary>Checks that <paramref name="surface"/> (the column <paramref name="label"/>) is
    /// a surface of the lens, object (0) to image.</summary>
    public int Surface(int surface, string label = "Surf")
    {
        if (surface < 0 || surface > Lens.ImageIndex)
            throw new MeritOperandException($"{label} {surface} is not a surface; the lens has surfaces 0 (object) to {Lens.ImageIndex} (image).");
        return surface;
    }

    /// <summary>Checks a wavelength number (1-based) against the system's table.</summary>
    public int Wavelength(int wavelength, string label = "Wave")
    {
        int count = System.Wavelengths.Wavelengths.Count;
        if (wavelength < 1 || wavelength > count)
            throw new MeritOperandException($"{label} {wavelength} is not a wavelength; the system has wavelengths 1 to {count}.");
        return wavelength;
    }
}
