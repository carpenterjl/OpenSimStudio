using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential.Scattering;

/// <summary>
/// The geometry shared by the scatter models (docs/Optics-Conventions.md §74). Directions are
/// projected onto the surface ("Scatter Models": β₀ the specular ray's projection, β the
/// scattered ray's, x = β − β₀); a projected point inside the unit disk is a direction on the
/// scattered side, and since dβ_x dβ_y = cos θ_s dΩ (derived: the Jacobian of the projection of
/// the unit hemisphere onto its base), the scattered power per unit projected area is the BSDF
/// times the incident power: the help's TIS integral ∫∫ BSDF cos θ sin θ dθ dφ is the integral of
/// the BSDF over the unit disk.
/// </summary>
public static class NscScatterMath
{
    /// <summary>
    /// ∫₀^{2π} F(ρ(φ)) dφ, the integral over the unit disk of a function of |x| alone written in
    /// polar coordinates about the specular point β₀ (|β₀| = b), where F(R) = ∫₀^R f(r) r dr and
    /// ρ(φ) = −b cos φ + √(1 − b² sin² φ) is the distance from β₀ to the unit circle along φ
    /// (derived from |β₀ + ρ(cos φ, sin φ)| = 1). The integrand is smooth, even and 2π-periodic
    /// for b &lt; 1, so the trapezoidal rule converges geometrically; the rule is doubled from 32
    /// points until two estimates agree to 1e-14 relative, and refuses (throws) if 2¹⁸ points do
    /// not reach that.
    /// </summary>
    public static double DiskIntegral(Func<double, double> F, double b)
    {
        if (!(b >= 0 && b <= 1)) throw new ArgumentOutOfRangeException(nameof(b), b, "The specular projection must lie in the unit disk.");
        if (b == 0) return 2 * Math.PI * F(1);
        // Even in φ: 2∫₀^π. Trapezoid on [0, π] with n panels: nodes φ_k = kπ/n.
        double Rho(double phi)
        {
            double s = Math.Sin(phi), c = Math.Cos(phi);
            return Math.Max(0, -b * c + Math.Sqrt(Math.Max(0, 1 - b * b * s * s)));
        }
        int n = 32;
        double sum = 0.5 * (F(Rho(0)) + F(Rho(Math.PI)));
        for (int k = 1; k < n; k++) sum += F(Rho(k * Math.PI / n));
        double previous = 2 * Math.PI / n * sum;
        while (n < (1 << 18))
        {
            for (int k = 1; k < 2 * n; k += 2) sum += F(Rho(k * Math.PI / (2 * n)));
            n *= 2;
            double estimate = 2 * Math.PI / n * sum;
            if (Math.Abs(estimate - previous) <= 1e-14 * Math.Abs(estimate) || estimate == 0) return estimate;
            previous = estimate;
        }
        throw new InvalidOperationException($"The scatter integral over the unit disk did not converge for a specular projection |β₀| = {b:R} (grazing specular ray).");
    }

    /// <summary>The angular measure (radians) of the circle |x| = r that lies in the unit disk
    /// when the disk is centred at −β₀, |β₀| = b: 2π for r ≤ 1 − b, 0 for r ≥ 1 + b, otherwise
    /// 2·acos((r² + b² − 1)/(2rb)) (derived: the law of cosines on the triangle 0, −β₀, x).</summary>
    public static double ArcInDisk(double r, double b)
    {
        if (r <= 1 - b) return 2 * Math.PI;
        if (r >= 1 + b) return 0;
        double c = (r * r + b * b - 1) / (2 * r * b);
        return 2 * Math.Acos(Math.Clamp(c, -1, 1));
    }

    /// <summary>A direction from its projection β on the tangent frame (u, v, n).</summary>
    public static Vector3D FromProjection(double bx, double by, Vector3D u, Vector3D v, Vector3D n)
    {
        double z = Math.Sqrt(Math.Max(0, 1 - bx * bx - by * by));
        return u * bx + v * by + n * z;
    }

    /// <summary>A uniformly distributed direction inside the cone of half angle α (cos α given)
    /// about the unit axis <paramref name="axis"/>: cos θ uniform on [cos α, 1], φ uniform
    /// (derived: dΩ = d(cos θ) dφ).</summary>
    public static Vector3D InCone(Vector3D axis, double cosAlpha, SplitMix64 rng)
    {
        double ct = 1 - rng.NextDouble() * (1 - cosAlpha);
        double st = Math.Sqrt(Math.Max(0, 1 - ct * ct));
        double phi = 2 * Math.PI * rng.NextDouble();
        var (p, q) = Perpendiculars(axis);
        return axis * ct + p * (st * Math.Cos(phi)) + q * (st * Math.Sin(phi));
    }

    /// <summary>The first index whose cumulative weight exceeds <paramref name="target"/> (the
    /// last index if none does): with 0 ≤ target &lt; total it never lands on a zero-weight entry.</summary>
    public static int UpperBound(double[] cumulative, double target)
    {
        int lo = 0, hi = cumulative.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (cumulative[mid] > target) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>Two unit vectors at right angles to a unit vector and to each other.</summary>
    public static (Vector3D P, Vector3D Q) Perpendiculars(Vector3D a)
    {
        var t = Math.Abs(a.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitY;
        var p = t - a * Vector3D.Dot(t, a);
        p /= p.Length;
        return (p, Vector3D.Cross(a, p));
    }
}
