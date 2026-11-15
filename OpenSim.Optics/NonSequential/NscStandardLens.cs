using System.Globalization;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.NonSequential;

/// <summary>
/// OpticStudio's Standard Lens object (help, "Standard Lens"): a front and a rear Standard
/// (conic) face, an annular flat between each face's clear aperture and its edge, and a possibly
/// tapered cylinder joining the two edges. Nine parameters: front radius, conic, clear
/// semi-diameter, edge radial aperture; centre thickness; rear radius, conic, clear
/// semi-diameter, edge radial aperture. "The reference point is the center of the front face
/// of the lens. Face Numbers: Front face Face 1, back face Face 2, all other faces Face 0."
/// <para>
/// <b>Faces.</b> The front face is the conic z = sag(c₁, k₁, r) for r ≤ clear₁, the rear face
/// z = T + sag(c₂, k₂, r) for r ≤ clear₂, with the conic and normal of the sequential trace
/// (<see cref="ConicIntersection"/>, docs/Optics-Conventions.md §13): F = c(x² + y² + (1 + k)z²)
/// − 2z = 0 on its vertex sheet 1 − c(1 + k)z &gt; 0. The ray is first moved to the face's vertex
/// plane, exactly as the sequential trace does, and the vertex-sheet root is the sequential
/// trace's own expression −C/(B − sign(N)√(B² − AC)); the second root (needed here because a ray
/// may start anywhere, even inside the lens) is C/(A·s₁)'s stable twin from the same quadratic.
/// <c>SurfaceShape.cs</c> and <c>ConicIntersection.cs</c> are not changed. The annuli are flat at
/// z_f = sag₁(clear₁) and z_b = T + sag₂(clear₂); the side is the cone (a cylinder when the edges
/// are equal) through (edge₁, z_f) and (edge₂, z_b).
/// </para>
/// <para>
/// <b>Not built</b> (OP-72): the hyperhemispheric sag point (a negative clear semi-diameter) is
/// refused by name.
/// </para>
/// </summary>
public sealed class NscStandardLens : NscGeometry
{
    private static readonly IReadOnlyDictionary<int, string> FaceNames =
        new Dictionary<int, string> { [0] = "Edge", [1] = "Front", [2] = "Back" };

    public double FrontCurvature { get; }
    public double FrontConic { get; }
    public double FrontClear { get; }
    public double FrontEdge { get; }
    public double Thickness { get; }
    public double BackCurvature { get; }
    public double BackConic { get; }
    public double BackClear { get; }
    public double BackEdge { get; }

    /// <summary>z of the front annulus (the front face's sag at its clear semi-diameter).</summary>
    public double FrontEdgeZ { get; }

    /// <summary>z of the rear annulus.</summary>
    public double BackEdgeZ { get; }

    private readonly double _slope;   // d(side radius)/dz

    public override bool IsVolume => true;
    public override string TypeName => "Standard Lens";
    public override IReadOnlyDictionary<int, string> Faces => FaceNames;
    public override Aabb LocalBounds { get; }

    /// <summary>A standard lens from OpticStudio's nine parameters, in meters (a radius of 0 or
    /// infinity is flat). Throws, naming the parameter, for a lens that cannot be built: a
    /// non-positive thickness or clear semi-diameter, an edge inside its clear aperture, a face
    /// whose sag is not defined at its clear semi-diameter, or faces that cross.</summary>
    public NscStandardLens(double frontRadius, double frontConic, double frontClear, double frontEdge,
        double thickness, double backRadius, double backConic, double backClear, double backEdge)
    {
        if (frontClear < 0 || backClear < 0)
            throw new NotSupportedException(
                "Standard Lens: a negative clear semi-diameter asks for the hyperhemispheric sag point, which is not built (plan item OP-72, the object library).");
        Check(frontClear > 0 && double.IsFinite(frontClear), "the front clear semi-diameter must be positive", frontClear);
        Check(backClear > 0 && double.IsFinite(backClear), "the rear clear semi-diameter must be positive", backClear);
        Check(frontEdge >= frontClear && double.IsFinite(frontEdge), "the front edge must be at least the front clear semi-diameter", frontEdge);
        Check(backEdge >= backClear && double.IsFinite(backEdge), "the rear edge must be at least the rear clear semi-diameter", backEdge);
        Check(thickness > 0 && double.IsFinite(thickness), "the centre thickness must be positive", thickness);
        Check(double.IsFinite(frontConic) && double.IsFinite(backConic), "the conic constants must be finite", double.IsFinite(frontConic) ? backConic : frontConic);

        FrontCurvature = Curvature(frontRadius);
        BackCurvature = Curvature(backRadius);
        FrontConic = frontConic;
        BackConic = backConic;
        FrontClear = frontClear;
        BackClear = backClear;
        FrontEdge = frontEdge;
        BackEdge = backEdge;
        Thickness = thickness;

        FrontEdgeZ = Sag(FrontCurvature, FrontConic, FrontClear)
            ?? throw new ArgumentException($"Standard Lens: the front face (radius {F(frontRadius)} m, conic {F(frontConic)}) has no sag at its clear semi-diameter {F(frontClear)} m; make the semi-diameter smaller.");
        BackEdgeZ = thickness + (Sag(BackCurvature, BackConic, BackClear)
            ?? throw new ArgumentException($"Standard Lens: the rear face (radius {F(backRadius)} m, conic {F(backConic)}) has no sag at its clear semi-diameter {F(backClear)} m; make the semi-diameter smaller."));
        if (!(BackEdgeZ > FrontEdgeZ))
            throw new ArgumentException(
                $"Standard Lens: the edge thickness is {F(BackEdgeZ - FrontEdgeZ)} m (front annulus at z = {F(FrontEdgeZ)} m, rear at {F(BackEdgeZ)} m); it must be positive. Make the lens thicker or the semi-diameters smaller.");
        _slope = (BackEdge - FrontEdge) / (BackEdgeZ - FrontEdgeZ);

        // The faces must not cross anywhere inside the lens: rear above front at every radius.
        double rMax = Math.Max(FrontEdge, BackEdge);
        for (int i = 0; i <= 256; i++)
        {
            double r = rMax * i / 256;
            double front = FrontZ(Math.Min(r, FrontEdge)), back = BackZ(Math.Min(r, BackEdge));
            if (!(back > front))
                throw new ArgumentException(
                    $"Standard Lens: the front and rear faces meet or cross at radius {F(r)} m (front z = {F(front)} m, rear z = {F(back)} m). Make the lens thicker.");
        }

        double zMin = Math.Min(Math.Min(0, FrontEdgeZ), Math.Min(thickness, BackEdgeZ));
        double zMax = Math.Max(Math.Max(0, FrontEdgeZ), Math.Max(thickness, BackEdgeZ));
        LocalBounds = new Aabb(new Vector3D(-rMax, -rMax, zMin), new Vector3D(rMax, rMax, zMax));

        static void Check(bool ok, string what, double value)
        {
            if (!ok) throw new ArgumentOutOfRangeException(null, value, $"Standard Lens: {what} (it is {F(value)} m).");
        }
    }

    private static double Curvature(double radius) =>
        radius == 0 || double.IsInfinity(radius) ? 0 : 1 / radius;

    /// <summary>The conic sag c·r²/(1 + √(1 − (1 + k)c²r²)), or null where it is not defined.</summary>
    private static double? Sag(double c, double k, double r)
    {
        double under = 1 - (1 + k) * c * c * r * r;
        if (!(under >= 0)) return null;
        return c * r * r / (1 + Math.Sqrt(under));
    }

    /// <summary>The front boundary's z at radius r ≤ front edge.</summary>
    private double FrontZ(double r) => r <= FrontClear ? Sag(FrontCurvature, FrontConic, r) ?? double.NaN : FrontEdgeZ;

    /// <summary>The rear boundary's z at radius r ≤ rear edge.</summary>
    private double BackZ(double r) => r <= BackClear ? Thickness + (Sag(BackCurvature, BackConic, r) ?? double.NaN) : BackEdgeZ;

    /// <summary>The side's radius at height z.</summary>
    private double SideRadius(double z) => FrontEdge + _slope * (z - FrontEdgeZ);

    public override void Intersect(Vector3D origin, Vector3D direction, double tMin, double tMax, List<NscLocalHit> hits)
    {
        ConicFace(origin, direction, 0, FrontCurvature, FrontConic, FrontClear, -1, 1, tMin, tMax, hits);
        ConicFace(origin, direction, Thickness, BackCurvature, BackConic, BackClear, +1, 2, tMin, tMax, hits);
        Annulus(origin, direction, FrontEdgeZ, FrontClear, FrontEdge, -1, tMin, tMax, hits);
        Annulus(origin, direction, BackEdgeZ, BackClear, BackEdge, +1, tMin, tMax, hits);
        Side(origin, direction, tMin, tMax, hits);
    }

    /// <summary>A conic face with its vertex at z = <paramref name="vertexZ"/>; the outward
    /// normal is <paramref name="outward"/> times the sequential normal (+z at the vertex).</summary>
    private static void ConicFace(Vector3D origin, Vector3D d, double vertexZ, double c, double k, double clear,
        int outward, int face, double tMin, double tMax, List<NscLocalHit> hits)
    {
        var o = new Vector3D(origin.X, origin.Y, origin.Z - vertexZ);
        double L = d.X, M = d.Y, N = d.Z;
        double clear2 = clear * clear;
        if (c == 0)
        {
            if (N == 0) return;
            double t = -o.Z / N;
            var p = new Vector3D(o.X + t * L, o.Y + t * M, 0);
            if (p.X * p.X + p.Y * p.Y <= clear2)
                Add(hits, t, tMin, tMax, new Vector3D(p.X, p.Y, vertexZ), new Vector3D(0, 0, outward), face);
            return;
        }
        double kp = 1 + k;
        double s0, x, y, z0;
        if (N != 0)
        {
            // As the sequential trace: move to the vertex plane first.
            s0 = -o.Z / N;
            x = o.X + s0 * L; y = o.Y + s0 * M; z0 = 0;
        }
        else
        {
            s0 = 0; x = o.X; y = o.Y; z0 = o.Z;
        }
        double a = c * (L * L + M * M + kp * N * N);
        double b = c * (x * L + y * M + kp * z0 * N) - N;
        double cc = c * (x * x + y * y + kp * z0 * z0) - 2 * z0;
        int count = Roots(a, b, cc, out double s1, out double s2);
        for (int i = 0; i < count; i++)
        {
            double s = i == 0 ? s1 : s2;
            if (i == 1 && s2 == s1) break;
            if (!double.IsFinite(s)) continue;
            var p = new Vector3D(x + s * L, y + s * M, z0 + s * N);
            if (!(1 - c * kp * p.Z > 0)) continue;              // vertex sheet only
            if (p.X * p.X + p.Y * p.Y > clear2) continue;
            var n = ConicIntersection.Normal(p, c, k) * outward;
            Add(hits, s0 + s, tMin, tMax, new Vector3D(p.X, p.Y, p.Z + vertexZ), n, face);
        }
    }

    private static void Annulus(Vector3D o, Vector3D d, double z, double inner, double outer, int outward,
        double tMin, double tMax, List<NscLocalHit> hits)
    {
        if (!(outer > inner) || d.Z == 0) return;
        double t = (z - o.Z) / d.Z;
        double x = o.X + t * d.X, y = o.Y + t * d.Y, r2 = x * x + y * y;
        if (r2 > inner * inner && r2 <= outer * outer)
            Add(hits, t, tMin, tMax, new Vector3D(x, y, z), new Vector3D(0, 0, outward), 0);
    }

    /// <summary>The side: x² + y² = w(z)², w(z) = edge₁ + m(z − z_f), for z_f ≤ z ≤ z_b.</summary>
    private void Side(Vector3D o, Vector3D d, double tMin, double tMax, List<NscLocalHit> hits)
    {
        double m = _slope;
        double w = SideRadius(o.Z);
        double a = d.X * d.X + d.Y * d.Y - m * m * d.Z * d.Z;
        double b = o.X * d.X + o.Y * d.Y - m * w * d.Z;
        double c = o.X * o.X + o.Y * o.Y - w * w;
        int count = Roots(a, b, c, out double t1, out double t2);
        for (int i = 0; i < count; i++)
        {
            double t = i == 0 ? t1 : t2;
            if (i == 1 && t2 == t1) break;
            if (!(t > tMin && t <= tMax)) continue;
            var p = o + d * t;
            if (p.Z < FrontEdgeZ || p.Z > BackEdgeZ) continue;
            double radius = SideRadius(p.Z);
            if (!(radius > 0)) continue;
            var n = new Vector3D(p.X, p.Y, -radius * m);
            double length = n.Length;
            if (!(length > 0)) continue;
            Add(hits, t, tMin, tMax, p, n / length, 0);
        }
    }

    public override bool IsInside(Vector3D point)
    {
        double r = Math.Sqrt(point.X * point.X + point.Y * point.Y);
        if (point.Z <= Math.Min(0, FrontEdgeZ) || point.Z >= Math.Max(Thickness, BackEdgeZ)) return false;
        double side = point.Z < FrontEdgeZ ? FrontEdge : point.Z > BackEdgeZ ? BackEdge : SideRadius(point.Z);
        if (!(r < side)) return false;
        return point.Z > FrontZ(Math.Min(r, FrontEdge)) && point.Z < BackZ(Math.Min(r, BackEdge));
    }

    public override double FaceRadius(int face) => face switch
    {
        1 => FrontCurvature == 0 ? double.PositiveInfinity : 1 / FrontCurvature,
        2 => BackCurvature == 0 ? double.PositiveInfinity : 1 / BackCurvature,
        _ => double.PositiveInfinity,
    };

    public override string Describe(Func<double, string> length) =>
        $"front R {(FrontCurvature == 0 ? "∞" : length(1 / FrontCurvature))}, k {F(FrontConic)}, clear {length(FrontClear)}, edge {length(FrontEdge)}; " +
        $"thickness {length(Thickness)}; back R {(BackCurvature == 0 ? "∞" : length(1 / BackCurvature))}, k {F(BackConic)}, clear {length(BackClear)}, edge {length(BackEdge)}";

    private static string F(double v) => v.ToString("g6", CultureInfo.InvariantCulture);
}
