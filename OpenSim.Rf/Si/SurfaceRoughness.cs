namespace OpenSim.Rf.Si;

/// <summary>
/// Copper surface roughness as a multiplier on the smooth-surface impedance: once the skin
/// depth is no larger than the roughness, current follows the profile and the loss rises.
/// <list type="bullet">
/// <item><b>Hammerstad</b> (Hammerstad and Jensen 1980, after Morgan 1949):
/// K = 1 + (2/π)·atan(1.4·(Δ/δ)²), Δ the RMS roughness. It saturates at 2, which real
/// treated foils exceed above a few GHz.</item>
/// <item><b>Huray</b> (the "snowball" model): spheres of radius a on a flat base,
/// K = 1 + (3/2)·S/(1 + δ/a + δ²/2a²), S = N·4πa²/A the spheres' area per base area. It
/// saturates at 1 + 1.5·S. The base-area ratio A_matte/A_flat is taken as 1.</item>
/// </list>
/// Both tend to 1 at low frequency. The factor is real and scales the whole frequency-
/// dependent part of the series impedance (resistance and internal inductance alike), which
/// is the usual use and is not strictly causal.
/// </summary>
public sealed record SurfaceRoughness
{
    private const double Mu0 = 4e-7 * Math.PI;

    private SurfaceRoughness() { }

    /// <summary>RMS roughness Δ [m] (Hammerstad), 0 for Huray.</summary>
    public double RmsMeters { get; private init; }

    /// <summary>Sphere radius a [m] (Huray), 0 for Hammerstad.</summary>
    public double SphereRadiusMeters { get; private init; }

    /// <summary>S = N·4πa²/A_flat (Huray).</summary>
    public double SurfaceRatio { get; private init; }

    public bool IsHuray => SphereRadiusMeters > 0;

    public static SurfaceRoughness Hammerstad(double rmsMeters)
    {
        if (!(rmsMeters > 0)) throw new ArgumentOutOfRangeException(nameof(rmsMeters));
        return new SurfaceRoughness { RmsMeters = rmsMeters };
    }

    public static SurfaceRoughness Huray(double sphereRadiusMeters, double surfaceRatio)
    {
        if (!(sphereRadiusMeters > 0)) throw new ArgumentOutOfRangeException(nameof(sphereRadiusMeters));
        if (!(surfaceRatio > 0)) throw new ArgumentOutOfRangeException(nameof(surfaceRatio));
        return new SurfaceRoughness { SphereRadiusMeters = sphereRadiusMeters, SurfaceRatio = surfaceRatio };
    }

    /// <summary>The loss multiplier at f for a conductor of the given conductivity.</summary>
    public double Factor(double frequencyHz, double conductivitySiemensPerMeter)
    {
        if (!(frequencyHz > 0)) return 1;
        double skinDepth = 1 / Math.Sqrt(Math.PI * frequencyHz * Mu0 * conductivitySiemensPerMeter);
        if (IsHuray)
        {
            double x = skinDepth / SphereRadiusMeters;
            return 1 + 1.5 * SurfaceRatio / (1 + x + 0.5 * x * x);
        }
        double ratio = RmsMeters / skinDepth;
        return 1 + 2 / Math.PI * Math.Atan(1.4 * ratio * ratio);
    }

    public string Describe() => IsHuray
        ? $"Huray, sphere radius {SphereRadiusMeters * 1e6:g3} µm, surface ratio {SurfaceRatio:g3}"
        : $"Hammerstad, {RmsMeters * 1e6:g3} µm RMS";
}
