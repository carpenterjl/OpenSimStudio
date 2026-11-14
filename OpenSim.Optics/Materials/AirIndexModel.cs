namespace OpenSim.Optics.Materials;

/// <summary>
/// The formula that gives the refractive index of air (plan item OP-13). Ciddor is the default:
/// the owner's decision OQ-10 (2026-11-12) makes the most accurate model the default and the
/// OpticStudio-compatible formula a setting (docs/Optics-Conventions.md, the two rules at the top).
/// </summary>
public enum AirIndexModel
{
    /// <summary>The Ciddor equation as NIST's Engineering Metrology Toolbox computes it
    /// (J. A. Stone and J. H. Zimmerman, "Refractive Index of Air", Documentation, Appendix A,
    /// equations A1–A41), with CO2 and humidity. Valid, per that text, for vacuum wavelengths
    /// 300–1700 nm and −40 °C to +100 °C.</summary>
    Ciddor,

    /// <summary>The formula of SCHOTT TIE-19 (Version August 2023), equations (8) and (9): the
    /// one OpticStudio-compatible readers use (AGFFileReader.jl <c>absairindex</c>,
    /// ZemaxGlass.py <c>get_dispersion</c>). Dry air of fixed composition; no stated range.</summary>
    Schott,

    /// <summary>The modified Edlén equation of the same NIST documentation (A42–A49), CO2
    /// fixed at 450 µmol/mol by the formula.</summary>
    ModifiedEdlen,
}

/// <summary>Saved names, labels and descriptions of <see cref="AirIndexModel"/>.</summary>
public static class AirIndexModels
{
    public static SavedNameTable<AirIndexModel> Names { get; } = new(
        (AirIndexModel.Ciddor, "Ciddor", "Ciddor (NIST)"),
        (AirIndexModel.Schott, "SchottTie19", "SCHOTT TIE-19 (OpticStudio-compatible)"),
        (AirIndexModel.ModifiedEdlen, "ModifiedEdlen", "Modified Edlén (NIST)"));

    /// <summary>The default: the most accurate model (OQ-10).</summary>
    public const AirIndexModel Default = AirIndexModel.Ciddor;

    /// <summary>Whether the model reads the CO2 mole fraction.</summary>
    public static bool UsesCarbonDioxide(AirIndexModel model) => model == AirIndexModel.Ciddor;

    /// <summary>Whether the model reads the humidity.</summary>
    public static bool UsesHumidity(AirIndexModel model) => model != AirIndexModel.Schott;

    /// <summary>One line naming the model and the inputs it reads, for result lines and reports.</summary>
    public static string Describe(AirIndexModel model, AirConditions air) => model switch
    {
        AirIndexModel.Ciddor =>
            $"Air index: Ciddor equation (NIST Engineering Metrology Toolbox, Appendix A, A1–A41), CO2 {Number(air.CarbonDioxideMicromolesPerMole)} µmol/mol, relative humidity {Number(air.RelativeHumidityPercent)} %.",
        AirIndexModel.Schott =>
            "Air index: SCHOTT TIE-19 equations (8)–(9), the OpticStudio-compatible formula (dry air; CO2 and humidity are not inputs of it).",
        AirIndexModel.ModifiedEdlen =>
            $"Air index: modified Edlén equation (NIST Engineering Metrology Toolbox, Appendix A, A42–A49), CO2 fixed at 450 µmol/mol by the formula, relative humidity {Number(air.RelativeHumidityPercent)} %.",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Not an air index model.")
    };

    private static string Number(double v) => v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The air a refractive index refers to: temperature, pressure and composition. Pressure zero
/// is a vacuum, whose index is 1 exactly in every model. CO2 is read by the Ciddor model only,
/// humidity by Ciddor and modified Edlén; the TIE-19 formula reads neither.
/// </summary>
/// <param name="TemperatureCelsius">Air temperature, °C.</param>
/// <param name="PressurePascals">Total air pressure, Pa; zero is a vacuum.</param>
/// <param name="CarbonDioxideMicromolesPerMole">CO2 mole fraction, µmol/mol. The default 450 is
/// the value the NIST documentation (Section IV, "CO2 Concentration") calls "a conventionally
/// accepted value ... recommended by Birch and Downs".</param>
/// <param name="RelativeHumidityPercent">Relative humidity, 0–100 %. The default is dry air (0 %):
/// OpticStudio's formula is for dry air and exposes no humidity, so dry air keeps the two models
/// comparable by default; the NIST text recommends no default humidity.</param>
public sealed record AirConditions(double TemperatureCelsius, double PressurePascals,
    double CarbonDioxideMicromolesPerMole = AirConditions.DefaultCarbonDioxide, double RelativeHumidityPercent = 0)
{
    /// <summary>450 µmol/mol: NIST EMToolbox documentation, Section IV, "CO2 Concentration".</summary>
    public const double DefaultCarbonDioxide = 450;

    /// <summary>True for a vacuum (pressure exactly zero).</summary>
    public bool IsVacuum => PressurePascals == 0;
}
