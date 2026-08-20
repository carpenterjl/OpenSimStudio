using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>
/// One place a fluid volume reaches a face of the CFD domain box — a bore mouth, a duct
/// end, a plenum window. What the user picks from when they say "this hole is the inlet".
/// </summary>
/// <param name="Face">The box face it sits on.</param>
/// <param name="UMin">In-plane rectangle, in the same (u, v) convention
/// <see cref="FlowOpening"/> uses: an X face is (y, z), a Y face (x, z), a Z face (x, y).</param>
/// <param name="Area">Summed area of the fluid triangles lying in the plane [m²] — the
/// REAL open area, which for a circular bore is π/4 of the rectangle that bounds it.</param>
/// <param name="Center">Centroid of the patch in world coordinates.</param>
/// <param name="EquivalentDiameter">√(4·Area/π) [m] — the diameter of the round hole with
/// the same area, which is how a bore is described in every drawing and report.</param>
public sealed record FlowOpeningCandidate(BoxFace Face, double UMin, double UMax,
    double VMin, double VMax, double Area, Vector3D Center, double EquivalentDiameter)
{
    /// <summary>The opening this candidate becomes once the user says what it does.</summary>
    public FlowOpening ToOpening(FlowFaceKind kind, Vector3D velocity, double? temperature) =>
        new()
        {
            Face = Face,
            UMin = UMin, UMax = UMax, VMin = VMin, VMax = VMax,
            Kind = kind,
            Velocity = velocity,
            Temperature = temperature
        };

    /// <summary>"XMin, ⌀20.0 mm at (0.0, 180.0, 25.0) mm" — a row in the picker.</summary>
    public string Describe() =>
        $"{Face}, ⌀{EquivalentDiameter * 1000:F1} mm at " +
        $"({Center.X * 1000:F1}, {Center.Y * 1000:F1}, {Center.Z * 1000:F1}) mm";
}

/// <summary>
/// Finds where a fluid volume meets the walls of the flow domain.
/// <para>
/// A CAD assembly of an internal-flow part carries the passage as its OWN solid (the
/// "Water" body beside the "Copper Body"), and its end caps are exactly the openings.
/// Detecting them beats asking the user to type four coordinates per port: the numbers
/// come from the geometry that is already on screen, so they cannot disagree with it.
/// </para>
/// <para>
/// A triangle counts as lying in a box face when ALL THREE of its vertices are within
/// tolerance of that plane — a triangle merely touching the plane along one edge belongs
/// to the passage wall, not to the mouth. Coplanar triangles are then grouped into
/// connected components through shared vertices, so a part with two bores on the same
/// face reports two candidates and never one rectangle spanning both.
/// </para>
/// </summary>
public static class FlowOpeningDetector
{
    /// <param name="fluid">The fluid volume's surface geometry.</param>
    /// <param name="domain">The flow domain box.</param>
    /// <param name="tolerance">Plane-coincidence tolerance [m]; 0 = 1e-6 of the domain
    /// diagonal, which is well below any tessellation chord and well above the round-off
    /// of a transformed assembly placement.</param>
    public static IReadOnlyList<FlowOpeningCandidate> Detect(TriangleMesh fluid, Aabb domain,
        double tolerance = 0)
    {
        double tol = tolerance > 0 ? tolerance : 1e-6 * domain.Diagonal;
        var candidates = new List<FlowOpeningCandidate>();

        foreach (var face in new[] { BoxFace.XMin, BoxFace.XMax, BoxFace.YMin,
                     BoxFace.YMax, BoxFace.ZMin, BoxFace.ZMax })
        {
            int axis = (int)face / 2;
            double plane = Coordinate(face switch
            {
                BoxFace.XMin or BoxFace.YMin or BoxFace.ZMin => domain.Min,
                _ => domain.Max
            }, axis);

            // Coplanar triangles, and a union-find over the vertices they share.
            var members = new List<int>();
            var parent = new Dictionary<int, int>();
            for (int t = 0; t < fluid.Triangles.Count; t++)
            {
                var tri = fluid.Triangles[t];
                if (!OnPlane(fluid, tri.A, axis, plane, tol)
                    || !OnPlane(fluid, tri.B, axis, plane, tol)
                    || !OnPlane(fluid, tri.C, axis, plane, tol)) continue;
                members.Add(t);
                Union(parent, tri.A, tri.B);
                Union(parent, tri.B, tri.C);
            }
            if (members.Count == 0) continue;

            var groups = new Dictionary<int, List<int>>();
            foreach (int t in members)
            {
                int root = Find(parent, fluid.Triangles[t].A);
                if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<int>();
                list.Add(t);
            }

            // Deterministic order: by the patch's own in-plane position, so the same
            // geometry always numbers its ports the same way.
            foreach (var group in groups.Values
                         .OrderBy(g => g.Min())
                         .ToList())
            {
                double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
                double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
                double area = 0;
                var centroid = new Vector3D(0, 0, 0);
                foreach (int t in group)
                {
                    double a = fluid.TriangleArea(t);
                    area += a;
                    var tri = fluid.Triangles[t];
                    foreach (int v in new[] { tri.A, tri.B, tri.C })
                    {
                        var (u, w) = InPlane(fluid.Vertices[v], axis);
                        uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u);
                        vMin = Math.Min(vMin, w); vMax = Math.Max(vMax, w);
                    }
                    centroid += (fluid.Vertices[tri.A] + fluid.Vertices[tri.B]
                                 + fluid.Vertices[tri.C]) * (a / 3.0);
                }
                if (!(area > 0)) continue;
                candidates.Add(new FlowOpeningCandidate(face, uMin, uMax, vMin, vMax, area,
                    centroid * (1.0 / area), Math.Sqrt(4 * area / Math.PI)));
            }
        }

        return candidates;
    }

    private static bool OnPlane(TriangleMesh mesh, int vertex, int axis, double plane, double tol)
        => Math.Abs(Coordinate(mesh.Vertices[vertex], axis) - plane) <= tol;

    private static double Coordinate(Vector3D v, int axis) =>
        axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static (double U, double V) InPlane(Vector3D v, int axis) => axis switch
    {
        0 => (v.Y, v.Z),
        1 => (v.X, v.Z),
        _ => (v.X, v.Y)
    };

    private static int Find(Dictionary<int, int> parent, int x)
    {
        if (!parent.TryGetValue(x, out int p)) { parent[x] = x; return x; }
        if (p == x) return x;
        int root = Find(parent, p);
        parent[x] = root;
        return root;
    }

    private static void Union(Dictionary<int, int> parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb) parent[ra] = rb;
    }
}
