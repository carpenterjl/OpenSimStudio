using OpenSim.Optics.Materials;

namespace OpenSim.Optics;

/// <summary>
/// The environment group of the system data: the system's temperature and air pressure, the
/// air index model and the air's composition, and whether glass indices are adjusted to them.
/// Kept in the editor's units (°C and atmospheres, as OpticStudio states them). The defaults,
/// 20 °C and 1 atm, are OpticStudio's (docs/Optics-Conventions.md §2); the default air model is
/// Ciddor (owner decision OQ-10), with 450 µmol/mol CO2 and dry air (<see cref="AirConditions"/>).
/// <para>
/// The air index always converts the wavelength table (in air at this temperature and pressure)
/// to vacuum wavelengths (<see cref="VacuumMicrometers"/>). <see cref="AdjustIndexToEnvironment"/>
/// decides the glasses: on, a glass's index comes from the thermal and pressure model
/// (<see cref="ThermalIndex"/>); off, it is the catalog index at the catalog's reference
/// conditions, as OP-11 gives it (<see cref="GlassIndex"/>).
/// </para>
/// </summary>
public sealed record OpticsEnvironment
{
    public double TemperatureCelsius { get; init; } = 20;

    /// <summary>Air pressure in atmospheres; zero is a vacuum.</summary>
    public double PressureAtmospheres { get; init; } = 1;

    /// <summary>Adjust glass indices to the temperature and pressure above.</summary>
    public bool AdjustIndexToEnvironment { get; init; }

    /// <summary>The formula for the index of air.</summary>
    public AirIndexModel AirModel { get; init; } = AirIndexModels.Default;

    /// <summary>CO2 mole fraction, µmol/mol (read by the Ciddor model only).</summary>
    public double CarbonDioxideMicromolesPerMole { get; init; } = AirConditions.DefaultCarbonDioxide;

    /// <summary>Relative humidity, % (read by the Ciddor and modified Edlén models).</summary>
    public double RelativeHumidityPercent { get; init; }

    /// <summary>The pressure in pascals: one atmosphere is 101325 Pa (<see cref="AirIndex.StandardPressurePascals"/>).</summary>
    public double PressurePascals => PressureAtmospheres * AirIndex.StandardPressurePascals;

    /// <summary>The system's air, as the air index models take it.</summary>
    public AirConditions Air => new(TemperatureCelsius, PressurePascals, CarbonDioxideMicromolesPerMole, RelativeHumidityPercent);

    /// <summary>One line naming the air model and its inputs, for result lines and reports.</summary>
    public string AirModelDescription => AirIndexModels.Describe(AirModel, Air);

    /// <summary>The absolute index of the system's air at a vacuum wavelength (µm).</summary>
    public double AirIndexAt(double vacuumMicrometers) => AirIndex.Absolute(AirModel, vacuumMicrometers, Air);

    /// <summary>The vacuum wavelength (µm) of a wavelength the table gives in this air (µm).</summary>
    public double VacuumMicrometers(double airMicrometers) => AirIndex.VacuumFromAir(AirModel, airMicrometers, Air);

    /// <summary>
    /// A glass's index relative to the system's air at a wavelength measured in that air (µm),
    /// as the optics code takes it. With <see cref="AdjustIndexToEnvironment"/> on, the thermal
    /// and pressure model (<see cref="ThermalIndex.AtAirWavelength"/>); off, the catalog index at
    /// that wavelength, unchanged (<see cref="OpticalMaterial.RelativeIndex"/>), as OpticStudio
    /// does with the setting off.
    /// </summary>
    public double GlassIndex(OpticalMaterial material, double airMicrometers) =>
        AdjustIndexToEnvironment
            ? ThermalIndex.AtAirWavelength(material, airMicrometers, AirModel, Air).Relative
            : material.RelativeIndex(airMicrometers);

    /// <summary>Adds one line to <paramref name="problems"/> per problem.</summary>
    public void Validate(ICollection<string> problems)
    {
        // −273.15 °C is 0 K by the definition of the Celsius scale.
        if (!double.IsFinite(TemperatureCelsius) || !(TemperatureCelsius > -273.15))
        {
            problems.Add($"Environment: the temperature must be above absolute zero, −273.15 °C (it is {TemperatureCelsius} °C).");
            return;
        }
        if (!double.IsFinite(PressureAtmospheres) || PressureAtmospheres < 0)
        {
            problems.Add($"Environment: the pressure must be zero (a vacuum) or positive (it is {PressureAtmospheres} atm).");
            return;
        }
        // The air model's own range, at a wavelength inside every model's range (the check of
        // each table wavelength is the wavelength set's, below).
        if (AirIndex.Check(AirModel, 0.55, Air) is { } problem)
            problems.Add("Environment: " + problem);
    }

    /// <summary>Adds a problem for each wavelength (in air, µm) the air model cannot take.</summary>
    public void ValidateWavelengths(IEnumerable<double> airMicrometers, ICollection<string> problems)
    {
        if (AirIndex.Check(AirModel, 0.55, Air) is not null) return;   // already reported by Validate
        foreach (double air in airMicrometers)
        {
            if (!double.IsFinite(air) || air <= 0) continue;           // the wavelength set reports these
            // The model's range is in vacuum wavelength, so the conversion itself is the check.
            try
            {
                VacuumMicrometers(air);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
            {
                problems.Add($"Wavelength {air} µm: {e.Message.Split(" (Parameter")[0]}");
            }
        }
    }
}
