using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Optics.Sequential.Diffractive;

// OP-26: binary optics (docs/Optics-Conventions.md §57). Definitions: OpticStudio 2025 R2 help,
// pages "Binary 1 (sequential surfaces, lens data editor)" and "Binary 2 (sequential surfaces,
// lens data editor)", saved under plans/sources/optics/op-26-help/. Both have "The shape of the
// binary optic … identical to the even asphere surface type" (<see cref="EvenAsphere"/>,
// parameters 1–8) and coefficients "in radians (2π radians is one wave)".

/// <summary>
/// OpticStudio's Binary Optic 1: Φ = M·Σᵢ Aᵢ·Eᵢ(x/R, y/R), Eᵢ the extended polynomial terms
/// (x, y, x², xy, y², …; <see cref="ExtendedPolynomialTerms"/>), R the normalization radius
/// (parameter 14), N the maximum term number (13, up to 230), A₁ … A_N in parameters 15 on, M
/// the order (parameter 0). "If Parameter 9 is any value other than zero, then the absolute value
/// of the x and y coordinates are used": Φ is then evaluated at (|x|, |y|) and its slopes take
/// the signs of x and y (on the axes, where |x| has no slope, the one-sided slope of x ≥ 0 is
/// taken). Sign convention ("Binary Optic Coefficients Sign Conventions"): "a positive power
/// binary optic has a negative quadratic phase coefficient", which the rule of
/// <see cref="DiffractiveShape"/> reproduces.
/// </summary>
public sealed class BinaryOptic1 : DiffractiveShape
{
    private const string Form = "Binary Optic 1";
    private readonly double[] _terms;

    /// <param name="order">M.</param>
    /// <param name="alpha">The substrate's α1 … α8 (Even Asphere), m^(1 − 2i).</param>
    /// <param name="absolute">Parameter 9 non-zero.</param>
    /// <param name="normalizationRadius">R, meters.</param>
    /// <param name="terms">A₁ … A_N, radians (at most 230).</param>
    public BinaryOptic1(double order, IReadOnlyList<double> alpha, bool absolute, double normalizationRadius, IReadOnlyList<double> terms)
        : base(new EvenAsphere(alpha))
    {
        Order = Finite(order, Form, "the diffraction order");
        Absolute = absolute;
        _terms = FiniteCopy(terms, Form, "phase coefficient", ExtendedPolynomialTerms.MaxTerms);
        NormalizationRadius = Finite(normalizationRadius, Form, "the normalization radius");
        if (_terms.Length > 0 && !(normalizationRadius > 0))
            throw new ArgumentException($"{Form}: {_terms.Length} phase terms are used but the normalization radius is {normalizationRadius}; it must be a positive length (the coordinates are divided by it).");
    }

    public override string TypeName => Form;

    public double Order { get; }
    public bool Absolute { get; }
    public double NormalizationRadius { get; }

    /// <summary>The substrate's α1 … αn.</summary>
    public IReadOnlyList<double> Alpha => ((EvenAsphere)Substrate!).Alpha;

    /// <summary>A₁ … A_N, radians.</summary>
    public IReadOnlyList<double> Terms => _terms;

    public override bool TryPhase(Vector3D point, SequentialSurface surface, out double phase, out Vector3D gradient)
    {
        if (_terms.Length == 0)
        {
            phase = 0;
            gradient = Vector3D.Zero;
            return true;
        }
        double r = NormalizationRadius, u = point.X / r, v = point.Y / r;
        double su = 1, sv = 1;
        if (Absolute)
        {
            if (u < 0) { u = -u; su = -1; }
            if (v < 0) { v = -v; sv = -1; }
        }
        var (value, du, dv) = ExtendedPolynomialTerms.Evaluate(_terms, u, v);
        phase = Order * value;
        gradient = new Vector3D(Order * su * du / r, Order * sv * dv / r, 0);
        return true;
    }

    /// <summary>Terms 1 (x), 2 (y), 3 (x²), 4 (xy) and 5 (y²) at the vertex: slopes M·A₁/R and
    /// M·A₂/R, second derivatives 2M·A₃/R², M·A₄/R² and 2M·A₅/R² (derived from the term order).
    /// With the absolute option a linear or xy term has a kink at the vertex and is refused.</summary>
    internal override VertexTaylor PhaseAtVertex(SequentialSurface surface, double yCurvature)
    {
        double A(int i) => i <= _terms.Length ? _terms[i - 1] : 0;
        if (Absolute && (A(1) != 0 || A(2) != 0 || A(4) != 0))
            throw new ArgumentException($"{Form}: with the absolute option (parameter 9) the x, y or xy phase term has a kink at the vertex, so the surface has no first-order model. Paraxial data, ray aiming and solves are not available for this lens.");
        if (_terms.Length == 0) return new VertexTaylor();
        double r = NormalizationRadius, m = Order;
        return new VertexTaylor
        {
            Gx = m * A(1) / r, Gy = m * A(2) / r,
            Hxx = 2 * m * A(3) / (r * r), Hxy = m * A(4) / (r * r), Hyy = 2 * m * A(5) / (r * r),
        };
    }
}

/// <summary>
/// OpticStudio's Binary Optic 2: Φ = M·Σᵢ Aᵢ·ρ²ⁱ, ρ = r/R, "Ai is the coefficient on the 2ith
/// power of ρ", N up to 240 (parameter 13), R the normalization radius (14), A₁ on ρ² in 15, A₂
/// on ρ⁴ in 16, and so on; M the order (parameter 0). Evaluated by Horner's rule in s = ρ², with
/// ∇Φ = M·(dΦ/ds)·2(x, y)/R², smooth through the axis. With A₁ alone the paraxial power is
/// φ = −λ·Φ″/(2π) = −2·M·A₁·λ/(2π·R²) (<see cref="DiffractiveShape"/>), so the focal length is
/// f = −R²/(2·A₁·M·λ/2π), the plan's f = −1/(2·A₁·m·λ/2π) with R = 1 in the length unit; it
/// scales as 1/λ.
/// </summary>
public sealed class BinaryOptic2 : DiffractiveShape
{
    private const string Form = "Binary Optic 2";

    /// <summary>The most coefficients the form holds ("up to 240 (or the ρ 480 term)").</summary>
    public const int MaxTerms = 240;

    private readonly double[] _terms;

    /// <param name="order">M.</param>
    /// <param name="alpha">The substrate's α1 … α8 (Even Asphere), m^(1 − 2i).</param>
    /// <param name="normalizationRadius">R, meters.</param>
    /// <param name="terms">A₁ … A_N on ρ², ρ⁴, …, radians (at most 240).</param>
    public BinaryOptic2(double order, IReadOnlyList<double> alpha, double normalizationRadius, IReadOnlyList<double> terms)
        : base(new EvenAsphere(alpha))
    {
        Order = Finite(order, Form, "the diffraction order");
        _terms = FiniteCopy(terms, Form, "phase coefficient", MaxTerms);
        NormalizationRadius = Finite(normalizationRadius, Form, "the normalization radius");
        if (_terms.Length > 0 && !(normalizationRadius > 0))
            throw new ArgumentException($"{Form}: {_terms.Length} phase terms are used but the normalization radius is {normalizationRadius}; it must be a positive length (the coordinates are divided by it).");
    }

    public override string TypeName => Form;

    public double Order { get; }
    public double NormalizationRadius { get; }

    /// <summary>The substrate's α1 … αn.</summary>
    public IReadOnlyList<double> Alpha => ((EvenAsphere)Substrate!).Alpha;

    /// <summary>A₁ … A_N on ρ², ρ⁴, …, radians.</summary>
    public IReadOnlyList<double> Terms => _terms;

    public override bool TryPhase(Vector3D point, SequentialSurface surface, out double phase, out Vector3D gradient)
    {
        if (_terms.Length == 0)
        {
            phase = 0;
            gradient = Vector3D.Zero;
            return true;
        }
        double r2 = NormalizationRadius * NormalizationRadius;
        double s = (point.X * point.X + point.Y * point.Y) / r2;
        // Horner: Σ Aᵢ sⁱ = s·(A₁ + s·(A₂ + …)); derivative Σ i·Aᵢ s^(i−1).
        double p = 0, dp = 0;
        for (int i = _terms.Length - 1; i >= 0; i--)
        {
            dp = dp * s + (i + 1) * _terms[i];
            p = p * s + _terms[i];
        }
        p *= s;
        phase = Order * p;
        double g = Order * dp * 2 / r2;
        gradient = new Vector3D(g * point.X, g * point.Y, 0);
        return true;
    }

    /// <summary>Φ = M·A₁·(x² + y²)/R² + O(r⁴): second derivatives 2M·A₁/R² in x and y.</summary>
    internal override VertexTaylor PhaseAtVertex(SequentialSurface surface, double yCurvature)
    {
        if (_terms.Length == 0) return new VertexTaylor();
        double h = 2 * Order * _terms[0] / (NormalizationRadius * NormalizationRadius);
        return new VertexTaylor { Hxx = h, Hyy = h };
    }
}
