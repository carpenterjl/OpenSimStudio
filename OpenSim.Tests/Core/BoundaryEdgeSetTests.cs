using OpenSim.Core.Model;
using OpenSim.Tests.Solvers;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Gates for deriving geometric edges and vertices from a meshed skin.
///
/// The fixture is the test-local STRUCTURED box, not the production mesher, for the same
/// reason the contact gates use it: the mesher jitters its surface points, so its box faces
/// are not exact planes and an edge length could only be checked to a couple of percent.
/// Here the box faces are exactly planar and every assertion below is an identity.
/// </summary>
public class BoundaryEdgeSetTests
{
    private static FeMesh Box(int n = 2) => StructuredBoxMesh.Build(0, 1, 0, 2, 0, 3, n, n, n);

    [Fact]
    public void Box_HasTwelveEdges_FourOfEachSideLength()
    {
        var edges = BoundaryEdgeSet.Extract(Box()).Edges;

        Assert.Equal(12, edges.Count);
        // Every edge runs along the one axis both of its faces contain, so its length is
        // exactly that side of the box — the grid coordinates sum back to it bitwise.
        Assert.Equal(4, edges.Count(e => Math.Abs(e.Length - 1) < 1e-12));
        Assert.Equal(4, edges.Count(e => Math.Abs(e.Length - 2) < 1e-12));
        Assert.Equal(4, edges.Count(e => Math.Abs(e.Length - 3) < 1e-12));
    }

    [Fact]
    public void Box_EdgesAreEveryNonOppositeFacePair()
    {
        var edges = BoundaryEdgeSet.Extract(Box()).Edges;
        var pairs = edges.Select(e => (e.FaceA, e.FaceB)).ToHashSet();

        // Twelve pairs: all fifteen face pairs except the three opposite ones, which never meet.
        Assert.Equal(12, pairs.Count);
        Assert.DoesNotContain((StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceXMax), pairs);
        Assert.DoesNotContain((StructuredBoxMesh.FaceYMin, StructuredBoxMesh.FaceYMax), pairs);
        Assert.DoesNotContain((StructuredBoxMesh.FaceZMin, StructuredBoxMesh.FaceZMax), pairs);
        Assert.Contains((StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceYMin), pairs);
    }

    [Fact]
    public void Box_HasEightVertices_EachOnThreeFaces()
    {
        var vertices = BoundaryEdgeSet.Extract(Box()).Vertices;

        Assert.Equal(8, vertices.Count);
        Assert.All(vertices, v => Assert.Equal(3, v.FaceIds.Count));
    }

    [Fact]
    public void Box_HasNoNonManifoldEdges()
    {
        Assert.Equal(0, BoundaryEdgeSet.Extract(Box()).NonManifoldEdgeCount);
    }

    [Fact]
    public void EdgeIds_AreOrderedByFacePairThenLowestNode_AndDeterministic()
    {
        var mesh = Box(3);
        var a = BoundaryEdgeSet.Extract(mesh).Edges;
        var b = BoundaryEdgeSet.Extract(mesh).Edges;

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

    /// <summary>
    /// THE reason an edge is a connected component and not just a face pair. Tag the top
    /// and bottom of a box as one face and the four sides as another: that single pair now
    /// meets along TWO disjoint rims. One id for the pair would silently constrain both
    /// when the user picked one.
    /// </summary>
    [Fact]
    public void OneFacePairMeetingAlongTwoRims_YieldsTwoEdges()
    {
        var box = Box();
        var relabelled = box.BoundaryTriangles
            .Select(t => new BoundaryTriangle(t.A, t.B, t.C,
                t.FaceId is StructuredBoxMesh.FaceZMin or StructuredBoxMesh.FaceZMax ? 1 : 0))
            .ToList();
        var mesh = new FeMesh(box.Nodes, box.Elements, relabelled);

        var edges = BoundaryEdgeSet.Extract(mesh).Edges;

        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal((0, 1), (e.FaceA, e.FaceB)));
        // Each rim is the full perimeter of the 1 x 2 cross-section.
        Assert.All(edges, e => Assert.Equal(6.0, e.Length, 12));
        // Disjoint: the two rims share no node.
        Assert.Empty(edges[0].NodeIds.Intersect(edges[1].NodeIds));
    }

    [Fact]
    public void EdgesBetween_ReturnsOnlyTheEdgeSharedByTheSelectedFaces()
    {
        var mesh = Box();
        var set = BoundaryEdgeSet.Extract(mesh);

        var shared = set.EdgesBetween(new[] { StructuredBoxMesh.FaceZMin, StructuredBoxMesh.FaceXMin });

        Assert.Single(shared);
        var edge = set.EdgeById(shared[0])!;
        Assert.Equal((StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceZMin), (edge.FaceA, edge.FaceB));
        Assert.Equal(2.0, edge.Length, 12);   // runs along y
    }

    [Fact]
    public void EdgesBetween_ThreeFacesOfACorner_ReturnsTheirThreeEdges()
    {
        var mesh = Box();
        var shared = BoundaryEdgeSet.Extract(mesh).EdgesBetween(
            new[] { StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceYMin, StructuredBoxMesh.FaceZMin });

        Assert.Equal(3, shared.Count);
    }

    [Fact]
    public void GetEdgeNodes_MatchesTheEdgeOwnNodeList_AndUnknownIdsResolveToNothing()
    {
        var mesh = Box();
        var edge = mesh.Edges.Edges[0];

        Assert.Equal(edge.NodeIds.ToHashSet(), mesh.GetEdgeNodes(new[] { edge.Id }).ToHashSet());
        Assert.Empty(mesh.GetEdgeNodes(new[] { 9999 }));
    }

    [Fact]
    public void FeMeshEdges_AreCachedAcrossCalls()
    {
        var mesh = Box();
        Assert.Same(mesh.Edges, mesh.Edges);
    }

    /// <summary>
    /// The back-compatibility pin: a condition that names no edges or vertices resolves to
    /// exactly the face node set it always did, so every pre-existing solve is untouched.
    /// </summary>
    [Fact]
    public void ScopeWithoutEdgesOrVertices_IsExactlyTheFaceNodeSet()
    {
        var mesh = Box();
        var faces = new[] { StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceZMax };
        var condition = new FixedSupport { Name = "s", FaceIds = faces };

        Assert.Null(condition.EdgeIds);
        Assert.Equal(mesh.GetFaceNodes(faces).ToHashSet(), mesh.GetScopeNodes(condition).ToHashSet());
    }

    [Fact]
    public void EdgeScope_ResolvesToFewerNodesThanEitherAdjacentFace()
    {
        var mesh = Box(4);
        var edge = mesh.Edges.EdgesBetween(
            new[] { StructuredBoxMesh.FaceXMin, StructuredBoxMesh.FaceZMin })[0];
        var condition = new FixedSupport { Name = "s", FaceIds = Array.Empty<int>(), EdgeIds = new[] { edge } };

        int edgeNodes = mesh.GetScopeNodes(condition).Count;

        Assert.Equal(5, edgeNodes);                                    // a 4-division edge has 5 nodes
        Assert.True(edgeNodes < mesh.GetFaceNodes(new[] { StructuredBoxMesh.FaceXMin }).Count);
    }

    [Fact]
    public void VertexScope_ResolvesToASingleNode()
    {
        var mesh = Box();
        var condition = new FixedSupport
        {
            Name = "v", FaceIds = Array.Empty<int>(), VertexIds = new[] { mesh.Edges.Vertices[0].Id }
        };

        Assert.Single(mesh.GetScopeNodes(condition));
    }

    [Fact]
    public void GetScopeSegments_OfAnEdge_AreExactlyThatEdgeSegments()
    {
        var mesh = Box();
        var edge = mesh.Edges.Edges[0];
        var condition = new FixedSupport
        {
            Name = "s", FaceIds = Array.Empty<int>(), EdgeIds = new[] { edge.Id }
        };

        Assert.Equal(edge.Segments, mesh.GetScopeSegments(condition));
    }
}
