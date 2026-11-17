using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Volume;

/// <summary>
/// The angular distribution of a volume scatter event: the cosine μ of the polar angle between the
/// new and the old direction is drawn from it; the azimuth is uniform (plan item OP-81;
/// docs/Optics-Conventions.md §77).
/// </summary>
public abstract class NscPhaseFunction
{
    /// <summary>A cosine μ of the scattering angle from two uniform numbers in [0, 1) (u for μ;
    /// the caller draws the azimuth).</summary>
    public abstract double SampleCosine(double u);

    /// <summary>The mean of μ (the asymmetry g) of the distribution as defined.</summary>
    public abstract double MeanCosine { get; }

    public abstract string Describe();

    /// <summary>The direction at polar angle acos μ and azimuth 2πv about <paramref name="d"/>
    /// (unit): d·μ + (u cos φ + w sin φ)·√(1 − μ²), with u, w any orthonormal pair at right
    /// angles to d (derived).</summary>
    public static Vector3D Turn(Vector3D d, double mu, double v)
    {
        var a = Math.Abs(d.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitY;
        var u = a - d * Vector3D.Dot(a, d);
        u /= u.Length;
        var w = Vector3D.Cross(d, u);
        double phi = 2 * Math.PI * v, s = Math.Sqrt(Math.Max(0, 1 - mu * mu));
        var r = d * mu + (u * Math.Cos(phi) + w * Math.Sin(phi)) * s;
        return r / r.Length;
    }
}

/// <summary>
/// Angle Scattering (help, "Bulk scattering"): "the new ray direction lies uniformly distributed
/// within a cone" of semi-angle θ_max (0–180°; 180 "will scatter the ray randomly in any
/// direction"). This program reads "uniformly" as uniform per solid angle, so μ is uniform on
/// [cos θ_max, 1] (Conventions §10 item 73).
/// </summary>
public sealed class NscConePhase : NscPhaseFunction
{
    private readonly double _cosMax;

    public double AngleDegrees { get; }

    public NscConePhase(double angleDegrees)
    {
        if (!(angleDegrees >= 0 && angleDegrees <= 180))
            throw new ArgumentException($"Angle Scattering: the cone angle must lie in 0–180° (help, \"Bulk scattering\"); it is {angleDegrees}.");
        AngleDegrees = angleDegrees;
        _cosMax = angleDegrees == 180 ? -1 : Math.Cos(angleDegrees * Math.PI / 180);
    }

    public override double SampleCosine(double u) => 1 - u * (1 - _cosMax);

    /// <summary>(1 + cos θ_max)/2 (derived: the mean of a uniform μ).</summary>
    public override double MeanCosine => (1 + _cosMax) / 2;

    public override string Describe() => $"uniform within a {AngleDegrees:g6}° cone";
}

/// <summary>
/// Henyey–Greenstein: P(θ) = (1 − g²)/(4π(1 + g² − 2g cos θ)^(3/2)) (help, "Bulk scattering",
/// sample Henyey-Greenstein-bulk.DLL), with the help's limits on g: "less than −1, g is set to −1",
/// "greater than 0.9999, then g is set to 0.9999", |g| "less than 1.0e-4, then g is set to 1.0e-4".
/// μ by inverting the distribution: μ = (1 + g² − ((1 − g²)/(1 − g + 2gu))²)/(2g) (derived).
/// Its mean cosine is g (derived; the gate checks the samples against it).
/// </summary>
public sealed class NscHenyeyGreensteinPhase : NscPhaseFunction
{
    public double G { get; }

    /// <summary>The value given, before the help's limits.</summary>
    public double Requested { get; }

    public NscHenyeyGreensteinPhase(double g)
    {
        if (!double.IsFinite(g)) throw new ArgumentException($"Henyey-Greenstein: g must be a finite number; it is {g}.");
        Requested = g;
        if (g < -1) g = -1;
        if (g > 0.9999) g = 0.9999;
        if (Math.Abs(g) < 1e-4) g = 1e-4;
        G = g;
    }

    public override double SampleCosine(double u)
    {
        double g = G, f = (1 - g * g) / (1 - g + 2 * g * u);
        return Math.Clamp((1 + g * g - f * f) / (2 * g), -1, 1);
    }

    public override double MeanCosine => G;

    public override string Describe() => Requested == G ? $"Henyey-Greenstein, g = {G:g6}" : $"Henyey-Greenstein, g = {G:g6} (the help's limits on {Requested:g6})";
}

/// <summary>
/// Rayleigh: P(θ) ∝ 1 + cos²θ (help, "Bulk scattering", sample Rayleigh.DLL: 3(1 + cos²θ)/(8λ⁴)).
/// μ solves μ³ + 3μ + 4 − 8u = 0 (the inverse of F(μ) = (μ³ + 3μ + 4)/8, derived), whose one real
/// root is Cardano's ∛(−q/2 + √(q²/4 + 1)) + ∛(−q/2 − √(q²/4 + 1)), q = 4 − 8u. Mean cosine 0.
/// </summary>
public sealed class NscRayleighPhase : NscPhaseFunction
{
    public static NscRayleighPhase Instance { get; } = new();

    public override double SampleCosine(double u)
    {
        double q = 4 - 8 * u, r = Math.Sqrt(q * q / 4 + 1);
        return Math.Clamp(Math.Cbrt(-q / 2 + r) + Math.Cbrt(-q / 2 - r), -1, 1);
    }

    public override double MeanCosine => 0;

    public override string Describe() => "Rayleigh (1 + cos²θ)";
}

/// <summary>
/// The Mie phase function of <see cref="NscMie"/> for unpolarized light, sampled from a table:
/// p(μ) at polar angles θ_k = π(k/K)² (dense near the forward peak), the cumulative distribution
/// C(θ) = 2π ∫ p sin θ dθ by the trapezoid rule, and θ drawn by inverting C linearly between nodes.
/// The table is accepted only when its integral is 1 and its mean cosine equals the series
/// asymmetry g within 2e-4 (both absolute); K doubles from 2048 until it is, up to 262144, and
/// otherwise the table is refused naming the particle. Polarization-dependent Mie scattering
/// (the full Mueller matrix) is not built.
/// </summary>
public sealed class NscMiePhase : NscPhaseFunction
{
    private readonly double[] _theta, _cdf;

    public NscMie Mie { get; }

    /// <summary>The table's own mean cosine.</summary>
    public double TableMeanCosine { get; }

    public int Nodes => _theta.Length;

    public const double Tolerance = 2e-4;

    public NscMiePhase(NscMie mie)
    {
        Mie = mie ?? throw new ArgumentNullException(nameof(mie));
        for (int k = 2048; ; k *= 2)
        {
            var (theta, cdf, mean) = Table(mie, k);
            double total = cdf[^1];
            if (Math.Abs(total - 1) <= Tolerance && Math.Abs(mean - mie.Asymmetry) <= Tolerance)
            {
                for (int i = 0; i < cdf.Length; i++) cdf[i] /= total;
                _theta = theta;
                _cdf = cdf;
                TableMeanCosine = mean;
                return;
            }
            if (k >= 262144)
                throw new InvalidOperationException(
                    $"Mie scattering (m = {mie.RelativeIndex:g6}, x = {mie.SizeParameter:g6}): the phase-function table of {k + 1} angles integrates to {total:g8} with mean cosine {mean:g8} against the series' g = {mie.Asymmetry:g8}; it is under-sampled. Use a smaller particle.");
        }
    }

    private static (double[] Theta, double[] Cdf, double Mean) Table(NscMie mie, int k)
    {
        var theta = new double[k + 1];
        var p = new double[k + 1];
        for (int i = 0; i <= k; i++)
        {
            double t = (double)i / k;
            theta[i] = Math.PI * t * t;
            p[i] = mie.Phase(Math.Cos(theta[i]));
        }
        var cdf = new double[k + 1];
        double mean = 0;
        for (int i = 1; i <= k; i++)
        {
            double h = theta[i] - theta[i - 1];
            double f0 = p[i - 1] * Math.Sin(theta[i - 1]), f1 = p[i] * Math.Sin(theta[i]);
            cdf[i] = cdf[i - 1] + 2 * Math.PI * h * (f0 + f1) / 2;
            mean += 2 * Math.PI * h * (f0 * Math.Cos(theta[i - 1]) + f1 * Math.Cos(theta[i])) / 2;
        }
        return (theta, cdf, mean);
    }

    public override double SampleCosine(double u)
    {
        int lo = 0, hi = _cdf.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_cdf[mid] <= u) lo = mid; else hi = mid;
        }
        double span = _cdf[hi] - _cdf[lo];
        double t = span > 0 ? (u - _cdf[lo]) / span : 0;
        return Math.Cos(_theta[lo] + t * (_theta[hi] - _theta[lo]));
    }

    public override double MeanCosine => Mie.Asymmetry;

    public override string Describe() => $"Mie (m = {Mie.RelativeIndex:g6}, x = {Mie.SizeParameter:g6}, g = {Mie.Asymmetry:g6})";
}
