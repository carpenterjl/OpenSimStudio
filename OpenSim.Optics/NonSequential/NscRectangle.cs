using OpenSim.Core.Numerics;

namespace OpenSim.Optics.NonSequential;

/// <summary>
/// OpticStudio's Rectangle object (help, "Rectangle"): a flat surface of x and y half widths in
/// the local XY plane at z = 0, reference point at its centre, every face face 0. A surface, not
/// a volume: with a blank or glass material it is ignored, MIRROR reflects and ABSORB absorbs
/// (help, "Refraction and Reflection From NSC Objects"). Its normal is +z.
/// </summary>
public sealed class NscRectangle : NscGeometry
{
    private static readonly IReadOnlyDictionary<int, string> FaceNames = new Dictionary<int, string> { [0] = "Rectangle" };

    public double HalfWidthX { get; }
    public double HalfWidthY { get; }
    public override bool IsVolume => false;
    public override string TypeName => "Rectangle";
    public override IReadOnlyDictionary<int, string> Faces => FaceNames;
    public override Aabb LocalBounds { get; }

    /// <param name="halfWidthX">Meters, positive.</param>
    /// <param name="halfWidthY">Meters, positive.</param>
    public NscRectangle(double halfWidthX, double halfWidthY)
    {
        if (!(halfWidthX > 0) || !double.IsFinite(halfWidthX) || !(halfWidthY > 0) || !double.IsFinite(halfWidthY))
            throw new ArgumentOutOfRangeException(nameof(halfWidthX),
                $"Rectangle: both half widths must be positive and finite (meters); they are {halfWidthX} and {halfWidthY}.");
        HalfWidthX = halfWidthX;
        HalfWidthY = halfWidthY;
        LocalBounds = new Aabb(new Vector3D(-halfWidthX, -halfWidthY, 0), new Vector3D(halfWidthX, halfWidthY, 0));
    }

    public override void Intersect(Vector3D origin, Vector3D direction, double tMin, double tMax, List<NscLocalHit> hits)
    {
        if (direction.Z == 0) return;
        double t = -origin.Z / direction.Z;
        if (!(t > tMin && t <= tMax)) return;
        double x = origin.X + t * direction.X, y = origin.Y + t * direction.Y;
        if (Math.Abs(x) > HalfWidthX || Math.Abs(y) > HalfWidthY) return;
        Add(hits, t, tMin, tMax, new Vector3D(x, y, 0), Vector3D.UnitZ, 0);
    }

    public override bool IsInside(Vector3D point) => false;

    public override string Describe(Func<double, string> length) =>
        $"half widths {length(HalfWidthX)} × {length(HalfWidthY)}";
}
