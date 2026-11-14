using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Sequential.Anamorphic;

/// <summary>
/// OpticStudio's Toroidal surface (OpticStudio 2025 R2 help, "Toroidal", saved under
/// plans/sources/optics/op-24-help/). "Toroidal surfaces are formed by defining a curve in the
/// Y-Z plane, and then rotating this curve about an axis parallel to the Y axis and intersecting
/// the Z axis." The curve is
/// <code>
/// z_y(y) = c·y²/(1 + √(1 − (1 + k)c²y²)) + α1·y² + α2·y⁴ + … + α7·y¹⁴
/// </code>
/// ("similar to the even aspheric surface sag formula, except the sixteenth order term has been
/// omitted, and the coordinate argument is y, not r"), c and k the surface's own curvature and
/// conic. It is "rotated about an axis a distance R from the vertex", R the radius of rotation
/// (parameter 1), "zero, which OpticStudio interprets as infinite radius", which models "a
/// cylinder lens which is flat in the X direction". Optional Zernike Standard sag terms are added
/// (parameters 0, 13, 14, 15–245; <see cref="ZernikeSagTerms"/>).
/// <para>
/// <b>The rotated sag</b> (derived from the help's two figures: the point of the curve at height
/// y lies R − z_y from the axis, and turning it about the axis keeps that distance in the x–z
/// plane at that y): with ρ = R − z_y, (R − z)² + x² = ρ², and the sheet through the vertex is
/// z = R − sign(ρ)·√(ρ² − x²) = z_y + x²/(ρ + sign(ρ)·√(ρ² − x²)), the second form free of
/// cancellation. Slopes: ∂z/∂x = sign(ρ)·x/√(ρ² − x²), ∂z/∂y = (|ρ|/√(ρ² − x²))·z_y′(y). The sag
/// is not defined where x² ≥ ρ², or where ρ has the other sign from R (the curve has crossed the
/// axis of rotation). With R = 0 the surface is z_y(y), a cylinder along x.
/// </para>
/// <para>
/// The paraxial engine sees the y–z vertex curvature c + 2α1 plus the Zernike terms' share
/// (<see cref="AnamorphicParaxial"/>); the x–z curvature 1/R is not passed on.
/// </para>
/// </summary>
public sealed class ToroidalSurface : SagSurfaceShape
{
    private readonly double[] _alpha;
    private readonly ZernikeSagTerms _zernike;

    /// <param name="radiusOfRotation">R, meters; 0 for infinite (a cylinder along x).</param>
    /// <param name="alpha">α1 … α7 (at most 7), on y², y⁴, …, y¹⁴, in m^(1 − 2i).</param>
    /// <param name="zernike">A1 … AN on the Zernike Standard terms, meters (at most 231).</param>
    /// <param name="normalizationRadius">The Zernike normalization radius, meters.</param>
    /// <param name="extrapolate">The extrapolate flag (parameter 0) as true for 1.</param>
    public ToroidalSurface(double radiusOfRotation, IReadOnlyList<double> alpha,
        IReadOnlyList<double>? zernike = null, double normalizationRadius = 0, bool extrapolate = false)
    {
        if (!double.IsFinite(radiusOfRotation))
            throw new ArgumentException($"Toroidal: the radius of rotation is {radiusOfRotation}; it must be a finite length (0 means infinite).");
        RadiusOfRotation = radiusOfRotation;
        _alpha = CheckedCopy(alpha, "Toroidal", "coefficient", 7);
        _zernike = new ZernikeSagTerms("Toroidal", zernike ?? Array.Empty<double>(), normalizationRadius, extrapolate, 231);
    }

    public override string TypeName => "Toroidal";

    /// <summary>R, meters; 0 is infinite.</summary>
    public double RadiusOfRotation { get; }

    /// <summary>α1 … αn on y², y⁴, …</summary>
    public IReadOnlyList<double> Alpha => _alpha;

    public IReadOnlyList<double> Zernike => _zernike.Coefficients;
    public double NormalizationRadius => _zernike.NormalizationRadius;
    public bool Extrapolate => _zernike.Extrapolate;

    public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
    {
        dzdx = dzdy = 0;
        // ConicSag's dz/dr = c·r/φ and the series' derivative are odd, so with r = y they are dz/dy.
        if (!ConicSag.TryEvaluate(y, surface.Curvature, surface.Conic, out double zy, out double zyp))
        {
            z = double.NaN;
            return false;
        }
        PowerSeries.EvenInR(_alpha, y, out double p, out double dp);
        zy += p;
        zyp += dp;
        if (RadiusOfRotation == 0)
        {
            z = zy;
            dzdy = zyp;
        }
        else
        {
            double rho = RadiusOfRotation - zy;
            double w2 = rho * rho - x * x;
            if (!(rho * RadiusOfRotation > 0) || !(w2 > 0))
            {
                z = double.NaN;
                return false;
            }
            double w = Math.CopySign(Math.Sqrt(w2), rho);
            z = zy + x * x / (rho + w);
            dzdx = x / w;
            dzdy = rho / w * zyp;
        }
        if (!_zernike.Add(x, y, ref z, ref dzdx, ref dzdy) || !double.IsFinite(z) || !double.IsFinite(dzdx) || !double.IsFinite(dzdy))
        {
            z = double.NaN;
            return false;
        }
        return true;
    }

    /// <summary>What the surface adds to its curvature c at the vertex: 2α1 in y (derived:
    /// z(0, y) = z_y(y) = (c/2 + α1)y² + O(y⁴)), and the Zernike terms' expansion.</summary>
    internal VertexTaylor AddedAtVertex() =>
        new VertexTaylor { Hyy = 2 * ParaxialCurvature.Term(_alpha, 0) } + _zernike.AtVertex();

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        AnamorphicParaxial.YCurvature("Toroidal", surface.Curvature, AddedAtVertex(), "from its Zernike terms");
}
