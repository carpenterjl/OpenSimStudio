using OpenSim.Core.Numerics;

namespace OpenSim.Optics.Sequential.Aspheres;

/// <summary>
/// A surface given by its sag z(x, y) over the vertex plane (docs/Optics-Conventions.md §17):
/// the aspheres of OP-23. A derived form supplies the sag and its two slopes; the intersection
/// (<see cref="AsphereIntersection"/>) and the normal are shared. The base conic is the
/// surface's own <see cref="SequentialSurface.Curvature"/> and
/// <see cref="SequentialSurface.Conic"/>; the form's coefficients are carried by the shape.
/// Instances are immutable and safe to share between threads.
/// </summary>
public abstract class SagSurfaceShape : SurfaceShape
{
    /// <summary>
    /// The sag z and its slopes ∂z/∂x and ∂z/∂y at (<paramref name="x"/>, <paramref name="y"/>),
    /// local frame, meters. False where the sag is not defined (outside the conic's square
    /// root, a vertical tangent, or a value that is not finite).
    /// </summary>
    public abstract bool TrySag(double x, double y, SequentialSurface surface,
        out double z, out double dzdx, out double dzdy);

    /// <summary>True for OpticStudio's Alternate Even and Alternate Odd: the ray meets the
    /// surface at the other root of the base conic's quadratic (§17).</summary>
    public virtual bool UsesAlternateRoot => false;

    public sealed override double Sag(double x, double y, SequentialSurface surface) =>
        TrySag(x, y, surface, out double z, out _, out _) ? z : double.NaN;

    public sealed override bool Intersect(double x, double y, Vector3D direction, SequentialSurface surface,
        out Vector3D point, out double distance) =>
        AsphereIntersection.Intersect(this, x, y, direction, surface, out point, out distance);

    /// <summary>The unit normal (−∂z/∂x, −∂z/∂y, 1)/|…|, +z at the vertex like the conic's;
    /// NaN components where the sag is not defined.</summary>
    public sealed override Vector3D Normal(Vector3D point, SequentialSurface surface)
    {
        if (!TrySag(point.X, point.Y, surface, out _, out double zx, out double zy))
            return new Vector3D(double.NaN, double.NaN, double.NaN);
        var n = new Vector3D(-zx, -zy, 1);
        return n / n.Length;
    }

    /// <summary>Throws when a coefficient is not finite, naming the form and the entry.</summary>
    protected static double[] CheckedCopy(IReadOnlyList<double> values, string form, string what, int maximum)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximum)
            throw new ArgumentException($"{form}: {values.Count} {what} given; OpticStudio's form holds at most {maximum}.");
        var copy = new double[values.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            if (!double.IsFinite(values[i]))
                throw new ArgumentException($"{form}: {what} {i + 1} is {values[i]}; every coefficient must be finite.");
            copy[i] = values[i];
        }
        return copy;
    }
}

/// <summary>
/// A rotationally symmetric sag z(r), r = √(x² + y²). The slopes are dz/dr·(x/r, y/r), taken as
/// zero on the axis (where a term odd in r has a cone point and no gradient).
/// </summary>
public abstract class RadialSagShape : SagSurfaceShape
{
    /// <summary>The sag and dz/dr at radius <paramref name="r"/> ≥ 0, meters; false where not defined.</summary>
    public abstract bool TryRadialSag(double r, SequentialSurface surface, out double z, out double dzdr);

    public sealed override bool TrySag(double x, double y, SequentialSurface surface,
        out double z, out double dzdx, out double dzdy)
    {
        double r = Math.Sqrt(x * x + y * y);
        dzdx = dzdy = 0;
        if (!TryRadialSag(r, surface, out z, out double dzdr) || !double.IsFinite(z) || !double.IsFinite(dzdr))
        {
            z = double.NaN;
            return false;
        }
        if (r > 0)
        {
            dzdx = dzdr * (x / r);
            dzdy = dzdr * (y / r);
        }
        return true;
    }
}

/// <summary>The Standard conic's sag and slope, the first term of every form in §17.</summary>
public static class ConicSag
{
    /// <summary>
    /// z = c·r²/(1 + φ), φ = √(1 − (1 + k)c²r²), and dz/dr = c·r/φ (derived: the conic
    /// c(r² + (1 + k)z²) − 2z = 0 differentiated, with 1 − c(1 + k)z = φ on the vertex sheet).
    /// False when (1 + k)c²r² ≥ 1 (the square root's domain; at equality the tangent is
    /// vertical and the slope infinite).
    /// </summary>
    public static bool TryEvaluate(double r, double curvature, double conic, out double z, out double dzdr)
    {
        double c = curvature;
        if (c == 0)
        {
            z = 0;
            dzdr = 0;
            return true;
        }
        double arg = 1 - (1 + conic) * c * c * r * r;
        if (!(arg > 0))
        {
            z = dzdr = double.NaN;
            return false;
        }
        double phi = Math.Sqrt(arg);
        z = c * r * r / (1 + phi);
        dzdr = c * r / phi;
        return true;
    }
}
