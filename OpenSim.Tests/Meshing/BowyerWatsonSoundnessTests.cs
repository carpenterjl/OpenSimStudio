using OpenSim.Core.Numerics;
using OpenSim.Meshing;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// D20 (evaluation 2026-09-09, worked 2026-10-25): the Bowyer–Watson cavity heuristic could exit
/// with a tetrahedron the point lies on or beyond, <c>AddFiniteTet</c> re-oriented it silently and
/// <c>LinkFace</c> overwrote a face's second tetrahedron, so an overlap corrupted the adjacency
/// without a word. Now a cavity that cannot be made star-shaped leaves the point out and records it
/// (<see cref="BowyerWatson.SkippedPoints"/>), a flat tetrahedron or a third tetrahedron on a face
/// throws, and <see cref="BowyerWatson.Audit"/> checks a triangulation end to end. These tests drive
/// the degenerate placements the heuristic is built for — lattices (every cell's eight corners
/// cospherical), points on faces and edges, duplicates — and ask for a sound triangulation of the
/// hull every time.
/// </summary>
public class BowyerWatsonSoundnessTests
{
    private readonly ITestOutputHelper _out;
    public BowyerWatsonSoundnessTests(ITestOutputHelper output) => _out = output;

    private static List<Vector3D> UnitCubeCorners() => new()
    {
        new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(1, 1, 0), new(1, 0, 1), new(0, 1, 1), new(1, 1, 1)
    };

    private static double HullVolume(BowyerWatson bw, IReadOnlyList<Vector3D> points)
    {
        double volume = 0;
        foreach (var (a, b, c, d) in bw.FiniteTets())
        {
            double v6 = GeometricPredicates.Orient3D(points[a], points[b], points[c], points[d]);
            Assert.True(v6 > 0, "every returned tetrahedron is positively oriented");
            volume += v6 / 6.0;
        }
        return volume;
    }

    [Fact]
    public void RandomPoints_AuditClean_AndDelaunay()
    {
        var rng = new Random(7);
        var points = UnitCubeCorners();
        for (int i = 0; i < 300; i++) points.Add(new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()));
        var bw = new BowyerWatson();
        bw.Triangulate(points);
        Assert.Null(bw.Audit(checkDelaunay: true));
        Assert.Empty(bw.SkippedPoints);
        Assert.Equal(1.0, HullVolume(bw, points), 9);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void ALattice_EveryCellCospherical_IsTriangulatedSoundly(int n)
    {
        // n³ points on a regular lattice: every cube cell's eight corners lie on one sphere, so the
        // in-sphere test ties (returns 0) everywhere and the cavities are as degenerate as they come.
        var points = new List<Vector3D>();
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int k = 0; k < n; k++)
                    points.Add(new Vector3D(i / (n - 1.0), j / (n - 1.0), k / (n - 1.0)));
        var bw = new BowyerWatson();
        bw.Triangulate(points);
        _out.WriteLine($"{n}³ lattice: {bw.FiniteTets().Count} tetrahedra, {bw.SkippedPoints.Count} point(s) skipped");
        foreach (var s in bw.SkippedPoints) _out.WriteLine($"  skipped {s.Index} at ({s.Position.X}, {s.Position.Y}, {s.Position.Z}): {s.Reason}");
        Assert.Null(bw.Audit());
        Assert.Equal(1.0, HullVolume(bw, points), 9);          // the hull is tiled whether or not a point was left out
        // A lattice point left out would be a hole in the FE node set: none may be.
        Assert.Empty(bw.SkippedPoints);
    }

    [Fact]
    public void PointsOnFacesEdgesAndVertices_AreSoundOrLeftOutAndSaid()
    {
        var rng = new Random(19);
        var points = UnitCubeCorners();
        for (int i = 0; i < 60; i++) points.Add(new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()));
        var bw = new BowyerWatson();
        bw.Triangulate(points);
        Assert.Null(bw.Audit(checkDelaunay: true));

        // Points exactly on existing faces, edges and vertices of the current triangulation, and
        // on the hull's faces: each is either inserted into a sound triangulation or reported.
        var tets = bw.FiniteTets();
        int inserted = 0, duplicates = 0;
        for (int t = 0; t < tets.Count; t += 7)
        {
            var (a, b, c, d) = tets[t];
            var candidates = new[]
            {
                (points[a] + points[b] + points[c]) / 3.0,                // on a face
                (points[a] + points[d]) / 2.0,                            // on an edge
                points[b],                                                // a vertex again
            };
            foreach (var p in candidates)
            {
                points.Add(p);
                int before = bw.SkippedPoints.Count;
                bool ok = bw.InsertPoint(p);
                if (ok) inserted++;
                else if (bw.SkippedPoints.Count == before) duplicates++;
                Assert.Null(bw.Audit());
            }
        }
        foreach (var hullPoint in new[] { new Vector3D(0.5, 0.5, 0), new Vector3D(1, 0.25, 0.75), new Vector3D(0.3, 1, 0.3) })
        {
            points.Add(hullPoint);
            bw.InsertPoint(hullPoint);
            Assert.Null(bw.Audit());
        }
        _out.WriteLine($"{inserted} inserted, {duplicates} duplicates, {bw.SkippedPoints.Count} skipped");
        Assert.Null(bw.Audit(checkDelaunay: true));
        Assert.Equal(1.0, HullVolume(bw, points), 9);
        Assert.True(duplicates > 0, "re-inserting a vertex is reported as a duplicate (false, not skipped)");
        Assert.True(inserted >= 60, $"the face and edge points are inserted, not skipped ({inserted} inserted)");
        Assert.Empty(bw.SkippedPoints);
    }

    [Fact]
    public void AFarPoint_BeyondASmallHullFacet_IsNotReadAsCoplanar()
    {
        // The review's case (2026-10-25): a point far from a small facet, just beyond its plane.
        // A tolerance that grew with the point's distance read it as coplanar, left the facet in
        // place and the hull non-convex. The audit's hull checks must see such a hull, and the
        // builder must not make one.
        var points = new List<Vector3D> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, -1), new(1000, 1000, 1e-3) };
        var bw = new BowyerWatson();
        bw.Triangulate(points);
        Assert.Null(bw.Audit(checkDelaunay: true));                    // the audit now checks the hull's winding and convexity
        Assert.Empty(bw.SkippedPoints);
        Assert.Contains(bw.FiniteTets(), t => t.Item1 == 4 || t.Item2 == 4 || t.Item3 == 4 || t.Item4 == 4);   // the far point is in
        Assert.True(HullVolume(bw, points) > 1.0 / 6, "the hull grew past the seed tetrahedron");
    }

    [Fact]
    public void CubePlusCentre_IsTwelveTets_AndTheAuditSeesAnInvertedOne()
    {
        var points = UnitCubeCorners();
        points.Add(new Vector3D(0.5, 0.5, 0.5));
        var bw = new BowyerWatson();
        bw.Triangulate(points);
        Assert.Null(bw.Audit(checkDelaunay: true));
        Assert.Equal(1.0, HullVolume(bw, points), 12);
        Assert.Equal(12, bw.FiniteTets().Count);                        // the centre splits the cube into 12 tets

        // The audit is the net under the rest: a hand-made defect must not pass it.
        int index = 0;
        while (!bw.InvertForAudit(index)) index++;                      // the first live finite tet
        var verdict = bw.Audit();
        Assert.NotNull(verdict);
        Assert.Contains("flat or inverted", verdict);
        bw.InvertForAudit(index);                                        // swap it back: sound again
        Assert.Null(bw.Audit(checkDelaunay: true));
    }
}
