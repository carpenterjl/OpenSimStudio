namespace OpenSim.Core.Model;

/// <summary>
/// The merit function (OP-54) as the project and the lens file store it: the operand rows in
/// order, each by OpticStudio's four-letter code. Null on <see cref="OpticsSettings.MeritFunction"/>
/// — every file written before OP-54 — is an empty merit function. Values and percent
/// contributions are results and are not saved; they are recomputed. OpenSim.Optics'
/// <c>MeritFunctionSettings</c> converts to and from its model and names any code it does not
/// know.
/// </summary>
public sealed record OpticsMeritFunctionSettings
{
    public List<OpticsMeritOperandSettings>? Operands { get; init; }
}

/// <summary>
/// One merit function row: the operand's four-letter code, its comment, the two integer and up to
/// six real parameters (OpticStudio's Int1, Int2 and Data1 to Data6), the target and the weight.
/// Lengths are in the lens's units, as the editor shows them.
/// </summary>
public sealed record OpticsMeritOperandSettings
{
    /// <summary>OpticStudio's four-letter operand code ("EFFL", "BLNK"...).</summary>
    public string? Type { get; init; }

    public string? Comment { get; init; }

    public int Int1 { get; init; }

    public int Int2 { get; init; }

    /// <summary>Data1 to Data6; a missing entry is zero.</summary>
    public List<double>? Data { get; init; }

    public double Target { get; init; }

    public double Weight { get; init; }
}
