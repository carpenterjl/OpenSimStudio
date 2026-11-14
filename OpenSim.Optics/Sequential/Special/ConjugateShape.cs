using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Special;

/// <summary>
/// OpticStudio's Conjugate surface (OpticStudio 2025 R2 help, "Conjugate"): the surface through
/// the vertex on which the distances to two points P₁ = (x₁, y₁, z₁) and P₂ = (x₂, y₂, z₂),
/// given relative to the vertex, satisfy |P − P₁| + |P − P₂| = const when z₁ and z₂ have the
/// same sign (a mirror then images one point perfectly onto the other, a real image), and
/// |P − P₁| − |P − P₂| = const when they have opposite signs (a virtual image); "the surface
/// must intersect the point (0,0,0)", which fixes the constant as |P₁| ± |P₂|. "Neither the z1
/// nor the z2 value can be zero." Examples on the help page: x = y = 0 and both z equal to R
/// is a sphere of radius R; equal and opposite z on the axis is a plane. Any material.
/// <para>
/// With F(p) = |p − P₁| ± |p − P₂| − F₀, the intersection is Newton's iteration on F along the
/// ray from the vertex plane (F′ = ∇F·d), the sag Newton's iteration on F in z, and the normal
/// ±∇F/|∇F| with ∇|p − P| = (p − P)/|p − P|, turned so it is +z at the vertex. As for the conic
/// (docs/Optics-Conventions.md §13), only the sheet whose normal keeps the vertex's z sense is
/// the surface, so a ray meeting the far side of a closed ellipsoid is a miss.
/// </para>
/// <para>
/// First order: the curvature the paraxial engine gets is ∂²z/∂y² at the vertex, from the
/// implicit function F(0, y, z(y)) = 0 (derived): z_y = −F_y/F_z and
/// z_yy = −(F_yy + 2F_yz·z_y + F_zz·z_y²)/F_z, with the Hessian of |p − P| equal to
/// (I − ûûᵀ)/|p − P|. On the axis it is (1/z₁ + 1/z₂)/2, which is 1/R for the sphere above.
/// With x or y offsets the surface is tilted at its vertex and that is the y-z section's
/// second derivative only; the paraxial data are then approximate.
/// </para>
/// </summary>
public sealed class ConjugateShape : SurfaceShape
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;
    private readonly double _f0, _sign, _orientation;

    public ConjugateShape(Vector3D first, Vector3D second)
    {
        if (!double.IsFinite(first.X) || !double.IsFinite(first.Y) || !double.IsFinite(first.Z)
            || !double.IsFinite(second.X) || !double.IsFinite(second.Y) || !double.IsFinite(second.Z))
            throw new ArgumentException("Conjugate surface: both construction points must have finite coordinates.");
        if (first.Z == 0 || second.Z == 0)
            throw new ArgumentException("Conjugate surface: neither z1 nor z2 can be zero (OpticStudio's rule).");
        First = first;
        Second = second;
        _sign = Math.Sign(first.Z) == Math.Sign(second.Z) ? 1 : -1;
        _f0 = first.Length + _sign * second.Length;
        var g = Gradient(Vector3D.Zero);
        if (!(Math.Abs(g.Z) > 1e-12 * g.Length))
            throw new ArgumentException("Conjugate surface: the surface through the vertex is parallel to the local z axis there, so it has no sag; move the construction points.");
        _orientation = g.Z > 0 ? 1 : -1;   // the normal σ·∇F/|∇F| is +z at the vertex
    }

    public override string TypeName => "Conjugate";

    /// <summary>The first construction point (x₁, y₁, z₁), meters, from the vertex.</summary>
    public Vector3D First { get; }

    /// <summary>The second construction point (x₂, y₂, z₂), meters, from the vertex.</summary>
    public Vector3D Second { get; }

    /// <summary>True when the image between the points is real (z₁ and z₂ of one sign).</summary>
    public bool IsReal => _sign > 0;

    private double F(Vector3D p) => (p - First).Length + _sign * (p - Second).Length - _f0;

    private Vector3D Gradient(Vector3D p)
    {
        var a = p - First;
        var b = p - Second;
        return a / a.Length + b / b.Length * _sign;
    }

    public override double Sag(double x, double y, SequentialSurface surface)
    {
        double z = 0;
        for (int i = 0; i < 64; i++)
        {
            var p = new Vector3D(x, y, z);
            double f = F(p), fz = Gradient(p).Z;
            if (!double.IsFinite(f) || fz == 0) return double.NaN;
            double step = f / fz;
            z -= step;
            if (Math.Abs(step) <= 4 * Eps * (Math.Abs(z) + Math.Abs(x) + Math.Abs(y)))
                return OnVertexSheet(new Vector3D(x, y, z)) ? z : double.NaN;
        }
        return double.NaN;
    }

    public override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance)
    {
        double s = 0, scale = Math.Abs(x) + Math.Abs(y);
        for (int i = 0; i < 64; i++)
        {
            var p = new Vector3D(x + s * direction.X, y + s * direction.Y, s * direction.Z);
            double f = F(p), df = Vector3D.Dot(Gradient(p), direction);
            if (!double.IsFinite(f) || !double.IsFinite(df) || df == 0) break;
            double step = f / df;
            s -= step;
            if (Math.Abs(step) <= 4 * Eps * (Math.Abs(s) + scale))
            {
                point = new Vector3D(x + s * direction.X, y + s * direction.Y, s * direction.Z);
                distance = s;
                if (OnVertexSheet(point)) return true;
                break;
            }
        }
        point = default;
        distance = double.NaN;
        return false;
    }

    private bool OnVertexSheet(Vector3D p) => _orientation * Gradient(p).Z > 0;

    public override Vector3D Normal(Vector3D point, SequentialSurface surface)
    {
        var g = Gradient(point) * _orientation;
        return g / g.Length;
    }

    public override ParaxialShapeEquivalent ParaxialEquivalent(SequentialSurface surface)
    {
        // F's derivatives at the vertex: ∇|p − P| = −P/|P| =: û, Hessian (I − ûûᵀ)/|P|.
        double fy = 0, fz = 0, fyy = 0, fyz = 0, fzz = 0;
        foreach (var (point, weight) in new[] { (First, 1.0), (Second, _sign) })
        {
            double r = point.Length;
            double uy = -point.Y / r, uz = -point.Z / r;
            fy += weight * uy;
            fz += weight * uz;
            fyy += weight * (1 - uy * uy) / r;
            fyz += weight * (-uy * uz) / r;
            fzz += weight * (1 - uz * uz) / r;
        }
        double zy = -fy / fz;
        double zyy = -(fyy + 2 * fyz * zy + fzz * zy * zy) / fz;
        return new() { Curvature = zyy };
    }
}
