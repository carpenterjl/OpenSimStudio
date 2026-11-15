// OP-44
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Analysis;

/// <summary>The volume of one element and how it was found.</summary>
/// <param name="CubicMeters">The volume, m³.</param>
/// <param name="Exact">True when both faces were integrated in closed form.</param>
/// <param name="Method">One phrase naming the method for each face.</param>
public sealed record ElementVolumeResult(double CubicMeters, bool Exact, string Method)
{
    /// <summary>The volume in cubic centimetres ("cc", as OpticStudio's Prescription Data lists it).</summary>
    public double CubicCentimeters => CubicMeters * 1e6;
}

/// <summary>
/// The volume of glass between two faces that share one axis (docs/Optics-Conventions.md §29):
/// the front face's vertex at z = 0, the back face's at z = t, each face with a circular edge
/// of its own clear semi-diameter. The face with the smaller semi-diameter is "squared up" to the
/// larger one, as OpticStudio's help states for Prescription Data ("Comments on Computing Element
/// Volumes", 2025 R2): beyond its own edge it continues as a plane perpendicular to the axis at
/// its edge sag, out to the larger semi-diameter R. So
/// <code>
/// V = π R² t + W(back) − W(front),   W(face) = ∫∫_{r ≤ R} s(min(r, a)) dA
///   = ∫∫_{r ≤ a} s dA + ½ (R² − a²) ∫₀^{2π} s(a, θ) dθ
/// </code>
/// with s the face's sag and a its semi-diameter.
/// <para>
/// <b>Standard faces (sphere, conic, plane): exact.</b> The conic sag obeys r² = 2ρs − (1 + k)s²
/// (ρ = 1/c), so r dr = (ρ − (1 + k)s) ds and ∫₀^a 2πr s dr = π s_a² (ρ − 2(1 + k)s_a/3)
/// (derived here; for a sphere it is the cylinder π a² s_a less the cap π s_a²(ρ − s_a/3)).
/// </para>
/// <para>
/// <b>Other shapes: numerical.</b> A rotationally symmetric sag (<see cref="RadialSagShape"/>) is
/// integrated with a 64-node Gauss–Legendre rule in r on [0, a] (exact for a polynomial sag up to
/// r¹²⁶ times r; the conic part converges spectrally); any other shape with the same rule in r times
/// a 128-point trapezoidal rule in θ (spectrally accurate for a smooth periodic integrand). A sag
/// that is undefined anywhere on the face fails, naming the face.
/// </para>
/// </summary>
public static class ElementVolume
{
    /// <summary>Gauss–Legendre nodes in r for a shape that is not the Standard conic.</summary>
    public const int RadialNodes = 64;

    /// <summary>Trapezoidal points in θ for a shape that is not rotationally symmetric.</summary>
    public const int AzimuthPoints = 128;

    /// <summary>
    /// The volume between <paramref name="front"/> (semi-diameter <paramref name="frontSemi"/>,
    /// vertex at z = 0) and <paramref name="back"/> (semi-diameter <paramref name="backSemi"/>,
    /// vertex at z = <paramref name="thickness"/>), meters. Throws
    /// <see cref="InvalidOperationException"/> naming <paramref name="what"/> when a sag is
    /// undefined inside its semi-diameter, or when the faces cross (the local thickness is not
    /// positive somewhere on the element).
    /// </summary>
    public static ElementVolumeResult Compute(SequentialSurface front, double frontSemi, SequentialSurface back, double backSemi,
        double thickness, string what)
    {
        ArgumentNullException.ThrowIfNull(front);
        ArgumentNullException.ThrowIfNull(back);
        if (!(frontSemi > 0) || !(backSemi > 0) || !double.IsFinite(frontSemi) || !double.IsFinite(backSemi))
            throw Fail($"{what}: both semi-diameters must be positive (they are {frontSemi:R} m and {backSemi:R} m).");
        if (!(thickness > 0) || !double.IsFinite(thickness))
            throw Fail($"{what}: the centre thickness must be positive (it is {thickness:R} m).");
        double edge = Math.Max(frontSemi, backSemi);
        CheckFacesDoNotCross(front, frontSemi, back, backSemi, thickness, edge, what);
        var (wFront, exactFront, methodFront) = FaceIntegral(front, frontSemi, edge, what + ", front face");
        var (wBack, exactBack, methodBack) = FaceIntegral(back, backSemi, edge, what + ", back face");
        double volume = Math.PI * edge * edge * thickness + wBack - wFront;
        string method = $"front {methodFront}; back {methodBack}";
        return new ElementVolumeResult(volume, exactFront && exactBack, method);
    }

    private static InvalidOperationException Fail(FormattableString message) =>
        new(FormattableString.Invariant(message));

    /// <summary>The sag of a face at (x, y), meters, or NaN where undefined.</summary>
    public static double Sag(SequentialSurface face, double x, double y)
    {
        face = OpenSim.Optics.Sequential.Composite.CompositeSurfaces.WithEffectiveShape(face);   // OP-32: composite add-ons
        if (face.Shape is { } shape) return shape.Sag(x, y, face);
        return ConicSagClosed(Math.Sqrt(x * x + y * y), face.Curvature, face.Conic);
    }

    /// <summary>The Standard conic's sag, NaN outside its domain; at (1 + k)c²r² = 1 exactly
    /// (a hemisphere's rim) it is 1/((1 + k)c), the limit of the formula.</summary>
    private static double ConicSagClosed(double r, double c, double k)
    {
        if (c == 0) return 0;
        double arg = 1 - (1 + k) * c * c * r * r;
        if (arg < 0) return double.NaN;
        return c * r * r / (1 + Math.Sqrt(arg));
    }

    private static (double W, bool Exact, string Method) FaceIntegral(SequentialSurface face, double a, double edge, string what)
    {
        double ring = 0.5 * (edge * edge - a * a);
        face = OpenSim.Optics.Sequential.Composite.CompositeSurfaces.WithEffectiveShape(face);   // OP-32: composite add-ons
        if (face.Shape is null)
        {
            double c = face.Curvature, k = face.Conic;
            if (c == 0) return (0, true, "plane (exact)");
            double sa = ConicSagClosed(a, c, k);
            if (double.IsNaN(sa))
                throw Fail($"{what}: the conic (radius {1 / c:R} m, conic {k:R}) is not defined out to the semi-diameter {a:R} m.");
            double rho = 1 / c;
            double disc = Math.PI * sa * sa * (rho - 2 * (1 + k) * sa / 3);
            return (disc + 2 * Math.PI * ring * sa, true, k == 0 ? "sphere (exact)" : "conic (exact)");
        }
        var (nodes, weights) = GaussLegendre.Rule(RadialNodes, 0, a);
        if (face.Shape is RadialSagShape radial)
        {
            double sum = 0;
            for (int i = 0; i < nodes.Length; i++)
                sum += weights[i] * 2 * Math.PI * nodes[i] * RadialSag(radial, face, nodes[i], what);
            double sa = RadialSag(radial, face, a, what);
            return (sum + 2 * Math.PI * ring * sa, false,
                $"{face.Shape.TypeName} ({RadialNodes}-node Gauss–Legendre in r)");
        }
        double total = 0, rim = 0, dTheta = 2 * Math.PI / AzimuthPoints;
        for (int m = 0; m < AzimuthPoints; m++)
        {
            var (sin, cos) = Math.SinCos(m * dTheta);
            double ringSum = 0;
            for (int i = 0; i < nodes.Length; i++)
                ringSum += weights[i] * nodes[i] * SagOrThrow(face, nodes[i] * cos, nodes[i] * sin, what);
            total += ringSum * dTheta;
            rim += SagOrThrow(face, a * cos, a * sin, what) * dTheta;
        }
        return (total + ring * rim, false,
            $"{face.Shape.TypeName} ({RadialNodes}-node Gauss–Legendre in r × {AzimuthPoints}-point trapezoid in θ)");
    }

    private static double RadialSag(RadialSagShape shape, SequentialSurface face, double r, string what)
    {
        if (!shape.TryRadialSag(r, face, out double z, out _) || !double.IsFinite(z))
            throw Fail($"{what}: the {shape.TypeName} sag is not defined at r = {r:R} m.");
        return z;
    }

    private static double SagOrThrow(SequentialSurface face, double x, double y, string what)
    {
        double z = Sag(face, x, y);
        if (!double.IsFinite(z))
            throw Fail($"{what}: the sag is not defined at (x, y) = ({x:R}, {y:R}) m.");
        return z;
    }

    /// <summary>The local thickness t + s₂ − s₁ (each face clamped at its own edge) sampled on
    /// 65 radii along x and y must stay positive.</summary>
    private static void CheckFacesDoNotCross(SequentialSurface front, double a1, SequentialSurface back, double a2,
        double t, double edge, string what)
    {
        const int samples = 64;
        foreach (var (ux, uy) in new[] { (1.0, 0.0), (0.0, 1.0), (-1.0, 0.0), (0.0, -1.0) })
            for (int i = 0; i <= samples; i++)
            {
                double r = edge * i / samples;
                double r1 = Math.Min(r, a1), r2 = Math.Min(r, a2);
                double s1 = SagOrThrow(front, r1 * ux, r1 * uy, what + ", front face");
                double s2 = SagOrThrow(back, r2 * ux, r2 * uy, what + ", back face");
                double local = t + s2 - s1;
                if (!(local > 0))
                    throw Fail(
                        $"{what}: the faces cross (local thickness {local:R} m at r = {r:R} m), so the element has no volume as entered; reduce the semi-diameters or thicken the element.");
            }
    }
}
