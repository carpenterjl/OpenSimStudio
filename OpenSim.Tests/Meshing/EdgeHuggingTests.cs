using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// The Delaunay mesher's feature-edge contract: a node that ends up on a geometric edge
/// lies EXACTLY on it, not within a jitter amplitude of it.
///
/// This is what makes a support scoped to an edge the fixed line the user drew. Before it,
/// the mesher's deliberate sub-element jitter left those nodes scattered around the line —
/// harmless for a face-scoped condition, worth several percent of deflection for one
/// scoped to a line, which is exactly what the Ansys beam reproduction measured.
///
/// The claim is exactness, so the assertions are at 1e-12 of the model size. A band would
/// prove nothing: the jittered mesher already lands within 0.2% of an element edge.
/// </summary>
public class EdgeHuggingTests
{
    private const double Lx = 0.200, Ly = 0.060, Lz = 0.020;

    private static FeMesh MeshBox(double h, ElementOrder order = ElementOrder.Linear) =>
        new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Lx, Ly, Lz),
            new MeshSettings { TargetEdgeLength = h, ElementOrder = order });

    /// <summary>The coordinate a box face pins, by its face id.</summary>
    private static (int Axis, double Value) PlaneOf(int faceId) => faceId switch
    {
        0 => (0, 0.0), 1 => (0, Lx),
        2 => (1, 0.0), 3 => (1, Ly),
        4 => (2, 0.0), _ => (2, Lz)
    };

    private static double Component(Vector3D v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    /// <summary>
    /// THE gate. Every node of every geometric edge sits exactly on the line where its two
    /// faces meet — both pinned coordinates, on all twelve edges.
    /// </summary>
    [Fact]
    public void EveryEdgeNode_LiesExactlyOnTheLineWhereItsTwoFacesMeet()
    {
        var mesh = MeshBox(0.012);
        double tolerance = 1e-12 * new Vector3D(Lx, Ly, Lz).Length;

        Assert.Equal(12, mesh.Edges.Edges.Count);
        foreach (var edge in mesh.Edges.Edges)
        {
            var a = PlaneOf(edge.FaceA);
            var b = PlaneOf(edge.FaceB);
            foreach (int n in edge.NodeIds)
            {
                Assert.True(Math.Abs(Component(mesh.Nodes[n], a.Axis) - a.Value) <= tolerance,
                    $"Edge {edge.Id} node {n} is off face {edge.FaceA}: {mesh.Nodes[n]}");
                Assert.True(Math.Abs(Component(mesh.Nodes[n], b.Axis) - b.Value) <= tolerance,
                    $"Edge {edge.Id} node {n} is off face {edge.FaceB}: {mesh.Nodes[n]}");
            }
        }
    }

    /// <summary>
    /// The eight corners are carried through bit for bit — they are the one place three
    /// faces have to agree, and the endpoints of every support edge.
    /// </summary>
    [Fact]
    public void EveryBoxCorner_IsAMeshNode_Bitwise()
    {
        var mesh = MeshBox(0.012);
        var nodes = mesh.Nodes.ToHashSet();

        foreach (var corner in PrimitiveFactory.CreateBox(Lx, Ly, Lz).Vertices)
            Assert.Contains(corner, nodes);

        Assert.Equal(8, mesh.Edges.Vertices.Count);
        Assert.All(mesh.Edges.Vertices, v => Assert.Contains(mesh.Nodes[v.NodeId],
            PrimitiveFactory.CreateBox(Lx, Ly, Lz).Vertices.ToHashSet()));
    }

    [Fact]
    public void HuggingDoesNotPinchTheSkin_OrInvertAnyElement()
    {
        foreach (double h in new[] { 0.020, 0.012, 0.008 })
        {
            var mesh = MeshBox(h);
            Assert.Equal(0, mesh.Edges.NonManifoldEdgeCount);
            for (int e = 0; e < mesh.ElementCount; e++)
                Assert.True(mesh.ElementVolume(e) > 0,
                    $"h = {h}: element {e} inverted, volume {mesh.ElementVolume(e):g3}.");
            // The snap must not dent the surface either.
            Assert.InRange(mesh.TotalVolume() / (Lx * Ly * Lz), 0.98, 1.02);
        }
    }

    /// <summary>
    /// Quadratic meshes follow: a support pins mid-edge nodes through the edge's segments,
    /// and a mid-edge node of two exact corner nodes is their exact midpoint, so it is on
    /// the line too.
    /// </summary>
    [Fact]
    public void MidEdgeNodesOfAnEdgeScope_AreAlsoExactlyOnTheLine()
    {
        var mesh = MeshBox(0.012, ElementOrder.Quadratic);
        double tolerance = 1e-12 * new Vector3D(Lx, Ly, Lz).Length;

        var support = mesh.Edges.EdgesBetween(new[] { 2, 0 });
        Assert.Single(support);
        var condition = new FixedSupport
        {
            Name = "s", FaceIds = Array.Empty<int>(), EdgeIds = new[] { support[0] }
        };

        var midMap = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);
        var pinned = mesh.GetScopeNodes(condition).ToHashSet();
        foreach (var segment in mesh.GetScopeSegments(condition))
        {
            int mid = midMap[(Math.Min(segment.A, segment.B), Math.Max(segment.A, segment.B))];
            pinned.Add(mid);
        }

        Assert.True(pinned.Count > mesh.Edges.EdgeById(support[0])!.NodeIds.Count,
            "The quadratic scope should add mid-edge nodes.");
        foreach (int n in pinned)
        {
            Assert.True(Math.Abs(mesh.Nodes[n].X) <= tolerance, $"node {n} off x = 0: {mesh.Nodes[n]}");
            Assert.True(Math.Abs(mesh.Nodes[n].Y) <= tolerance, $"node {n} off y = 0: {mesh.Nodes[n]}");
        }
    }

    /// <summary>
    /// A curved edge is hugged to the model the tessellation already carries: a rim node
    /// lies exactly on the CHORD polyline the geometry is made of, not on the ideal circle.
    /// The same discretization model the STEP benchmarks are written against, stated
    /// sharply rather than hidden inside a tolerance.
    /// </summary>
    [Fact]
    public void CurvedRimNodes_LieExactlyOnTheTessellatedPolyline_TheStatedChordModel()
    {
        const double radius = 0.01, height = 0.04;
        var geometry = PrimitiveFactory.CreateCylinder(radius, height, 32);
        var mesh = new DelaunayMeshGenerator().Generate(
            geometry, new MeshSettings { TargetEdgeLength = 0.004 });
        double tolerance = 1e-12 * geometry.Bounds.Diagonal;

        var rims = geometry.FeatureEdges.Edges;
        Assert.Equal(2, rims.Count);

        foreach (var meshEdge in mesh.Edges.Edges)
        {
            // The geometry rim on the same face pair.
            var rim = rims.Single(r => r.FaceA == meshEdge.FaceA && r.FaceB == meshEdge.FaceB);
            foreach (int n in meshEdge.NodeIds)
            {
                double distance = rim.Segments.Min(s => SegmentDistance.PointToSegment(
                    mesh.Nodes[n], geometry.Vertices[s.A], geometry.Vertices[s.B]));
                Assert.True(distance <= tolerance,
                    $"Rim node {n} is {distance:g3} m off the tessellated rim.");
            }
        }
    }

    /// <summary>
    /// Geometry with no feature edges at all — one face id over the whole surface, which is
    /// what a raw STL or a PCB net looks like — still meshes. It takes the seeding path
    /// unchanged from before edge hugging existed, so this is the pin that says the new
    /// code cannot have broken it.
    /// </summary>
    [Fact]
    public void GeometryWithNoFeatureEdges_MeshesExactlyAsItAlwaysDid()
    {
        var box = PrimitiveFactory.CreateBox(Lx, Ly, Lz);
        var single = new TriangleMesh(box.Vertices, box.Triangles,
            Enumerable.Repeat(0, box.Triangles.Count).ToArray());

        Assert.Empty(single.FeatureEdges.Edges);
        Assert.Empty(single.FeatureEdges.Vertices);

        var mesh = new DelaunayMeshGenerator().Generate(
            single, new MeshSettings { TargetEdgeLength = 0.012 });

        Assert.Empty(mesh.Edges.Edges);
        Assert.InRange(mesh.TotalVolume() / (Lx * Ly * Lz), 0.98, 1.02);
        Assert.True(MeshQuality.Compute(mesh).MinQuality > 0.02);
    }

    /// <summary>Mesh quality must survive the hugging: the seeded edge samples and the
    /// crease clearance change the point set, and a mesher that hugged edges by wrecking
    /// its elements would have traded one kind of accuracy for another.</summary>
    [Fact]
    public void HuggedMeshes_KeepTheDocumentedQualityFloor()
    {
        foreach (double h in new[] { 0.020, 0.012 })
        {
            var stats = MeshQuality.Compute(MeshBox(h));
            Assert.True(stats.MinQuality > 0.02, $"h = {h}: min quality {stats.MinQuality:g3}.");
        }
    }
}
