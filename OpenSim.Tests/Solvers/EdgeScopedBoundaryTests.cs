using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Gates for scoping a boundary condition to a geometric EDGE or VERTEX rather than a face
/// — the scope an Ansys "Fixed Support on 2 Edges" needs, and the one the beam benchmark
/// rests on.
/// </summary>
public class EdgeScopedBoundaryTests
{
    private static Material Steel() => new()
    {
        Name = "Steel", Density = 7850, YoungsModulus = 2e11, PoissonRatio = 0.3
    };

    private static FeMesh Box(int n = 2) => StructuredBoxMesh.Build(0, 1, 0, 2, 0, 3, n, n, n);

    private static int BottomEdge(FeMesh mesh, int sideFace) =>
        mesh.Edges.EdgesBetween(new[] { StructuredBoxMesh.FaceYMin, sideFace })[0];

    // ---------------------------------------------------------------- scope validation

    [Fact]
    public void DistributedLoadOnAnEdge_IsATypedFailure()
    {
        var mesh = Box();
        var force = new ForceLoad
        {
            Name = "F",
            FaceIds = Array.Empty<int>(),
            EdgeIds = new[] { mesh.Edges.Edges[0].Id },
            TotalForce = new Vector3D(0, -1, 0)
        };

        var ex = Assert.Throws<InvalidOperationException>(() => BoundaryScope.Validate(force, mesh));
        Assert.Contains("area", ex.Message);
        Assert.Contains("F", ex.Message);
    }

    [Fact]
    public void DistributedLoadOnAVertex_IsATypedFailure()
    {
        var mesh = Box();
        var flux = new HeatFlux
        {
            Name = "Q", FaceIds = Array.Empty<int>(),
            VertexIds = new[] { mesh.Edges.Vertices[0].Id }, TotalPower = 1
        };

        Assert.Throws<InvalidOperationException>(() => BoundaryScope.Validate(flux, mesh));
    }

    [Fact]
    public void DirichletConditionsAcceptZeroAreaScopes()
    {
        var mesh = Box();
        int edge = mesh.Edges.Edges[0].Id;

        BoundaryScope.Validate(
            new FixedSupport { Name = "s", FaceIds = Array.Empty<int>(), EdgeIds = new[] { edge } }, mesh);
        BoundaryScope.Validate(
            new FixedTemperature { Name = "t", FaceIds = Array.Empty<int>(), EdgeIds = new[] { edge }, Kelvin = 300 },
            mesh);
        BoundaryScope.Validate(
            new VoltagePotential { Name = "v", FaceIds = Array.Empty<int>(), EdgeIds = new[] { edge }, Volts = 1 },
            mesh);
    }

    [Fact]
    public void EmptyScope_IsATypedFailure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BoundaryScope.Validate(
            new FixedSupport { Name = "nothing", FaceIds = Array.Empty<int>() }, Box()));
        Assert.Contains("no faces, edges or vertices", ex.Message);
    }

    /// <summary>
    /// A named id the mesh does not carry must fail loudly. Dropped quietly it would
    /// under-constrain the solve, and an under-constrained static solve does not fail — it
    /// returns a wrong answer.
    /// </summary>
    [Fact]
    public void EdgeIdNotOnTheMesh_IsATypedFailureNamingTheId()
    {
        var mesh = Box();
        var support = new FixedSupport
        {
            Name = "s", FaceIds = new[] { StructuredBoxMesh.FaceYMin }, EdgeIds = new[] { 9999 }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => BoundaryScope.Validate(support, mesh));
        Assert.Contains("9999", ex.Message);
    }

    [Fact]
    public void VertexIdNotOnTheMesh_IsATypedFailureNamingTheId()
    {
        var mesh = Box();
        var support = new FixedSupport
        {
            Name = "s", FaceIds = new[] { StructuredBoxMesh.FaceYMin }, VertexIds = new[] { 4242 }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => BoundaryScope.Validate(support, mesh));
        Assert.Contains("4242", ex.Message);
    }

    // ---------------------------------------------------------------- quadratic meshes

    /// <summary>
    /// A TET10 solve must pin the MID-EDGE node of every segment its scope covers. Pinning
    /// only corners leaves the mid-nodes free, which is spurious compliance right at the
    /// support — the same rule face scoping has always followed, now reaching edges.
    /// </summary>
    [Fact]
    public void QuadraticEdgeScope_CoversEveryMidEdgeNodeOfTheEdge()
    {
        var mesh = QuadraticMeshBuilder.Upgrade(Box());
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);
        int edge = BottomEdge(mesh, StructuredBoxMesh.FaceXMin);
        var support = new FixedSupport { Name = "s", FaceIds = Array.Empty<int>(), EdgeIds = new[] { edge } };

        var segments = mesh.GetScopeSegments(support);

        Assert.NotEmpty(segments);
        foreach (var s in segments)
            Assert.True(edgeMid.ContainsKey(s.A < s.B ? (s.A, s.B) : (s.B, s.A)),
                $"Edge segment ({s.A},{s.B}) has no mid-edge node — it is not a tet edge.");
    }

    // ---------------------------------------------------------------- a real solve

    /// <summary>
    /// The reference configuration in miniature: a block supported on its two opposite
    /// bottom edges and pushed on the top face. Fixing lines rather than faces is the whole
    /// point, so the assertions are that it solves at all, that the supported nodes really
    /// are held, and that the body deflects the way a supported beam does.
    /// </summary>
    [Fact]
    public void BlockOnTwoOppositeBottomEdges_SolvesAndHoldsThoseNodes()
    {
        var mesh = StructuredBoxMesh.Build(0, 0.2, 0, 0.06, 0, 0.02, 8, 3, 2);
        int left = BottomEdge(mesh, StructuredBoxMesh.FaceXMin);
        int right = BottomEdge(mesh, StructuredBoxMesh.FaceXMax);

        var input = new SolveInput
        {
            Mesh = mesh,
            Material = Steel(),
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedSupport
                {
                    Name = "Supports", FaceIds = Array.Empty<int>(), EdgeIds = new[] { left, right }
                },
                new ForceLoad
                {
                    Name = "Load",
                    FaceIds = new[] { StructuredBoxMesh.FaceYMax },
                    TotalForce = new Vector3D(0, -5e5, 0)
                }
            }
        };

        var output = new LinearStaticSolver().Solve(input);
        var displacement = (NodalVectorField)output.Fields.First(f => f.Name == "Displacement");

        foreach (int node in mesh.GetScopeNodes(input.BoundaryConditions[0]))
            Assert.Equal(0.0, displacement.GetVector(node).Length, 12);

        // The mid-span bottom node sags, and it sags downward.
        double sag = displacement.Values.Min(v => v.Y);
        Assert.True(sag < 0, "The block should deflect in the direction of the load.");
        Assert.True(Math.Abs(sag) > 1e-9, "The block should deflect measurably.");
    }

    [Fact]
    public void EdgeSupportIsSofterThanTheWholeBottomFace()
    {
        var mesh = StructuredBoxMesh.Build(0, 0.2, 0, 0.06, 0, 0.02, 8, 3, 2);
        int left = BottomEdge(mesh, StructuredBoxMesh.FaceXMin);
        int right = BottomEdge(mesh, StructuredBoxMesh.FaceXMax);

        double Sag(BoundaryCondition support)
        {
            var input = new SolveInput
            {
                Mesh = mesh,
                Material = Steel(),
                BoundaryConditions = new[]
                {
                    support,
                    new ForceLoad
                    {
                        Name = "Load",
                        FaceIds = new[] { StructuredBoxMesh.FaceYMax },
                        TotalForce = new Vector3D(0, -5e5, 0)
                    }
                }
            };
            var field = (NodalVectorField)new LinearStaticSolver().Solve(input)
                .Fields.First(f => f.Name == "Displacement");
            return Math.Abs(field.Values.Min(v => v.Y));
        }

        double onEdges = Sag(new FixedSupport
        {
            Name = "Edges", FaceIds = Array.Empty<int>(), EdgeIds = new[] { left, right }
        });
        double onFace = Sag(new FixedSupport
        {
            Name = "Face", FaceIds = new[] { StructuredBoxMesh.FaceYMin }
        });

        // Two lines restrain far less than the whole bottom plane: bending is free between
        // them. This is exactly why the reference case cannot be reproduced with face scoping.
        Assert.True(onEdges > 10 * onFace,
            $"Edge-supported sag {onEdges:g4} m should greatly exceed face-supported {onFace:g4} m.");
    }

    // ---------------------------------------------------------------- assembly merge

    /// <summary>
    /// The identity the assembler edge/vertex offsets rest on: because bodies are never
    /// re-welded, each keeps a contiguous ascending face range and its own node range, so
    /// the merged skin reproduces every body edge list in body order, shifted by a constant.
    /// If that ever stopped holding, a merged edge-scoped condition would silently point at
    /// another body edge.
    /// </summary>
    [Fact]
    public void MergedEdgeIds_AreTheBodyLocalIdsShiftedByTheirBase()
    {
        var material = StructuredBoxMesh.Conductor("c", 100);
        var bodies = new[]
        {
            StructuredBoxMesh.Box("A", 0, 1, 0, 1, 0, 1, 2, 2, 2, material),
            StructuredBoxMesh.Box("B", 3, 5, 0, 2, 0, 3, 2, 2, 2, material)
        };

        var assembled = FeMeshAssembler.Assemble(bodies);
        var merged = assembled.Mesh.Edges;

        for (int b = 0; b < bodies.Length; b++)
        {
            var local = bodies[b].Mesh!.Edges;
            int eb = assembled.EdgeIdBases[b], fb = assembled.FaceIdBases[b], nb = assembled.NodeBases[b];

            for (int i = 0; i < local.Edges.Count; i++)
            {
                var expected = local.Edges[i];
                var actual = merged.Edges[eb + i];
                Assert.Equal(expected.FaceA + fb, actual.FaceA);
                Assert.Equal(expected.FaceB + fb, actual.FaceB);
                Assert.Equal(expected.NodeIds.Select(n => n + nb), actual.NodeIds);
                Assert.Equal(expected.Length, actual.Length, 12);
            }

            var localVertices = local.Vertices;
            int vb = assembled.VertexIdBases[b];
            for (int i = 0; i < localVertices.Count; i++)
                Assert.Equal(localVertices[i].NodeId + nb, merged.Vertices[vb + i].NodeId);
        }
    }

    [Fact]
    public void MergedConditions_CarryOffsetEdgeAndVertexIds()
    {
        var material = StructuredBoxMesh.Conductor("c", 100);
        var bodies = new[]
        {
            StructuredBoxMesh.Box("A", 0, 1, 0, 1, 0, 1, 2, 2, 2, material),
            StructuredBoxMesh.Box("B", 3, 5, 0, 2, 0, 3, 2, 2, 2, material)
        };
        bodies[1].BoundaryConditions.Add(new FixedTemperature
        {
            Name = "T", FaceIds = Array.Empty<int>(), EdgeIds = new[] { 0 }, VertexIds = new[] { 0 }, Kelvin = 300
        });

        var assembled = FeMeshAssembler.Assemble(bodies);
        var merged = assembled.BoundaryConditions.Single();

        Assert.Equal(new[] { assembled.EdgeIdBases[1] }, merged.EdgeIds);
        Assert.Equal(new[] { assembled.VertexIdBases[1] }, merged.VertexIds);
        // And the merged scope resolves onto body B nodes, not body A.
        Assert.All(assembled.Mesh.GetScopeNodes(merged),
            n => Assert.Equal(1, assembled.BodyOfNode(n)));
    }

    /// <summary>Bodies with no edge scoping must leave the merged conditions exactly as before.</summary>
    [Fact]
    public void MergeWithoutEdgeScoping_LeavesEdgeAndVertexIdsNull()
    {
        var material = StructuredBoxMesh.Conductor("c", 100);
        var body = StructuredBoxMesh.Box("A", 0, 1, 0, 1, 0, 1, 2, 2, 2, material);
        body.BoundaryConditions.Add(new FixedTemperature
        {
            Name = "T", FaceIds = new[] { StructuredBoxMesh.FaceXMin }, Kelvin = 300
        });

        var merged = FeMeshAssembler.Assemble(new[] { body }).BoundaryConditions.Single();

        Assert.Null(merged.EdgeIds);
        Assert.Null(merged.VertexIds);
    }

    /// <summary>
    /// An assembly whose second body is scoped in GEOMETRY space: the resolver runs
    /// per body, on that body own geometry and mesh, and only then are the ids rebased into
    /// the merged space. Resolving after the merge would mean matching a curve against a
    /// skin carrying every other body as well.
    /// </summary>
    [Fact]
    public void GeometryScopedConditions_AreResolvedPerBody_ThenRebasedLikeAnyOther()
    {
        var material = StructuredBoxMesh.Conductor("c", 100);
        var geometry = OpenSim.Geometry.PrimitiveFactory.CreateBox(0.02, 0.02, 0.02);
        var mesher = new OpenSim.Meshing.StructuredLatticeMeshGenerator();
        var settings = new MeshSettings
        {
            Method = MeshMethod.StructuredLattice, Divisions = new LatticeDivisions(2, 2, 2)
        };

        var a = StructuredBoxMesh.Box("A", 0, 1, 0, 1, 0, 1, 2, 2, 2, material);
        var b = new Body
        {
            Name = "B", Geometry = geometry, Mesh = mesher.Generate(geometry, settings), Material = material
        };
        // The edge where the box bottom (face 2) meets its x-min end (face 0), named on the
        // GEOMETRY - the id a project file would store.
        int geometryEdge = geometry.FeatureEdges.EdgesBetween(new[] { 2, 0 })[0];
        b.BoundaryConditions.Add(new FixedTemperature
        {
            Name = "T", FaceIds = Array.Empty<int>(),
            GeometryEdgeIds = new[] { geometryEdge }, Kelvin = 300
        });

        var assembled = FeMeshAssembler.Assemble(new[] { a, b });
        var merged = assembled.BoundaryConditions.Single(c => c.Name.StartsWith("B: "));

        // Resolved, then rebased: no geometry ids survive, and the mesh id carries body B base.
        Assert.Null(merged.GeometryEdgeIds);
        int localEdge = GeometryScopeResolver
            .Resolve(b.BoundaryConditions[0], geometry, b.Mesh!).EdgeIds!.Single();
        Assert.Equal(new[] { localEdge + assembled.EdgeIdBases[1] }, merged.EdgeIds);

        // And it lands on body B nodes, along the line the geometry named.
        var nodes = assembled.Mesh.GetScopeNodes(merged);
        Assert.NotEmpty(nodes);
        Assert.All(nodes, n => Assert.Equal(1, assembled.BodyOfNode(n)));
        Assert.All(nodes, n =>
        {
            Assert.Equal(0.0, assembled.Mesh.Nodes[n].X);
            Assert.Equal(0.0, assembled.Mesh.Nodes[n].Y);
        });
    }
}
