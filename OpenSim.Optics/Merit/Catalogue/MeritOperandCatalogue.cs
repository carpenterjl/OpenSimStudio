// OP-55B
namespace OpenSim.Optics.Merit.Catalogue;

/// <summary>
/// What a four-letter operand code is to this build, for the messages of the merit function
/// editor, the saved-file and .zmx/.MF readers and the <c>optics</c> command line: a registered
/// operand ("Edge thickness value (ETVA)"), an OpticStudio operand that is not built yet (with
/// its description and the plan item it waits for, from <see cref="NotBuiltOperands"/>), or no
/// operand at all. <see cref="MeritOperandRegistry.Find"/> is unchanged; this lookup is additive.
/// </summary>
public static class MeritOperandCatalogue
{
    /// <summary>
    /// One sentence about <paramref name="code"/>: "Edge thickness value (ETVA): …" when the
    /// registry has it; "NSDD (OpticStudio: …) is not built yet: needs OP-88: …" when it is an
    /// OpticStudio operand this build lacks; "there is no operand 'XXXX'." otherwise.
    /// </summary>
    public static string Describe(string? code, MeritOperandRegistry? registry = null)
    {
        string c = (code ?? "").Trim();
        registry ??= DefaultRegistry.Value;
        if (registry.Find(c) is { } d) return $"{d.DisplayName}: {d.Description}";
        return Unknown(c);
    }

    /// <summary>The message for a code the registry does not have: the not-built sentence, or
    /// "there is no operand 'XXXX'.".</summary>
    public static string Unknown(string? code)
    {
        string c = (code ?? "").Trim();
        if (NotBuiltOperands.Find(c) is not { } e) return $"there is no operand '{c}'.";
        string what = e.Description.Length > 0 ? $"OpticStudio: {TrimPeriod(e.Description)}" : "an OpticStudio operand";
        return $"{c.ToUpperInvariant()} ({what}) is not built yet: {TrimPeriod(e.Why)}.";
    }

    /// <summary>True when <paramref name="code"/> is an OpticStudio operand listed as not built.</summary>
    public static bool IsNotBuilt(string? code) => NotBuiltOperands.Find(code) is not null;

    private static readonly Lazy<MeritOperandRegistry> DefaultRegistry = new(MeritOperandRegistry.CreateDefault);

    private static string TrimPeriod(string s) => s.TrimEnd().TrimEnd('.');
}
