using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential.Anamorphic;

namespace OpenSim.Optics.Sequential.Diffractive;

// OP-26: ruled gratings (docs/Optics-Conventions.md §57). Definitions: OpticStudio 2025 R2 help,
// pages "Diffraction Grating (sequential surfaces, lens data editor)", "Toroidal Grating" and
// "Variable Line Space Grating", saved under plans/sources/optics/op-26-help/.

/// <summary>
/// OpticStudio's Diffraction Grating: "straight-line ruled gratings. The grating lines are
/// parallel to the local x-axis", on a "plane, spherical, or conical" Standard substrate, with
/// n₂·sin θ₂ − n₁·sin θ₁ = M·λ·T, T "the grating frequency in lines per micrometer", "the
/// y-spacing of the grating lines" (parameter 1) and M the order (parameter 2). Read as the phase
/// Φ = 2π·M·T·y (T per meter, y the intercept's local y), whose y slope 2π·M·T makes the plane
/// grating's rule exactly that equation with sin θ the y direction cosine; on a curved substrate
/// the lines are the planes y = const and the slope is projected on the tangent plane
/// (<see cref="DiffractiveShape"/>). The sign of M follows the equation ("the sign convention for
/// M is completely arbitrary").
/// </summary>
public sealed class DiffractionGrating : DiffractiveShape
{
    /// <param name="linesPerMicrometer">T, lines per micrometer (whatever the lens units).</param>
    /// <param name="order">M.</param>
    public DiffractionGrating(double linesPerMicrometer, double order) : base(null)
    {
        LinesPerMicrometer = Finite(linesPerMicrometer, "Diffraction Grating", "the grating frequency");
        Order = Finite(order, "Diffraction Grating", "the diffraction order");
    }

    public override string TypeName => "Diffraction Grating";

    /// <summary>T, lines per micrometer.</summary>
    public double LinesPerMicrometer { get; }

    /// <summary>M.</summary>
    public double Order { get; }

    /// <summary>∂Φ/∂y = 2π·M·T, T in lines per meter.</summary>
    private double Slope => 2 * Math.PI * Order * (LinesPerMicrometer * 1e6);

    public override bool TryPhase(Vector3D point, SequentialSurface surface, out double phase, out Vector3D gradient)
    {
        double k = Slope;
        phase = k * point.Y;
        gradient = new Vector3D(0, k, 0);
        return true;
    }

    internal override VertexTaylor PhaseAtVertex(SequentialSurface surface, double yCurvature) => new() { Gy = Slope };
}

/// <summary>
/// OpticStudio's Toroidal Grating: the Toroidal surface's shape with "no aspheric sag terms"
/// (the y–z conic of the surface's radius and conic rotated about an axis at the radius of
/// rotation, parameter 1, 0 for infinite; <see cref="ToroidalSurface"/>), carrying a grating of
/// "lines per micrometer and the diffraction order … in parameter columns 2 and 3"; "The grating
/// lines are parallel to the local x axis, and are evenly spaced when projected onto a plane",
/// so the phase is <see cref="DiffractionGrating"/>'s 2π·M·T·y.
/// </summary>
public sealed class ToroidalGrating : DiffractiveShape
{
    private readonly DiffractionGrating _grating;

    /// <param name="radiusOfRotation">R, meters; 0 is infinite.</param>
    /// <param name="linesPerMicrometer">T, lines per micrometer.</param>
    /// <param name="order">M.</param>
    public ToroidalGrating(double radiusOfRotation, double linesPerMicrometer, double order)
        : base(new ToroidalSurface(radiusOfRotation, Array.Empty<double>()))
    {
        _grating = new DiffractionGrating(linesPerMicrometer, order);
    }

    public override string TypeName => "Toroidal Grating";

    /// <summary>R, meters; 0 is infinite.</summary>
    public double RadiusOfRotation => ((ToroidalSurface)Substrate!).RadiusOfRotation;

    public double LinesPerMicrometer => _grating.LinesPerMicrometer;
    public double Order => _grating.Order;

    public override bool TryPhase(Vector3D point, SequentialSurface surface, out double phase, out Vector3D gradient) =>
        _grating.TryPhase(point, surface, out phase, out gradient);

    internal override VertexTaylor PhaseAtVertex(SequentialSurface surface, double yCurvature) =>
        _grating.PhaseAtVertex(surface, yCurvature);
}

/// <summary>
/// OpticStudio's Variable Line Space Grating (Hettrick and Bowyer, Appl. Opt. 22, 3921 (1983), as
/// the help cites it; the paper itself is not on this machine): straight lines "parallel to the
/// local x axis", "Only plane gratings are supported", with the frequency in lines per micrometer
/// <code>
/// T(y) = (1/λ₀)·[ (L·cos β₀ − y)/√(y² + L² − 2L·y·cos β₀) − (L·cos α₀ − y)/√(y² + L² − 2L·y·cos α₀) ]
/// </code>
/// (parameters 1 M, 2 L, 3 cos α₀, 4 cos β₀, 5 λ₀ in micrometers). The phase is Φ = 2π·M·∫T dy,
/// in closed form (derived: d/dy √(y² + L² − 2L·y·c) = (y − L·c)/√(…), so ∫(L·c − y)/√(…) dy =
/// −√(…)): Φ(y) = (2π·M/λ₀)·(S_α(y) − S_β(y)), S_c(y) = √(y² + L² − 2L·y·c), zero at y = 0. y, L
/// and λ₀ are in meters here, so T is per meter. The page says the surface "has only been tested
/// when the grating is used in reflection"; this program traces it in transmission too, by the
/// same rule.
/// </summary>
public sealed class VariableLineSpaceGrating : DiffractiveShape
{
    private const string Form = "Variable Line Space Grating";

    /// <param name="order">M, the diffraction order upon playback.</param>
    /// <param name="radius">L, the (positive) radius of the focal surface, meters.</param>
    /// <param name="cosAlpha0">cos α₀.</param>
    /// <param name="cosBeta0">cos β₀.</param>
    /// <param name="constructionMicrometers">λ₀, micrometers.</param>
    public VariableLineSpaceGrating(double order, double radius, double cosAlpha0, double cosBeta0, double constructionMicrometers)
        : base(null)
    {
        Order = Finite(order, Form, "the order M");
        if (!(radius > 0) || !double.IsFinite(radius))
            throw new ArgumentException($"{Form}: L (parameter 2) is {radius}; it must be a positive length, the radius of the focal surface.");
        if (!(Math.Abs(cosAlpha0) <= 1) || !(Math.Abs(cosBeta0) <= 1))
            throw new ArgumentException($"{Form}: cos α₀ = {cosAlpha0} and cos β₀ = {cosBeta0} (parameters 3 and 4) must both lie from −1 to 1.");
        if (!(constructionMicrometers > 0) || !double.IsFinite(constructionMicrometers))
            throw new ArgumentException($"{Form}: λ₀ (parameter 5) is {constructionMicrometers} µm; it must be a positive wavelength.");
        Radius = radius;
        CosAlpha0 = cosAlpha0;
        CosBeta0 = cosBeta0;
        ConstructionMicrometers = constructionMicrometers;
    }

    public override string TypeName => Form;

    public double Order { get; }

    /// <summary>L, meters.</summary>
    public double Radius { get; }

    public double CosAlpha0 { get; }
    public double CosBeta0 { get; }

    /// <summary>λ₀, micrometers.</summary>
    public double ConstructionMicrometers { get; }

    private double K => 2 * Math.PI * Order / OpticsConventions.MicrometersToMeters(ConstructionMicrometers);

    public override bool TryPhase(Vector3D point, SequentialSurface surface, out double phase, out Vector3D gradient)
    {
        if (surface.Curvature != 0 || surface.Conic != 0)
            throw new InvalidOperationException($"{Form}: only plane gratings are supported (help, \"Variable Line Space Grating\"); this surface has curvature {surface.Curvature:R} 1/m and conic {surface.Conic:R}.");
        double y = point.Y, l = Radius;
        double sa = Math.Sqrt(y * y + l * l - 2 * l * y * CosAlpha0);
        double sb = Math.Sqrt(y * y + l * l - 2 * l * y * CosBeta0);
        if (!(sa > 0) || !(sb > 0))
        {
            phase = double.NaN;
            gradient = default;
            return false;
        }
        double k = K;
        phase = k * (sa - sb);
        gradient = new Vector3D(0, k * ((l * CosBeta0 - y) / sb - (l * CosAlpha0 - y) / sa), 0);
        return true;
    }

    /// <summary>Φ′(0) = (2πM/λ₀)(cos β₀ − cos α₀) and Φ″(0) = (2πM/λ₀)(sin²α₀ − sin²β₀)/L (derived:
    /// d/dy of (L·c − y)/S_c at y = 0 is (c² − 1)/L).</summary>
    internal override VertexTaylor PhaseAtVertex(SequentialSurface surface, double yCurvature)
    {
        double k = K;
        return new VertexTaylor
        {
            Gy = k * (CosBeta0 - CosAlpha0),
            Hyy = k * ((1 - CosAlpha0 * CosAlpha0) - (1 - CosBeta0 * CosBeta0)) / Radius,
        };
    }
}
