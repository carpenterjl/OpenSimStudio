namespace OpenSim.Core.Model;

/// <summary>
/// The Glass Substitution Template (plan item OP-59; OpticStudio 2025 R2 help, "Glass
/// Substitution Template (global optimizers group)"): which catalog glasses glass substitution
/// and the model-to-real glass conversion may choose. Saved with the lens, as the help says
/// ("Template data is stored along with the lens file"). OpenSim.Optics' <c>GlassSubstitutionTemplate</c>
/// applies it.
/// <para>
/// The limits are the help's "Maximum Relative Cost" and the maximum CR, FR, SR, AR and PR codes.
/// The help turns each test off with zero "or uncheck the box next to this item"; here a null
/// limit is the unchecked box and zero, as in the help, also ignores the test.
/// </para>
/// </summary>
public sealed record OpticsGlassSubstitutionSettings
{
    /// <summary>"Use Glass Substitution Template": when false only the catalogs' own
    /// "Exclude Substitution" flags and the wavelength range restrict the glasses.</summary>
    public bool UseTemplate { get; init; }

    /// <summary>"Exclude Glasses With Incomplete Data": a glass that lacks a datum one of the
    /// active tests reads is not chosen.</summary>
    public bool ExcludeIncompleteData { get; init; }

    /// <summary>The acceptable catalog statuses (the help's Standard, Preferred, Obsolete and
    /// Special check boxes, additive).</summary>
    public bool AllowStandard { get; init; } = true;
    public bool AllowPreferred { get; init; } = true;
    public bool AllowObsolete { get; init; } = true;
    public bool AllowSpecial { get; init; } = true;

    /// <summary>Maximum relative cost (N-BK7 = 1); null or 0 ignores it.</summary>
    public double? MaxRelativeCost { get; init; }

    /// <summary>Maximum climatic resistance code (CR); null or 0 ignores it.</summary>
    public double? MaxClimaticResistance { get; init; }

    /// <summary>Maximum stain resistance code (FR); null or 0 ignores it.</summary>
    public double? MaxStainResistance { get; init; }

    /// <summary>Maximum acid resistance code (SR); null or 0 ignores it.</summary>
    public double? MaxAcidResistance { get; init; }

    /// <summary>Maximum alkali resistance code (AR); null or 0 ignores it.</summary>
    public double? MaxAlkaliResistance { get; init; }

    /// <summary>Maximum phosphate resistance code (PR); null or 0 ignores it.</summary>
    public double? MaxPhosphateResistance { get; init; }

    /// <summary>The catalogs glass substitution draws from, by name; null or empty is every
    /// active catalog. Not part of OpticStudio's template (its substitute solve names one
    /// catalog per surface); this program keeps one list for the lens.</summary>
    public List<string>? Catalogs { get; init; }
}
