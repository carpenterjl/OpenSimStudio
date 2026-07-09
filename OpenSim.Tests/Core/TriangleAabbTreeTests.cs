using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// The tree's contract is PURE PRUNING: for any ray, counting triangle intersections
/// over its candidate set must give exactly the brute-force count. These tests compare
/// counts (not just parity) so a dropped-and-compensating pair of crossings cannot hide.
/// </summary>
public class TriangleAabbTreeTests
{
    /// <summary>Same fixed irrational-ish directions SolidClassifier votes with.</summary>
    private static readonly Vector3D[] RayDirections =
    {
        new Vector3D(0.5773502691896258, 0.5773502691896257, 0.5773502691896259).Normalized(),
        new Vector3D(-0.2672612419124244, 0.5345224838248488, 0.8017837257372732).Normalized(),
        new Vector3D(0.8451542547285166, -0.1690308509457033, -0.5070925528371099).Normalized()
    };

    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    [InlineData("cylinder-refined")]
    public void CollectRayCandidates_CrossingCountsMatchBruteForce(string shape)
    {
        // Fixture sizes are budgeted against the O(probes · triangles) brute-force
        // reference loop, which must stay fast in Debug CI runs.
        var (mesh, grid) = shape switch
        {
            "box" => (PrimitiveFactory.CreateBox(1, 1, 1), 15),
            "cylinder" => (PrimitiveFactory.CreateCylinder(0.01, 0.05, 96), 15),
            _ => (SurfaceRefiner.Refine(PrimitiveFactory.CreateCylinder(0.01, 0.05, 96), 0.007), 9)
        };
        var triangles = mesh.Triangles.Select(t => (t.A, t.B, t.C)).ToList();
        var tree = new TriangleAabbTree(mesh.Vertices, triangles);

        var bounds = Aabb.FromPoints(mesh.Vertices);
        var expanded = bounds.Expanded(0.2 * bounds.Diagonal);
        var candidates = new List<int>();

        for (int ix = 0; ix < grid; ix++)
        for (int iy = 0; iy < grid; iy++)
        for (int iz = 0; iz < grid; iz++)
        {
            var origin = new Vector3D(
                expanded.Min.X + expanded.Size.X * ix / (grid - 1),
                expanded.Min.Y + expanded.Size.Y * iy / (grid - 1),
                expanded.Min.Z + expanded.Size.Z * iz / (grid - 1));

            foreach (var dir in RayDirections)
            {
                int brute = 0;
                foreach (var (a, b, c) in triangles)
                {
                    if (RayIntersectsTriangle(origin, dir,
                            mesh.Vertices[a], mesh.Vertices[b], mesh.Vertices[c]))
                        brute++;
                }

                candidates.Clear();
                tree.CollectRayCandidates(origin, dir, candidates);
                Assert.Equal(candidates.Count, candidates.Distinct().Count()); // each triangle at most once
                int pruned = 0;
                foreach (int t in candidates)
                {
                    var (a, b, c) = triangles[t];
                    if (RayIntersectsTriangle(origin, dir,
                            mesh.Vertices[a], mesh.Vertices[b], mesh.Vertices[c]))
                        pruned++;
                }

                Assert.Equal(brute, pruned);
            }
        }
    }

    [Fact]
    public void CollectRayCandidates_VisitsSmallFractionOfTriangles()
    {
        // Structural (not wall-clock) perf assertion: on a ~10k-triangle mesh the tree
        // must prune the vast majority of candidates or it is not doing its job.
        var mesh = SurfaceRefiner.Refine(PrimitiveFactory.CreateCylinder(0.01, 0.05, 96), 0.004);
        Assert.True(mesh.Triangles.Count > 5000, $"Fixture too small ({mesh.Triangles.Count} tris).");
        var tree = new TriangleAabbTree(mesh.Vertices, mesh.Triangles.Select(t => (t.A, t.B, t.C)).ToList());

        var bounds = Aabb.FromPoints(mesh.Vertices);
        var expanded = bounds.Expanded(0.1 * bounds.Diagonal);
        var candidates = new List<int>();
        long queries = 0;
        var rng = new Random(97);
        for (int i = 0; i < 2000; i++)
        {
            var origin = new Vector3D(
                expanded.Min.X + expanded.Size.X * rng.NextDouble(),
                expanded.Min.Y + expanded.Size.Y * rng.NextDouble(),
                expanded.Min.Z + expanded.Size.Z * rng.NextDouble());
            foreach (var dir in RayDirections)
            {
                candidates.Clear();
                tree.CollectRayCandidates(origin, dir, candidates);
                queries++;
            }
        }

        double averageFraction = (double)tree.CandidatesVisited / (queries * mesh.Triangles.Count);
        Assert.True(averageFraction < 0.20,
            $"Tree visited {averageFraction:P1} of triangles per query on average — pruning is broken.");
    }

    [Fact]
    public void SingleTriangle_AndAllInOneLeaf_Work()
    {
        var vertices = new List<Vector3D> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0) };
        var tree = new TriangleAabbTree(vertices, new List<(int, int, int)> { (0, 1, 2) });

        var candidates = new List<int>();
        tree.CollectRayCandidates(new Vector3D(0.2, 0.2, -1), new Vector3D(0, 0, 1), candidates);
        Assert.Equal(new[] { 0 }, candidates);

        candidates.Clear();
        tree.CollectRayCandidates(new Vector3D(5, 5, -1), new Vector3D(0, 0, 1), candidates);
        Assert.Empty(candidates);

        // Axis-parallel ray with zero direction components inside the box slab must not
        // produce NaN misses (the 0·∞ hazard the slab test guards against).
        candidates.Clear();
        tree.CollectRayCandidates(new Vector3D(0.2, 0.2, 0), new Vector3D(1, 0, 0), candidates);
        Assert.Single(candidates);
    }

    [Fact]
    public void EmptyTriangleList_ThrowsLoudly()
    {
        Assert.Throws<ArgumentException>(() =>
            new TriangleAabbTree(new List<Vector3D> { new(0, 0, 0) }, new List<(int, int, int)>()));
    }

    /// <summary>
    /// Brute-force reference copy of SolidClassifier's Möller–Trumbore test (same
    /// constants) so the parity comparison exercises the exact production predicate.
    /// </summary>
    private static bool RayIntersectsTriangle(Vector3D origin, Vector3D dir,
        Vector3D a, Vector3D b, Vector3D c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3D.Cross(dir, e2);
        double det = Vector3D.Dot(e1, p);
        if (Math.Abs(det) < 1e-14) return false;

        double invDet = 1.0 / det;
        var s = origin - a;
        double u = Vector3D.Dot(s, p) * invDet;
        if (u < 0 || u > 1) return false;

        var q = Vector3D.Cross(s, e1);
        double v = Vector3D.Dot(dir, q) * invDet;
        if (v < 0 || u + v > 1) return false;

        double t = Vector3D.Dot(e2, q) * invDet;
        return t > 1e-12;
    }
}
