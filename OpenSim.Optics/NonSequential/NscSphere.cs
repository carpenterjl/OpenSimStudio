using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential;

/// <summary>
/// OpticStudio's Sphere object (help, "Sphere (non-sequential geometry objects)"): a radius and
/// an "Is Volume?" flag. The reference point is the centre; every face is face 0. A hollow shell
/// (not a volume) must reflect or absorb: "Rays must either reflect or absorb from such a
/// surface; refraction is not allowed" (same page), which the scene checks.
/// <para>
/// The intersection solves |o + t·d|² = r² for a unit d: t² + 2(o·d)t + |o|² − r² = 0, with the
/// discriminant written as r² − |o − (o·d)d|² (the squared distance of the line from the
/// centre subtracted from r²), the accurate form of Pharr, Jakob and Humphreys, Physically Based
/// Rendering, 4th ed., section 6.8.3 (Accurate Quadratic Discriminants), which does not lose the
/// small discriminant of a near-grazing ray to cancellation.
/// </para>
/// </summary>
public sealed class NscSphere : NscGeometry
{
    private static readonly IReadOnlyDictionary<int, string> FaceNames = new Dictionary<int, string> { [0] = "Sphere" };

    public double Radius { get; }
    public override bool IsVolume { get; }
    public override string TypeName => "Sphere";
    public override IReadOnlyDictionary<int, string> Faces => FaceNames;
    public override Aabb LocalBounds { get; }

    /// <param name="radius">Meters, positive.</param>
    /// <param name="isVolume">True for a solid; false for a hollow shell.</param>
    public NscSphere(double radius, bool isVolume = true)
    {
        if (!(radius > 0) || !double.IsFinite(radius))
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Sphere: the radius must be positive and finite (meters).");
        Radius = radius;
        IsVolume = isVolume;
        LocalBounds = new Aabb(new Vector3D(-radius, -radius, -radius), new Vector3D(radius, radius, radius));
    }

    public override void Intersect(Vector3D origin, Vector3D direction, double tMin, double tMax, List<NscLocalHit> hits)
    {
        double b = Vector3D.Dot(origin, direction);
        double c = origin.LengthSquared - Radius * Radius;
        var v = origin - direction * b;
        double vLength = v.Length;
        double disc = (Radius - vLength) * (Radius + vLength);   // PBR 6.8.3
        if (!(disc >= 0)) return;
        double q = -(b + Math.CopySign(Math.Sqrt(disc), b));
        double t1, t2;
        if (q == 0) { t1 = t2 = 0; }
        else { t1 = q; t2 = c / q; }
        Hit(origin, direction, t1, tMin, tMax, hits);
        if (t2 != t1) Hit(origin, direction, t2, tMin, tMax, hits);
    }

    private void Hit(Vector3D o, Vector3D d, double t, double tMin, double tMax, List<NscLocalHit> hits)
    {
        if (!(t > tMin && t <= tMax)) return;
        var p = o + d * t;
        Add(hits, t, tMin, tMax, p, p / p.Length, 0);
    }

    public override bool IsInside(Vector3D point) => IsVolume && point.LengthSquared < Radius * Radius;

    public override double FaceRadius(int face) => Radius;

    public override string Describe(Func<double, string> length) =>
        $"radius {length(Radius)}, {(IsVolume ? "solid volume" : "hollow shell")}";
}
