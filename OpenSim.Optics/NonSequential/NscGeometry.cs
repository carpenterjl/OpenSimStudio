using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential;

/// <summary>
/// One intersection of a ray with an object, in the object's local frame: the ray parameter
/// (meters along the unit direction), the point, the unit normal and the face number.
/// <para>
/// For a volume the normal points out of the volume, so a ray with direction·normal &lt; 0 is
/// entering it. For a surface object (no volume) it is the face's own fixed sense (+z for a
/// flat rectangle); entering and leaving have no meaning there.
/// </para>
/// </summary>
public readonly record struct NscLocalHit(double T, Vector3D Point, Vector3D Normal, int Face);

/// <summary>
/// The shape of a non-sequential object (docs/Optics-Conventions.md §69): exact analytic ray
/// intersection, outward normals, named faces, a local bounding box and, for a volume, an
/// inside test. Every member works in the object's local frame, in meters, and must be
/// thread-safe (one scene is traced from many threads); instances are immutable.
/// <para>
/// This is the extension point the object library (OP-72), the illumination and constructive
/// objects (OP-73, OP-74) and CAD parts (OP-75) derive from. OP-70 itself ships only
/// <see cref="NscSphere"/>, <see cref="NscRectangle"/> and <see cref="NscStandardLens"/>.
/// </para>
/// </summary>
public abstract class NscGeometry
{
    /// <summary>OpticStudio's name of the object type ("Sphere", "Standard Lens", ...).</summary>
    public abstract string TypeName { get; }

    /// <summary>True when the object encloses a volume (it can refract and has an inside);
    /// false for a surface, which can only reflect, absorb, detect or be ignored (OpticStudio
    /// help, "Refraction and Reflection From NSC Objects").</summary>
    public abstract bool IsVolume { get; }

    /// <summary>The object's face numbers and names (OpticStudio's per-object face numbering).</summary>
    public abstract IReadOnlyDictionary<int, string> Faces { get; }

    /// <summary>A box around the whole object in its local frame.</summary>
    public abstract Aabb LocalBounds { get; }

    /// <summary>Appends to <paramref name="hits"/> every intersection of the ray
    /// <paramref name="origin"/> + t·<paramref name="direction"/> (unit direction) with the
    /// object for t in (<paramref name="tMin"/>, <paramref name="tMax"/>], in any order.</summary>
    public abstract void Intersect(Vector3D origin, Vector3D direction, double tMin, double tMax, List<NscLocalHit> hits);

    /// <summary>True when the point lies strictly inside the volume; always false for a
    /// surface object.</summary>
    public abstract bool IsInside(Vector3D point);

    /// <summary>The radius of curvature (meters) of a face at which coating tapers are read;
    /// infinite (a plane) unless the shape says otherwise.</summary>
    public virtual double FaceRadius(int face) => double.PositiveInfinity;

    /// <summary>The parameters, for reports ("radius 10 mm, volume").</summary>
    public abstract string Describe(Func<double, string> length);

    /// <summary>The stable root of a·t² + 2b·t + c = 0: (−b ∓ √(b² − ac))/a written so that
    /// neither root subtracts near-equal numbers (q = −(b + sign(b)√disc), roots q/a and c/q).
    /// Returns how many real roots there are (0, 1 for a linear equation, or 2).</summary>
    internal static int Roots(double a, double b, double c, out double t1, out double t2)
    {
        t1 = t2 = double.NaN;
        if (a == 0)
        {
            if (b == 0) return 0;
            t1 = -c / (2 * b);
            return 1;
        }
        double disc = b * b - a * c;
        if (!(disc >= 0)) return 0;
        double q = -(b + Math.CopySign(Math.Sqrt(disc), b));
        if (q == 0)
        {
            // b = 0 and disc = 0: the double root t = 0 (c = 0).
            t1 = t2 = 0;
            return 2;
        }
        t1 = q / a;
        t2 = c / q;
        return 2;
    }

    internal static void Add(List<NscLocalHit> hits, double t, double tMin, double tMax, Vector3D point, Vector3D normal, int face)
    {
        if (t > tMin && t <= tMax && double.IsFinite(t)) hits.Add(new NscLocalHit(t, point, normal, face));
    }
}
