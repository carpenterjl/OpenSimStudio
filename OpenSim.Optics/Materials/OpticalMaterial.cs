using System.Globalization;

namespace OpenSim.Optics.Materials;

/// <summary>
/// An optical material: a glass from a catalog, or a tabulated material. It is separate from
/// the mechanical <c>OpenSim.Core.Model.Material</c>, whose Young's modulus, Poisson ratio and
/// density are required and mean nothing to a ray trace; an optical material has its own
/// data, almost all of it optional because catalogs omit much of it.
/// <para>
/// The fields follow the AGF catalog layout (field order and meanings from the ZemaxGlass user
/// manual, zemaxglass_manual.txt, and AGFFileReader.jl, <c>src/GlassDownload.jl</c>): NM (name,
/// formula, glass code, nd, Vd, exclude-substitution, status, melt frequency), GC (comment),
/// ED (thermal expansion, density, ΔPgF, ignore-thermal-expansion), CD (coefficients), TD
/// (<see cref="ThermalData"/>), OD (relative cost and resistance classes), LD (valid range), IT
/// (<see cref="TransmittancePoint"/>). Numbers a file prints as "-" or "_" are absent (null).
/// Codes such as −1 that vendors print for "no data" are kept as printed.
/// </para>
/// <para>
/// <b>Reference conditions.</b> <see cref="RelativeIndex"/> is the index the catalog states:
/// relative to air at the catalog's reference temperature (<see cref="ThermalData.ReferenceTemperatureCelsius"/>)
/// and pressure, at a wavelength measured in that air. AGFFileReader takes that pressure as
/// 1 atm (<c>src/constants.jl</c>, <c>PRESSURE_REF = 1.0</c>). No temperature or pressure
/// correction is applied here: converting to the absolute index, shifting it by the TD model and
/// referring it to the system's air is <see cref="ThermalIndex"/>'s (OP-13), which builds on
/// this method. A material without a TD line has no stated reference temperature.
/// </para>
/// </summary>
public sealed record OpticalMaterial
{
    /// <summary>The name as the catalog prints it.</summary>
    public required string Name { get; init; }

    /// <summary>The catalog the material came from (its file name without extension), or "".</summary>
    public string Catalog { get; init; } = "";

    /// <summary>The GC line(s): the vendor's comment.</summary>
    public string Comment { get; init; } = "";

    /// <summary>The dispersion formula number as the NM line prints it; null for a tabulated
    /// material. A number outside 1–13 is kept, and evaluating the material names it.</summary>
    public int? FormulaNumber { get; init; }

    /// <summary>C1, C2, … from the CD line, as printed.</summary>
    public IReadOnlyList<double> Coefficients { get; init; } = Array.Empty<double>();

    /// <summary>The n, k table of a tabulated material; null for a formula material.</summary>
    public TabulatedIndex? Tabulated { get; init; }

    /// <summary>The LD line: the shortest and longest wavelength (µm) the data are valid for.
    /// A tabulated material's range is its table's.</summary>
    public double? MinMicrometers { get; init; }
    public double? MaxMicrometers { get; init; }

    /// <summary>NM: the printed nd and Vd.</summary>
    public PrintedNumber? Nd { get; init; }
    public PrintedNumber? Vd { get; init; }

    /// <summary>NM field 3: the six-digit glass code the manual calls "MIL", kept as text
    /// (vendors print it as "517642", "755523.422" or "000000.000").</summary>
    public string? GlassCode { get; init; }

    /// <summary>NM: excluded from glass substitution during optimization.</summary>
    public bool ExcludeSubstitution { get; init; }

    /// <summary>NM: the status number as printed, and its meaning when it is one of 0–4.</summary>
    public int? StatusNumber { get; init; }
    public GlassStatus? Status => StatusNumber is int s && Enum.IsDefined(typeof(GlassStatus), s) ? (GlassStatus)s : null;

    /// <summary>NM: melt frequency, 1 (melted very frequently) to 5 (infrequently), per the manual.</summary>
    public int? MeltFrequency { get; init; }

    /// <summary>ED: thermal expansion coefficient over −30…+70 °C and over 100…300 °C, as printed
    /// (×10⁻⁶; the manual's ED layout names the two ranges).</summary>
    public double? ThermalExpansion { get; init; }
    public double? ThermalExpansionHigh { get; init; }

    /// <summary>ED: density, g/cm³ (the manual: "g/cc").</summary>
    public double? Density { get; init; }

    /// <summary>ED: the relative partial dispersion deviation ΔPgF, as printed.</summary>
    public double? DeltaPgF { get; init; }

    /// <summary>ED: ignore thermal expansion.</summary>
    public bool IgnoreThermalExpansion { get; init; }

    /// <summary>TD: the dn/dT constants and reference temperature; null when the file has no TD
    /// line or an empty one.</summary>
    public ThermalData? Thermal { get; init; }

    /// <summary>OD: cost relative to N-BK7, and the climatic (CR), stain (FR), acid (SR),
    /// alkali (AR) and phosphate (PR) resistance classes, as printed.</summary>
    public double? RelativeCost { get; init; }
    public double? ClimaticResistance { get; init; }
    public double? StainResistance { get; init; }
    public double? AcidResistance { get; init; }
    public double? AlkaliResistance { get; init; }
    public double? PhosphateResistance { get; init; }

    /// <summary>IT: internal transmittance against wavelength, in file order.</summary>
    public IReadOnlyList<TransmittancePoint> Transmittance { get; init; } = Array.Empty<TransmittancePoint>();

    /// <summary>The dispersion formula, when <see cref="FormulaNumber"/> is one of the thirteen.</summary>
    public DispersionFormula? Formula =>
        FormulaNumber is int n && DispersionFormulas.IsDefined(n) ? (DispersionFormula)n : null;

    /// <summary>The valid range actually used: the table's for a tabulated material, the LD
    /// line's otherwise; null when the material states none.</summary>
    public (double Min, double Max)? ValidRange =>
        Tabulated is { Points.Count: >= 2 } t ? (t.MinMicrometers, t.MaxMicrometers)
        : MinMicrometers is double lo && MaxMicrometers is double hi ? (lo, hi) : null;

    /// <summary>True when <paramref name="micrometers"/> lies in <see cref="ValidRange"/> (ends included).</summary>
    public bool Covers(double micrometers) => ValidRange is { } r && micrometers >= r.Min && micrometers <= r.Max;

    /// <summary>
    /// The refractive index at <paramref name="micrometers"/> at the catalog's reference
    /// conditions (see the class remarks): relative to the catalog's air, at a wavelength in
    /// that air, in µm. Fails loudly, naming the material, catalog and wavelength, when the
    /// wavelength is outside the valid range (nothing is extrapolated), when the material
    /// states no range, when its formula number is not one of the thirteen, when the CD line is
    /// short, or when the coefficients give no real index there.
    /// </summary>
    public double RelativeIndex(double micrometers)
    {
        CheckRange(micrometers);
        if (Tabulated is { } table) return table.Interpolate(micrometers).N;
        var formula = RequireFormula();
        double n;
        try
        {
            n = DispersionFormulas.Evaluate(formula, Coefficients, micrometers);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"{Who}: {e.Message.Split(" (Parameter")[0]}", e);
        }
        if (!double.IsFinite(n) || n <= 0)
            throw new InvalidOperationException(
                $"{Who}: dispersion formula {DispersionFormulas.NameOf((int)formula)} gives no real positive index at {Format(micrometers)} µm (it gives {n}); the coefficients are not usable there.");
        return n;
    }

    /// <summary>The extinction coefficient k of a tabulated material at <paramref name="micrometers"/>,
    /// range-checked like <see cref="RelativeIndex"/>. Null for a formula material: a catalog
    /// glass carries its absorption in the <see cref="Transmittance"/> table, not as k.</summary>
    public double? ExtinctionCoefficient(double micrometers)
    {
        if (Tabulated is not { } table) return null;
        CheckRange(micrometers);
        return table.Interpolate(micrometers).K;
    }

    /// <summary>"Glass 'N-BK7' in catalog 'schott'", for messages.</summary>
    public string Who => string.IsNullOrEmpty(Catalog) ? $"Glass '{Name}'" : $"Glass '{Name}' in catalog '{Catalog}'";

    private void CheckRange(double micrometers)
    {
        if (Tabulated is { } table && table.Validate() is { Count: > 0 } problems)
            throw new InvalidOperationException($"{Who}: the index table is not usable: {string.Join(" ", problems)}");
        if (ValidRange is not { } range)
            throw new InvalidOperationException($"{Who} states no valid wavelength range (no LD line), so no wavelength can be checked against it.");
        var (lo, hi) = range;
        if (!double.IsFinite(micrometers) || micrometers < lo || micrometers > hi)
            throw new ArgumentOutOfRangeException(nameof(micrometers), micrometers,
                $"{Who}: {Format(micrometers)} µm is outside its valid range {Format(lo)}–{Format(hi)} µm. Indices are not extrapolated.");
    }

    private DispersionFormula RequireFormula()
    {
        if (FormulaNumber is not int number)
            throw new InvalidOperationException($"{Who} has neither a dispersion formula nor an index table.");
        if (!DispersionFormulas.IsDefined(number))
            throw new NotSupportedException(
                $"{Who} uses dispersion formula {number}, which is not one of OpticStudio's thirteen (1–13). " +
                "AGFFileReader's own 0, −1 and −2 (Cauchy, fitted model) are not OpticStudio formulas and are not evaluated.");
        return (DispersionFormula)number;
    }

    private static string Format(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
