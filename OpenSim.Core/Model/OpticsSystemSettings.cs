namespace OpenSim.Core.Model;

/// <summary>
/// An optical system's system data (aperture, fields, wavelengths, environment, units, title
/// and notes; plan items OP-18, OP-10) as project and lens files store it. Lengths are in the
/// file's lens unit (<see cref="LensUnit"/>), as an OpticStudio lens file stores them and as the
/// editor shows them, so a value reads back digit for digit; angles are degrees, wavelengths
/// micrometers, temperature °C, pressure atmospheres. Every choice is the saved name
/// OpenSim.Optics defines for it, null meaning the default; a name this build does not know is
/// reported on load and the default kept. The defaults are those of the default system.
/// OpenSim.Optics' <c>SystemDataSettings</c> converts to and from its record in meters.
/// </summary>
public sealed record OpticsSystemSettings
{
    public string? Title { get; init; }
    public string? Notes { get; init; }

    // Units, by saved name.
    public string? LensUnit { get; init; }
    public string? SourceUnit { get; init; }
    public string? AnalysisAreaUnit { get; init; }
    public string? MtfUnit { get; init; }

    // Aperture. The value is in lens units for the entrance-pupil diameter, degrees for the
    // object cone angle, and dimensionless otherwise.
    public string? ApertureType { get; init; }
    public double ApertureValue { get; init; } = 10;
    public string? Apodization { get; init; }
    public double ApodizationFactor { get; init; }
    public double SemiDiameterMargin { get; init; }
    public double SemiDiameterMarginPercent { get; init; }
    public bool TelecentricObjectSpace { get; init; }
    public bool AfocalImageSpace { get; init; }

    // Fields: x and y in degrees or lens units, by the field type.
    public string? FieldType { get; init; }
    public string? FieldNormalization { get; init; }
    public List<OpticsFieldSettings>? Fields { get; init; }

    // Wavelengths; the primary is 1-based, as the table numbers rows.
    public List<OpticsWavelengthSettings>? Wavelengths { get; init; }
    public int PrimaryWavelength { get; init; } = 1;

    // Environment.
    public double TemperatureCelsius { get; init; } = 20;
    public double PressureAtmospheres { get; init; } = 1;
    public bool AdjustIndexToEnvironment { get; init; }
}

/// <summary>One field point: x and y in the field type's unit, the weight and the vignetting
/// factors (decenter and compression as fractions of the pupil radius, the angle in degrees).</summary>
public sealed record OpticsFieldSettings
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Weight { get; init; } = 1;
    public double DecenterX { get; init; }
    public double DecenterY { get; init; }
    public double CompressionX { get; init; }
    public double CompressionY { get; init; }
    public double TiltDegrees { get; init; }
}

/// <summary>One wavelength in micrometers (in air at the system temperature and pressure) and
/// its weight.</summary>
public sealed record OpticsWavelengthSettings
{
    public double Micrometers { get; init; } = 0.55;
    public double Weight { get; init; } = 1;
}
