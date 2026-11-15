using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Optics.Sequential.Freeform;

/// <summary>
/// OpticStudio's Off-Axis Conic Freeform surface (OP-25, docs/Optics-Conventions.md §55): the
/// part of a conic (the surface's radius R and conic κ) centred at the off-axis distance Y₀ on the
/// parent's y axis, in a frame "decentered and tilted to the center of the off-axis conic", plus
/// freeform terms Σ A_{j,k}(x/R_n)^j (y/R_n)^k (OpticStudio 2025 R2 help, "Off-Axis Conic
/// Freeform", its sag equation, term table and parameter table: 13 the offset Y₀, 14 the maximum
/// term number, 15 the normalization radius, 16–245 the coefficients, lens units). The terms run
/// "by increasing total power (j+k) and then by decreasing powers of x", A₀,₀ omitted, which is
/// the Extended Polynomial's order (<see cref="ExtendedPolynomialTerms"/>).
/// <para>
/// <b>Reading of the sag.</b> The page prints z = −p^{3/2}/q + κY₀√(p − Y₀²)·y/q
/// + R/|qR|·√S, S = p³ − 2κpY₀√(p(p − Y₀²))·y − (κ + 1)p²y² − pqx², p = R² − κY₀²,
/// q = (κ + 1)p − κY₀². Read literally it is the parent conic of radius −R for R &gt; 0 (at Y₀ = 0
/// it is −r²/(2R) + …, the opposite of the Standard surface of radius R), and it does not pass
/// through the vertex for R &lt; 0 or q &lt; 0 (a hyperbola). This program takes the branch that
/// passes through the vertex for every sign and makes the surface at Y₀ = 0 the Standard conic of
/// the surface's own radius and conic: z = sgn(R)·(p^{3/2} − Ly − √S)/q, L = κY₀√(p − Y₀²),
/// evaluated in the equivalent form z = sgn(R)·(R²y² + px²)/(p^{3/2} − Ly + √S) (derived: multiply
/// by the conjugate; 2κpY₀√(p(p − Y₀²)) = 2p^{3/2}L and L² + (κ + 1)p² = qR², so q cancels and
/// the paraboloid at Y₀ = 0, where q = 0, is regular). The frame is then the parent Standard conic
/// sag(Y) translated to (Y₀, sag(Y₀)) and turned by atan(sag′(Y₀)) about x (checked in the tests
/// against points of the parent). That sign is not checked against OpticStudio (§10 item 51).
/// A plane (R infinite) has no off-axis conic; the base is then zero and the terms remain.
/// </para>
/// </summary>
public sealed class OffAxisConicFreeform : SagSurfaceShape
{
    private readonly double[] _terms;

    /// <param name="offset">Y₀, meters.</param>
    /// <param name="normalizationRadius">R_n, meters; positive when a term is given.</param>
    /// <param name="terms">A₁ … A_N in the table's order, meters, N ≤ 230.</param>
    public OffAxisConicFreeform(double offset, double normalizationRadius, IReadOnlyList<double> terms)
    {
        if (!double.IsFinite(offset))
            throw new ArgumentException($"Off-Axis Conic Freeform: the offset Y0 is {offset}; it must be a finite length.");
        _terms = CheckedCopy(terms, "Off-Axis Conic Freeform", "term", ExtendedPolynomialTerms.MaxTerms);
        if (!double.IsFinite(normalizationRadius) || normalizationRadius < 0 || (_terms.Length > 0 && normalizationRadius == 0))
            throw new ArgumentException($"Off-Axis Conic Freeform: the normalization radius is {normalizationRadius}; it must be a positive length when terms are used.");
        Offset = offset;
        NormalizationRadius = normalizationRadius;
    }

    /// <summary>Y₀, meters.</summary>
    public double Offset { get; }

    public double NormalizationRadius { get; }

    /// <summary>A₁ … A_N, meters.</summary>
    public IReadOnlyList<double> Terms => _terms;

    public override string TypeName => "Off-Axis Conic Freeform";

    /// <summary>The off-axis conic's sag and slopes (no terms); false outside its domain.</summary>
    internal bool TryBase(double x, double y, double curvature, double conic, out double z, out double zx, out double zy)
    {
        z = zx = zy = 0;
        if (curvature == 0) return true;
        double R = 1 / curvature, k = conic, y0 = Offset;
        double s = Math.Sign(R);
        double p = R * R - k * y0 * y0;
        double inner = p - y0 * y0;          // R² − (κ + 1)Y₀², positive where the parent is defined at Y₀
        if (!(inner > 0) || !(p > 0))
        {
            z = double.NaN;
            return false;
        }
        double p15 = p * Math.Sqrt(p);
        double L = k * y0 * Math.Sqrt(inner);
        double q = (k + 1) * p - k * y0 * y0;
        double S = p * p * p - 2 * p15 * L * y - (k + 1) * p * p * y * y - p * q * x * x;
        if (!(S > 0))
        {
            z = double.NaN;
            return false;
        }
        double root = Math.Sqrt(S);
        double num = R * R * y * y + p * x * x;
        double den = p15 - L * y + root;
        if (!(den > 0))
        {
            z = double.NaN;
            return false;
        }
        z = s * num / den;
        // d√S/dx = −pq·x/√S, d√S/dy = −(p^{3/2}L + (κ + 1)p²y)/√S (derived from S).
        double dRootX = -p * q * x / root;
        double dRootY = -(p15 * L + (k + 1) * p * p * y) / root;
        zx = s * (2 * p * x * den - num * dRootX) / (den * den);
        zy = s * (2 * R * R * y * den - num * (-L + dRootY)) / (den * den);
        return double.IsFinite(z) && double.IsFinite(zx) && double.IsFinite(zy);
    }

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        if (!TryBase(x, y, surface.Curvature, surface.Conic, out z, out dzdx, out dzdy))
        {
            z = double.NaN;
            return false;
        }
        if (_terms.Length > 0)
        {
            double rn = NormalizationRadius;
            var (v, du, dv) = ExtendedPolynomialTerms.Evaluate(_terms, x / rn, y / rn);
            z += v;
            dzdx += du / rn;
            dzdy += dv / rn;
        }
        if (double.IsFinite(z) && double.IsFinite(dzdx) && double.IsFinite(dzdy)) return true;
        z = double.NaN;
        return false;
    }

    /// <summary>
    /// The vertex expansion. Derived from the form above: at (0, 0), S = p³, the denominator is
    /// 2p^{3/2}, so z = sgn(R)(R²y² + px²)/(2p^{3/2}) + O(3): ∂²z/∂x² = sgn(R)/√p,
    /// ∂²z/∂y² = sgn(R)R²/p^{3/2}, no twist and no slope (at Y₀ = 0 both are 1/R, the conic's
    /// vertex curvature). The terms add their order-1 and order-2 parts as the Extended
    /// Polynomial's do. The base curvature passed on is 0 and the whole y–z curvature comes from
    /// the expansion, since the off-axis part's curvature is not the parent's 1/R.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        var t = new VertexTaylor();
        double c = surface.Curvature;
        if (c != 0)
        {
            double R = 1 / c, p = R * R - surface.Conic * Offset * Offset;
            if (!(p - Offset * Offset > 0))
                throw new ArgumentException($"Off-Axis Conic Freeform: the offset {Offset:R} m lies outside the parent conic (R = {R:R} m, conic {surface.Conic:R}); there is no surface at the vertex.");
            double s = Math.Sign(R);
            t = new VertexTaylor { Hxx = s / Math.Sqrt(p), Hyy = s * R * R / (p * Math.Sqrt(p)) };
        }
        if (_terms.Length > 0)
        {
            double rn = NormalizationRadius;
            double T(int i) => i <= _terms.Length ? _terms[i - 1] : 0;
            t += new VertexTaylor
            {
                Gx = T(1) / rn, Gy = T(2) / rn,
                Hxx = 2 * T(3) / (rn * rn), Hxy = T(4) / (rn * rn), Hyy = 2 * T(5) / (rn * rn),
            };
        }
        if (t.Hyy == 0 && c == 0) return new ParaxialShapeEquivalent { Curvature = c };
        return AnamorphicParaxial.YCurvature(TypeName, 0, t, "from its off-axis conic and polynomial terms");
    }
}
