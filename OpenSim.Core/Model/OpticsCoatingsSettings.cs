namespace OpenSim.Core.Model;

/// <summary>
/// The thin-film coating catalog (OP-15) as the project, the lens file and the stand-alone
/// coating catalog file (<c>*.oscoat.json</c>) store it: coating materials, taper profiles and
/// coatings, in the shapes of OpticStudio's coating file sections (MATE, TAPR, COAT, IDEAL,
/// IDEAL2, TABLE). Null on <see cref="OpticsSettings.Coatings"/> — every file written before
/// OP-15 — is an empty catalog. OpenSim.Optics' <c>CoatingCatalogSettings</c> converts to and
/// from its model and checks it.
/// <para>
/// Units and signs: wavelengths in micrometers; an extinction coefficient k is zero or positive
/// for an absorbing material (complex index n + ik, docs/Optics-Conventions.md section 12 —
/// OpticStudio's own file writes it negative, and its reader and writer change the sign);
/// angles in degrees; taper decenters and coefficients in the lens's units, as OpticStudio
/// keeps them.
/// </para>
/// </summary>
public sealed record OpticsCoatingsSettings
{
    public List<OpticsCoatingMaterialSettings>? Materials { get; init; }
    public List<OpticsCoatingTaperSettings>? Tapers { get; init; }
    public List<OpticsCoatingSettings>? Coatings { get; init; }
}

/// <summary>A coating material (MATE): n and k against wavelength. One row is a constant index.</summary>
public sealed record OpticsCoatingMaterialSettings
{
    public string? Name { get; init; }
    public List<OpticsIndexRowSettings>? Rows { get; init; }
}

/// <summary>One row of a coating material: wavelength (µm), n, and k (zero or positive).</summary>
public sealed record OpticsIndexRowSettings
{
    public double Micrometers { get; init; }
    public double N { get; init; } = 1;
    public double K { get; init; }
}

/// <summary>A taper profile (TAPR): decenter, rotation and the radial (0–22), cosine (0–5) and
/// polynomial (0–20) terms, each list indexed by term number; a missing term is zero.</summary>
public sealed record OpticsCoatingTaperSettings
{
    public string? Name { get; init; }
    public double DecenterX { get; init; }
    public double DecenterY { get; init; }
    public double RotationDegrees { get; init; }
    public List<OpticsTaperTermSettings>? RadialTerms { get; init; }
    public List<OpticsTaperTermSettings>? CosineTerms { get; init; }
    public List<OpticsTaperTermSettings>? PolynomialTerms { get; init; }
}

/// <summary>One taper term: its number and coefficient.</summary>
public sealed record OpticsTaperTermSettings
{
    public int Term { get; init; }
    public double Coefficient { get; init; }
}

/// <summary>
/// A coating. <see cref="Kind"/> is "Layers" (COAT), "Ideal" (IDEAL, or the COAT I.xx
/// shorthand when <see cref="SimpleIdeal"/> is set), "Ideal2" (IDEAL2) or "Table" (TABLE); null
/// is "Layers". Only the fields of its kind are used.
/// </summary>
public sealed record OpticsCoatingSettings
{
    public string? Name { get; init; }
    public string? Kind { get; init; }

    // Layers (COAT).
    public List<OpticsCoatingLayerSettings>? Layers { get; init; }

    // Ideal (IDEAL): intensity transmission, reflection and the total-internal-reflection intensity.
    public double Transmission { get; init; }
    public double Reflection { get; init; }
    public double TotalInternalReflection { get; init; } = 1;
    public bool SimpleIdeal { get; init; }

    // Ideal2 (IDEAL2): amplitude coefficients as OpticStudio writes them (its phase convention).
    public double SReflectionReal { get; init; }
    public double SReflectionImaginary { get; init; }
    public double STransmissionReal { get; init; }
    public double STransmissionImaginary { get; init; }
    public double PReflectionReal { get; init; }
    public double PReflectionImaginary { get; init; }
    public double PTransmissionReal { get; init; }
    public double PTransmissionImaginary { get; init; }
    public bool NoPiFlag { get; init; }

    // Table (TABLE): one row per angle and wavelength.
    public List<OpticsCoatingTableRowSettings>? Table { get; init; }
}

/// <summary>One layer of a layer coating: material, thickness (waves of the primary
/// wavelength in the material, or µm when <see cref="IsAbsolute"/>), loop index and taper.</summary>
public sealed record OpticsCoatingLayerSettings
{
    public string? Material { get; init; }
    public double Thickness { get; init; }
    public bool IsAbsolute { get; init; }
    public int LoopIndex { get; init; }
    public string? Taper { get; init; }
}

/// <summary>One point of a table coating: angle of incidence (degrees), wavelength (µm), the
/// intensities Rs Rp Ts Tp and the phases (degrees) of the four amplitudes.</summary>
public sealed record OpticsCoatingTableRowSettings
{
    public double AngleDegrees { get; init; }
    public double Micrometers { get; init; }
    public double Rs { get; init; }
    public double Rp { get; init; }
    public double Ts { get; init; }
    public double Tp { get; init; }
    public double PhaseRs { get; init; }
    public double PhaseRp { get; init; }
    public double PhaseTs { get; init; }
    public double PhaseTp { get; init; }
}
