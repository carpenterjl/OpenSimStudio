namespace OpenSim.Optics.Sequential.Aspheres;

/// <summary>The two variations of OpticStudio's Q-Type Asphere (extra data 13: 0 Qbfs, 1 Qcon).</summary>
public enum QTypeKind
{
    Qbfs = 0,
    Qcon = 1,
}

/// <summary>
/// OpticStudio's Q-Type Asphere ("Q-Type Asphere (sequential surfaces, lens data editor)",
/// OpticStudio 2025 R2 help): a type (Qbfs or Qcon), a normalization radius ρmax ("used … to
/// normalize the radial coordinates, analogous to the Extended Asphere"), and coefficients
/// A0 … A(M), at most 100. The help cites, for Qcon, Forbes, Opt. Express 15, 5218 (2007),
/// Eqs. 2–3, and for Qbfs, Forbes, Opt. Express 19, 9923 (2011), Eqs. 2.1–2.4. With
/// u = r/ρmax, x = u², c the surface's curvature and κ its conic:
/// <list type="bullet">
/// <item>Qcon: z = c r²/(1 + φ) + x²·Σ a_m Q^con_m(x), φ = √(1 − (1 + κ)c²r²) (2007 Eqs. 2–3,
/// with the conic parameter ε = 1 + κ; the same as Forbes 2010, Opt. Express 18, 13851,
/// Eq. 5.1).</item>
/// <item>Qbfs: z = c r²/(1 + φ) + (1/σ)·x(1 − x)·Σ a_m Q^bfs_m(x), 1/σ = ψ/φ,
/// ψ = √(1 − κc²r²) (2011 Eqs. 2.1–2.4; with κ = 0 it is Forbes 2010, Opt. Express 18, 19700,
/// Eq. 2.1, and c is the best-fit sphere's curvature).</item>
/// </list>
/// The sums and their derivatives come from <see cref="QPolynomials"/> (recurrences only).
/// The coefficients a_m are lengths (meters).
/// </summary>
public sealed class QTypeAsphere : RadialSagShape
{
    private readonly double[] _a;
    private readonly double[] _auxiliary;   // Qbfs only: the b of Forbes 2010b Eq. (3.4)–(3.5)

    public QTypeAsphere(QTypeKind kind, double normalizationRadius, IReadOnlyList<double> coefficients)
    {
        if (kind is not (QTypeKind.Qbfs or QTypeKind.Qcon))
            throw new ArgumentException($"Q-Type Asphere: type {(int)kind} is neither 0 (Qbfs) nor 1 (Qcon).");
        if (!(normalizationRadius > 0) || !double.IsFinite(normalizationRadius))
            throw new ArgumentException($"Q-Type Asphere: the normalization radius is {normalizationRadius}; it must be a positive length.");
        Kind = kind;
        NormalizationRadius = normalizationRadius;
        _a = CheckedCopy(coefficients, "Q-Type Asphere", "coefficient", 100);
        _auxiliary = kind == QTypeKind.Qbfs ? QPolynomials.BfsToAuxiliary(_a) : Array.Empty<double>();
    }

    public QTypeKind Kind { get; }

    /// <summary>ρmax, meters.</summary>
    public double NormalizationRadius { get; }

    /// <summary>A0 … A(M), meters.</summary>
    public IReadOnlyList<double> Coefficients => _a;

    public override string TypeName => "Q-Type Asphere";

    public override bool TryRadialSag(double r, SequentialSurface surface, out double z, out double dzdr)
    {
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out dzdr)) return false;
        if (_a.Length == 0) return true;
        double rho = NormalizationRadius, u = r / rho, x = u * u;
        if (Kind == QTypeKind.Qcon)
        {
            // Forbes 2010a Eqs. (5.3)–(5.4): + u⁴S(u²); d/dr = (2u³/ρmax)[2S + u²S′].
            QPolynomials.ConSum(_a, x, out double s, out double ds);
            z += x * x * s;
            dzdr += 2 * u * x / rho * (2 * s + x * ds);
            return true;
        }

        // Qbfs, Forbes 2011 Eq. (2.4): + w·g with w = 1/σ = ψ/φ and g = x(1 − x)S(x).
        // Derived here: w′ = c²r/(ψφ³) (the numerator −κφ² + (1 + κ)ψ² is identically 1), and
        // dg/dr = (2u/ρmax)[(1 − 2x)S + x(1 − x)S′]. With κ = 0 the sum of the two S terms is
        // Forbes 2010b Eq. (3.14), which the tests check.
        double c = surface.Curvature, k = surface.Conic;
        double phi2 = 1 - (1 + k) * c * c * r * r, psi2 = 1 - k * c * c * r * r;
        if (!(phi2 > 0) || !(psi2 > 0)) return false;
        double phi = Math.Sqrt(phi2), psi = Math.Sqrt(psi2);
        QPolynomials.BfsSumAuxiliary(_auxiliary, x, out double sb, out double dsb);
        double w = psi / phi;
        double dw = c * c * r / (psi * phi2 * phi);
        double g = x * (1 - x) * sb;
        double dg = 2 * u / rho * ((1 - 2 * x) * sb + x * (1 - x) * dsb);
        z += w * g;
        dzdr += dw * g + w * dg;
        return true;
    }

    /// <summary>
    /// The vertex curvature (TC-6), derived from the two sags in the class summary with
    /// u = r/ρmax, x = u²:
    /// <list type="bullet">
    /// <item>Qcon: the added term x²·S(x) = (r/ρmax)⁴·S is O(r⁴), so d²z/dr² at 0 is the conic's
    /// c.</item>
    /// <item>Qbfs: w = ψ/φ = 1 + O(r²) (ψ² − φ² = c²r²) and x(1 − x)S(x) = (r²/ρmax²)·S(0) + O(r⁴),
    /// so the added term is S(0)·r²/ρmax² + O(r⁴) and the curvature is c + 2S(0)/ρmax², with
    /// S(0) = Σ a_m Q^bfs_m(0) from the same Clenshaw sum the sag uses (Forbes 2010b
    /// Eqs. 3.7–3.9).</item>
    /// </list>
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        if (Kind == QTypeKind.Qcon || _a.Length == 0) return new() { Curvature = surface.Curvature };
        QPolynomials.BfsSumAuxiliary(_auxiliary, 0, out double s0, out _);
        return new()
        {
            Curvature = ParaxialCurvature.Add(surface.Curvature, 2 * s0 / (NormalizationRadius * NormalizationRadius)),
        };
    }
}
