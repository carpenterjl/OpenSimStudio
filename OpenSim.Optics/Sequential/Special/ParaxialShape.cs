using OpenSim.Core.Numerics;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Sequential.Special;

/// <summary>
/// OpticStudio's Paraxial surface (OpticStudio 2025 R2 help, "Paraxial (sequential surfaces,
/// lens data editor)"): an ideal thin lens on a plane. "The paraxial surface refracts rays
/// using the following equations": n′u′x = n·ux − x·φ and n′u′y = n·uy − y·φ, φ the surface
/// power, primes the image side, and the angles slopes from the direction cosines, ux = l/n,
/// uy = m/n (there n is the z direction cosine). The focal length is "that which would be
/// measured in air", so φ = 1/f, and a zero focal length means no power. The surface's own
/// curvature and conic are not used.
/// <para>
/// <b>Direction.</b> With u = L/N and signed indices (−1 per mirror before the space,
/// docs/Optics-Conventions.md §11), u′ = (n_s·u − x·φ)/n′_s, and the direction leaving is
/// s′·(u′x, u′y, 1)/|…| with s′ the sense of travel after the surface. The rule is linear in
/// the slopes, so it images every point of every plane perfectly onto a point (derived in
/// <c>IdealSurfaceTests</c>): rays from (x₀, y₀) at axial distance ℓ meet again at ℓ′ with
/// n′/ℓ′ − n/ℓ = φ, whatever their angle.
/// </para>
/// <para>
/// <b>Optical path</b> (the help's OPD modes; docs/Optics-Conventions.md §18). Because the
/// surface images perfectly, the path it adds can make every ray from an object point arrive at
/// its image with one optical path. With P the object point on the incoming ray and P′ its image
/// on the outgoing ray, the path added at the hit X is
/// E = n·[ℓ(P→O) − ℓ(P→X)] + n′·[ℓ(O→P′) − ℓ(X→P′)], ℓ signed lengths along the direction of
/// travel and O the vertex, so the ray through the vertex gets none. P is where the incoming
/// ray crosses the object plane the surface is told to work at, <see cref="ObjectDistance"/>:
/// <list type="bullet">
/// <item>Infinite (the default): OpticStudio's mode 2, "assumes that the lens is used at
/// infinite conjugates regardless of the actual conjugates". The object term becomes −n·(X·d)
/// and P′ lies on the back focal plane.</item>
/// <item>Finite: OpticStudio's mode 3, "similar to Mode = 0, except paraxial rather than real
/// rays are used": <see cref="ResolveConjugates"/> sets it from the paraxial marginal ray.</item>
/// </list>
/// Then the OPD is exactly zero for every field point at those conjugates, at any aperture.
/// Modes 0 (a real parabasal ray) and 1 ("integrates the actual phase introduced by the surface
/// for each ray traced", with no formula given) are not built.
/// </para>
/// </summary>
public sealed class ParaxialShape : SurfaceShape
{
    /// <param name="focalLength">The focal length in air, meters; 0 for no power.</param>
    /// <param name="objectDistance">The object distance the optical-path term assumes, meters,
    /// signed along the direction of travel (negative for a real object before the surface);
    /// ±infinity for collimated light (OpticStudio's OPD mode 2).</param>
    public ParaxialShape(double focalLength, double objectDistance = double.NegativeInfinity)
    {
        FocalLength = ShapeChecks.Finite(focalLength, "Paraxial surface: the focal length");
        if (double.IsNaN(objectDistance) || objectDistance == 0)
            throw new ArgumentException($"Paraxial surface: the object distance must be nonzero or infinite (it is {objectDistance}); an object on the surface has no rays to image.");
        ObjectDistance = objectDistance;
    }

    public override string TypeName => "Paraxial";

    /// <summary>The focal length in air, meters (0: no power).</summary>
    public double FocalLength { get; }

    /// <summary>φ = 1/f, 1/m; zero when the focal length is zero.</summary>
    public double Power => FocalLength == 0 ? 0 : 1 / FocalLength;

    /// <summary>The object distance the optical-path term assumes (see the class remarks).</summary>
    public double ObjectDistance { get; }

    /// <summary>The same surface working at another object distance.</summary>
    public ParaxialShape WithObjectDistance(double objectDistance) => new(FocalLength, objectDistance);

    public override double Sag(double x, double y, SequentialSurface surface) => 0;

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance)
    {
        point = new Vector3D(x, y, 0);
        distance = 0;
        return true;
    }

    public override Vector3D Normal(Vector3D point, SequentialSurface surface) => Vector3D.UnitZ;

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface) =>
        new() { ThinLensPower = Power };

    public override bool TryRedirect(in SurfaceRedirectInput input, out Vector3D direction,
        out double extraOpticalPath, out RayStatus failure)
    {
        var d = input.Incoming;
        var x = input.Point;
        double phi = Power;
        double sb = ShapeChecks.Travel(d), sa = input.Surface.IsMirror ? -sb : sb;
        double nb = sb * input.IndexBefore, na = sa * input.IndexAfter;
        double ux = d.X / d.Z, uy = d.Y / d.Z;
        double vx = (nb * ux - x.X * phi) / na, vy = (nb * uy - x.Y * phi) / na;
        var after = new Vector3D(vx, vy, 1) * sa;
        after /= after.Length;
        direction = after;
        failure = RayStatus.Ok;
        extraOpticalPath = PathTerm(x, d, after, input.IndexBefore, input.IndexAfter, nb, na, sb, sa, phi);
        if (!double.IsFinite(extraOpticalPath) || !double.IsFinite(after.X) || !double.IsFinite(after.Y))
        {
            failure = RayStatus.MissedSurface;
            extraOpticalPath = 0;
        }
        return true;
    }

    /// <summary>E of the class remarks. Differences of two lengths from one point are written
    /// |a| − |b| = (|a|² − |b|²)/(|a| + |b|), which keeps the digits near the vertex.</summary>
    private double PathTerm(Vector3D x, Vector3D d, Vector3D after, double n, double nPrime,
        double nb, double na, double sb, double sa, double phi)
    {
        double xx = Vector3D.Dot(x, x);
        if (xx == 0) return 0;

        // Object side. ℓ is measured along the travel, so the object plane is at z = sb·ℓ.
        double objectTerm;
        double ell = ObjectDistance;
        if (double.IsInfinity(ell))
            objectTerm = -n * Vector3D.Dot(x, d);
        else
        {
            double zo = sb * ell;
            var p = x + d * (zo / d.Z);                     // where the incoming ray crosses z = zo
            double sign = Math.Sign(-sb * zo);              // + when O and X lie ahead of P
            double pa = p.Length, pb = (p - x).Length;
            objectTerm = n * sign * (2 * Vector3D.Dot(x, p) - xx) / (pa + pb);
        }

        // Image side, in positions along the local z with signed indices (section 11's
        // formalism, which the slope rule above is): n′_s/z_i − n_s/z_o = φ.
        double inverse = (double.IsInfinity(ell) ? 0 : nb / (sb * ell)) + phi;
        double imageTerm;
        if (inverse == 0)
            imageTerm = nPrime * Vector3D.Dot(x, after);
        else
        {
            double zi = na / inverse;
            var q = x + after * (zi / after.Z);
            double sign = Math.Sign(sa * zi);               // + when P′ lies ahead of O and X
            double qa = q.Length, qb = (q - x).Length;
            imageTerm = nPrime * sign * (2 * Vector3D.Dot(x, q) - xx) / (qa + qb);
        }
        return objectTerm + imageTerm;
    }

    /// <summary>
    /// OpticStudio's OPD mode 3 for every Paraxial surface of <paramref name="lens"/>: each
    /// surface's object distance is set where the paraxial marginal ray of the primary
    /// wavelength arriving at it crosses the axis (−h/u from its vertex, signed along the
    /// travel), infinite when that ray arrives parallel to the axis. Returns the lens with those
    /// surfaces replaced. The surfaces' frames are taken as the paraxial engine's (no tilts or
    /// decenters); throws when a Paraxial surface sits at an image of the object (the marginal
    /// ray crosses the axis on it), where no object distance exists.
    /// </summary>
    public static SequentialLens ResolveConjugates(SequentialLens lens, OpticalSystemData system)
    {
        ArgumentNullException.ThrowIfNull(lens);
        ArgumentNullException.ThrowIfNull(system);
        var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        var marginal = paraxial.MarginalRay;
        var surfaces = lens.Surfaces.ToArray();
        for (int j = 1; j < lens.ImageIndex; j++)
        {
            if (surfaces[j].Shape is not ParaxialShape shape || surfaces[j].IsIgnored) continue;
            double h = marginal.Heights[j], u = marginal.Slopes[j - 1];
            double travel = Math.Sign(paraxial.SignedIndices[j - 1]);
            double ell;
            if (u == 0) ell = double.NegativeInfinity;
            else
            {
                if (h == 0)
                    throw new InvalidOperationException($"Paraxial surface {j}: the paraxial marginal ray crosses the axis on it (it is at an image of the object), so its OPD has no object distance to work at; move it or use infinite conjugates.");
                ell = travel * (-h / u);
            }
            surfaces[j] = surfaces[j] with { Shape = shape.WithObjectDistance(ell) };
        }
        return lens with { Surfaces = surfaces };
    }
}
