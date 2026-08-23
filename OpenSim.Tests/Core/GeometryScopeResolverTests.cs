using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Gates for turning a geometry-space scope into the mesh-space one a solver resolves.
///
/// The point of the exercise is that a stored scope stops going stale: the ids a project
/// keeps are a function of the geometry, and the mesh ids are worked out fresh each solve.
/// So the headline test here is not "it resolves" but "it resolves to the same physical
/// curve after the body has been remeshed at a different element size" — the thing that
/// was impossible before.
/// </summary>
public class GeometryScopeResolverTests
{
    private const double Lx = 0.200, Ly = 0.060, Lz = 0.020;

    private static TriangleMesh Beam() => PrimitiveFactory.CreateBox(Lx, Ly, Lz);

    private static FeMesh Delaunay(TriangleMesh geometry, double h) =>
        new DelaunayMeshGenerator().Generate(geometry, new MeshSettings { TargetEdgeLength = h });

    private static FeMesh Lattice(TriangleMesh geometry, int nx, int ny, int nz) =>
        new StructuredLatticeMeshGenerator().Generate(geometry, new MeshSettings
        {
            Method = MeshMethod.StructuredLattice, Divisions = new LatticeDivisions(nx, ny, nz)
        });

    /// <summary>The beam's two supports, named on the geometry before any mesh exists.</summary>
    private static FixedSupport Supports(TriangleMesh geometry)
    {
        var set = geometry.FeatureEdges;
        return new FixedSupport
        {
            Name = "Supports",
            FaceIds = Array.Empty<int>(),
            GeometryEdgeIds = new[] { set.EdgesBetween(new[] { 2, 0 })[0], set.EdgesBetween(new[] { 2, 1 })[0] }
        };
    }

    // ------------------------------------------------------------------ resolution

    [Fact]
    public void AGeometryScope_ResolvesToTheMeshEdgesAlongTheSameCurves()
    {
        var geometry = Beam();
        var mesh = Delaunay(geometry, 0.012);

        var resolved = GeometryScopeResolver.Resolve(Supports(geometry), geometry, mesh);

        Assert.Null(resolved.GeometryEdgeIds);           // consumed, never left to be resolved twice
        Assert.Equal(2, resolved.EdgeIds!.Count);
        Assert.Equal(new[] { 2, 0 },
            new[] { mesh.Edges.EdgeById(resolved.EdgeIds[0])!.FaceB, mesh.Edges.EdgeById(resolved.EdgeIds[0])!.FaceA });
        // And it names real nodes: exactly the two support lines, y = 0 at each end.
        var nodes = mesh.GetScopeNodes(resolved);
        Assert.NotEmpty(nodes);
        Assert.All(nodes, n =>
        {
            Assert.True(Math.Abs(mesh.Nodes[n].Y) < 1e-12);
            Assert.True(Math.Abs(mesh.Nodes[n].X) < 1e-12 || Math.Abs(mesh.Nodes[n].X - Lx) < 1e-12);
        });
    }

    /// <summary>
    /// THE test. One stored scope, three different meshes — two element sizes and a
    /// different mesher entirely — and every time it resolves to the same two physical
    /// lines. Mesh-space ids could not survive any of these.
    /// </summary>
    [Fact]
    public void OneStoredScope_ResolvesToTheSameCurves_AcrossRemeshesAndMeshers()
    {
        var geometry = Beam();
        var stored = Supports(geometry);

        foreach (var mesh in new[]
                 {
                     Delaunay(geometry, 0.020),
                     Delaunay(geometry, 0.011),
                     Lattice(geometry, 20, 6, 2)
                 })
        {
            var resolved = GeometryScopeResolver.Resolve(stored, geometry, mesh);
            var nodes = mesh.GetScopeNodes(resolved);

            Assert.Equal(2, resolved.EdgeIds!.Count);
            Assert.NotEmpty(nodes);
            Assert.All(nodes, n =>
            {
                Assert.True(Math.Abs(mesh.Nodes[n].Y) < 1e-12, $"node {n} off y = 0: {mesh.Nodes[n]}");
                Assert.True(Math.Abs(mesh.Nodes[n].X) < 1e-12 || Math.Abs(mesh.Nodes[n].X - Lx) < 1e-12,
                    $"node {n} off both ends: {mesh.Nodes[n]}");
            });
            // Both ends are represented, not the same edge twice.
            Assert.Contains(nodes, n => mesh.Nodes[n].X < 0.5 * Lx);
            Assert.Contains(nodes, n => mesh.Nodes[n].X > 0.5 * Lx);
        }
    }

    /// <summary>
    /// The case a face pair alone cannot answer: one pair meeting along two disjoint rims.
    /// The resolver has to pick the rim the user actually chose, and the separation check is
    /// what makes that a decision rather than a guess.
    /// </summary>
    [Fact]
    public void OneFacePairMeetingAlongTwoRims_ResolvesEachRimToItsOwn()
    {
        // Relabel a box so the two z faces are one face and the four sides another: that
        // single pair now meets along the bottom rim and the top rim.
        var box = Beam();
        var relabelled = new TriangleMesh(box.Vertices, box.Triangles,
            box.TriangleFaceIds.Select(f => f is 4 or 5 ? 1 : 0).ToArray());

        var geometry = relabelled;
        var rims = geometry.FeatureEdges.Edges;
        Assert.Equal(2, rims.Count);

        var mesh = Delaunay(geometry, 0.012);
        Assert.Equal(2, mesh.Edges.Edges.Count);

        foreach (var rim in rims)
        {
            var condition = new FixedSupport
            {
                Name = $"rim {rim.Id}", FaceIds = Array.Empty<int>(), GeometryEdgeIds = new[] { rim.Id }
            };
            var resolved = GeometryScopeResolver.Resolve(condition, geometry, mesh);

            // The rim's own z: every node of the resolved mesh edge sits on the same plane.
            double z = geometry.Vertices[rim.NodeIds[0]].Z;
            Assert.All(mesh.GetScopeNodes(resolved),
                n => Assert.True(Math.Abs(mesh.Nodes[n].Z - z) < 1e-12,
                    $"rim {rim.Id} resolved to a node at z = {mesh.Nodes[n].Z:g6}, expected {z:g6}."));
        }

        // And the two rims resolved to DIFFERENT mesh edges — the whole point.
        var first = GeometryScopeResolver.Resolve(
            new FixedSupport { Name = "a", FaceIds = Array.Empty<int>(), GeometryEdgeIds = new[] { rims[0].Id } },
            geometry, mesh).EdgeIds![0];
        var second = GeometryScopeResolver.Resolve(
            new FixedSupport { Name = "b", FaceIds = Array.Empty<int>(), GeometryEdgeIds = new[] { rims[1].Id } },
            geometry, mesh).EdgeIds![0];
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AGeometryVertexScope_ResolvesToTheCornerItNames()
    {
        var geometry = Beam();
        var mesh = Delaunay(geometry, 0.012);
        var corner = geometry.FeatureEdges.Vertices[0];

        var resolved = GeometryScopeResolver.Resolve(new FixedSupport
        {
            Name = "corner", FaceIds = Array.Empty<int>(), GeometryVertexIds = new[] { corner.Id }
        }, geometry, mesh);

        var node = Assert.Single(mesh.GetScopeNodes(resolved));
        Assert.Equal(geometry.Vertices[corner.NodeId], mesh.Nodes[node]);   // bitwise: hugged
    }

    [Fact]
    public void AConditionWithNoGeometryScope_IsReturnedUntouched()
    {
        var geometry = Beam();
        var mesh = Delaunay(geometry, 0.020);
        var condition = new ForceLoad
        {
            Name = "Load", FaceIds = new[] { 3 }, TotalForce = new Vector3D(0, -1, 0)
        };

        Assert.Same(condition, GeometryScopeResolver.Resolve(condition, geometry, mesh));
    }

    // ------------------------------------------------------------------ typed failures

    [Fact]
    public void AnUnknownGeometryEdgeId_IsATypedFailureNamingTheCount()
    {
        var geometry = Beam();
        var mesh = Delaunay(geometry, 0.020);
        var condition = new FixedSupport
        {
            Name = "Stale", FaceIds = Array.Empty<int>(), GeometryEdgeIds = new[] { 99 }
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => GeometryScopeResolver.Resolve(condition, geometry, mesh));
        Assert.Contains("'Stale'", ex.Message);
        Assert.Contains("12 feature edge(s)", ex.Message);
    }

    [Fact]
    public void AGeometryScopeWithoutGeometry_IsATypedFailure()
    {
        var mesh = Delaunay(Beam(), 0.020);
        var condition = new FixedSupport
        {
            Name = "Orphan", FaceIds = Array.Empty<int>(), GeometryEdgeIds = new[] { 0 }
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => GeometryScopeResolver.Resolve(condition, null, mesh));
        Assert.Contains("no geometry", ex.Message);
    }

    /// <summary>
    /// The backstop: a condition that reached a solver still carrying a geometry scope has
    /// been mishandled by its caller. Refusing loudly is the difference between a visible
    /// bug and a static solve that silently drops a support and returns a wrong answer.
    /// </summary>
    [Fact]
    public void AnUnresolvedGeometryScope_IsRefusedByValidate()
    {
        var geometry = Beam();
        var mesh = Delaunay(geometry, 0.020);

        var ex = Assert.Throws<InvalidOperationException>(
            () => BoundaryScope.Validate(Supports(geometry), mesh));
        Assert.Contains("not resolved", ex.Message);
    }

    /// <summary>A geometry-scoped LOAD is refused for the same reason a mesh-scoped one is:
    /// a curve has no area to distribute a total force over. The refusal has to fire on the
    /// geometry ids too, or it would only appear after resolution — too late to be useful.</summary>
    [Fact]
    public void AGeometryEdgeScopedLoad_IsStillAZeroAreaRefusal()
    {
        var geometry = Beam();
        var mesh = Delaunay(geometry, 0.020);
        var load = new ForceLoad
        {
            Name = "Edge load", FaceIds = Array.Empty<int>(),
            GeometryEdgeIds = new[] { 0 }, TotalForce = new Vector3D(0, -1, 0)
        };

        Assert.True(load.HasZeroAreaScope);
        var ex = Assert.Throws<InvalidOperationException>(() => BoundaryScope.Validate(load, mesh));
        Assert.Contains("area", ex.Message);
    }

    [Fact]
    public void ScopeSummary_CountsGeometryEdgesToo_SoTheLogReadsTheSameBeforeAndAfterResolution()
    {
        var geometry = Beam();
        var stored = Supports(geometry);
        var resolved = GeometryScopeResolver.Resolve(stored, geometry, Delaunay(geometry, 0.020));

        Assert.Equal("2 edge(s)", stored.ScopeSummary);
        Assert.Equal(stored.ScopeSummary, resolved.ScopeSummary);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void GeometryScope_RoundTrips_AndOldConditionsLoadWithNone()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        BoundaryCondition stored = new FixedSupport
        {
            Name = "Supports", FaceIds = Array.Empty<int>(),
            GeometryEdgeIds = new[] { 4, 7 }, GeometryVertexIds = new[] { 2 }
        };

        string json = System.Text.Json.JsonSerializer.Serialize(stored, options);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<BoundaryCondition>(json, options)!;

        Assert.Equal(new[] { 4, 7 }, loaded.GeometryEdgeIds);
        Assert.Equal(new[] { 2 }, loaded.GeometryVertexIds);
        Assert.Null(loaded.EdgeIds);

        // A face-only condition writes neither new property name, so old readers see old files.
        string faceOnly = System.Text.Json.JsonSerializer.Serialize(
            (BoundaryCondition)new FixedSupport { Name = "Wall", FaceIds = new[] { 0 } }, options);
        Assert.DoesNotContain("geometryEdgeIds", faceOnly);
        Assert.DoesNotContain("geometryVertexIds", faceOnly);

        // And a pre-batch payload loads with both null.
        const string oldJson = """
        { "$type": "fixedSupport", "name": "Wall", "faceIds": [0], "edgeIds": [3] }
        """;
        var old = System.Text.Json.JsonSerializer.Deserialize<BoundaryCondition>(oldJson, options)!;
        Assert.Null(old.GeometryEdgeIds);
        Assert.Null(old.GeometryVertexIds);
        Assert.Equal(new[] { 3 }, old.EdgeIds);
        Assert.False(old.HasGeometryScope);
    }
}
