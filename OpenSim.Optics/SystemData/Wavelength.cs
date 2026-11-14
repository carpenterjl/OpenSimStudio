namespace OpenSim.Optics;

/// <summary>
/// One row of the wavelength table, in micrometers as the user entered it. Kept in the editor's
/// unit, as tilts are kept in degrees, so a value reads back digit for digit; <see cref="Meters"/>
/// converts. The value has OpticStudio's meaning: measured in air at the system temperature and
/// pressure, as glass catalogs and spectral-line tables give it. The vacuum wavelength a
/// <see cref="Ray"/> carries is this times the index of air at the vacuum wavelength
/// (<see cref="VacuumMicrometers"/>, OP-13; docs/Optics-Conventions.md §2).
/// </summary>
public sealed record Wavelength
{
    public double Micrometers { get; init; } = 0.55;

    /// <summary>Relative weight; zero leaves the wavelength out of weighted results.</summary>
    public double Weight { get; init; } = 1;

    /// <summary>The wavelength in meters (still the air value; see the type's remarks).</summary>
    public double Meters => OpticsConventions.MicrometersToMeters(Micrometers);

    /// <summary>The vacuum wavelength, µm, in the air of <paramref name="environment"/>
    /// (<see cref="OpticsEnvironment.VacuumMicrometers"/>).</summary>
    public double VacuumMicrometers(OpticsEnvironment environment) => environment.VacuumMicrometers(Micrometers);
}
