using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Optics.Sequential.Freeform;

/// <summary>
/// OpticStudio's Zernike Fringe Sag, Zernike Standard Sag and Zernike Annular Standard Sag
/// surfaces (OP-25, docs/Optics-Conventions.md §55): the Even Asphere's sag plus Zernike sag
/// terms, z = c·r²/(1 + √(1 − (1 + k)c²r²)) + Σ αᵢ·r²ⁱ (i = 1…8) + Σ Aᵢ·Zᵢ(ρ, φ) (the sag
/// equations of the help pages "Zernike Fringe Sag", "Zernike Standard Sag" and "Zernike Annular
/// Standard Sag", OpticStudio 2025 R2, read from their images; saved under
/// plans/sources/optics/op-25-help/). The Zernike terms are <see cref="FreeformZernikeTerms"/>.
/// Fringe holds 37 terms, Standard 231, Annular 80; Fringe and Standard take a Zernike decenter
/// (parameters 9, 10), Annular an obscuration (parameter 13) and no decenter.
/// </summary>
public sealed class ZernikeSagSurface : SagSurfaceShape
{
    /// <summary>The most terms each form holds, from the help pages: "up to 37", "up to 231",
    /// "Currently OpticStudio limits the number of terms to 80".</summary>
    public static int MaxTerms(ZernikeOrdering ordering) => ordering switch
    {
        ZernikeOrdering.Fringe => 37,
        ZernikeOrdering.Standard => 231,
        _ => 80,
    };

    /// <summary>The type name of each form.</summary>
    public static string NameOf(ZernikeOrdering ordering) => ordering switch
    {
        ZernikeOrdering.Fringe => "Zernike Fringe Sag",
        ZernikeOrdering.Standard => "Zernike Standard Sag",
        _ => "Zernike Annular Standard Sag",
    };

    private readonly double[] _alpha;
    private readonly FreeformZernikeTerms _zernike;

    /// <param name="ordering">Which of the three surfaces.</param>
    /// <param name="alpha">α1 … α8 on r², …, r¹⁶, m^(1 − 2i).</param>
    /// <param name="coefficients">A1 … AN, meters.</param>
    /// <param name="normalizationRadius">Meters.</param>
    /// <param name="extrapolate">The extrapolate flag (parameter 0).</param>
    /// <param name="decenterX">Zernike decenter x, meters (Fringe and Standard only).</param>
    /// <param name="decenterY">Zernike decenter y, meters (Fringe and Standard only).</param>
    /// <param name="obscuration">ε (Annular only).</param>
    public ZernikeSagSurface(ZernikeOrdering ordering, IReadOnlyList<double> alpha, IReadOnlyList<double> coefficients,
        double normalizationRadius, bool extrapolate, double decenterX = 0, double decenterY = 0, double obscuration = 0)
    {
        string name = NameOf(ordering);
        _alpha = CheckedCopy(alpha, name, "coefficient α", 8);
        if (ordering == ZernikeOrdering.Annular && (decenterX != 0 || decenterY != 0))
            throw new ArgumentException($"{name}: the surface has no Zernike decenter (its help page lists none).");
        if (ordering != ZernikeOrdering.Annular && obscuration != 0)
            throw new ArgumentException($"{name}: only the annular form has an obscuration.");
        Ordering = ordering;
        _zernike = new FreeformZernikeTerms(name, ordering, coefficients, normalizationRadius, extrapolate,
            MaxTerms(ordering), decenterX, decenterY, obscuration);
    }

    public ZernikeOrdering Ordering { get; }

    /// <summary>α1 … αn on r², r⁴, ….</summary>
    public IReadOnlyList<double> Alpha => _alpha;

    /// <summary>A1 … AN, meters.</summary>
    public IReadOnlyList<double> Zernike => _zernike.Coefficients;
    public double NormalizationRadius => _zernike.NormalizationRadius;
    public bool Extrapolate => _zernike.Extrapolate;
    public double DecenterX => _zernike.DecenterX;
    public double DecenterY => _zernike.DecenterY;
    public double Obscuration => _zernike.Obscuration;

    public override string TypeName => NameOf(Ordering);

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        dzdx = dzdy = 0;
        double r = Math.Sqrt(x * x + y * y);
        if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out double dzdr)) return false;
        PowerSeries.EvenInR(_alpha, r, out double p, out double dp);
        z += p;
        dzdr += dp;
        if (r > 0)
        {
            dzdx = dzdr * x / r;
            dzdy = dzdr * y / r;
        }
        if (!_zernike.Add(x, y, ref z, ref dzdx, ref dzdy) || !double.IsFinite(z) || !double.IsFinite(dzdx) || !double.IsFinite(dzdy))
        {
            z = double.NaN;
            return false;
        }
        return true;
    }

    /// <summary>c + 2α1 in both sections (the Even Asphere's, TC-6) and the Zernike terms'
    /// expansion (<see cref="FreeformZernikeTerms.AtVertex"/>), by §23's rule.</summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        double a1 = 2 * ParaxialCurvature.Term(_alpha, 0);
        var t = new VertexTaylor { Hxx = a1, Hyy = a1 } + _zernike.AtVertex();
        return AnamorphicParaxial.YCurvature(TypeName, surface.Curvature, t, "from its Zernike terms");
    }
}
