namespace OpenSim.Optics;

/// <summary>
/// How the amplitude of the light varies across the entrance pupil. Only the choice and its
/// factor are held here: the amplitude formulas are applied by pupil sampling (OP-22), and
/// are written there once read at the source (plan rule 4), not from memory here.
/// </summary>
public enum ApodizationType
{
    /// <summary>The same amplitude everywhere in the pupil.</summary>
    Uniform = 0,

    /// <summary>A Gaussian fall-off from the pupil centre, set by the apodization factor.</summary>
    Gaussian = 1,

    /// <summary>The fall-off of a point source illuminating a plane (cosine cubed of the angle
    /// from the axis). Takes no factor.</summary>
    CosineCubed = 2,
}

/// <summary>Saved names and labels for <see cref="ApodizationType"/>.</summary>
public static class ApodizationTypes
{
    public static SavedNameTable<ApodizationType> Names { get; } = new(
        (ApodizationType.Uniform, "Uniform", "Uniform"),
        (ApodizationType.Gaussian, "Gaussian", "Gaussian"),
        (ApodizationType.CosineCubed, "CosineCubed", "Cosine cubed"));

    /// <summary>True when the type uses the apodization factor.</summary>
    public static bool TakesFactor(ApodizationType type) => type == ApodizationType.Gaussian;
}
