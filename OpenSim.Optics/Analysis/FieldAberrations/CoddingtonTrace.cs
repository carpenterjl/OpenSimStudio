using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Analysis;

// OP-37: Coddington traces along the real chief ray.

/// <summary>
/// The tangential and sagittal foci of the thin pencil around one real chief ray
/// (<see cref="CoddingtonTrace"/>). Distances are meters along the chief ray from the point
/// where it meets the image surface, positive in the direction the light travels; infinite
/// when the pencil leaves collimated in that section.
/// </summary>
public sealed record CoddingtonFoci
{
    /// <summary>From the chief ray's image-surface point to the tangential focus, along the ray.</summary>
    public required double Tangential { get; init; }

    /// <summary>From the chief ray's image-surface point to the sagittal focus, along the ray.</summary>
    public required double Sagittal { get; init; }

    /// <summary>The chief ray at the image surface, in the image surface's local frame.</summary>
    public required Vector3D ImagePoint { get; init; }
    public required Vector3D ImageDirection { get; init; }

    /// <summary>The tangential focus measured along the image surface's local z from the
    /// chief ray's image point, positive in the direction the light travels: what OpticStudio's
    /// field curvature plot shows ("the distances measured along the Z-axis from the image
    /// surface to the paraxial image surface", help page "Field Curvature and Distortion").</summary>
    public double TangentialShift => Tangential * Math.Abs(ImageDirection.Z);

    /// <summary>As <see cref="TangentialShift"/> for the sagittal focus.</summary>
    public double SagittalShift => Sagittal * Math.Abs(ImageDirection.Z);
}

/// <summary>
/// Coddington's equations traced along a real chief ray (docs/Optics-Conventions.md §27).
/// <para>
/// <b>Source.</b> The equations are those of RayOptics 0.9.10 (M. Hayford, BSD 3-Clause),
/// <c>rayoptics/raytr/trace.py</c>, <c>trace_coddington_fan</c>, read under
/// <c>plans/sources/optics/pyref-venv</c>: with n, n′ the indices before and after a surface,
/// I, I′ the angles of incidence and refraction, and the "oblique power"
/// φ_obl = φ·(n′cos I′ − n cos I)/(n′ − n) = c·(n′cos I′ − n cos I) for a sphere of curvature c,
/// <code>
///   n′/s′ = n/s + φ_obl                      (sagittal)
///   n′cos²I′/t′ = n cos²I/t + φ_obl          (tangential)
/// </code>
/// and the transfer s ← s′ − d, t ← t′ − d over the chief ray's segment d to the next surface.
/// RayOptics notes "spherical surfaces only".
/// </para>
/// <para>
/// <b>Extension to aspheres and conics (derived here, not from a source).</b> The derivation of
/// those equations uses only the surface's curvature in the plane of incidence (tangential) and
/// at right angles to it (sagittal) at the point the chief ray meets it; for a sphere both are c.
/// For a general surface they are the normal curvatures κ_t and κ_s of the surface in those two
/// tangent directions, provided they are principal directions (the second fundamental form has
/// no cross term between them), so φ_obl,t = κ_t(n′cos I′ − n cos I) and
/// φ_obl,s = κ_s(n′cos I′ − n cos I). For the sag z = f(x, y) in the surface's local frame,
/// with W = √(1 + f_x² + f_y²) and unit normal (−f_x, −f_y, 1)/W, the second fundamental form
/// on tangent vectors a, b is II(a, b) = (f_xx a_x b_x + f_xy(a_x b_y + a_y b_x) + f_yy a_y b_y)/W
/// (Monge patch r(u, v) = (u, v, f), r_uu·n = f_xx/W), positive when the surface bends toward the
/// normal, which is +z at the vertex, so a sphere of curvature c gives c, the sign of
/// <see cref="SequentialSurface.Curvature"/>. The finite-difference foci of real rays check this
/// in <c>FieldAberrationTests</c>.
/// </para>
/// <para>
/// <b>Mirrors and signs.</b> Distances are along the direction of travel and indices are
/// positive (the program's convention, §5). The cosines are signed, cos I = d·n̂ and
/// cos I′ = d′·n̂ with the same normal n̂ (+z at the vertex), so at a mirror n′ = n and
/// cos I′ = −cos I give φ = −2n·κ·cos I: a concave mirror (κ &lt; 0 facing light travelling +z)
/// focuses a collimated tangential pencil at t′ = −cos I/(2κ) = (R/2)·cos I and a sagittal one
/// at s′ = R/(2 cos I), with R = −1/κ, the two foci of an obliquely used mirror.
/// </para>
/// <para>
/// <b>Where it applies.</b> The tangential plane must be one plane of symmetry through the whole
/// system: at every surface the given sagittal direction must be at right angles to the chief
/// ray before and after the surface and to the normal, and the surface's curvature must have no
/// cross term between the two directions. Otherwise the pencil is skew and needs a general
/// astigmatic trace, which is not built; the trace then refuses, naming the surface. Surfaces that
/// turn rays by their own rule (ideal lenses, ABCD blocks, Fresnel facets, lenslets,
/// retroreflectors) have no curvature to use and are refused too.
/// </para>
/// </summary>
public static class CoddingtonTrace
{
    /// <summary>Tolerance on the sagittal direction's components along the ray and the normal.</summary>
    private const double PlaneTolerance = 1e-9;

    /// <summary>
    /// The foci of the pencil about <paramref name="chief"/>, a real ray traced by
    /// <paramref name="trace"/> that reached the image surface. <paramref name="sagittal"/> is the
    /// sagittal direction in the launch (global) frame. <paramref name="where"/> names the ray in
    /// error messages ("field Hy = 0.7, 0.5876 µm").
    /// </summary>
    public static CoddingtonFoci Trace(SequentialTrace trace, RayPath chief, Vector3D sagittal, string where)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(chief);
        var lens = trace.Lens;
        int image = lens.ImageIndex;
        if (!chief.Final.IsOk)
            throw new InvalidOperationException($"Coddington trace, {where}: the chief ray does not reach the image surface ({RayStatuses.Label(chief.Final.Status)} at surface {chief.Final.FailedAtSurface}).");
        var hits = chief.Hits;
        if (hits.Count != image + 1)
            throw new InvalidOperationException($"Coddington trace, {where}: the chief ray's path has {hits.Count} surfaces, not {image + 1}.");

        // Distances along the ray from the current point to the foci: the object point itself
        // (0) for a finite object, collimated (+∞) for an object at infinity.
        double s = lens.ObjectAtInfinity ? double.PositiveInfinity : 0;
        double t = s;
        for (int j = 1; j < image; j++)
        {
            var hit = hits[j];
            s -= hit.SegmentLength;
            t -= hit.SegmentLength;
            var surface = lens.Surfaces[j];
            if (surface.IsIgnored || surface.IsCoordinateBreak) continue;
            if (surface.Shape is { } shape && TurnsRaysItself(shape))
                throw new InvalidOperationException($"Coddington trace, {where}: surface {j} is a {shape.TypeName} surface, which turns rays by its own rule rather than by refraction at a curved surface; field curvature by Coddington's equations is not built for it.");

            var normal = hit.Normal;
            var incoming = hit.IncomingDirection;
            var outgoing = hit.Direction;
            var ts = trace.GlobalPlacement(j).InverseApplyDirection(sagittal);
            if (Math.Abs(Vector3D.Dot(ts, incoming)) > PlaneTolerance || Math.Abs(Vector3D.Dot(ts, outgoing)) > PlaneTolerance
                || Math.Abs(Vector3D.Dot(ts, normal)) > PlaneTolerance)
                throw new InvalidOperationException($"Coddington trace, {where}: at surface {j} the chief ray or the surface normal leaves the meridional plane of the scan (the system is not symmetric about that plane there, e.g. a tilt or decenter out of it). The pencil is skew; a general astigmatic trace is not built.");
            ts -= normal * Vector3D.Dot(ts, normal);
            ts /= ts.Length;
            var tt = Vector3D.Cross(normal, ts);
            tt /= tt.Length;

            var (ktt, kss, kts) = SurfaceCurvature.SecondForm(surface, j, hit.Position, tt, ts, trace.SemiDiameters[j]);
            double scale = Math.Max(Math.Abs(ktt), Math.Abs(kss));
            if (Math.Abs(kts) > 1e-6 * scale + 1e-6)
                throw new InvalidOperationException($"Coddington trace, {where}: at surface {j} the tangential and sagittal directions are not principal directions of the surface (cross curvature {kts:g4} 1/m against {ktt:g4} and {kss:g4}); the pencil is skew and a general astigmatic trace is not built.");

            double n1 = hit.IndexBefore, n2 = hit.IndexAfter;
            double cos1 = Vector3D.Dot(incoming, normal), cos2 = Vector3D.Dot(outgoing, normal);
            double oblique = n2 * cos2 - n1 * cos1;
            s = n2 / (n1 / s + kss * oblique);
            t = n2 * cos2 * cos2 / (n1 * cos1 * cos1 / t + ktt * oblique);
            if (double.IsNaN(s) || double.IsNaN(t))
                throw new InvalidOperationException($"Coddington trace, {where}: the foci after surface {j} are undefined (a focus lies exactly on a surface with power).");
        }
        var last = hits[image];
        s -= last.SegmentLength;
        t -= last.SegmentLength;
        return new CoddingtonFoci { Tangential = t, Sagittal = s, ImagePoint = chief.Final.Position, ImageDirection = chief.Final.Direction };
    }

    /// <summary>True when the shape overrides <see cref="SurfaceShape.TryRedirect"/>, turning
    /// rays by its own rule.</summary>
    internal static bool TurnsRaysItself(SurfaceShape shape) =>
        shape.GetType().GetMethod(nameof(SurfaceShape.TryRedirect))!.DeclaringType != typeof(SurfaceShape);
}

/// <summary>The second fundamental form of a sequential surface at a point (see
/// <see cref="CoddingtonTrace"/> for the formula).</summary>
internal static class SurfaceCurvature
{
    /// <summary>II(a, a), II(b, b) and II(a, b) at <paramref name="p"/> (local frame) for unit
    /// tangent vectors a and b. The Standard conic is differentiated in closed form; a sag shape's
    /// second derivatives are central differences of its slopes, any other shape's central
    /// second differences of its sag.</summary>
    public static (double Aa, double Bb, double Ab) SecondForm(SequentialSurface surface, int index, Vector3D p,
        Vector3D a, Vector3D b, double semiDiameter)
    {
        var (fx, fy, fxx, fxy, fyy) = Derivatives(surface, index, p, semiDiameter);
        double w = Math.Sqrt(1 + fx * fx + fy * fy);
        double aa = (fxx * a.X * a.X + 2 * fxy * a.X * a.Y + fyy * a.Y * a.Y) / w;
        double bb = (fxx * b.X * b.X + 2 * fxy * b.X * b.Y + fyy * b.Y * b.Y) / w;
        double ab = (fxx * a.X * b.X + fxy * (a.X * b.Y + a.Y * b.X) + fyy * a.Y * b.Y) / w;
        return (aa, bb, ab);
    }

    private static (double Fx, double Fy, double Fxx, double Fxy, double Fyy) Derivatives(SequentialSurface surface, int index,
        Vector3D p, double semiDiameter)
    {
        double x = p.X, y = p.Y;
        if (surface.Shape is null)
        {
            // The conic F = c(x² + y²) + c(1 + k)z² − 2z = 0 (ConicIntersection.Normal's ∇F/2 =
            // (cx, cy, c(1 + k)z − 1)). Implicit differentiation, F_z = 2(c(1 + k)z − 1):
            // f_x = −F_x/F_z, f_xx = −(F_xx + F_zz f_x²)/F_z, f_xy = −F_zz f_x f_y/F_z, with
            // F_x = 2cx, F_xx = 2c, F_zz = 2c(1 + k), F_xy = F_xz = 0.
            double c = surface.Curvature;
            if (c == 0) return (0, 0, 0, 0, 0);
            double ck = c * (1 + surface.Conic);
            double fz = 2 * (ck * p.Z - 1);
            double fx = -2 * c * x / fz, fy = -2 * c * y / fz;
            double fzz = 2 * ck;
            double fxx = -(2 * c + fzz * fx * fx) / fz;
            double fyy = -(2 * c + fzz * fy * fy) / fz;
            double fxy = -(fzz * fx * fy) / fz;
            return (fx, fy, fxx, fxy, fyy);
        }
        double scale = double.IsFinite(semiDiameter) && semiDiameter > 0 ? semiDiameter : Math.Max(Math.Sqrt(x * x + y * y), 1e-3);
        if (surface.Shape is SagSurfaceShape sag)
        {
            double h = 1e-6 * scale;
            if (!sag.TrySag(x, y, surface, out _, out double fx, out double fy)
                || !sag.TrySag(x + h, y, surface, out _, out double fxp, out double fyxp)
                || !sag.TrySag(x - h, y, surface, out _, out double fxm, out double fyxm)
                || !sag.TrySag(x, y + h, surface, out _, out double fxyp, out double fyp)
                || !sag.TrySag(x, y - h, surface, out _, out double fxym, out double fym))
                throw new InvalidOperationException($"Coddington trace: the sag of surface {index} ({sag.TypeName}) is not defined around ({x:g6}, {y:g6}) m, so its curvature there cannot be found.");
            double fxx = (fxp - fxm) / (2 * h), fyy = (fyp - fym) / (2 * h);
            double fxy = ((fyxp - fyxm) + (fxyp - fxym)) / (4 * h);
            return (fx, fy, fxx, fxy, fyy);
        }
        else
        {
            var shape = surface.Shape;
            double h = 1e-4 * scale;
            double Z(double u, double v) => shape.Sag(u, v, surface);
            double z0 = Z(x, y), zxp = Z(x + h, y), zxm = Z(x - h, y), zyp = Z(x, y + h), zym = Z(x, y - h);
            double zpp = Z(x + h, y + h), zpm = Z(x + h, y - h), zmp = Z(x - h, y + h), zmm = Z(x - h, y - h);
            double fx = (zxp - zxm) / (2 * h), fy = (zyp - zym) / (2 * h);
            double fxx = (zxp - 2 * z0 + zxm) / (h * h), fyy = (zyp - 2 * z0 + zym) / (h * h);
            double fxy = (zpp - zpm - zmp + zmm) / (4 * h * h);
            if (!double.IsFinite(fx) || !double.IsFinite(fy) || !double.IsFinite(fxx) || !double.IsFinite(fyy) || !double.IsFinite(fxy))
                throw new InvalidOperationException($"Coddington trace: the sag of surface {index} ({shape.TypeName}) is not defined around ({x:g6}, {y:g6}) m, so its curvature there cannot be found.");
            return (fx, fy, fxx, fxy, fyy);
        }
    }
}
