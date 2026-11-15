using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Optics.Sequential.Freeform;

// The polynomial freeforms of OP-25 (docs/Optics-Conventions.md §55): OpticStudio's Polynomial,
// Extended Polynomial and Chebyshev Polynomial surfaces. Definitions: OpticStudio 2025 R2 help,
// pages "Polynomial", "Extended Polynomial" and "Chebyshev Polynomial" (saved 2026-11-14 under
// plans/sources/optics/op-25-help/, equations read from the pages' images). All values SI.

/// <summary>
/// OpticStudio's Polynomial surface: z = γ1x² + γ2x⁴ + γ3x⁶ + γ4x⁸ + γ5y² + γ6y⁴ + γ7y⁶ + γ8y⁸
/// ("Polynomial", its sag equation; parameters 1–8 are γ1–γ8). "The base radius of curvature
/// and the conic constant are not used by this surface model", so the layout refuses them.
/// </summary>
public sealed class PolynomialSurface : SagSurfaceShape
{
    private readonly double[] _gamma, _gx, _gy;

    /// <param name="gamma">γ1 … γ8 (fewer are zero-padded): γ1–γ4 on x², x⁴, x⁶, x⁸ in
    /// m^(1 − 2i), γ5–γ8 on y², y⁴, y⁶, y⁸.</param>
    public PolynomialSurface(IReadOnlyList<double> gamma)
    {
        var g = CheckedCopy(gamma, "Polynomial", "coefficient γ", 8);
        _gamma = new double[8];
        Array.Copy(g, _gamma, g.Length);
        _gx = _gamma[..4];
        _gy = _gamma[4..];
    }

    /// <summary>γ1 … γ8.</summary>
    public IReadOnlyList<double> Gamma => _gamma;

    public override string TypeName => "Polynomial";

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        PowerSeries.EvenInR(_gx, x, out double px, out double dpx);
        PowerSeries.EvenInR(_gy, y, out double py, out double dpy);
        z = px + py;
        dzdx = dpx;
        dzdy = dpy;
        if (double.IsFinite(z) && double.IsFinite(dzdx) && double.IsFinite(dzdy)) return true;
        z = double.NaN;
        return false;
    }

    /// <summary>Derived: z = γ1x² + γ5y² + O(4), so ∂²z/∂x² = 2γ1 and ∂²z/∂y² = 2γ5 at the
    /// vertex; the y–z curvature 2γ5 goes to the paraxial engine (§23's rule).</summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        AnamorphicParaxial.YCurvature(TypeName, surface.Curvature,
            new VertexTaylor { Hxx = 2 * _gamma[0], Hyy = 2 * _gamma[4] }, "from its polynomial");
}

/// <summary>
/// OpticStudio's Extended Polynomial surface: z = c·r²/(1 + √(1 − (1 + k)c²r²)) + Σᵢ Aᵢ·Eᵢ(x/R_n, y/R_n)
/// ("Extended Polynomial": "The first term is x, then y, then x*x, x*y, y*y, etc. … The maximum
/// order is 20, which makes a maximum of 230 polynomial aspheric coefficients"; parameter 13 the
/// maximum term number, 14 the normalization radius, 15–244 the terms; the Aᵢ are lengths).
/// The term order is <see cref="ExtendedPolynomialTerms"/> (OP-27).
/// </summary>
public sealed class ExtendedPolynomialSurface : SagSurfaceShape
{
    private readonly double[] _terms;

    /// <param name="normalizationRadius">R_n, meters; positive when a term is given.</param>
    /// <param name="terms">A1 … AN, meters, N ≤ 230.</param>
    public ExtendedPolynomialSurface(double normalizationRadius, IReadOnlyList<double> terms)
    {
        _terms = CheckedCopy(terms, "Extended Polynomial", "term", ExtendedPolynomialTerms.MaxTerms);
        if (!double.IsFinite(normalizationRadius) || normalizationRadius < 0 || (_terms.Length > 0 && normalizationRadius == 0))
            throw new ArgumentException($"Extended Polynomial: the normalization radius is {normalizationRadius}; it must be a positive length when terms are used (the coordinates are divided by it).");
        NormalizationRadius = normalizationRadius;
    }

    public double NormalizationRadius { get; }

    /// <summary>A1 … AN, meters.</summary>
    public IReadOnlyList<double> Terms => _terms;

    public override string TypeName => "Extended Polynomial";

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        dzdx = dzdy = 0;
        double r = Math.Sqrt(x * x + y * y);
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out double dzdr))
            return false;
        if (r > 0)
        {
            dzdx = dzdr * x / r;
            dzdy = dzdr * y / r;
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

    /// <summary>Derived: the terms of order 1 and 2 are A1·u + A2·v + A3·u² + A4·uv + A5·v²,
    /// u = x/R_n, so the slopes at the vertex are A1/R_n and A2/R_n, ∂²z/∂x² = 2A3/R_n²,
    /// ∂²z/∂x∂y = A4/R_n² and ∂²z/∂y² = 2A5/R_n² (§23's rule refuses a slope or a twist).</summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        double rn = NormalizationRadius;
        double T(int i) => i <= _terms.Length ? _terms[i - 1] : 0;
        var t = _terms.Length == 0 ? new VertexTaylor() : new VertexTaylor
        {
            Gx = T(1) / rn, Gy = T(2) / rn,
            Hxx = 2 * T(3) / (rn * rn), Hxy = T(4) / (rn * rn), Hyy = 2 * T(5) / (rn * rn),
        };
        return AnamorphicParaxial.YCurvature(TypeName, surface.Curvature, t, "from its polynomial terms");
    }
}

/// <summary>
/// OpticStudio's Chebyshev Polynomial surface ("Chebyshev Polynomial", its sag equation):
/// z = c(x² + y²)/(1 + √(1 − c²(x² + y²))) + x₀y₀·Σᵢ Σⱼ c(i, j)·Tᵢ(x/x₀)·Tⱼ(y/y₀), i ≤ N, j ≤ M,
/// with Tₙ(x) = cos(n·cos⁻¹x) the Chebyshev polynomials of the first kind, N and M at most 14,
/// c(i, j) = c_ij/(x₀y₀) "the normalized polynomial coefficients" (inverse meters here), and
/// "Chebyshev surfaces are not defined outside of the normalization radii, X0 and Y0" (such rays
/// miss). The base has no conic term; a conic on the surface is refused (the sag is undefined and
/// the paraxial model throws, naming it).
/// <para>
/// <b>Not registered</b> in <see cref="SurfaceTypes"/>: the help page gives parameters 13–236
/// for the c(i, j) without their order, and 224 values cannot hold the 15 × 15 coefficients of
/// N = M = 14, so the column layout is not readable from the page. The shape is built in code.
/// </para>
/// </summary>
public sealed class ChebyshevPolynomialSurface : SagSurfaceShape
{
    /// <summary>The largest order in x and in y (OpticStudio's limit).</summary>
    public const int MaxOrder = 14;

    private readonly double[,] _c;

    /// <param name="normX">x₀, meters, positive.</param>
    /// <param name="normY">y₀, meters, positive.</param>
    /// <param name="coefficients">c(i, j), inverse meters, i = 0…N on the first index (x),
    /// j = 0…M on the second (y); N, M ≤ 14.</param>
    public ChebyshevPolynomialSurface(double normX, double normY, double[,] coefficients)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        if (!(normX > 0) || !double.IsFinite(normX) || !(normY > 0) || !double.IsFinite(normY))
            throw new ArgumentException($"Chebyshev Polynomial: the normalization lengths ({normX}, {normY}) must be positive lengths.");
        int n = coefficients.GetLength(0), m = coefficients.GetLength(1);
        if (n < 1 || m < 1 || n > MaxOrder + 1 || m > MaxOrder + 1)
            throw new ArgumentException($"Chebyshev Polynomial: the coefficient table is {n} × {m}; the orders in x and y run from 0 to at most {MaxOrder}, so 1 to {MaxOrder + 1} rows and columns.");
        _c = (double[,])coefficients.Clone();
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
                if (!double.IsFinite(_c[i, j]))
                    throw new ArgumentException($"Chebyshev Polynomial: coefficient c({i}, {j}) is {_c[i, j]}; every coefficient must be finite.");
        NormX = normX;
        NormY = normY;
    }

    public double NormX { get; }
    public double NormY { get; }

    /// <summary>The largest x order N.</summary>
    public int OrderX => _c.GetLength(0) - 1;

    /// <summary>The largest y order M.</summary>
    public int OrderY => _c.GetLength(1) - 1;

    /// <summary>c(i, j), inverse meters.</summary>
    public double Coefficient(int i, int j) => i <= OrderX && j <= OrderY ? _c[i, j] : 0;

    public override string TypeName => "Chebyshev Polynomial";

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        dzdx = dzdy = 0;
        double u = x / NormX, v = y / NormY;
        if (surface.Conic != 0 || !(Math.Abs(u) <= 1) || !(Math.Abs(v) <= 1))
        {
            z = double.NaN;
            return false;
        }
        double r = Math.Sqrt(x * x + y * y);
        if (!ConicSag.TryEvaluate(r, surface.Curvature, 0, out z, out double dzdr)) return false;
        if (r > 0)
        {
            dzdx = dzdr * x / r;
            dzdy = dzdr * y / r;
        }
        Span<double> tx = stackalloc double[MaxOrder + 1];
        Span<double> dtx = stackalloc double[MaxOrder + 1];
        Span<double> ty = stackalloc double[MaxOrder + 1];
        Span<double> dty = stackalloc double[MaxOrder + 1];
        Chebyshev(u, OrderX, tx, dtx);
        Chebyshev(v, OrderY, ty, dty);
        double s = 0, su = 0, sv = 0;
        for (int i = 0; i <= OrderX; i++)
            for (int j = 0; j <= OrderY; j++)
            {
                double c = _c[i, j];
                if (c == 0) continue;
                s += c * tx[i] * ty[j];
                su += c * dtx[i] * ty[j];
                sv += c * tx[i] * dty[j];
            }
        double scale = NormX * NormY;
        z += scale * s;
        dzdx += scale * su / NormX;
        dzdy += scale * sv / NormY;
        if (double.IsFinite(z) && double.IsFinite(dzdx) && double.IsFinite(dzdy)) return true;
        z = double.NaN;
        return false;
    }

    /// <summary>T₀ … T_n at <paramref name="u"/> and their derivatives, by T_{k+1} = 2uT_k − T_{k−1}
    /// and its derivative T′_{k+1} = 2T_k + 2uT′_k − T′_{k−1} (derived by differentiating the
    /// recurrence; T₀ = 1, T₁ = u as the help page's table lists).</summary>
    internal static void Chebyshev(double u, int n, Span<double> t, Span<double> dt)
    {
        t[0] = 1;
        dt[0] = 0;
        if (n == 0) return;
        t[1] = u;
        dt[1] = 1;
        for (int k = 1; k < n; k++)
        {
            t[k + 1] = 2 * u * t[k] - t[k - 1];
            dt[k + 1] = 2 * t[k] + 2 * u * dt[k] - dt[k - 1];
        }
    }

    /// <summary>
    /// Derived at the vertex u = v = 0 from Tₙ(cos θ) = cos nθ at θ = π/2: Tₙ(0) = cos(nπ/2),
    /// Tₙ′(0) = n·sin(nπ/2), and from Chebyshev's equation (1 − u²)T″ − uT′ + n²T = 0,
    /// Tₙ″(0) = −n²·cos(nπ/2). With S = x₀y₀ΣΣ c(i, j)Tᵢ(x/x₀)Tⱼ(y/y₀): value x₀y₀ΣΣ c·Tᵢ(0)Tⱼ(0),
    /// slopes divided by x₀ (y₀), second derivatives by x₀² (y₀², x₀y₀).
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        if (surface.Conic != 0)
            throw new ArgumentException($"Chebyshev Polynomial: the conic is {surface.Conic:R}, but the surface's base is a sphere (its sag has no conic term); set the conic to zero.");
        static (double T, double D, double D2) At0(int n) => (n % 4) switch
        {
            0 => (1, 0, -n * n),
            1 => (0, n, 0),
            2 => (-1, 0, n * n),
            _ => (0, -n, 0),
        };
        double value = 0, gx = 0, gy = 0, hxx = 0, hyy = 0, hxy = 0;
        for (int i = 0; i <= OrderX; i++)
            for (int j = 0; j <= OrderY; j++)
            {
                double c = _c[i, j];
                if (c == 0) continue;
                var (ti, di, d2i) = At0(i);
                var (tj, dj, d2j) = At0(j);
                value += c * ti * tj;
                gx += c * di * tj;
                gy += c * ti * dj;
                hxx += c * d2i * tj;
                hyy += c * ti * d2j;
                hxy += c * di * dj;
            }
        double s = NormX * NormY;
        var t = new VertexTaylor
        {
            Value = s * value, Gx = s * gx / NormX, Gy = s * gy / NormY,
            Hxx = s * hxx / (NormX * NormX), Hyy = s * hyy / (NormY * NormY), Hxy = s * hxy / s,
        };
        return AnamorphicParaxial.YCurvature(TypeName, surface.Curvature, t, "from its Chebyshev terms");
    }
}
