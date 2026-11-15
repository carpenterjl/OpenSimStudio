using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Sequential.Freeform;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Sequential.Diffractive;

/// <summary>
/// OpticStudio's Zernike Fringe Phase, Zernike Standard Phase and Zernike Annular Phase surfaces
/// (OpticStudio 2025 R2 help, pages of those names, saved under
/// plans/sources/optics/op-26-help/): "a substrate shape identical to the Standard surface" plus
/// the phase Φ = M·Σᵢ 2π·Aᵢ·Zᵢ(ρ, φ), "The coefficients Aᵢ all have units of waves. One wave is
/// 2π radians", ρ = r/R (R the normalization radius, parameter 14), M the order (parameter 0),
/// the extrapolate flag in parameter 1 ("If the 'Extrapolate' flag is set to 0, the Zernike terms
/// are ignored outside of the normalization radius"), the number of terms in parameter 13 (Fringe
/// up to 37, Standard up to 231, Annular "Currently OpticStudio limits the number of terms to
/// 80") and the coefficients from parameter 15. The annular form has the obscuration ε in
/// parameter 3, and "Within the obscuration radius, the Zernike terms are always ignored". The
/// polynomials and their slopes are OP-25's (<see cref="FreeformZernikeTerms"/>, the tables
/// "Zernike Fringe Coefficients", "Zernike Standard Coefficients", "Zernike Annular
/// Coefficients"), applied to coefficients in waves.
/// </summary>
public sealed class ZernikePhaseShape : DiffractiveShape
{
    private readonly FreeformZernikeTerms _terms;

    /// <param name="ordering">Fringe, Standard or Annular.</param>
    /// <param name="order">M.</param>
    /// <param name="extrapolate">Parameter 1 as true for 1.</param>
    /// <param name="normalizationRadius">R, meters.</param>
    /// <param name="coefficients">A₁ … A_N, waves.</param>
    /// <param name="obscuration">ε (annular only), 0 ≤ ε &lt; 1.</param>
    public ZernikePhaseShape(ZernikeOrdering ordering, double order, bool extrapolate, double normalizationRadius,
        IReadOnlyList<double> coefficients, double obscuration = 0) : base(null)
    {
        string name = NameOf(ordering);
        if (ordering != ZernikeOrdering.Annular && obscuration != 0)
            throw new ArgumentException($"{name}: only the Zernike Annular Phase has an obscuration.");
        Ordering = ordering;
        Order = Finite(order, name, "the diffraction order");
        _terms = new FreeformZernikeTerms(name, ordering, coefficients, normalizationRadius, extrapolate, MaxTerms(ordering),
            obscuration: obscuration);
    }

    /// <summary>The type's name for <paramref name="ordering"/>.</summary>
    public static string NameOf(ZernikeOrdering ordering) => ordering switch
    {
        ZernikeOrdering.Fringe => "Zernike Fringe Phase",
        ZernikeOrdering.Standard => "Zernike Standard Phase",
        _ => "Zernike Annular Phase",
    };

    /// <summary>The most terms each form holds (help pages, parameter tables).</summary>
    public static int MaxTerms(ZernikeOrdering ordering) => ordering switch
    {
        ZernikeOrdering.Fringe => 37,
        ZernikeOrdering.Standard => 231,
        _ => 80,
    };

    public override string TypeName => NameOf(Ordering);

    public ZernikeOrdering Ordering { get; }
    public double Order { get; }

    /// <summary>A₁ … A_N, waves.</summary>
    public IReadOnlyList<double> Coefficients => _terms.Coefficients;

    /// <summary>R, meters.</summary>
    public double NormalizationRadius => _terms.NormalizationRadius;

    public bool Extrapolate => _terms.Extrapolate;

    /// <summary>ε (annular only).</summary>
    public double Obscuration => _terms.Obscuration;

    public override bool TryPhase(Vector3D point, SequentialSurface surface, out double phase, out Vector3D gradient)
    {
        double w = 0, wx = 0, wy = 0;
        bool ok = _terms.Add(point.X, point.Y, ref w, ref wx, ref wy);
        double k = 2 * Math.PI * Order;
        phase = k * w;
        gradient = new Vector3D(k * wx, k * wy, 0);
        return ok;
    }

    internal override VertexTaylor PhaseAtVertex(SequentialSurface surface, double yCurvature)
    {
        var t = _terms.AtVertex();
        double k = 2 * Math.PI * Order;
        return new VertexTaylor { Value = k * t.Value, Gx = k * t.Gx, Gy = k * t.Gy, Hxx = k * t.Hxx, Hyy = k * t.Hyy, Hxy = k * t.Hxy };
    }
}
