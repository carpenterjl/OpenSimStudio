namespace OpenSim.Optics.Sequential.Aspheres;

// The four power-series aspheres of OpticStudio and the two alternate-root variants
// (docs/Optics-Conventions.md §17). Definitions: OpticStudio 2025 R2 help, ansyshelp.ansys.com,
// pages "Even Asphere", "Odd Asphere", "Extended Asphere", "Extended Odd Asphere",
// "Alternate Even" and "Alternate Odd" (saved 2026-11-14 under plans/sources/optics/aspheres/).
// Coefficients are SI: a coefficient on r^n is in m^(1 − n); on a normalised ρ it is in meters.

/// <summary>
/// OpticStudio's Even Asphere: z = c·r²/(1 + √(1 − (1 + k)c²r²)) + Σᵢ αᵢ·r²ⁱ, i = 1…8
/// ("Even Asphere": parameters 1–8 are α1–α8). α1 multiplies r², so it changes the paraxial
/// curvature by 2α1.
/// </summary>
public class EvenAsphere : RadialSagShape
{
    private readonly double[] _alpha;

    /// <param name="alpha">α1, α2, … (at most 8), on r², r⁴, …, in m^(1 − 2i).</param>
    public EvenAsphere(IReadOnlyList<double> alpha)
    {
        _alpha = CheckedCopy(alpha, "Even Asphere", "coefficient", 8);
    }

    /// <summary>α1 … αn; αi multiplies r^(2i).</summary>
    public IReadOnlyList<double> Alpha => _alpha;

    public override string TypeName => "Even Asphere";

    public override bool TryRadialSag(double r, SequentialSurface surface, out double z, out double dzdr)
    {
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out dzdr)) return false;
        PowerSeries.EvenInR(_alpha, r, out double p, out double dp);
        z += p;
        dzdr += dp;
        return true;
    }

    /// <summary>
    /// The vertex curvature c + 2α1 (TC-6). Derived from the sag of "Even Asphere": the conic
    /// c r²/(1 + √(1 − (1 + k)c²r²)) = c r²/2 + O(r⁴) for every k, and Σ αᵢ r²ⁱ = α1 r² + O(r⁴),
    /// so d²z/dr² at r = 0 is c + 2α1. Alternate Even inherits it: its sag is the Even
    /// Asphere's ("identical … except the alternate solution is used"), §17.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { Curvature = ParaxialCurvature.Add(surface.Curvature, 2 * ParaxialCurvature.Term(_alpha, 0)) };
}

/// <summary>
/// OpticStudio's Odd Asphere: z = conic + Σᵢ βᵢ·rⁱ, i = 1…8 ("Odd Asphere": parameters 1–8
/// are β1–β8). A β1 ≠ 0 puts a cone point on the axis, where the normal is taken as +z.
/// </summary>
public class OddAsphere : RadialSagShape
{
    private readonly double[] _beta;

    /// <param name="beta">β1, β2, … (at most 8), on r, r², …, in m^(1 − i).</param>
    public OddAsphere(IReadOnlyList<double> beta)
    {
        _beta = CheckedCopy(beta, "Odd Asphere", "coefficient", 8);
    }

    public IReadOnlyList<double> Beta => _beta;

    public override string TypeName => "Odd Asphere";

    public override bool TryRadialSag(double r, SequentialSurface surface, out double z, out double dzdr)
    {
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out dzdr)) return false;
        PowerSeries.AllPowersInR(_beta, r, out double p, out double dp);
        z += p;
        dzdr += dp;
        return true;
    }

    /// <summary>
    /// The vertex curvature c + 2β2 (TC-6), the limit of d²z/dr² as r → 0⁺. Derived from the sag
    /// of "Odd Asphere": the conic is c r²/2 + O(r⁴); β2 r² adds 2β2; β1 r has zero second
    /// derivative for r &gt; 0, and β3 r³ … β8 r⁸ have second derivatives that vanish as r → 0.
    /// β1 r is a cone (axicon) at the vertex: along the y meridian it is β1|y|, a slope that
    /// jumps from −β1 to +β1, which turns every ray by about the same angle whatever its height.
    /// A first-order model is linear in height and cannot hold that, so β1 is left out of the
    /// paraxial data (OpticStudio's help does not say how its paraxial trace treats β1; this is
    /// this program's reading, docs/Optics-Conventions.md §17 and §10 item 13). The real trace
    /// keeps every term. Alternate Odd inherits this.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { Curvature = ParaxialCurvature.Add(surface.Curvature, 2 * ParaxialCurvature.Term(_beta, 1)) };
}

/// <summary>
/// OpticStudio's Extended Asphere: z = conic + Σᵢ αᵢ·ρ²ⁱ, i = 1…N, ρ = r/R_n with R_n the
/// normalization radius ("Extended Asphere": parameter 13 the maximum term number, at most
/// 240; 14 the normalization radius, by which "all ray-intercept points are divided"; 15 on
/// ρ², 16 on ρ⁴, …). The αᵢ are lengths (meters).
/// </summary>
public sealed class ExtendedAsphere : RadialSagShape
{
    private readonly double[] _alpha;

    public ExtendedAsphere(double normalizationRadius, IReadOnlyList<double> alpha)
    {
        if (!(normalizationRadius > 0) || !double.IsFinite(normalizationRadius))
            throw new ArgumentException($"Extended Asphere: the normalization radius is {normalizationRadius}; it must be a positive length.");
        NormalizationRadius = normalizationRadius;
        _alpha = CheckedCopy(alpha, "Extended Asphere", "coefficient", 240);
    }

    /// <summary>R_n, meters.</summary>
    public double NormalizationRadius { get; }

    /// <summary>α1 … αN, meters; αi multiplies ρ^(2i).</summary>
    public IReadOnlyList<double> Alpha => _alpha;

    public override string TypeName => "Extended Asphere";

    public override bool TryRadialSag(double r, SequentialSurface surface, out double z, out double dzdr)
    {
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out dzdr)) return false;
        PowerSeries.EvenInR(_alpha, r / NormalizationRadius, out double p, out double dp);
        z += p;
        dzdr += dp / NormalizationRadius;
        return true;
    }

    /// <summary>
    /// The vertex curvature c + 2α1/R_n² (TC-6). Derived from the sag of "Extended Asphere":
    /// α1 ρ² = (α1/R_n²) r², and every other term is O(r⁴), as is the conic beyond c r²/2.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new()
        {
            Curvature = ParaxialCurvature.Add(surface.Curvature,
                2 * ParaxialCurvature.Term(_alpha, 0) / (NormalizationRadius * NormalizationRadius)),
        };
}

/// <summary>
/// OpticStudio's Extended Odd Asphere: z = conic + Σᵢ αᵢ·ρⁱ, i = 1…N, ρ = r/R_n
/// ("Extended Odd Asphere": maximum term number at most 240, the normalization radius, then
/// the coefficients on ρ¹, ρ², ρ³, …). The αᵢ are lengths (meters).
/// </summary>
public sealed class ExtendedOddAsphere : RadialSagShape
{
    private readonly double[] _alpha;

    public ExtendedOddAsphere(double normalizationRadius, IReadOnlyList<double> alpha)
    {
        if (!(normalizationRadius > 0) || !double.IsFinite(normalizationRadius))
            throw new ArgumentException($"Extended Odd Asphere: the normalization radius is {normalizationRadius}; it must be a positive length.");
        NormalizationRadius = normalizationRadius;
        _alpha = CheckedCopy(alpha, "Extended Odd Asphere", "coefficient", 240);
    }

    public double NormalizationRadius { get; }

    /// <summary>α1 … αN, meters; αi multiplies ρ^i.</summary>
    public IReadOnlyList<double> Alpha => _alpha;

    public override string TypeName => "Extended Odd Asphere";

    public override bool TryRadialSag(double r, SequentialSurface surface, out double z, out double dzdr)
    {
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out dzdr)) return false;
        PowerSeries.AllPowersInR(_alpha, r / NormalizationRadius, out double p, out double dp);
        z += p;
        dzdr += dp / NormalizationRadius;
        return true;
    }

    /// <summary>
    /// The vertex curvature c + 2α2/R_n² (TC-6), the limit of d²z/dr² as r → 0⁺. Derived from the
    /// sag of "Extended Odd Asphere": α2 ρ² = (α2/R_n²) r²; α1 ρ is a cone and α3 ρ³ … vanish to
    /// second order, as for <see cref="OddAsphere.ParaxialEquivalent"/>, whose reading of the cone
    /// term applies here too.
    /// </summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new()
        {
            Curvature = ParaxialCurvature.Add(surface.Curvature,
                2 * ParaxialCurvature.Term(_alpha, 1) / (NormalizationRadius * NormalizationRadius)),
        };
}

/// <summary>
/// OpticStudio's Alternate Even: "identical to the even asphere surface model, except the
/// alternate solution is used" ("Alternate Even"). Read here as: the intersection starts from
/// the other root of the base conic's quadratic (<see cref="AsphereIntersection.AlternateConicRoot"/>)
/// instead of the vertex root, then refines on the asphere's sag as usual (§17).
/// </summary>
public sealed class AlternateEvenAsphere : EvenAsphere
{
    public AlternateEvenAsphere(IReadOnlyList<double> alpha) : base(alpha) { }

    public override string TypeName => "Alternate Even";

    public override bool UsesAlternateRoot => true;
}

/// <summary>OpticStudio's Alternate Odd: the Odd Asphere met at the alternate root
/// ("Alternate Odd"; see <see cref="AlternateEvenAsphere"/>).</summary>
public sealed class AlternateOddAsphere : OddAsphere
{
    public AlternateOddAsphere(IReadOnlyList<double> beta) : base(beta) { }

    public override string TypeName => "Alternate Odd";

    public override bool UsesAlternateRoot => true;
}

/// <summary>Shared arithmetic of the forms' <see cref="SurfaceShape.ParaxialEquivalent"/> overrides (TC-6).</summary>
internal static class ParaxialCurvature
{
    /// <summary>Coefficient <paramref name="index"/> (0-based), or 0 when the list is shorter.</summary>
    public static double Term(double[] coefficients, int index) =>
        index < coefficients.Length ? coefficients[index] : 0;

    /// <summary>c + δ, but c itself when δ = 0, so a surface without a second-order term gives
    /// the paraxial engine exactly the Standard surface's curvature (−0 stays −0).</summary>
    public static double Add(double curvature, double delta) => delta == 0 ? curvature : curvature + delta;
}

/// <summary>Horner evaluation of the power series of the asphere forms, with derivative.</summary>
internal static class PowerSeries
{
    /// <summary>p = Σᵢ cᵢ·s^(2i) (i from 1) and dp/ds.</summary>
    public static void EvenInR(double[] c, double s, out double p, out double dp)
    {
        // p = s²·q(s²), q(w) = c1 + c2·w + …; dp/ds = 2s·q + s²·q′·2s = 2s(q + w·q′).
        double w = s * s, q = 0, dq = 0;
        for (int i = c.Length - 1; i >= 0; i--)
        {
            dq = dq * w + q;
            q = q * w + c[i];
        }
        p = w * q;
        dp = 2 * s * (q + w * dq);
    }

    /// <summary>p = Σᵢ cᵢ·sⁱ (i from 1) and dp/ds.</summary>
    public static void AllPowersInR(double[] c, double s, out double p, out double dp)
    {
        // p = s·q(s), q(s) = c1 + c2·s + …; dp/ds = q + s·q′.
        double q = 0, dq = 0;
        for (int i = c.Length - 1; i >= 0; i--)
        {
            dq = dq * s + q;
            q = q * s + c[i];
        }
        p = s * q;
        dp = (q + s * dq);
    }
}
