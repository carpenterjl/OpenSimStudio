using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Meshing2D;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class Cdt2DTests
{
    private static IReadOnlyList<Point2> Grid(int nx, int ny, double h)
    {
        var pts = new List<Point2>();
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= ny; j++)
                pts.Add(new Point2(i * h, j * h));
        return pts;
    }

    [Fact]
    public void UniformGrid_ProducesValidNonDegenerateTriangulation()
    {
        // A regular grid is the exact cocircular/collinear degeneracy that breaks naive
        // predicates; the triangulator's deterministic jitter must yield a valid mesh.
        var cdt = new Cdt2D();
        cdt.Triangulate(Grid(4, 3, 1.0), Array.Empty<(int, int)>());

        Assert.Empty(cdt.Validate());                        // consistent adjacency

        double total = 0;
        foreach (var (a, b, c) in cdt.Triangles())
        {
            double area = Point2.Cross(cdt.Points[b] - cdt.Points[a], cdt.Points[c] - cdt.Points[a]);
            Assert.True(area > 1e-9, "No sliver/degenerate triangles allowed.");   // CCW + non-degenerate
            total += area / 2;
        }
        Assert.Equal(4.0 * 3.0, total, 1e-4);                // exactly fills the grid area
    }

    [Fact]
    public void ConstraintEdge_IsPresentInOutput()
    {
        // Square with both diagonals as candidate; force the (0→2) diagonal as a constraint.
        var points = new[]
        {
            new Point2(0, 0), new Point2(2, 0), new Point2(2, 2), new Point2(0, 2),
            new Point2(1, 1.05)                                   // interior point off-center
        };
        var cdt = new Cdt2D();
        cdt.Triangulate(points, new[] { (0, 2) });

        bool present = cdt.Triangles().Any(t =>
            (t.A == 0 || t.B == 0 || t.C == 0) && (t.A == 2 || t.B == 2 || t.C == 2));
        Assert.True(present, "The constrained edge (0,2) must appear in the triangulation.");
        Assert.True(cdt.IsConstrained(0, 2));
    }

    [Fact]
    public void TotalTriangleArea_EqualsConvexHullArea()
    {
        var points = new List<Point2>();
        var rng = new Random(1234);
        for (int i = 0; i < 40; i++)
            points.Add(new Point2(rng.NextDouble() * 10, rng.NextDouble() * 10));
        // Corners guarantee the hull is the full 10×10 square.
        points[0] = new Point2(0, 0); points[1] = new Point2(10, 0);
        points[2] = new Point2(10, 10); points[3] = new Point2(0, 10);

        var cdt = new Cdt2D();
        cdt.Triangulate(points, Array.Empty<(int, int)>());
        double area = cdt.Triangles().Sum(t =>
            Math.Abs(Point2.Cross(cdt.Points[t.B] - cdt.Points[t.A], cdt.Points[t.C] - cdt.Points[t.A])) / 2);
        // Sub-feature jitter (≈1e-6·diag) perturbs the hull area at the 1e-4 level.
        Assert.Equal(100.0, area, 1e-2);
    }

    [Fact]
    public void PathologicalConstraintOrdering_TerminatesInsteadOfHanging()
    {
        // A high-aspect rectangle whose boundary is split into many collinear constraint
        // points at one particular vertex order once drove constraint recovery into an
        // unbounded loop (re-pushing the same sub-segment). The depth guard must make it
        // terminate with a typed exception instead of hanging the process forever.
        var points = new List<Point2>();
        var constraints = new List<(int, int)>();
        // Long bottom/top edges with interior split points → the collinear degeneracy.
        int n = 90;
        for (int i = 0; i <= n; i++) points.Add(new Point2(38e-3 * i / n, 0));
        for (int i = 0; i <= n; i++) points.Add(new Point2(38e-3 * (n - i) / n, 1e-3));
        for (int i = 0; i < points.Count; i++) constraints.Add((i, (i + 1) % points.Count));

        var cdt = new Cdt2D();
        // Either it recovers, or it throws the typed exception — but it must return.
        try { cdt.Triangulate(points, constraints); }
        catch (ConstraintRecoveryException) { /* bounded failure is acceptable; a hang is not */ }
    }
}

public class PlanarMesherTests
{
    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[]
        {
            new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1)
        });

    [Fact]
    public void RectangleMesh_CoversAreaAndRespectsMinAngle()
    {
        var region = new PlanarRegion(0, new[] { Rect(0, 0, 10e-3, 4e-3) });
        var mesh = new PlanarMesher().Mesh(new[] { region }, 1e-3);

        double area = mesh.Triangles.Sum(t =>
            Math.Abs(Point2.Cross(
                mesh.Points[t.B] - mesh.Points[t.A],
                mesh.Points[t.C] - mesh.Points[t.A])) / 2);
        Assert.Equal(10e-3 * 4e-3, area, 10e-3 * 4e-3 * 1e-3);
        Assert.All(mesh.Triangles, t => Assert.Equal(0, t.RegionId));

        // No slivers survive (degenerate boundary triangles are dropped), and the bulk
        // of the area is well-shaped.
        double minAngle = mesh.Triangles.Min(t => MinAngleDeg(mesh, t));
        Assert.True(minAngle > 8, $"Worst triangle angle {minAngle:f1}° indicates a sliver.");

        double wellShaped = mesh.Triangles.Where(t => MinAngleDeg(mesh, t) >= 20).Sum(t => Area(mesh, t));
        Assert.True(wellShaped / area > 0.97, "At least 97% of the area should be well-shaped (≥20°).");
    }

    [Fact]
    public void TwoRegions_ClassifyByPriority()
    {
        // Copper rectangle sitting inside a larger board; copper has priority.
        var copper = new PlanarRegion(0, new[] { Rect(2e-3, 1e-3, 8e-3, 3e-3) });
        var board = new PlanarRegion(1, new[] { Rect(0, 0, 10e-3, 4e-3) });
        var mesh = new PlanarMesher().Mesh(new[] { copper, board }, 0.8e-3);

        double copperArea = mesh.Triangles.Where(t => t.RegionId == 0).Sum(t => Area(mesh, t));
        double boardArea = mesh.Triangles.Where(t => t.RegionId == 1).Sum(t => Area(mesh, t));
        Assert.Equal(6e-3 * 2e-3, copperArea, 6e-3 * 2e-3 * 5e-3);
        Assert.Equal(10e-3 * 4e-3 - 6e-3 * 2e-3, boardArea, 10e-3 * 4e-3 * 5e-3);
    }

    private static double Area(PlanarMesh m, Tri2 t) =>
        Math.Abs(Point2.Cross(m.Points[t.B] - m.Points[t.A], m.Points[t.C] - m.Points[t.A])) / 2;

    private static double MinAngleDeg(PlanarMesh m, Tri2 t)
    {
        var a = m.Points[t.A]; var b = m.Points[t.B]; var c = m.Points[t.C];
        double la = (c - b).Length, lb = (a - c).Length, lc = (b - a).Length;
        double angA = Math.Acos(Math.Clamp((lb * lb + lc * lc - la * la) / (2 * lb * lc), -1, 1));
        double angB = Math.Acos(Math.Clamp((la * la + lc * lc - lb * lb) / (2 * la * lc), -1, 1));
        return Math.Min(angA, Math.Min(angB, Math.PI - angA - angB)) * 180 / Math.PI;
    }
}
