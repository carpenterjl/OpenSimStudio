using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential;

/// <summary>How the source stand-in picks its points: Sobol (Joe and Kuo's sequence, OP-3) or
/// pseudo-random (SplitMix64, one stream per ray).</summary>
public enum NscSampling
{
    Sobol = 0,
    Random = 1,
}

/// <summary>
/// A minimal source for OP-70's Monte Carlo loop, a stand-in for OP-77's source objects: a
/// point emitting uniformly into a cone, and a collimated beam uniform over a disk. Each of the
/// N rays carries power P/N, as OpticStudio gives every analysis ray "the total power divided
/// by the number of rays to be traced" (help, "Ray Trace"). Ray k depends only on k (Sobol point
/// k + 1, skipping the origin, or SplitMix64.ForStream(seed, k)), so the rays are the same
/// whatever order or thread computes them. Spectra, polarization states other than
/// unpolarized, source units and every other source type are OP-77.
/// <para>Superseded by OP-77's source objects (<c>NonSequential/Sources/</c>, run through
/// <c>NscSourceRun</c>); kept for the tests that build rays directly.</para>
/// </summary>
public static class NscSourceStandIn
{
    /// <summary>
    /// A point at <paramref name="position"/> emitting into the cone of half angle
    /// <paramref name="halfAngleDegrees"/> (0–180) about <paramref name="axis"/>, uniform in solid
    /// angle: cos θ uniform on [cos α, 1] and the azimuth uniform on [0, 2π) (derived: the solid
    /// angle element is d(cos θ)·dφ).
    /// </summary>
    public static IReadOnlyList<NscLaunchRay> PointCone(Vector3D position, Vector3D axis, double halfAngleDegrees,
        int rays, double power, NscSampling sampling = NscSampling.Sobol, long seed = 1, int insideOf = 0)
    {
        if (!(halfAngleDegrees >= 0 && halfAngleDegrees <= 180))
            throw new ArgumentOutOfRangeException(nameof(halfAngleDegrees), halfAngleDegrees, "Source stand-in: the cone half angle must lie in 0–180°.");
        Check(rays, power);
        var (u, v, w) = Basis(axis);
        double cosAlpha = Math.Cos(halfAngleDegrees * Math.PI / 180);
        var sample = Sampler(sampling, seed);
        var list = new NscLaunchRay[rays];
        for (int k = 0; k < rays; k++)
        {
            var (a, b) = sample(k);
            double cosT = 1 - a * (1 - cosAlpha);
            double sinT = Math.Sqrt(Math.Max(0, 1 - cosT * cosT));
            double phi = 2 * Math.PI * b;
            var d = w * cosT + u * (sinT * Math.Cos(phi)) + v * (sinT * Math.Sin(phi));
            list[k] = new NscLaunchRay { Position = position, Direction = d, Intensity = power / rays, InsideOf = insideOf, BulkScatter = Sources.NscSourceBulkScatter.Many };   // wave 17: the source default
        }
        return list;
    }

    /// <summary>
    /// A collimated beam along <paramref name="axis"/> uniform over the disk of
    /// <paramref name="radius"/> centred on <paramref name="center"/> at right angles to it:
    /// r = R√a, φ = 2πb (derived: the area element is r·dr·dφ = R²/2·da·dφ).
    /// </summary>
    public static IReadOnlyList<NscLaunchRay> CollimatedDisk(Vector3D center, Vector3D axis, double radius,
        int rays, double power, NscSampling sampling = NscSampling.Sobol, long seed = 1, int insideOf = 0)
    {
        if (!(radius >= 0) || !double.IsFinite(radius))
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Source stand-in: the beam radius must be zero or positive (meters).");
        Check(rays, power);
        var (u, v, w) = Basis(axis);
        var sample = Sampler(sampling, seed);
        var list = new NscLaunchRay[rays];
        for (int k = 0; k < rays; k++)
        {
            var (a, b) = sample(k);
            double r = radius * Math.Sqrt(a), phi = 2 * Math.PI * b;
            var p = center + u * (r * Math.Cos(phi)) + v * (r * Math.Sin(phi));
            list[k] = new NscLaunchRay { Position = p, Direction = w, Intensity = power / rays, InsideOf = insideOf, BulkScatter = Sources.NscSourceBulkScatter.Many };   // wave 17: the source default
        }
        return list;
    }

    private static void Check(int rays, double power)
    {
        if (rays < 1) throw new ArgumentOutOfRangeException(nameof(rays), rays, "Source stand-in: at least one ray.");
        if (!(power > 0) || !double.IsFinite(power)) throw new ArgumentOutOfRangeException(nameof(power), power, "Source stand-in: the power must be positive.");
    }

    private static Func<int, (double, double)> Sampler(NscSampling sampling, long seed)
    {
        if (sampling == NscSampling.Sobol)
        {
            var sobol = new SobolSequence(2);
            return k =>
            {
                var p = sobol.Point(k + 1L);
                return (p[0], p[1]);
            };
        }
        return k =>
        {
            var rng = SplitMix64.ForStream(seed, k);
            return (rng.NextDouble(), rng.NextDouble());
        };
    }

    private static (Vector3D U, Vector3D V, Vector3D W) Basis(Vector3D axis)
    {
        double length = axis.Length;
        if (!(length > 0) || !double.IsFinite(length)) throw new ArgumentException("Source stand-in: the axis must be a finite, non-zero vector.");
        var w = axis / length;
        var a = Math.Abs(w.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitY;
        var u = a - w * Vector3D.Dot(a, w);
        u /= u.Length;
        return (u, Vector3D.Cross(w, u), w);
    }
}
