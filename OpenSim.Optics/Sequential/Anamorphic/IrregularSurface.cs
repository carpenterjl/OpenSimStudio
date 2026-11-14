using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Optics.Sequential.Anamorphic;

/// <summary>
/// OpticStudio's Irregular surface (OpticStudio 2025 R2 help, "Irregular", saved under
/// plans/sources/optics/op-24-help/): "a Standard surface shape (plane, spherical, or conic) that
/// has additional aspheric deviations in terms of decenter, tilt, spherical aberration,
/// astigmatism, and coma", used "by the tolerancing algorithm":
/// <code>
/// z = c·r²/(1 + √(1 − (1 + k)c²r²)) + Zs·ρ⁴ + Za·ρ′y² + Zc·ρ²·ρ′y
/// ρx = x/r_max, ρy = y/r_max, ρ² = ρx² + ρy², ρ′y = ρy·cos θ − ρx·sin θ
/// </code>
/// "r_max is the maximum radial aperture of the lens, defined by the clear semi-diameter or
/// semi-diameter value for the surface"; Zs, Za, Zc are lengths "at the maximum radial
/// aperture"; θ is in degrees. Parameters: 1 decenter x, 2 decenter y, 3 tilt about x, 4 tilt
/// about y (degrees), 5 Zs, 6 Za, 7 Zc, 8 θ.
/// <para>
/// <b>Tilt and decenter.</b> "The x and y coordinates of the previous equations are in a
/// decentered and tilted coordinate system", working "exactly like the coordinate break" and
/// undone after the trace: "The surface is decentered, tilted about x, then about y. The ray is
/// traced to the surface. The surface is untilted about y, untilted about x, then undecentered."
/// So the whole sag, base conic included, is written in the frame of the coordinate break
/// (dx, dy, tilt x, tilt y, 0, decenter then tilt) of docs/Optics-Conventions.md §4
/// (<see cref="TiltDecenter.ToTransform"/>). The ray is carried into that frame, met there by
/// the shared asphere intersection (from where it crosses that frame's z = 0 plane), and the
/// point and normal are carried back; the distance along the ray is the same in both frames.
/// </para>
/// <para>
/// <b>r_max</b> is read from the surface's fixed clear semi-diameter
/// (<see cref="SequentialSurface.SemiDiameter"/>). When any of Zs, Za, Zc is non-zero and the
/// semi-diameter is automatic (null) or zero, the sag throws <see cref="InvalidOperationException"/>
/// saying so: an automatic semi-diameter is itself found by tracing this surface, so it cannot
/// define the surface (this program's reading; the help does not say which value OpticStudio
/// takes for an automatic semi-diameter).
/// </para>
/// <para>
/// The paraxial engine sees c + 2·Za·cos²θ/r_max² when the surface has no tilt or decenter and
/// its astigmatism lies along x or y; otherwise it is refused by name (<see cref="AnamorphicParaxial"/>).
/// </para>
/// </summary>
public sealed class IrregularSurface : SurfaceShape
{
    private readonly RigidTransform _frame;
    private readonly IrregularSag _sag;

    /// <param name="decenterX">Meters.</param>
    /// <param name="decenterY">Meters.</param>
    /// <param name="tiltXDegrees">Tilt about x, degrees.</param>
    /// <param name="tiltYDegrees">Tilt about the new y, degrees.</param>
    /// <param name="spherical">Zs, meters.</param>
    /// <param name="astigmatism">Za, meters.</param>
    /// <param name="coma">Zc, meters.</param>
    /// <param name="angleDegrees">θ, degrees from the y axis.</param>
    public IrregularSurface(double decenterX, double decenterY, double tiltXDegrees, double tiltYDegrees,
        double spherical, double astigmatism, double coma, double angleDegrees)
    {
        foreach (var (v, what) in new[]
                 {
                     (decenterX, "decenter x"), (decenterY, "decenter y"), (tiltXDegrees, "tilt about x"), (tiltYDegrees, "tilt about y"),
                     (spherical, "Zs"), (astigmatism, "Za"), (coma, "Zc"), (angleDegrees, "θ"),
                 })
            if (!double.IsFinite(v)) throw new ArgumentException($"Irregular: {what} is {v}; it must be a finite number.");
        Placement = new TiltDecenter(decenterX, decenterY, tiltXDegrees, tiltYDegrees, 0);
        _frame = Placement.ToTransform();
        _sag = new IrregularSag(spherical, astigmatism, coma, angleDegrees);
    }

    public override string TypeName => "Irregular";

    /// <summary>The decenter and tilts as a break (order: decenter then tilt, no z tilt).</summary>
    public TiltDecenter Placement { get; }

    public double DecenterX => Placement.DecenterX;
    public double DecenterY => Placement.DecenterY;
    public double TiltXDegrees => Placement.TiltXDegrees;
    public double TiltYDegrees => Placement.TiltYDegrees;
    public double Spherical => _sag.Zs;
    public double Astigmatism => _sag.Za;
    public double Coma => _sag.Zc;
    public double AngleDegrees => _sag.ThetaDegrees;

    private bool Placed => Placement != TiltDecenter.None;

    /// <summary>The sag along the local z line through (x, y): the ray (x, y, 0) + t·(0, 0, 1)
    /// met, NaN where it misses.</summary>
    public override double Sag(double x, double y, SequentialSurface surface)
    {
        if (!Placed) return _sag.Sag(x, y, surface);
        return Intersect(x, y, Vector3D.UnitZ, surface, out _, out double t) ? t : double.NaN;
    }

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance)
    {
        if (!Placed) return AsphereIntersection.Intersect(_sag, x, y, direction, surface, out point, out distance);
        point = default;
        distance = double.NaN;
        var q = _frame.InverseApply(new Vector3D(x, y, 0));
        var d = _frame.InverseApplyDirection(direction);
        if (d.Z == 0) return false;
        double s = -q.Z / d.Z;
        if (!AsphereIntersection.Intersect(_sag, q.X + s * d.X, q.Y + s * d.Y, d, surface, out _, out double delta))
            return false;
        double t = s + delta;
        if (!double.IsFinite(t)) return false;
        point = new Vector3D(x + t * direction.X, y + t * direction.Y, t * direction.Z);
        distance = t;
        return true;
    }

    public override Vector3D Normal(Vector3D point, SequentialSurface surface)
    {
        if (!Placed) return _sag.Normal(point, surface);
        var local = _frame.InverseApply(point);
        return _frame.ApplyDirection(_sag.Normal(local, surface));
    }

    /// <summary>
    /// What the surface adds to its curvature c at the vertex, when it has no tilt or decenter
    /// (derived from the sag: Zs·ρ⁴ and Zc·ρ²·ρ′y are of fourth and third order; Za·ρ′y² =
    /// Za·(y·cos θ − x·sin θ)²/r_max² gives ∂²/∂y² = 2Za·cos²θ/r_max², ∂²/∂x² = 2Za·sin²θ/r_max²,
    /// ∂²/∂x∂y = −2Za·sin θ·cos θ/r_max²).
    /// </summary>
    internal VertexTaylor AddedAtVertex(SequentialSurface surface)
    {
        if (_sag.Za == 0) return default;
        double r = _sag.MaxRadius(surface);
        var (sin, cos) = OpticsConventions.SinCosDegrees(_sag.ThetaDegrees);
        double f = 2 * _sag.Za / (r * r);
        return new VertexTaylor { Hyy = f * cos * cos, Hxx = f * sin * sin, Hxy = -f * sin * cos };
    }

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        if (Placed)
        {
            bool plane = surface.Curvature == 0 && _sag.Zs == 0 && _sag.Za == 0 && _sag.Zc == 0;
            if (Placement.TiltXDegrees != 0 || Placement.TiltYDegrees != 0 || !plane)
                throw new ArgumentException(
                    $"Irregular: the surface is decentered ({Placement.DecenterX:R}, {Placement.DecenterY:R}) m and tilted ({Placement.TiltXDegrees:R}°, {Placement.TiltYDegrees:R}°), " +
                    "so its vertex is off the axis or tilted, and the rotationally symmetric paraxial engine has no model of it (as for any tilted or decentered lens). " +
                    "Paraxial data, ray aiming and solves are not available for this lens.");
            return new() { Curvature = surface.Curvature };
        }
        return AnamorphicParaxial.YCurvature("Irregular", surface.Curvature, AddedAtVertex(surface), "from Za at an angle θ that is not a multiple of 90°");
    }

    /// <summary>The untilted, undecentered surface: the sag of the help's equation in the
    /// surface's own frame.</summary>
    private sealed class IrregularSag : SagSurfaceShape
    {
        public IrregularSag(double zs, double za, double zc, double thetaDegrees)
        {
            Zs = zs;
            Za = za;
            Zc = zc;
            ThetaDegrees = thetaDegrees;
            (Sin, Cos) = OpticsConventions.SinCosDegrees(thetaDegrees);
        }

        public double Zs { get; }
        public double Za { get; }
        public double Zc { get; }
        public double ThetaDegrees { get; }
        private double Sin { get; }
        private double Cos { get; }

        public override string TypeName => "Irregular";

        /// <summary>r_max from the surface's fixed clear semi-diameter; throws when there is none.</summary>
        public double MaxRadius(SequentialSurface surface)
        {
            if (surface.SemiDiameter is double r && r > 0 && double.IsFinite(r)) return r;
            throw new InvalidOperationException(
                $"Irregular surface{(string.IsNullOrEmpty(surface.Comment) ? "" : $" '{surface.Comment}'")}: Zs, Za and Zc are given at the maximum radial aperture r_max, " +
                $"which is the surface's clear semi-diameter, but that is {(surface.SemiDiameter is null ? "automatic" : surface.SemiDiameter.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " m")}. " +
                "Give the surface a fixed clear semi-diameter.");
        }

        public override bool TrySag(double x, double y, SequentialSurface surface, out double z, out double dzdx, out double dzdy)
        {
            double r = Math.Sqrt(x * x + y * y);
            if (!ConicSag.TryEvaluate(r, surface.Curvature, surface.Conic, out z, out double dzdr))
            {
                dzdx = dzdy = double.NaN;
                return false;
            }
            dzdx = r > 0 ? dzdr * x / r : 0;
            dzdy = r > 0 ? dzdr * y / r : 0;
            if (Zs != 0 || Za != 0 || Zc != 0)
            {
                double m = MaxRadius(surface);
                double px = x / m, py = y / m, rho2 = px * px + py * py;
                double q = py * Cos - px * Sin;       // ρ′y
                // z += Zs ρ⁴ + Za ρ′y² + Zc ρ² ρ′y; ∂ρ²/∂ρx = 2ρx, ∂ρ′y/∂ρx = −sin θ, ∂ρ′y/∂ρy = cos θ.
                z += Zs * rho2 * rho2 + Za * q * q + Zc * rho2 * q;
                double gx = Zs * 4 * rho2 * px + Za * 2 * q * -Sin + Zc * (2 * px * q + rho2 * -Sin);
                double gy = Zs * 4 * rho2 * py + Za * 2 * q * Cos + Zc * (2 * py * q + rho2 * Cos);
                dzdx += gx / m;
                dzdy += gy / m;
            }
            return double.IsFinite(z) && double.IsFinite(dzdx) && double.IsFinite(dzdy);
        }
    }
}
