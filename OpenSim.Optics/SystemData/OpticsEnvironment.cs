namespace OpenSim.Optics;

/// <summary>
/// The environment group of the system data: the system's temperature and air pressure, and
/// whether indices are adjusted to them. Kept in the editor's units (°C and atmospheres, as
/// OpticStudio states them). The defaults, 20 °C and 1 atm, are OpticStudio's
/// (docs/Optics-Conventions.md §2). The index model that uses them is the thermal and pressure
/// item's (OP-13); until it exists the values are held and saved but change nothing.
/// </summary>
public sealed record OpticsEnvironment
{
    public double TemperatureCelsius { get; init; } = 20;

    /// <summary>Air pressure in atmospheres; zero is a vacuum.</summary>
    public double PressureAtmospheres { get; init; } = 1;

    /// <summary>Adjust glass and air indices to the temperature and pressure above.</summary>
    public bool AdjustIndexToEnvironment { get; init; }

    /// <summary>Adds one line to <paramref name="problems"/> per problem.</summary>
    public void Validate(ICollection<string> problems)
    {
        // −273.15 °C is 0 K by the definition of the Celsius scale.
        if (!double.IsFinite(TemperatureCelsius) || !(TemperatureCelsius > -273.15))
            problems.Add($"Environment: the temperature must be above absolute zero, −273.15 °C (it is {TemperatureCelsius} °C).");
        if (!double.IsFinite(PressureAtmospheres) || PressureAtmospheres < 0)
            problems.Add($"Environment: the pressure must be zero (a vacuum) or positive (it is {PressureAtmospheres} atm).");
    }
}
