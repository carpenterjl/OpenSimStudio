namespace OpenSim.Core.Interfaces;

/// <summary>
/// Frequency-sweep settings for the harmonic (electro-quasistatic) electrical solver.
/// Attached to <see cref="SolveInput.HarmonicElectric"/>; ignored by every other solver.
/// </summary>
public sealed record HarmonicElectricSettings
{
    /// <summary>Lowest sweep frequency [Hz].</summary>
    public required double MinFrequency { get; init; }

    /// <summary>Highest sweep frequency [Hz].</summary>
    public required double MaxFrequency { get; init; }

    /// <summary>Number of geometrically spaced frequency points (1 solves only
    /// <see cref="MinFrequency"/>).</summary>
    public int PointCount { get; init; } = 15;
}
