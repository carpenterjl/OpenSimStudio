namespace OpenSim.Core.Model;

/// <summary>
/// The High-Yield optimization settings of the Optimize window (plan item OP-62; OpticStudio
/// 2025 R2 help, "Optimization Goal": "Weight sets the weight of the overall contribution from
/// the HYLD operands with respect to the rest of the merit function"). Saved with the lens.
/// OpenSim.Optics' <c>HighYieldMerit</c> writes the HYLD rows from it.
/// </summary>
public sealed record OpticsHighYieldSettings
{
    /// <summary>The weight of the HYLD rows relative to the default merit function's rows; null
    /// is the default (1).</summary>
    public double? Weight { get; init; }
}
