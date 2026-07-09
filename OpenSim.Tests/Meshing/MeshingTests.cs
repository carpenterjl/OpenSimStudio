using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Meshing;

public class GeometricPredicatesTests
{
    [Fact]
    public void InSphere_UnitTet_SignConvention()
    {
        var a = new Vector3D(0, 0, 0);
        var b = new Vector3D(1, 0, 0);
        var c = new Vector3D(0, 1, 0);
        var d = new Vector3D(0, 0, 1);
        Assert.True(GeometricPredicates.Orient3D(a, b, c, d) > 0);

        // Centroid is inside the circumsphere; a far point is outside.
        Assert.True(GeometricPredicates.InSphere(a, b, c, d, new Vector3D(0.25, 0.25, 0.25)) > 0);
        Assert.True(GeometricPredicates.InSphere(a, b, c, d, new Vector3D(10, 10, 10)) < 0);
    }
}

public class BowyerWatsonTests
{
    [Fact]
    public void Triangulate_RandomPoints_FillsConvexHullVolume()
    {
        var rng = new Random(11);
        var points = new List<Vector3D>
        {
            // Include the unit cube corners so the hull volume is exactly 1.
            new(0,0,0), new(1,0,0), new(0,1,0), new(0,0,1),
            new(1,1,0), new(1,0,1), new(0,1,1), new(1,1,1)
        };
        for (int i = 0; i < 200; i++)
            points.Add(new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()));

        var tets = new BowyerWatson().Triangulate(points);

        double volume = 0;
        foreach (var (a, b, c, d) in tets)
        {
            double v6 = GeometricPredicates.Orient3D(points[a], points[b], points[c], points[d]);
            Assert.True(v6 > 0, "Every returned tetrahedron must be positively oriented.");
            volume += v6 / 6.0;
        }
        // A Delaunay triangulation tiles the convex hull exactly.
        Assert.Equal(1.0, volume, 6);
    }

    [Fact]
    public void InsertPoint_Incremental_KeepsHullVolumeExact()
    {
        // The refiner inserts Steiner points after bulk construction; every insertion
        // must re-establish a valid space-filling Delaunay triangulation of the hull.
        var rng = new Random(23);
        var points = new List<Vector3D>
        {
            new(0,0,0), new(1,0,0), new(0,1,0), new(0,0,1),
            new(1,1,0), new(1,0,1), new(0,1,1), new(1,1,1)
        };
        for (int i = 0; i < 100; i++)
            points.Add(new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()));

        var bw = new BowyerWatson();
        bw.Triangulate(points);
        for (int i = 0; i < 50; i++)
        {
            var p = new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble());
            points.Add(p);                    // caller keeps index parity
            bw.InsertPoint(p);
        }

        double volume = 0;
        foreach (var (a, b, c, d) in bw.FiniteTets())
        {
            double v6 = GeometricPredicates.Orient3D(points[a], points[b], points[c], points[d]);
            Assert.True(v6 > 0, "Every tetrahedron must stay positively oriented after insertion.");
            volume += v6 / 6.0;
        }
        Assert.Equal(1.0, volume, 6);

        // A duplicate point conflicts with nothing and is reported skipped.
        Assert.False(bw.InsertPoint(points[0]));
    }
}

public class SolidClassifierTests
{
    [Fact]
    public void IsInside_BoxProbes()
    {
        var box = PrimitiveFactory.CreateBox(1, 1, 1);
        var classifier = new SolidClassifier(box);
        Assert.True(classifier.IsInside(new Vector3D(0.5, 0.5, 0.5)));
        Assert.True(classifier.IsInside(new Vector3D(0.01, 0.99, 0.5)));
        Assert.False(classifier.IsInside(new Vector3D(1.5, 0.5, 0.5)));
        Assert.False(classifier.IsInside(new Vector3D(-0.01, 0.5, 0.5)));
    }
}

public class DelaunayMeshGeneratorTests
{
    private static void AssertBoundaryIsClosed(FeMesh mesh)
    {
        // Every boundary edge must be shared by exactly two boundary triangles.
        var edgeCount = new Dictionary<(int, int), int>();
        void Count(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            edgeCount[key] = edgeCount.GetValueOrDefault(key) + 1;
        }
        foreach (var bt in mesh.BoundaryTriangles)
        {
            Count(bt.A, bt.B); Count(bt.B, bt.C); Count(bt.C, bt.A);
        }
        Assert.All(edgeCount.Values, c => Assert.Equal(2, c));
    }

    [Fact]
    public void Generate_UnitCube_ProducesValidConformingMesh()
    {
        var box = PrimitiveFactory.CreateBox(1, 1, 1);
        var mesh = new DelaunayMeshGenerator().Generate(box, new MeshSettings { TargetEdgeLength = 0.25 });

        Assert.True(mesh.ElementCount > 50);
        for (int i = 0; i < mesh.ElementCount; i++)
            Assert.True(mesh.ElementVolume(i) > 0, $"Element {i} has non-positive volume.");

        // Volume within 2% of analytic (jitter and boundary faceting cost a little).
        Assert.InRange(mesh.TotalVolume(), 0.98, 1.02);

        AssertBoundaryIsClosed(mesh);

        // All six geometric faces must be represented and carry nodes.
        var faceIds = mesh.BoundaryTriangles.Select(bt => bt.FaceId).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, faceIds);
        foreach (int face in faceIds)
            Assert.NotEmpty(mesh.GetFaceNodes(new[] { face }));

        var stats = MeshQuality.Compute(mesh);
        // Quality-driven refinement drives kept tets toward TargetMinQuality (0.08);
        // 0.02 matches the near-surface sliver cull, so the whole pipeline is
        // self-consistent — anything below it would be a refinement regression.
        Assert.True(stats.MinQuality > 0.02, $"Minimum element quality too low: {stats.MinQuality:g3}");
        Assert.True(stats.AverageQuality > 0.5, $"Average element quality too low: {stats.AverageQuality:g3}");
    }

    [Fact]
    public void Generate_Cylinder_VolumeNearAnalytic()
    {
        var cyl = PrimitiveFactory.CreateCylinder(0.5, 1.0, 48);
        var mesh = new DelaunayMeshGenerator().Generate(cyl, new MeshSettings { TargetEdgeLength = 0.15 });

        double analytic = Math.PI * 0.25 * 1.0;
        Assert.InRange(mesh.TotalVolume(), analytic * 0.95, analytic * 1.02);
        AssertBoundaryIsClosed(mesh);
        Assert.True(MeshQuality.Compute(mesh).MinQuality > 0.02);

        // Bottom cap (0), top cap (1) and lateral surface (2) all present.
        var faceIds = mesh.BoundaryTriangles.Select(bt => bt.FaceId).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, faceIds);
    }

    [Fact]
    public void Refinement_InsertsInteriorPoints_AndReducesBadElements()
    {
        var box = PrimitiveFactory.CreateBox(1, 1, 1);

        var unrefined = new DelaunayMeshGenerator().Generate(box,
            new MeshSettings { TargetEdgeLength = 0.25, TargetMinQuality = 0 });
        var refined = new DelaunayMeshGenerator().Generate(box,
            new MeshSettings { TargetEdgeLength = 0.25 });

        // Refinement inserted interior Steiner points and lowered the count of
        // below-target elements (the sliver floor itself is owned by the cull and
        // asserted in the conforming-mesh test).
        Assert.True(refined.NodeCount > unrefined.NodeCount,
            $"Refinement inserted no points ({unrefined.NodeCount} → {refined.NodeCount}).");
        Assert.True(CountBelow(refined, 0.08) < CountBelow(unrefined, 0.08),
            $"Refinement must reduce below-target elements " +
            $"({CountBelow(unrefined, 0.08)} → {CountBelow(refined, 0.08)}).");

        // A tiny budget must still terminate and produce a valid, closed mesh.
        var budgeted = new DelaunayMeshGenerator().Generate(box,
            new MeshSettings { TargetEdgeLength = 0.25, MaxRefinementPoints = 5 });
        Assert.InRange(budgeted.TotalVolume(), 0.98, 1.02);
        AssertBoundaryIsClosed(budgeted);
    }

    private static int CountBelow(FeMesh mesh, double quality)
    {
        int count = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            if (MeshQuality.RadiusRatio(mesh.Nodes[el.N0], mesh.Nodes[el.N1],
                    mesh.Nodes[el.N2], mesh.Nodes[el.N3]) < quality)
                count++;
        }
        return count;
    }

    [Fact]
    public void Generate_OpenSurface_ThrowsActionableError()
    {
        // A single triangle is not a closed solid.
        var open = new TriangleMesh(
            new List<Vector3D> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) },
            new List<Triangle> { new(0, 1, 2) },
            new[] { 0 });
        Assert.Throws<InvalidOperationException>(() =>
            new DelaunayMeshGenerator().Generate(open, new MeshSettings()));
    }
}
