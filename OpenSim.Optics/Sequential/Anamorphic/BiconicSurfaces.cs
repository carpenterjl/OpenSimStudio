using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Sequential.Anamorphic;

/// <summary>
/// The biconic sag both biconic forms share (OpticStudio 2025 R2 help, "Biconic", saved under
/// plans/sources/optics/op-24-help/):
/// <code>
/// z = (cx·x² + cy·y²)/(1 + √(1 − (1 + kx)cx²x² − (1 + ky)cy²y²)),   cx = 1/Rx, cy = 1/Ry
/// </code>
/// with Ry and ky the surface's radius and conic and Rx, kx parameters 1 and 2 ("If set to zero,
/// the x radius is interpreted to be infinity"). Slopes (derived, quotient rule): with
/// N = cx·x² + cy·y², φ = √(1 − a·x² − b·y²), a = (1 + kx)cx², b = (1 + ky)cy², z = N/(1 + φ) and
/// ∂φ/∂x = −a·x/φ, so ∂z/∂x = (2cx·x + z·a·x/φ)/(1 + φ), and likewise in y. With cx = cy and
/// kx = ky it is the Standard conic: the slope reduces to c·x/φ because z·(1 + k)c = 1 − φ there.
/// Not defined where the square root's argument is not positive.
/// </summary>
internal static class BiconicSag
{
    public static bool TryEvaluate(double x, double y, double cx, double kx, double cy, double ky,
        out double z, out double dzdx, out double dzdy)
    {
        double a = (1 + kx) * cx * cx, b = (1 + ky) * cy * cy;
        double arg = 1 - a * x * x - b * y * y;
        if (!(arg > 0))
        {
            z = dzdx = dzdy = double.NaN;
            return false;
        }
        double phi = Math.Sqrt(arg), d = 1 + phi;
        z = (cx * x * x + cy * y * y) / d;
        dzdx = (2 * cx * x + z * a * x / phi) / d;
        dzdy = (2 * cy * y + z * b * y / phi) / d;
        return double.IsFinite(z) && double.IsFinite(dzdx) && double.IsFinite(dzdy);
    }

    /// <summary>1/R, or 0 for R = 0 (infinite).</summary>
    public static double Curvature(double radius) => radius == 0 ? 0 : 1 / radius;

    public static double CheckedRadius(double radius, string form)
    {
        if (!double.IsFinite(radius))
            throw new ArgumentException($"{form}: the x radius is {radius}; it must be a finite length (0 means infinite).");
        return radius;
    }

    public static double CheckedConic(double conic, string form)
    {
        if (!double.IsFinite(conic))
            throw new ArgumentException($"{form}: the x conic is {conic}; it must be a number.");
        return conic;
    }
}

/// <summary>
/// OpticStudio's Biconic surface (OpticStudio 2025 R2 help, "Biconic"): "similar to a toroidal
/// surface, except the conic constant and base radius may be different in the X and Y
/// directions"; the sag is <see cref="BiconicSag"/>. Parameter 1 is Rx, parameter 2 kx; the
/// radius and conic columns are Ry and ky. The paraxial engine sees cy (the y–z curvature,
/// <see cref="AnamorphicParaxial"/>); cx is not passed on.
/// </summary>
public sealed class BiconicSurface : SagSurfaceShape
{
    /// <param name="radiusX">Rx, meters; 0 for infinite.</param>
    /// <param name="conicX">kx.</param>
    public BiconicSurface(double radiusX, double conicX)
    {
        RadiusX = BiconicSag.CheckedRadius(radiusX, "Biconic");
        ConicX = BiconicSag.CheckedConic(conicX, "Biconic");
    }

    public override string TypeName => "Biconic";

    /// <summary>Rx, meters; 0 is infinite.</summary>
    public double RadiusX { get; }

    public double ConicX { get; }

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy) =>
        BiconicSag.TryEvaluate(x, y, BiconicSag.Curvature(RadiusX), ConicX, surface.Curvature, surface.Conic, out z, out dzdx, out dzdy);

    /// <summary>The vertex curvature in y is cy, the surface's own (derived: z(0, y) is the
    /// Standard conic of cy, ky); the default <see cref="SurfaceShape.ParaxialEquivalent"/>
    /// gives exactly that.</summary>
    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { Curvature = surface.Curvature };
}

/// <summary>
/// OpticStudio's Biconic Zernike surface (OpticStudio 2025 R2 help, "Biconic Zernike"):
/// <code>
/// z = biconic + Σᵢ₌₁¹⁶ αᵢ·xⁱ + Σᵢ₌₁¹⁶ βᵢ·yⁱ + Σᵢ₌₁ᴺ Aᵢ·Zᵢ(ρ, φ)
/// </code>
/// the biconic of <see cref="BiconicSag"/> and the Zernike Standard terms of
/// <see cref="ZernikeSagTerms"/>. Parameters: 0 the extrapolate flag, 1 Rx, 2 kx, 3 the number of
/// Zernike terms ("between 0 and 210, inclusive"), 4 the normalization radius, 13–28 the
/// coefficients on x¹ … x¹⁶, 29–44 those on y¹ … y¹⁶, 45–254 the Zernike coefficients. αᵢ and βᵢ
/// are in m^(1 − i), the Aᵢ in meters.
/// <para>
/// The paraxial engine sees cy + 2β2 plus the Zernike share; α1, β1 (slopes at the vertex) and
/// Zernike piston, tilt, defocus or 45° astigmatism are refused there by name
/// (<see cref="AnamorphicParaxial"/>).
/// </para>
/// </summary>
public sealed class BiconicZernikeSurface : SagSurfaceShape
{
    private readonly double[] _alpha, _beta;
    private readonly ZernikeSagTerms _zernike;

    /// <param name="radiusX">Rx, meters; 0 for infinite.</param>
    /// <param name="conicX">kx.</param>
    /// <param name="alpha">α1 … α16 on x¹ … x¹⁶ (at most 16), m^(1 − i).</param>
    /// <param name="beta">β1 … β16 on y¹ … y¹⁶ (at most 16), m^(1 − i).</param>
    /// <param name="zernike">A1 … AN, meters (at most 210).</param>
    /// <param name="normalizationRadius">Meters.</param>
    /// <param name="extrapolate">Parameter 0 as true for 1.</param>
    public BiconicZernikeSurface(double radiusX, double conicX, IReadOnlyList<double> alpha, IReadOnlyList<double> beta,
        IReadOnlyList<double>? zernike = null, double normalizationRadius = 0, bool extrapolate = false)
    {
        RadiusX = BiconicSag.CheckedRadius(radiusX, "Biconic Zernike");
        ConicX = BiconicSag.CheckedConic(conicX, "Biconic Zernike");
        _alpha = CheckedCopy(alpha, "Biconic Zernike", "x coefficient", 16);
        _beta = CheckedCopy(beta, "Biconic Zernike", "y coefficient", 16);
        _zernike = new ZernikeSagTerms("Biconic Zernike", zernike ?? Array.Empty<double>(), normalizationRadius, extrapolate, 210);
    }

    public override string TypeName => "Biconic Zernike";

    /// <summary>Rx, meters; 0 is infinite.</summary>
    public double RadiusX { get; }
    public double ConicX { get; }

    /// <summary>α1 … αn on x¹, x², …</summary>
    public IReadOnlyList<double> Alpha => _alpha;

    /// <summary>β1 … βn on y¹, y², …</summary>
    public IReadOnlyList<double> Beta => _beta;

    public IReadOnlyList<double> Zernike => _zernike.Coefficients;
    public double NormalizationRadius => _zernike.NormalizationRadius;
    public bool Extrapolate => _zernike.Extrapolate;

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        if (!BiconicSag.TryEvaluate(x, y, BiconicSag.Curvature(RadiusX), ConicX, surface.Curvature, surface.Conic, out z, out dzdx, out dzdy))
            return false;
        PowerSeries.AllPowersInR(_alpha, x, out double px, out double dpx);
        PowerSeries.AllPowersInR(_beta, y, out double py, out double dpy);
        z += px + py;
        dzdx += dpx;
        dzdy += dpy;
        if (!_zernike.Add(x, y, ref z, ref dzdx, ref dzdy) || !double.IsFinite(z) || !double.IsFinite(dzdx) || !double.IsFinite(dzdy))
        {
            z = double.NaN;
            return false;
        }
        return true;
    }

    /// <summary>What the polynomial and Zernike terms add at the vertex (derived: α1·x and β1·y
    /// are slopes, α2·x² and β2·y² second derivatives 2α2 and 2β2; higher powers vanish to
    /// second order).</summary>
    internal VertexTaylor AddedAtVertex() => new VertexTaylor
    {
        Gx = ParaxialCurvature.Term(_alpha, 0), Gy = ParaxialCurvature.Term(_beta, 0),
        Hxx = 2 * ParaxialCurvature.Term(_alpha, 1), Hyy = 2 * ParaxialCurvature.Term(_beta, 1),
    } + _zernike.AtVertex();

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        AnamorphicParaxial.YCurvature("Biconic Zernike", surface.Curvature, AddedAtVertex(),
            "from its x or y polynomial terms of first order or its Zernike terms");
}
