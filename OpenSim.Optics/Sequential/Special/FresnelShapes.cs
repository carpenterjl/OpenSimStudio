using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Special;

/// <summary>
/// The Fresnel family's common rule: the ray meets a smooth substrate z = s(x, y), and is
/// refracted or reflected there by the trace's Snell's law with the normal of a different
/// facet function f(x, y), (−f_x, −f_y, 1)/|…|, the sense that is +z at the vertex. The
/// substrate intercept is Newton's iteration (<see cref="SagNewton"/>) from the root of the
/// surface's base conic when the substrate is radial, from the vertex plane otherwise. The
/// grooves themselves are not traced, which the help calls an approximation "adequate for
/// Fresnel lens which have fine grooves"; the optical path is the geometric path to the
/// substrate, with no term for the facets.
/// </summary>
public abstract class FresnelShapeBase : SurfaceShape
{
    /// <summary>The substrate z, ∂z/∂x, ∂z/∂y at (x, y).</summary>
    private protected abstract (double Z, double Zx, double Zy) Substrate(double x, double y, SequentialSurface surface);

    /// <summary>The facet function's ∂f/∂x, ∂f/∂y at (x, y).</summary>
    private protected abstract (double Fx, double Fy) Facet(double x, double y, SequentialSurface surface);

    /// <summary>True when the substrate is the plane z = 0.</summary>
    private protected abstract bool PlaneSubstrate(SequentialSurface surface);

    /// <summary>True when the base conic of the surface starts the iteration (radial substrates).</summary>
    private protected virtual bool RadialSubstrate => true;

    public override double Sag(double x, double y, SequentialSurface surface) =>
        PlaneSubstrate(surface) ? 0 : Substrate(x, y, surface).Z;

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance)
    {
        if (PlaneSubstrate(surface))
        {
            point = new Vector3D(x, y, 0);
            distance = 0;
            return true;
        }
        double start = 0;
        if (RadialSubstrate && ConicIntersection.Intersect(x, y, direction, surface.Curvature, surface.Conic, out _, out double conic))
            start = conic;
        return SagNewton.Intersect(x, y, direction, start, (px, py) => Substrate(px, py, surface), out point, out distance);
    }

    public override Vector3D Normal(Vector3D point, SequentialSurface surface)
    {
        var (fx, fy) = Facet(point.X, point.Y, surface);
        return SagNewton.Normal(fx, fy);
    }

    /// <summary>The trace refracts with <see cref="Normal"/>; this only stops a ray where the
    /// facet function is not defined (its conic's square root is not real), which would
    /// otherwise give a NaN normal.</summary>
    public override bool TryRedirect(in SurfaceRedirectInput input, out Vector3D direction,
        out double extraOpticalPath, out RayStatus failure)
    {
        direction = input.Incoming;
        extraOpticalPath = 0;
        var n = input.Normal;
        bool defined = double.IsFinite(n.X) && double.IsFinite(n.Y) && double.IsFinite(n.Z);
        failure = defined ? RayStatus.Ok : RayStatus.MissedSurface;
        return !defined;
    }

    public override string? OpdLimitation(SequentialSurface surface) =>
        PlaneSubstrate(surface) ? null
            : $"OPD is not defined through a {TypeName} surface on a curved substrate (OpticStudio: there is no reliable way to compute the phase through it).";

    private protected static (double Z, double Zx, double Zy) Radial(EvenProfile profile, double x, double y)
    {
        double rho = x * x + y * y;
        double slope = profile.SlopeRho(rho);
        return (profile.Sag(rho), 2 * x * slope, 2 * y * slope);
    }
}

/// <summary>
/// OpticStudio's Fresnel surface (OpticStudio 2025 R2 help, "Fresnel"): a flat surface "etched
/// to have a spherical (or optionally aspheric) profile on a small scale". "The surface
/// intercept is determined by computing the intersection of the incoming ray with a plane",
/// then "the surface is then treated as generally curved for the purposes of refraction". The
/// radius and conic are the surface's (<see cref="SequentialSurface.Curvature"/>,
/// <see cref="SequentialSurface.Conic"/>), and the parameters α₁…α₈ are the even asphere's
/// (r² to r¹⁶). The paraxial engine sees the profile's vertex curvature c + 2α₁.
/// </summary>
public sealed class FresnelShape : FresnelShapeBase
{
    public FresnelShape(IReadOnlyList<double>? alpha = null)
    {
        EvenProfile.CheckCoefficients(alpha, "Fresnel surface");
        Alpha = alpha?.ToArray() ?? Array.Empty<double>();
    }

    public override string TypeName => "Fresnel";

    /// <summary>α₁…α₈ on r², r⁴, …, r¹⁶, meters^(1−2i).</summary>
    public IReadOnlyList<double> Alpha { get; }

    private EvenProfile Profile(SequentialSurface s) => new(s.Curvature, s.Conic, Alpha);

    private protected override bool PlaneSubstrate(SequentialSurface surface) => true;

    private protected override (double Z, double Zx, double Zy) Substrate(double x, double y, SequentialSurface surface) => (0, 0, 0);

    private protected override (double Fx, double Fy) Facet(double x, double y, SequentialSurface surface)
    {
        var (_, zx, zy) = Radial(Profile(surface), x, y);
        return (zx, zy);
    }

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { Curvature = Profile(surface).VertexCurvature };
}

/// <summary>
/// OpticStudio's Extended Fresnel surface (OpticStudio 2025 R2 help, "Extended Fresnel"): an
/// even-asphere substrate (the surface's curvature and conic, parameters α₁…α₈) used for the
/// ray-surface intercept, and a Fresnel pattern with its own curvature (not radius), conic and
/// coefficients on r² … r¹⁶ (extra data 14, 15, 16–23). "The refraction at the surface accounts
/// for both the substrate sag and the Fresnel sag, while the ray-surface intercept depends only
/// upon the substrate sag." The help gives no formula for "accounts for both"; this surface
/// refracts with the normal of z_s + z_f, the facet slope added to the substrate slope, which
/// is a plane Fresnel pattern's slope carried onto the curved substrate to first order
/// (docs/Optics-Conventions.md §18 lists it as this program's reading). The extra data's
/// "maximum term number" only shortens OpticStudio's sum and is not kept.
/// </summary>
public sealed class ExtendedFresnelShape : FresnelShapeBase
{
    public ExtendedFresnelShape(IReadOnlyList<double>? substrateAlpha, double fresnelCurvature, double fresnelConic,
        IReadOnlyList<double>? fresnelAlpha)
    {
        EvenProfile.CheckCoefficients(substrateAlpha, "Extended Fresnel surface (substrate)");
        EvenProfile.CheckCoefficients(fresnelAlpha, "Extended Fresnel surface (Fresnel pattern)");
        SubstrateAlpha = substrateAlpha?.ToArray() ?? Array.Empty<double>();
        FresnelCurvature = ShapeChecks.Finite(fresnelCurvature, "Extended Fresnel surface: the Fresnel curvature");
        FresnelConic = ShapeChecks.Finite(fresnelConic, "Extended Fresnel surface: the Fresnel conic");
        FresnelAlpha = fresnelAlpha?.ToArray() ?? Array.Empty<double>();
    }

    public override string TypeName => "Extended Fresnel";

    public IReadOnlyList<double> SubstrateAlpha { get; }
    /// <summary>The Fresnel pattern's curvature, 1/m (OpticStudio stores curvature, not radius, here).</summary>
    public double FresnelCurvature { get; }
    public double FresnelConic { get; }
    public IReadOnlyList<double> FresnelAlpha { get; }

    private EvenProfile SubstrateProfile(SequentialSurface s) => new(s.Curvature, s.Conic, SubstrateAlpha);
    private EvenProfile FresnelProfile => new(FresnelCurvature, FresnelConic, FresnelAlpha);

    private protected override bool PlaneSubstrate(SequentialSurface surface) =>
        surface.Curvature == 0 && !SubstrateProfile(surface).HasPolynomial;

    private protected override (double Z, double Zx, double Zy) Substrate(double x, double y, SequentialSurface surface) =>
        Radial(SubstrateProfile(surface), x, y);

    private protected override (double Fx, double Fy) Facet(double x, double y, SequentialSurface surface)
    {
        var (_, sx, sy) = Radial(SubstrateProfile(surface), x, y);
        var (_, fx, fy) = Radial(FresnelProfile, x, y);
        return (sx + fx, sy + fy);
    }

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { Curvature = SubstrateProfile(surface).VertexCurvature + FresnelProfile.VertexCurvature };
}

/// <summary>
/// OpticStudio's Generalized Fresnel surface (OpticStudio 2025 R2 help, "Generalized Fresnel"):
/// an even-asphere substrate (the surface's curvature and conic, α₁…α₈) for the intercept; then
/// "the ray reflects or refracts as if the surface had a shape described by" Z = Σ Aᵢ·Eᵢ(x, y),
/// the extended polynomial terms (<see cref="ExtendedPolynomialTerms"/>) in x and y divided by
/// the normalization radius, coefficients in lens units. "It is the gradient of the extended
/// polynomial terms alone that determines the reflective or refractive properties of the
/// surface, not the surface substrate shape." The paraxial engine sees the y-z curvature of Z on
/// the axis, 2·A₅/R² (term 5 is y²).
/// </summary>
public sealed class GeneralizedFresnelShape : FresnelShapeBase
{
    public GeneralizedFresnelShape(IReadOnlyList<double>? substrateAlpha, double normalizationRadius,
        IReadOnlyList<double> terms)
    {
        EvenProfile.CheckCoefficients(substrateAlpha, "Generalized Fresnel surface");
        ArgumentNullException.ThrowIfNull(terms);
        if (!(normalizationRadius > 0) || !double.IsFinite(normalizationRadius))
            throw new ArgumentException($"Generalized Fresnel surface: the normalization radius must be positive (it is {normalizationRadius}).");
        if (terms.Count > ExtendedPolynomialTerms.MaxTerms)
            throw new ArgumentException($"Generalized Fresnel surface: at most {ExtendedPolynomialTerms.MaxTerms} polynomial terms are defined; {terms.Count} were given.");
        for (int i = 0; i < terms.Count; i++)
            if (!double.IsFinite(terms[i]))
                throw new ArgumentException($"Generalized Fresnel surface: term {i + 1} must be a number (it is {terms[i]}).");
        SubstrateAlpha = substrateAlpha?.ToArray() ?? Array.Empty<double>();
        NormalizationRadius = normalizationRadius;
        Terms = terms.ToArray();
    }

    public override string TypeName => "Generalized Fresnel";

    public IReadOnlyList<double> SubstrateAlpha { get; }
    /// <summary>Meters.</summary>
    public double NormalizationRadius { get; }
    /// <summary>A₁, A₂, … in meters, in the extended polynomial order.</summary>
    public IReadOnlyList<double> Terms { get; }

    private EvenProfile SubstrateProfile(SequentialSurface s) => new(s.Curvature, s.Conic, SubstrateAlpha);

    private protected override bool PlaneSubstrate(SequentialSurface surface) =>
        surface.Curvature == 0 && !SubstrateProfile(surface).HasPolynomial;

    private protected override (double Z, double Zx, double Zy) Substrate(double x, double y, SequentialSurface surface) =>
        Radial(SubstrateProfile(surface), x, y);

    private protected override (double Fx, double Fy) Facet(double x, double y, SequentialSurface surface)
    {
        double r = NormalizationRadius;
        var (_, du, dv) = ExtendedPolynomialTerms.Evaluate(Terms, x / r, y / r);
        return (du / r, dv / r);
    }

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { Curvature = Terms.Count >= 5 ? 2 * Terms[4] / (NormalizationRadius * NormalizationRadius) : 0 };
}

/// <summary>
/// OpticStudio's Cylinder Fresnel surface (OpticStudio 2025 R2 help, "Cylinder Fresnel"): a
/// cylindrical substrate z_s = c_s·y²/(1 + √(1 − (1 + k_s)c_s²y²)) + α₁y² + … + α₈y¹⁶ (the
/// surface's curvature and conic, parameters 1–8) for the intercept, and an "identical
/// expression, with independent coefficients" z_f in y (extra data 14: y curvature, not radius;
/// 15: conic; 16–23: y² … y¹⁶) for the facets. "The refraction at the surface accounts for only
/// the Fresnel sag, while the ray-surface intercept depends only upon the substrate sag."
/// Neither depends on x. The paraxial engine sees the facet's y curvature c_f + 2β₁.
/// </summary>
public sealed class CylinderFresnelShape : FresnelShapeBase
{
    public CylinderFresnelShape(IReadOnlyList<double>? substrateAlpha, double fresnelCurvature, double fresnelConic,
        IReadOnlyList<double>? fresnelAlpha)
    {
        EvenProfile.CheckCoefficients(substrateAlpha, "Cylinder Fresnel surface (substrate)");
        EvenProfile.CheckCoefficients(fresnelAlpha, "Cylinder Fresnel surface (Fresnel pattern)");
        SubstrateAlpha = substrateAlpha?.ToArray() ?? Array.Empty<double>();
        FresnelCurvature = ShapeChecks.Finite(fresnelCurvature, "Cylinder Fresnel surface: the Fresnel curvature");
        FresnelConic = ShapeChecks.Finite(fresnelConic, "Cylinder Fresnel surface: the Fresnel conic");
        FresnelAlpha = fresnelAlpha?.ToArray() ?? Array.Empty<double>();
    }

    public override string TypeName => "Cylinder Fresnel";

    public IReadOnlyList<double> SubstrateAlpha { get; }
    public double FresnelCurvature { get; }
    public double FresnelConic { get; }
    public IReadOnlyList<double> FresnelAlpha { get; }

    private EvenProfile SubstrateProfile(SequentialSurface s) => new(s.Curvature, s.Conic, SubstrateAlpha);
    private EvenProfile FresnelProfile => new(FresnelCurvature, FresnelConic, FresnelAlpha);

    private protected override bool RadialSubstrate => false;

    private protected override bool PlaneSubstrate(SequentialSurface surface) =>
        surface.Curvature == 0 && !SubstrateProfile(surface).HasPolynomial;

    private protected override (double Z, double Zx, double Zy) Substrate(double x, double y, SequentialSurface surface)
    {
        var profile = SubstrateProfile(surface);
        double rho = y * y;
        return (profile.Sag(rho), 0, 2 * y * profile.SlopeRho(rho));
    }

    private protected override (double Fx, double Fy) Facet(double x, double y, SequentialSurface surface) =>
        (0, 2 * y * FresnelProfile.SlopeRho(y * y));

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { Curvature = FresnelProfile.VertexCurvature };
}
