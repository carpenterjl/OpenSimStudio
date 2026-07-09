namespace OpenSim.Rf;

/// <summary>Free-space electromagnetic constants (SI). µ₀ is the exact pre-2019 value
/// 4π×10⁻⁷ — consistent with the PEEC kernel's µ₀/4π = 10⁻⁷ so the two inductance
/// paths never disagree in the sixth digit over a constant.</summary>
internal static class RfConstants
{
    public const double SpeedOfLight = 299_792_458.0;
    public static readonly double Mu0 = 4e-7 * Math.PI;
    public static readonly double Eps0 = 1.0 / (Mu0 * SpeedOfLight * SpeedOfLight);
}
