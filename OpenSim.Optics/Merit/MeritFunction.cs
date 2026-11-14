namespace OpenSim.Optics.Merit;

/// <summary>
/// One row of a merit function (OP-54): the operand's four-letter code, the two integer and six
/// real parameters, the target and the weight, as OpticStudio's Merit Function Editor holds them
/// ("The fields are called Int1 and Int2 for the two integer values, and Data1 through Data6 for
/// up to 6 double precision values", OpticStudio 2025 R2 help, Merit Function Editor (automatic
/// optimization group)). Which parameter means what is the operand's
/// (<see cref="MeritOperandDefinition"/>). Lengths are in the lens's units.
/// </summary>
public sealed record MeritOperandRow
{
    /// <summary>The number of real parameters (Data1 to Data6).</summary>
    public const int DataCount = 6;

    private readonly IReadOnlyList<double> _data = new double[DataCount];

    /// <summary>OpticStudio's four-letter code ("EFFL", "BLNK"...).</summary>
    public required string Code { get; init; }

    public string Comment { get; init; } = "";

    public int Int1 { get; init; }

    public int Int2 { get; init; }

    /// <summary>Data1 to Data6, always six entries (a shorter list is padded with zeros).</summary>
    public IReadOnlyList<double> Data
    {
        get => _data;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count > DataCount)
                throw new ArgumentException($"A merit operand has {DataCount} real parameters; {value.Count} were given.", nameof(value));
            var d = new double[DataCount];
            for (int i = 0; i < value.Count; i++) d[i] = value[i];
            _data = d;
        }
    }

    public double Target { get; init; }

    public double Weight { get; init; }

    /// <summary>Data<paramref name="k"/> (1 to 6).</summary>
    public double DataAt(int k) => _data[k - 1];

    /// <summary>A row of <paramref name="code"/> with the given parameters.</summary>
    public static MeritOperandRow Of(string code, int int1 = 0, int int2 = 0, double target = 0, double weight = 0,
        params double[] data) => new() { Code = code, Int1 = int1, Int2 = int2, Target = target, Weight = weight, Data = data };

    public bool Equals(MeritOperandRow? other) =>
        other is not null && Code == other.Code && Comment == other.Comment && Int1 == other.Int1 && Int2 == other.Int2
        && Target.Equals(other.Target) && Weight.Equals(other.Weight) && _data.SequenceEqual(other._data);

    public override int GetHashCode() => HashCode.Combine(Code, Int1, Int2, Target, Weight);
}

/// <summary>
/// A merit function: operand rows in order, numbered from 1 as in OpticStudio's editor. Math
/// operands and the GOTO, SKIS and SKIN controls refer to rows by that number.
/// </summary>
public sealed record MeritFunction
{
    public IReadOnlyList<MeritOperandRow> Rows { get; init; } = Array.Empty<MeritOperandRow>();

    public MeritFunction() { }

    public MeritFunction(IEnumerable<MeritOperandRow> rows) => Rows = rows.ToArray();

    /// <summary>The row numbered <paramref name="number"/> (1 = the first).</summary>
    public MeritOperandRow Row(int number) => Rows[number - 1];
}
