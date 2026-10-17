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
/// <para>
/// <b>Cannonball</b> (Simonovich, "Practical Method for Modeling Conductor Surface Roughness
/// Using The Cannonball Stack Principle", LAMSIM white paper issue 2.0, 2015, and Polar
/// application note AP8195, 2017) gives Huray's two parameters from the one number a foil data
/// sheet publishes: 14 equal spheres stacked 9 + 4 + 1 on a square tile of side 6r, so
/// A_flat = 36r² and, with A_matte/A_flat = 1, S = 14·4πr²/36r² = 14π/9; the stack's height,
/// two square pyramids of edge 2r (height r√2 each) plus one diameter, 2r(1 + √2), is the RMS
/// height of the profile, and a triangular profile of peak-to-valley Rz has RMS height
/// Rz/(2√3). Hence r = Rz/(4√3(1 + √2)) = Rz/16.73, and from an RMS value directly
/// r = Rq/(2(1 + √2)). The saturation 1 + 1.5·S = 8.33 is far above any band of interest: the
/// sphere is so small against the skin depth that the factor keeps rising through tens of GHz.
/// </para>
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

    /// <summary>Where the numbers came from, for the report ("Cannonball from Rz 2.5 µm,
    /// Circuit Foil BF-TZA 18 µm"); null when the user typed them.</summary>
    public string? Origin { get; init; }

    public bool IsHuray => SphereRadiusMeters > 0;

    /// <summary>Spheres in the Cannonball stack (9 + 4 + 1).</summary>
    public const int CannonballSpheres = 14;

    /// <summary>Huray's surface ratio of the Cannonball stack: 14 spheres of 4πr² on a tile of
    /// (6r)², i.e. 14π/9 ≈ 4.886.</summary>
    public static readonly double CannonballSurfaceRatio = CannonballSpheres * 4 * Math.PI / 36;

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

    /// <summary>The Cannonball sphere radius for a stack whose RMS height is
    /// <paramref name="rmsHeightMeters"/>: r = H/(2(1 + √2)).</summary>
    public static double CannonballRadiusFromRms(double rmsHeightMeters) =>
        rmsHeightMeters / (2 * (1 + Math.Sqrt(2)));

    /// <summary>The Cannonball sphere radius from a data sheet's ten-point mean roughness Rz:
    /// the profile is taken as triangular, RMS height Rz/(2√3), so r = Rz/(4√3(1 + √2)).</summary>
    public static double CannonballRadius(double rzMeters) =>
        CannonballRadiusFromRms(rzMeters / (2 * Math.Sqrt(3)));

    /// <summary>Huray by the Cannonball stack from a data-sheet Rz (see the class remarks).</summary>
    public static SurfaceRoughness Cannonball(double rzMeters, string? origin = null)
    {
        if (!(rzMeters > 0)) throw new ArgumentOutOfRangeException(nameof(rzMeters));
        return Huray(CannonballRadius(rzMeters), CannonballSurfaceRatio) with
        {
            Origin = origin ?? $"Cannonball from Rz {rzMeters * 1e6:g3} µm"
        };
    }

    /// <summary>Huray by the Cannonball stack from an RMS roughness (Rq or Sq): the stack's
    /// height is that RMS value, r = Rq/(2(1 + √2)).</summary>
    public static SurfaceRoughness CannonballFromRms(double rmsMeters, string? origin = null)
    {
        if (!(rmsMeters > 0)) throw new ArgumentOutOfRangeException(nameof(rmsMeters));
        return Huray(CannonballRadiusFromRms(rmsMeters), CannonballSurfaceRatio) with
        {
            Origin = origin ?? $"Cannonball from RMS {rmsMeters * 1e6:g3} µm"
        };
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

    public string Describe() => (IsHuray
        ? $"Huray, sphere radius {SphereRadiusMeters * 1e6:g3} µm, surface ratio {SurfaceRatio:g3}"
        : $"Hammerstad, {RmsMeters * 1e6:g3} µm RMS")
        + (Origin is { } origin ? $" ({origin})" : "");
}
