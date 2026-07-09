using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Core;

public class QuadraticMeshTests
{
    private static FeMesh LinearBox() => new DelaunayMeshGenerator().Generate(
        PrimitiveFactory.CreateBox(0.05, 0.02, 0.01), new MeshSettings { TargetEdgeLength = 0.008 });

    [Fact]
    public void Upgrade_AddsOneExactMidpointPerUniqueEdge_SharedBetweenElements()
    {
        var linear = LinearBox();
        var quadratic = QuadraticMeshBuilder.Upgrade(linear);

        // Count unique corner edges.
        var edges = new HashSet<(int, int)>();
        foreach (var e in linear.Elements)
        {
            void Add(int a, int b) => edges.Add(a < b ? (a, b) : (b, a));
            Add(e.N0, e.N1); Add(e.N0, e.N2); Add(e.N0, e.N3);
            Add(e.N1, e.N2); Add(e.N1, e.N3); Add(e.N2, e.N3);
        }

        Assert.True(quadratic.IsQuadratic);
        Assert.Equal(linear.NodeCount + edges.Count, quadratic.NodeCount);
        Assert.Equal(linear.ElementCount, quadratic.ElementCount);

        // Corners, boundary triangles, and element volumes are untouched (straight edges).
        Assert.Equal(linear.Elements, quadratic.Elements);
        Assert.Equal(linear.BoundaryTriangles, quadratic.BoundaryTriangles);
        for (int i = 0; i < linear.NodeCount; i++)
            Assert.Equal(linear.Nodes[i], quadratic.Nodes[i]);
        Assert.Equal(linear.TotalVolume(), quadratic.TotalVolume(), 15);

        // Every mid node sits exactly on its edge midpoint, and shared edges share it.
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(quadratic);
        Assert.Equal(edges.Count, edgeMid.Count);
        foreach (var ((a, b), mid) in edgeMid)
        {
            var expected = (quadratic.Nodes[a] + quadratic.Nodes[b]) / 2.0;
            Assert.Equal(0, Vector3D.Distance(expected, quadratic.Nodes[mid]), 15);
        }

        // Every boundary-triangle edge is a tet edge, so BC application never misses.
        foreach (var t in quadratic.BoundaryTriangles)
        {
            Assert.True(edgeMid.ContainsKey(Key(t.A, t.B)));
            Assert.True(edgeMid.ContainsKey(Key(t.B, t.C)));
            Assert.True(edgeMid.ContainsKey(Key(t.C, t.A)));
        }

        // GetElementNodes exposes 10 nodes per element in corner-then-mid order.
        var nodes = quadratic.GetElementNodes(0);
        Assert.Equal(10, nodes.Length);
        Assert.Equal(quadratic.Elements[0].N0, nodes[0]);
        Assert.Equal(quadratic.MidEdgeNodes![0].M01, nodes[4]);
    }

    [Fact]
    public void QuadraticMesh_RoundTrips_AndOldLinearJsonLoadsAsLinear()
    {
        var quadratic = QuadraticMeshBuilder.Upgrade(LinearBox());
        var project = new SimProject { Name = "Quadratic" };
        project.Bodies.Add(new Body { Name = "Body", Mesh = quadratic });

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = Assert.Single(serializer.Load(path).Bodies).Mesh!;

            Assert.True(loaded.IsQuadratic);
            Assert.Equal(quadratic.MidEdgeNodes, loaded.MidEdgeNodes);
            Assert.Equal(quadratic.NodeCount, loaded.NodeCount);
        }
        finally
        {
            File.Delete(path);
        }

        // A pre-TET10 mesh payload has no midEdgeNodes property → linear.
        const string oldJson = """
        {
            "nodes": [ {"x":0,"y":0,"z":0}, {"x":1,"y":0,"z":0}, {"x":0,"y":1,"z":0}, {"x":0,"y":0,"z":1} ],
            "elements": [ {"n0":0,"n1":1,"n2":2,"n3":3} ],
            "boundaryTriangles": [ {"a":0,"b":2,"c":1,"faceId":0} ]
        }
        """;
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var oldMesh = System.Text.Json.JsonSerializer.Deserialize<FeMesh>(oldJson, options)!;
        Assert.False(oldMesh.IsQuadratic);
        Assert.Equal(new[] { 0, 1, 2, 3 }, oldMesh.GetElementNodes(0));
    }

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
}
