namespace OpenSim.Core.Model;

/// <summary>
/// The non-sequential system as saved (plan item OP-70): the objects in editor order (object 1
/// first) and the non-sequential controls of OpticStudio's System Explorer. Lengths are in the
/// lens unit of the system data, as the editors show them. OpenSim.Optics'
/// <c>ProjectNonSequential</c> turns it into a scene and trace settings. Every property is
/// nullable: null is the engine's default.
/// </summary>
public sealed record OpticsNonSequentialSettings
{
    public List<OpticsNscObjectSettings>? Objects { get; init; }

    public int? MaxIntersectionsPerRay { get; init; }
    public int? MaxSegmentsPerRay { get; init; }
    public int? MaxNestedObjects { get; init; }
    public double? MinimumRelativeIntensity { get; init; }
    public double? MinimumAbsoluteIntensity { get; init; }

    /// <summary>Lens units (OpticStudio limits it to 1e-10–1e-3).</summary>
    public double? GlueDistance { get; init; }

    /// <summary>OpticStudio's "Simple Ray Splitting" system setting.</summary>
    public bool? SimpleRaySplitting { get; init; }

    /// <summary>The material outside every object: blank (air), a glass name or an index.</summary>
    public string? BackgroundMaterial { get; init; }

    // OP-83
    /// <summary>OpticStudio's named filters (System Explorer, "Named Filters"): one
    /// "filtername; filterstring" line each, usable wherever a filter string is.</summary>
    public List<string>? NamedFilters { get; init; }

    // OP-80
    /// <summary>The project's ABg scatter catalog (OpticStudio's ABg Scatter Data Catalog,
    /// saved here rather than in ABG_DATA.DAT).</summary>
    public List<OpticsAbgProfileSettings>? AbgCatalog { get; init; }
}

// OP-80
/// <summary>One saved ABg profile: an upper-case name, the reference wavelength (µm, 0 or null
/// for no scaling) and its rows.</summary>
public sealed record OpticsAbgProfileSettings
{
    public string? Name { get; init; }
    public double? Wavelength { get; init; }
    public List<OpticsAbgRowSettings>? Rows { get; init; }
}

// OP-80
/// <summary>One saved ABg row: angle of incidence (degrees), A, B, g.</summary>
public sealed record OpticsAbgRowSettings
{
    public double? Angle { get; init; }
    public double? A { get; init; }
    public double? B { get; init; }
    public double? G { get; init; }
}

// OP-80
/// <summary>One saved importance-sampling target sphere: the object it is centred on, its
/// radius (lens units) and the largest solid angle (steradians) it may subtend.</summary>
public sealed record OpticsNscScatterTargetSettings
{
    public int? Object { get; init; }
    public double? Size { get; init; }
    public double? Limit { get; init; }
}

/// <summary>One saved object row.</summary>
public sealed record OpticsNscObjectSettings
{
    /// <summary>OpticStudio's object type name ("Sphere", "Rectangle", "Standard Lens").</summary>
    public string? Type { get; init; }

    /// <summary>The type's parameters in OpticStudio's order, lengths in lens units.</summary>
    public List<double>? Parameters { get; init; }

    public int? ReferenceObject { get; init; }
    public int? InsideOf { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public double? TiltX { get; init; }
    public double? TiltY { get; init; }
    public double? TiltZ { get; init; }
    public bool? GlobalXyzRotationOrder { get; init; }

    /// <summary>Blank, MIRROR, ABSORB, a glass name or an index.</summary>
    public string? Material { get; init; }

    public List<OpticsNscFaceSettings>? Faces { get; init; }

    /// <summary>OP-70's detecting stand-in (absorbs and counts; OP-79's detector objects record pixels).</summary>
    public bool? DetectingStandIn { get; init; }

    public string? Comment { get; init; }

    // OP-79: detector object properties (OpticStudio's Object Properties, Type tab, "Detector
    // Settings"); null is the engine's default.
    /// <summary>"Use Pixel Interpolation" (Detector Rectangle and Color); default off.</summary>
    public bool? DetectorPixelInterpolation { get; init; }

    /// <summary>"Normalize Coherent Power" (Detector Rectangle); default on, as the help says.</summary>
    public bool? DetectorNormalizeCoherentPower { get; init; }

    /// <summary>"Record Spectral Data" (Detector Color): number of bins, null for off.</summary>
    public int? DetectorSpectralBins { get; init; }

    /// <summary>"Min wave" and "Max wave" of Record Spectral Data, µm.</summary>
    public double? DetectorSpectralMinMicrometers { get; init; }
    public double? DetectorSpectralMaxMicrometers { get; init; }

    // OP-77
    /// <summary>A source object's Sources tab (null for a non-source or the defaults).</summary>
    public OpticsNscSourceSettings? Source { get; init; }

    // OP-88
    /// <summary>Cells flagged for the optimizer (OpticStudio's Variable on object cells), as the
    /// Lens Data Editor saves its flags: <see cref="OpticsCellFlagSettings.Column"/> "X", "Y", "Z",
    /// "TiltX", "TiltY", "TiltZ" or "Par1", "Par2", ... and <see cref="OpticsCellFlagSettings.Kind"/>
    /// "Variable". Null for none. Solves and pickups on object cells are not built.</summary>
    public List<OpticsCellFlagSettings>? Flags { get; init; }

    // OP-80: the Scatter To section.
    /// <summary>"None", "Scatter To List" or "Importance Sampling".</summary>
    public string? ScatterToMethod { get; init; }

    /// <summary>The Scatter To list: object numbers separated by spaces ("3 4").</summary>
    public string? ScatterToList { get; init; }

    /// <summary>Importance-sampling targets, ray 1 first (at most 6).</summary>
    public List<OpticsNscScatterTargetSettings>? ScatterTargets { get; init; }
}

// OP-77
/// <summary>
/// A source object's Sources tab as saved (plan item OP-77; help, "Sources"). Lengths in lens
/// units; every property nullable, null the default. Names: <see cref="Sampling"/> "Sobol" or
/// "Random"; <see cref="BulkScatter"/> "Many", "Once" or "Never"; <see cref="ArrayType"/> "None",
/// "Rectangular", "Circular", "Hexapolar" or "Hexagonal"; <see cref="ColorModel"/> OpticStudio's
/// colour-model name ("System Wavelength", "Uniform Power Spectrum", "Black Body Spectrum",
/// "Spectrum File", ...).
/// </summary>
public sealed record OpticsNscSourceSettings
{
    public bool? RandomPolarization { get; init; }
    public double? Jx { get; init; }
    public double? Jy { get; init; }
    public double? XPhase { get; init; }
    public double? YPhase { get; init; }
    public double? InitialPhase { get; init; }
    public double? CoherenceLength { get; init; }
    public bool? ReverseRays { get; init; }
    public double? PrePropagation { get; init; }
    public string? Sampling { get; init; }
    public string? BulkScatter { get; init; }
    public string? ArrayType { get; init; }
    public int? ArrayCount1 { get; init; }
    public int? ArrayCount2 { get; init; }
    public double? ArraySize1 { get; init; }
    public double? ArraySize2 { get; init; }
    public string? ColorModel { get; init; }
    public double? Temperature { get; init; }
    public int? SpectrumCount { get; init; }
    public double? WavelengthFrom { get; init; }
    public double? WavelengthTo { get; init; }

    /// <summary>The spectrum table's wavelengths (µm) and weights, pair by pair.</summary>
    public List<double>? SpectrumWavelengths { get; init; }
    public List<double>? SpectrumWeights { get; init; }
}

/// <summary>One face's saved properties.</summary>
public sealed record OpticsNscFaceSettings
{
    public int? Face { get; init; }

    /// <summary>"Object Default", "Reflective" or "Absorbing".</summary>
    public string? FaceIs { get; init; }

    public string? Coating { get; init; }

    // OP-80: the face's scattering (Coat/Scatter section).
    /// <summary>"None", "Lambertian", "Gaussian", "ABg", "ABg File", "BSDF" or "User Defined".</summary>
    public string? ScatterModel { get; init; }

    /// <summary>"Number Of Rays" (0–300).</summary>
    public int? ScatterRays { get; init; }

    /// <summary>"Scatter Fraction" (Lambertian, Gaussian, BSDF).</summary>
    public double? ScatterFraction { get; init; }

    /// <summary>Gaussian σ.</summary>
    public double? ScatterSigma { get; init; }

    /// <summary>Reflect and transmit data: an ABg catalog name (ABg), the full path of an ABGF
    /// file (ABg File) or of a BSDF file (BSDF).</summary>
    public string? ScatterReflect { get; init; }
    public string? ScatterTransmit { get; init; }

    /// <summary>BSDF "Angle", degrees.</summary>
    public double? ScatterAngle { get; init; }

    /// <summary>"Thin Window Scattering".</summary>
    public bool? ThinWindowScattering { get; init; }
}
