using OpenSim.Core.Model;
using OpenSim.Geometry;
using OpenSim.Geometry.Step;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Gates for deriving geometric edges and vertices from UNMESHED geometry.
///
/// The mesh-derived set (<see cref="BoundaryEdgeSetTests"/>) can only exist after meshing
/// and is renumbered by every remesh. These ids come from the tessellation the user
/// imported or created, so they are stable for the life of the geometry — which is what
/// lets a boundary condition name an edge before a mesh exists and keep meaning it
/// afterwards.
///
/// A primitive box is an exact fixture: its faces are exact planes and its edges are exact
/// lines, so every assertion here is an identity rather than a tolerance.
/// </summary>
public class GeometryEdgeSetTests
{
    private const double Lx = 0.200, Ly = 0.060, Lz = 0.020;

    private static TriangleMesh Beam() => PrimitiveFactory.CreateBox(Lx, Ly, Lz);

    [Fact]
    public void Box_HasTwelveEdges_FourOfEachSideLength()
    {
        var edges = Beam().FeatureEdges.Edges;

        Assert.Equal(12, edges.Count);
        Assert.Equal(4, edges.Count(e => Math.Abs(e.Length - Lx) < 1e-12));
        Assert.Equal(4, edges.Count(e => Math.Abs(e.Length - Ly) < 1e-12));
        Assert.Equal(4, edges.Count(e => Math.Abs(e.Length - Lz) < 1e-12));
    }

    [Fact]
    public void Box_HasEightVertices_EachOnThreeFaces()
    {
        var vertices = Beam().FeatureEdges.Vertices;

        Assert.Equal(8, vertices.Count);
        Assert.All(vertices, v => Assert.Equal(3, v.FaceIds.Count));
    }

    [Fact]
    public void Box_EdgesAreEveryNonOppositeFacePair_AndNoneAreNonManifold()
    {
        var set = Beam().FeatureEdges;
        var pairs = set.Edges.Select(e => (e.FaceA, e.FaceB)).ToHashSet();

        // All fifteen face pairs except the three opposite ones, which never meet.
        Assert.Equal(12, pairs.Count);
        Assert.DoesNotContain((0, 1), pairs);   // x-min / x-max
        Assert.DoesNotContain((2, 3), pairs);   // y-min / y-max
        Assert.DoesNotContain((4, 5), pairs);   // z-min / z-max
        Assert.Equal(0, set.NonManifoldEdgeCount);
    }

    /// <summary>
    /// The two supports of the Ansys beam reproduction, addressed on the raw geometry: the
    /// bottom face meeting each end face. Before this overload existed the user had to mesh
    /// first, and the answer changed every time they remeshed.
    /// </summary>
    [Fact]
    public void EdgesBetween_ResolvesTheBeamSupportEdges_BeforeAnyMeshExists()
    {
        var set = Beam().FeatureEdges;

        var atXMin = set.EdgesBetween(new[] { 2, 0 });
        var atXMax = set.EdgesBetween(new[] { 2, 1 });

        Assert.Single(atXMin);
        Assert.Single(atXMax);
        // Each support runs across the beam's thickness.
        Assert.Equal(Lz, set.EdgeById(atXMin[0])!.Length, 12);
        Assert.Equal(Lz, set.EdgeById(atXMax[0])!.Length, 12);
    }

    [Fact]
    public void GeometryEdgeIds_AreDeterministicAndOrderedByFacePairThenLowestVertex()
    {
        var geometry = Beam();
        var a = BoundaryEdgeSet.Extract(geometry).Edges;
        var b = BoundaryEdgeSet.Extract(geometry).Edges;

        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(i, a[i].Id);
            Assert.Equal(a[i].FaceA, b[i].FaceA);
            Assert.Equal(a[i].FaceB, b[i].FaceB);
            Assert.Equal(a[i].NodeIds, b[i].NodeIds);
            Assert.Equal(a[i].Length, b[i].Length);   // bitwise: same summation order
            if (i > 0)
                Assert.True(
                    a[i].FaceA > a[i - 1].FaceA ||
                    (a[i].FaceA == a[i - 1].FaceA && a[i].FaceB > a[i - 1].FaceB) ||
                    (a[i].FaceA == a[i - 1].FaceA && a[i].FaceB == a[i - 1].FaceB
                        && a[i].NodeIds[0] > a[i - 1].NodeIds[0]),
                    $"Edge {i} is out of order.");
        }
    }

    [Fact]
    public void FeatureEdges_AreCachedAcrossCalls()
    {
        var geometry = Beam();
        Assert.Same(geometry.FeatureEdges, geometry.FeatureEdges);
    }

    /// <summary>
    /// THE property the mesh-derived set cannot offer: meshing the body — twice, at
    /// different element sizes — leaves every geometric edge id, face pair and length
    /// bitwise identical, because they were never a function of the mesh.
    /// </summary>
    [Fact]
    public void GeometryEdgeIds_SurviveRemeshingAtADifferentElementSize()
    {
        var geometry = Beam();
        var before = geometry.FeatureEdges.Edges
            .Select(e => (e.Id, e.FaceA, e.FaceB, e.Length)).ToArray();

        var mesher = new DelaunayMeshGenerator();
        mesher.Generate(geometry, new MeshSettings { TargetEdgeLength = 0.010 });
        mesher.Generate(geometry, new MeshSettings { TargetEdgeLength = 0.008 });

        var after = geometry.FeatureEdges.Edges
            .Select(e => (e.Id, e.FaceA, e.FaceB, e.Length)).ToArray();

        Assert.Equal(before, after);
    }

    [Fact]
    public void Cylinder_HasExactlyItsTwoRims_EachAClosedLoop()
    {
        const int segments = 24;
        var set = PrimitiveFactory.CreateCylinder(0.01, 0.05, segments).FeatureEdges;

        Assert.Equal(2, set.Edges.Count);
        Assert.Equal(new[] { (0, 2), (1, 2) },
            set.Edges.Select(e => (e.FaceA, e.FaceB)).OrderBy(p => p.Item1).ToArray());
        // A closed ring visits as many segments as nodes; an open curve would have one fewer.
        Assert.All(set.Edges, e => Assert.Equal(e.NodeIds.Count, e.Segments.Count));
        Assert.All(set.Edges, e => Assert.Equal(segments, e.NodeIds.Count));
        // A tessellated circle is its inscribed polygon — the chord model, stated sharply.
        Assert.All(set.Edges, e =>
            Assert.Equal(2 * segments * 0.01 * Math.Sin(Math.PI / segments), e.Length, 12));
        // The rims are not vertices: only two faces meet on each, never three.
        Assert.Empty(set.Vertices);
    }

    /// <summary>
    /// A real CAD import. STEP face ids are the file's own faces and every EDGE_CURVE is
    /// sampled once with both adjacent faces consuming the identical points, so the feature
    /// edges fall out of the tessellation with no repair pass.
    /// </summary>
    [Fact]
    public void RealStepImport_YieldsStableFeatureEdgesOnItsNativeFaces()
    {
        string? path = Geometry.Step.Part21Tests.FindExampleStepFile();
        if (path is null) return;   // example not present in this checkout

        var geometry = new StepImporter().Import(path);
        var set = BoundaryEdgeSet.Extract(geometry);
        var again = BoundaryEdgeSet.Extract(geometry);

        Assert.NotEmpty(set.Edges);
        Assert.Equal(set.Edges.Count, again.Edges.Count);
        for (int i = 0; i < set.Edges.Count; i++)
        {
            Assert.Equal(set.Edges[i].FaceA, again.Edges[i].FaceA);
            Assert.Equal(set.Edges[i].FaceB, again.Edges[i].FaceB);
            Assert.Equal(set.Edges[i].Length, again.Edges[i].Length);
        }
        // Every edge node is a geometry vertex and every edge separates two distinct faces.
        Assert.All(set.Edges, e =>
        {
            Assert.All(e.NodeIds, n => Assert.InRange(n, 0, geometry.Vertices.Count - 1));
            Assert.NotEqual(e.FaceA, e.FaceB);
            Assert.True(e.Length > 0);
        });
    }
}
