namespace OpenSim.Optics;

/// <summary>
/// The system data of an optical system (OpticStudio's System Explorer, plan item OP-18): the
/// aperture, fields, wavelengths, environment, units, title and notes. Lengths are meters,
/// angles degrees, wavelengths micrometers; the lens unit is for display and files only.
/// <para>
/// The record holds what the user entered and never refuses a value: <see cref="Validate"/>
/// lists every problem, and a trace refuses a system with any. The checks are the ones that
/// need no surfaces; those that do (an object height with an object at infinity, a real image
/// height the chief ray cannot reach) belong to the paraxial engine and ray aiming (OP-19,
/// OP-22).
/// </para>
/// </summary>
public sealed record OpticalSystemData
{
    public string Title { get; init; } = "";

    public string Notes { get; init; } = "";

    public SystemUnits Units { get; init; } = new();

    public Aperture Aperture { get; init; } = new();

    public FieldSet Fields { get; init; } = new();

    public WavelengthSet Wavelengths { get; init; } = new();

    public OpticsEnvironment Environment { get; init; } = new();

    // OP-51
    /// <summary>The system polarization (OP-51).</summary>
    public SystemPolarization Polarization { get; init; } = new();
    /// <summary>Ray aiming (OP-22); off by default.</summary>
    public RayAiming RayAiming { get; init; } = new();

    /// <summary>The system a new project starts with: a 10 mm entrance pupil, one on-axis
    /// angle field, 0.55 µm, 20 °C and 1 atm, millimeters.</summary>
    public static OpticalSystemData Default { get; } = new();

    /// <summary>Every problem, one actionable line each; empty when the data can be traced.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        Aperture.Validate(problems);
        Fields.Validate(problems);
        Wavelengths.Validate(problems);
        Environment.Validate(problems);
        Polarization.Validate(problems);
        RayAiming.Validate(problems);
        // Every table wavelength must be one the air model can convert to vacuum (OP-13).
        Environment.ValidateWavelengths(Wavelengths.Wavelengths.Select(w => w.Micrometers), problems);
        return problems;
    }
}
