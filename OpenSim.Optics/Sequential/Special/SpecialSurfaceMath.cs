using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Special;

/// <summary>
/// The even-asphere profile z(r) = c·r²/(1 + √(1 − (1 + k)·c²·r²)) + Σ αᵢ·r^(2i), i = 1…8, that
/// OpticStudio's Fresnel, Extended Fresnel, Generalized Fresnel and Cylinder Fresnel surfaces use
/// for their substrates and facets (OpticStudio 2025 R2 help, "Even Asphere", its sag equation,
/// and "Fresnel": "The parameter values are exactly the same as for the even asphere model;
/// polynomial aspherics are supported to 16th order"). OP-23 builds the Even Asphere surface
/// itself; this is the profile alone, kept here so OP-27 does not depend on it.
/// <para>
/// Written in ρ = r² so no division by r is needed on the axis. The derivative is derived:
/// d/dρ of c·ρ/(1 + √Q), Q = 1 − (1 + k)c²ρ, is c/(2√Q) (multiply out (1 + √Q)² and use
/// 1 − Q = (1 + k)c²ρ), and d/dρ of αᵢ·ρⁱ is i·αᵢ·ρ^(i−1). Then ∂z/∂x = 2x·dz/dρ.
/// </para>
/// </summary>
internal readonly struct EvenProfile
{
    private readonly double _c, _k;
    private readonly double[] _alpha;

    public EvenProfile(double curvature, double conic, IReadOnlyList<double>? alpha)
    {
        _c = curvature;
        _k = conic;
        _alpha = alpha?.ToArray() ?? Array.Empty<double>();
    }

    public bool HasPolynomial => _alpha.Any(a => a != 0);

    /// <summary>The second derivative of the sag on the axis, d²z/dr² at r = 0: c + 2α₁.</summary>
    public double VertexCurvature => _c + 2 * (_alpha.Length > 0 ? _alpha[0] : 0);

    /// <summary>z at ρ = r², NaN where the conic's square root is not real.</summary>
    public double Sag(double rho)
    {
        double q = 1 - (1 + _k) * _c * _c * rho;
        if (!(q >= 0)) return double.NaN;
        double z = _c == 0 ? 0 : _c * rho / (1 + Math.Sqrt(q));
        double power = rho;
        for (int i = 0; i < _alpha.Length; i++)
        {
            z += _alpha[i] * power;
            power *= rho;
        }
        return z;
    }

    /// <summary>dz/dρ at ρ = r², NaN where the conic's square root is not real or zero.</summary>
    public double SlopeRho(double rho)
    {
        double q = 1 - (1 + _k) * _c * _c * rho;
        if (!(q > 0) && _c != 0) return double.NaN;
        double d = _c == 0 ? 0 : _c / (2 * Math.Sqrt(q));
        double power = 1;
        for (int i = 0; i < _alpha.Length; i++)
        {
            d += (i + 1) * _alpha[i] * power;
            power *= rho;
        }
        return d;
    }

    public static void CheckCoefficients(IReadOnlyList<double>? alpha, string what)
    {
        if (alpha is null) return;
        if (alpha.Count > 8)
            throw new ArgumentException($"{what}: at most 8 polynomial coefficients (r² to r¹⁶) are defined; {alpha.Count} were given.");
        for (int i = 0; i < alpha.Count; i++)
            if (!double.IsFinite(alpha[i]))
                throw new ArgumentException($"{what}: polynomial coefficient α{i + 1} must be a number (it is {alpha[i]}).");
    }
}

/// <summary>
/// OpticStudio's extended polynomial terms (2025 R2 help, "Extended Polynomial"): a power
/// series in the normalised coordinates u = x/R, v = y/R taken in the order x, y, x², xy, y²,
/// x³, x²y, …, so order k has k + 1 terms x^(k−j)·y^j, j = 0…k, up to order 20 (230 terms).
/// The help's own checks: term 12 is x²y² and term 13 is xy³.
/// </summary>
internal static class ExtendedPolynomialTerms
{
    /// <summary>The most terms OpticStudio defines (order 20).</summary>
    public const int MaxTerms = 230;

    /// <summary>The exponents (of x, of y) of 1-based term <paramref name="term"/>.</summary>
    public static (int Px, int Py) Exponents(int term)
    {
        if (term < 1 || term > MaxTerms) throw new ArgumentOutOfRangeException(nameof(term), term, "Extended polynomial terms run from 1 to 230.");
        int order = 1, first = 1;
        while (term >= first + order + 1) { first += order + 1; order++; }
        int j = term - first;
        return (order - j, j);
    }

    /// <summary>Σ Aᵢ·Eᵢ(u, v) and its derivatives with respect to u and v.</summary>
    public static (double Value, double Du, double Dv) Evaluate(IReadOnlyList<double> coefficients, double u, double v)
    {
        double value = 0, du = 0, dv = 0;
        for (int i = 0; i < coefficients.Count; i++)
        {
            double a = coefficients[i];
            if (a == 0) continue;
            var (px, py) = Exponents(i + 1);
            double xp = IntPow(u, px), yp = IntPow(v, py);
            value += a * xp * yp;
            if (px > 0) du += a * px * IntPow(u, px - 1) * yp;
            if (py > 0) dv += a * py * xp * IntPow(v, py - 1);
        }
        return (value, du, dv);
    }

    private static double IntPow(double x, int n)
    {
        double r = 1;
        for (int i = 0; i < n; i++) r *= x;
        return r;
    }
}

/// <summary>
/// Newton's iteration for the intersection of a ray with a surface z = sag(x, y), from a start
/// on the ray (the vertex plane, or a nearby closed-form root). g(s) = sag(p + s·d) − (p + s·d)_z
/// has g′(s) = z_x·L + z_y·M − N, the dot product of the direction with the unnormalised
/// gradient (z_x, z_y, −1). It stops when a step is below a few ulps of the distances involved
/// and fails (false) on a NaN sag, a zero derivative (the ray grazes the surface) or 64 steps.
/// </summary>
internal static class SagNewton
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;

    public delegate (double Z, double Zx, double Zy) SagFunction(double x, double y);

    public static bool Intersect(double x, double y, Vector3D d, double start, SagFunction sag,
        out Vector3D point, out double distance)
    {
        double s = start;
        double scale = Math.Abs(x) + Math.Abs(y);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            double px = x + s * d.X, py = y + s * d.Y, pz = s * d.Z;
            var (z, zx, zy) = sag(px, py);
            double g = z - pz, dg = zx * d.X + zy * d.Y - d.Z;
            if (!double.IsFinite(g) || !double.IsFinite(dg) || dg == 0) break;
            double step = g / dg;
            s -= step;
            if (Math.Abs(step) <= 4 * Eps * (Math.Abs(s) + scale + Math.Abs(z)))
            {
                point = new Vector3D(x + s * d.X, y + s * d.Y, s * d.Z);
                distance = s;
                return double.IsFinite(s);
            }
        }
        point = default;
        distance = double.NaN;
        return false;
    }

    /// <summary>The unit normal of z = sag(x, y), the sense that is +z: (−z_x, −z_y, 1)/|…|.</summary>
    public static Vector3D Normal(double zx, double zy)
    {
        var n = new Vector3D(-zx, -zy, 1);
        return n / n.Length;
    }
}

/// <summary>Shared argument checks with actionable messages.</summary>
internal static class ShapeChecks
{
    public static double Finite(double value, string what)
    {
        if (!double.IsFinite(value)) throw new ArgumentException($"{what} must be a number (it is {value}).");
        return value;
    }

    /// <summary>The sense of travel along the local z: +1 or −1 (a ray with N = 0 never
    /// reaches a shape, the trace stops it at the vertex plane).</summary>
    public static double Travel(Vector3D d) => d.Z >= 0 ? 1 : -1;
}
