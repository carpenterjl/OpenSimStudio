// OP-85
using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Optics.NonSequential.Sources;

namespace OpenSim.Optics.NonSequential.Lightning;

/// <summary>
/// A source's far field as a chart of the unit square (plan item OP-85; docs/Optics-Conventions.md
/// §79): (s, b) ∈ [0, 1]² → a unit direction in the source's local frame, with the source's power
/// spread over the chart by a known measure, so a mesh of chart cells is a mesh of flux tubes
/// whose powers are exact.
/// <para>
/// <b>Construction.</b> Every built source type samples its rays by inverting its distributions
/// (OP-77, Conventions §72): <see cref="NscSource.Sample"/> maps numbers u ∈ [0, 1)^k, uniform,
/// onto a start point and a direction, so the power of a region of u-space is the source power
/// times its volume. For the types whose direction depends on two of those numbers only, those two
/// (u_a, u_b) are the chart and the other numbers are held at ½ (they move the start point, which
/// LightningTrace replaces by the source's centre: "All rays launched from each mesh start at the
/// same position, corresponding to the center position of the corresponding source. Thus,
/// LightningTrace neglects the size and spatial distribution of each source, and represents each
/// source by its far-field description"; help, "Lightning Trace"). The power of the chart cell
/// [s0, s1] × [b0, b1] is P·(w(s1) − w(s0))·(b1 − b0), u_a = w(s) a monotone warp with w(0) = 0
/// and w(1) = 1 chosen per type so that the polar angle is close to linear in s (a pole of an
/// inverse CDF, θ ∝ √u, would otherwise make the first ring of cells coarse).
/// </para>
/// <para>
/// <b>Refused types</b>, by name: Source Ray and the collimated modes ("LightningTrace cannot be
/// used to model perfectly collimated beams"; help), Source Gaussian (its radial inversion reaches
/// the horizon only in the limit u → 1, so the outer ring of cells has no finite image), Source
/// Ellipse/Rectangle with a Gaussian far field or an annular point-mode emitter (drawn by
/// rejection, which has no chart), and any type this class does not know.
/// </para>
/// </summary>
public sealed class NscLightningChart
{
    private readonly NscSource _source;
    private readonly int _ia, _ib;
    private readonly int _dims;
    private readonly Func<double, double> _warp;

    private NscLightningChart(NscSource source, int ia, int ib, Func<double, double> warp, string description)
    {
        _source = source;
        _ia = ia;
        _ib = ib;
        _dims = source.Dimensions;
        _warp = warp;
        Description = description;
        var b = source.LocalBounds;
        Center = (b.Min + b.Max) * 0.5;
    }

    /// <summary>The start point of every mesh ray in the source's local frame (meters): the
    /// centre of the source's local bounding box (a reading: the help says "the center position
    /// of the corresponding source", Conventions §10 item 75).</summary>
    public Vector3D Center { get; }

    /// <summary>How the chart is built, for reports.</summary>
    public string Description { get; }

    /// <summary>The fraction of the source's power in chart rows s0 ≤ s ≤ s1 (all b): w(s1) − w(s0).</summary>
    public double Weight(double s0, double s1) => _warp(s1) - _warp(s0);

    /// <summary>The unit local direction of chart point (s, b).</summary>
    public Vector3D Direction(double s, double b)
    {
        Span<double> u = stackalloc double[Math.Max(1, _dims)];
        for (int i = 0; i < _dims; i++) u[i] = 0.5;
        u[_ia] = Math.Clamp(_warp(s), 0, 1);
        u[_ib] = Math.Clamp(b, 0, 1);
        // The extra stream is drawn only by rejection steps, which the chosen types do not reach
        // for the direction (an annular cosine-mode emitter draws its start point by rejection,
        // and the start point is discarded).
        _source.Sample(u, SplitMix64.ForStream(0, 0), out _, out var d);
        double length = d.Length;
        if (!(length > 0) || !double.IsFinite(length))
            throw new InvalidOperationException($"{_source.TypeName}: the far-field chart gives no direction at s = {F(s)}, b = {F(b)} ({d}).");
        return d / length;
    }

    private static double Identity(double s) => s;

    private static double Square(double s) => s * s;

    /// <summary>u = (1 − cos(sα))/(1 − cos α): with cos θ = 1 − u(1 − cos α) this is θ = sα.</summary>
    private static Func<double, double> Cone(double alphaRadians)
    {
        double denominator = 1 - Math.Cos(alphaRadians);
        return s => s <= 0 ? 0 : s >= 1 ? 1 : (1 - Math.Cos(s * alphaRadians)) / denominator;
    }

    /// <summary>The chart of <paramref name="source"/>; throws <see cref="NotSupportedException"/>
    /// naming <paramref name="where"/> and the reason for a type or mode LightningTrace refuses.</summary>
    public static NscLightningChart For(NscSource source, string where)
    {
        ArgumentNullException.ThrowIfNull(source);
        string refusal = $"{where}: LightningTrace refuses this source: ";
        switch (source)
        {
            case NscSourcePoint p:
                if (p.ConeAngleDegrees == 0)
                    throw new NotSupportedException(refusal + "a cone angle of 0 is a single direction, a perfectly collimated beam (\"LightningTrace cannot be used to model perfectly collimated beams\"; help, \"Lightning Trace\").");
                // Sample: cos θ = 1 − u0(1 − cos α), φ = 2π u1.
                return new NscLightningChart(source, 0, 1, Cone(p.ConeAngleDegrees * Math.PI / 180), "polar angle linear in s over the cone, azimuth 2πb");
            case NscSourceRay:
                throw new NotSupportedException(refusal + "Source Ray emits one direction, a perfectly collimated beam (\"LightningTrace cannot be used to model perfectly collimated beams\"; help, \"Lightning Trace\").");
            case NscSourceGaussian:
                throw new NotSupportedException(refusal + "Source Gaussian is not built for LightningTrace: collimated at position 0 (\"LightningTrace cannot be used to model perfectly collimated beams\"), and otherwise its radial inversion r = σ√(−2 ln(1 − u)) reaches the horizon only as u → 1, so the outer ring of mesh cells has no finite image.");
            case NscSourceEllipse e:
            {
                switch (e.Mode)
                {
                    case "cosine":
                    {
                        // Sample: cos θ = (1 − u2)^(1/(n + 1)), φ = 2π u3; u2 = 1 − cos^(n+1)(sπ/2) makes θ = sπ/2.
                        double n1 = e.CosineExponent + 1;
                        return new NscLightningChart(source, 2, 3, s => s <= 0 ? 0 : s >= 1 ? 1 : 1 - Math.Pow(Math.Cos(s * Math.PI / 2), n1),
                            $"cosine-exponent far field, polar angle linear in s over 0–90°, azimuth 2πb");
                    }
                    case "Gaussian":
                        throw new NotSupportedException(refusal + $"{e.TypeName}'s Gaussian far field (Gx, Gy) is drawn by rejection, which has no chart for the mesh.");
                    case "collimated":
                        throw new NotSupportedException(refusal + $"{e.TypeName} with a source distance of 0 and no cosine or Gaussian far field is collimated (\"LightningTrace cannot be used to model perfectly collimated beams\"; help, \"Lightning Trace\").");
                    default:
                        // Point mode: the direction runs from the apparent point (Source X, Y, −distance) through
                        // the start point, so the far field is the emitting region seen from that point.
                        if (e.MinHalfWidthX > 0 && e.MinHalfWidthY > 0)
                            throw new NotSupportedException(refusal + $"an annular {e.TypeName} draws its start points by rejection, and in point mode the start point sets the direction, so it has no chart for the mesh.");
                        return e.IsRectangle
                            ? new NscLightningChart(source, 0, 1, Identity, "far field of the rectangle seen from the apparent point, x and y linear in s and b")
                            : new NscLightningChart(source, 0, 1, Square, "far field of the ellipse seen from the apparent point, radius linear in s, azimuth 2πb");
                }
            }
            case NscSourceTwoAngle t:
                // Sample: (p, q) uniform over the angle rectangle (u2, u3) or ellipse (r = √u2, φ = 2π u3).
                return t.EllipticalAngles
                    ? new NscLightningChart(source, 2, 3, Square, "elliptical angular cone, radius linear in s, azimuth 2πb")
                    : new NscLightningChart(source, 2, 3, Identity, "rectangular angular cone, the two angles' coordinates linear in s and b");
            case NscSourceRadial r:
                // Sample: θ = F⁻¹(u2) of I(θ)·sin θ, φ = 2π u3; from θ = 0 the inverse has θ ∝ √u, so u = s².
                return new NscLightningChart(source, 2, 3, r.MinAngleDegrees == 0 ? Square : Identity,
                    r.MinAngleDegrees == 0 ? "spline intensity, u = s² (θ about linear in s near the axis), azimuth 2πb" : "spline intensity, u linear in s, azimuth 2πb");
            case NscSourceDiode:
                // Sample: θx = F⁻¹(u0), θy = F⁻¹(u1) of the super-Gaussian profiles.
                return new NscLightningChart(source, 0, 1, Identity, "super-Gaussian far field, θx and θy from s and b by their inverse distributions");
            case NscSourceVolume:
            case NscSourceTube:
            case NscSourceFilament:
            {
                // Sample: Isotropic(u_{k−2}, u_{k−1}): cos θ = 1 − 2u, φ = 2πu'; u = (1 − cos πs)/2 makes θ = πs.
                int k = source.Dimensions;
                return new NscLightningChart(source, k - 2, k - 1, Cone(Math.PI), "isotropic, polar angle linear in s over 0–180°, azimuth 2πb");
            }
            default:
                throw new NotSupportedException(refusal + $"{source.TypeName} has no far-field chart for LightningTrace.");
        }
    }

    private static string F(double v) => v.ToString("g9", CultureInfo.InvariantCulture);
}
