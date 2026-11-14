namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// The per-cell flag: fixed, variable, a solve with its code and parameters, or a pickup with its
/// source. Immutable; compare with <see cref="Equals(CellFlag?)"/>, which compares the solve
/// parameters element by element.
/// </summary>
public sealed record CellFlag
{
    private CellFlag(CellFlagKind kind, string? solveCode, IReadOnlyList<double> parameters, PickupSource? pickup)
    {
        Kind = kind;
        SolveCode = solveCode;
        SolveParameters = parameters;
        Pickup = pickup;
    }

    /// <summary>Which of the four flags this is.</summary>
    public CellFlagKind Kind { get; }

    /// <summary>The solve's <see cref="SheetSolveCode.Key"/>; null unless <see cref="Kind"/> is Solve.</summary>
    public string? SolveCode { get; }

    /// <summary>The solve's parameters, in the order of <see cref="SheetSolveCode.ParameterNames"/>.</summary>
    public IReadOnlyList<double> SolveParameters { get; }

    /// <summary>The pickup's source; null unless <see cref="Kind"/> is Pickup.</summary>
    public PickupSource? Pickup { get; }

    /// <summary>A typed-in value.</summary>
    public static CellFlag Fixed { get; } = new(CellFlagKind.Fixed, null, Array.Empty<double>(), null);

    /// <summary>A value the optimizer may change.</summary>
    public static CellFlag Variable { get; } = new(CellFlagKind.Variable, null, Array.Empty<double>(), null);

    /// <summary>A solve. Parameters left out are taken as zero when the flag is set on a cell.</summary>
    public static CellFlag Solve(string code, params double[] parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return new(CellFlagKind.Solve, code, (double[])parameters.Clone(), null);
    }

    /// <summary>A pickup from <paramref name="source"/>.</summary>
    public static CellFlag PickupFrom(PickupSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(CellFlagKind.Pickup, null, Array.Empty<double>(), source);
    }

    /// <summary>A pickup from <paramref name="row"/>'s grid column <paramref name="column"/>.</summary>
    public static CellFlag PickupFrom(SheetRow row, int column, double scale = 1, double offset = 0) =>
        PickupFrom(new PickupSource(row.Id, column, scale, offset));

    /// <summary>The same solve with its parameter list replaced (used to pad missing ones).</summary>
    internal CellFlag WithParameters(IReadOnlyList<double> parameters) =>
        new(Kind, SolveCode, parameters.ToArray(), Pickup);

    /// <inheritdoc/>
    public bool Equals(CellFlag? other) =>
        other is not null
        && Kind == other.Kind
        && SolveCode == other.SolveCode
        && Equals(Pickup, other.Pickup)
        && SolveParameters.SequenceEqual(other.SolveParameters);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, SolveCode, Pickup, SolveParameters.Count);

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        CellFlagKind.Solve => $"Solve {SolveCode}",
        CellFlagKind.Pickup => $"Pickup {Pickup}",
        _ => Kind.ToString(),
    };
}
