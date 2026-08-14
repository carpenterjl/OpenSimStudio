using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using Xunit;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// Gates for merging separately meshed bodies into the single mesh the solvers consume.
/// The merge is pure bookkeeping, so almost every assertion here is an exact identity —
/// a tolerance would only hide an off-by-one in an offset.
/// </summary>
public class FeMeshAssemblerTests
{
    private static readonly Material Steel = StructuredBoxMesh.Conductor("Steel", 45);
    private static readonly Material Copper = StructuredBoxMesh.Conductor("Copper", 400);

    private static List<Body> TwoBoxes() => new()
    {
        StructuredBoxMesh.Box("Base", 0, 0.02, 0, 0.01, 0, 0.01, 2, 1, 1, Steel),
        StructuredBoxMesh.Box("Lid", 0.02, 0.05, 0, 0.01, 0, 0.01, 3, 1, 1, Copper)
    };

    [Fact]
    public void Merge_ConcatenatesCountsExactly_AndOffsetsIndices()
    {
        var bodies = TwoBoxes();
        var assembled = FeMeshAssembler.Assemble(bodies);

        Assert.Equal(bodies.Sum(b => b.Mesh!.NodeCount), assembled.Mesh.NodeCount);
        Assert.Equal(bodies.Sum(b => b.Mesh!.ElementCount), assembled.Mesh.ElementCount);
        Assert.Equal(bodies.Sum(b => b.Mesh!.BoundaryTriangles.Count),
            assembled.Mesh.BoundaryTriangles.Count);
        Assert.Equal(new[] { 0, bodies[0].Mesh!.NodeCount }, assembled.NodeBases);
        Assert.Equal(new[] { 0, bodies[0].Mesh!.ElementCount }, assembled.ElementBases);

        // Second body's connectivity is its own, shifted by the node base — element by element.
        int nb = assembled.NodeBases[1], eb = assembled.ElementBases[1];
        for (int e = 0; e < bodies[1].Mesh!.ElementCount; e++)
        {
            var local = bodies[1].Mesh!.Elements[e];
            var merged = assembled.Mesh.Elements[eb + e];
            Assert.Equal(new Tet4(local.N0 + nb, local.N1 + nb, local.N2 + nb, local.N3 + nb), merged);
        }
    }

    [Fact]
    public void Merge_CopiesCoordinatesBitwise()
    {
        var bodies = TwoBoxes();
        var assembled = FeMeshAssembler.Assemble(bodies);
        for (int b = 0; b < bodies.Count; b++)
            for (int i = 0; i < bodies[b].Mesh!.NodeCount; i++)
            {
                var expected = bodies[b].Mesh!.Nodes[i];
                var actual = assembled.Mesh.Nodes[assembled.NodeBases[b] + i];
                Assert.Equal(expected.X, actual.X);   // bitwise: a merge must not move a node
                Assert.Equal(expected.Y, actual.Y);
                Assert.Equal(expected.Z, actual.Z);
            }
    }

    [Fact]
    public void Merge_SetsRegionIdToTheBodyIndex()
    {
        var bodies = TwoBoxes();
        var assembled = FeMeshAssembler.Assemble(bodies);
        for (int e = 0; e < assembled.Mesh.ElementCount; e++)
        {
            int expected = e < bodies[0].Mesh!.ElementCount ? 0 : 1;
            Assert.Equal(expected, assembled.Mesh.RegionOf(e));
            Assert.Equal(expected, assembled.BodyOfElement(e));
        }
        Assert.Same(Steel, assembled.RegionMaterials[0]);
        Assert.Same(Copper, assembled.RegionMaterials[1]);
    }

    [Fact]
    public void Merge_GivesEachBodyItsOwnFaceIdRange()
    {
        var bodies = TwoBoxes();
        var assembled = FeMeshAssembler.Assemble(bodies);

        // A structured box carries face ids 0..5, so the second body starts at 6.
        Assert.Equal(new[] { 0, 6 }, assembled.FaceIdBases);
        foreach (var t in assembled.Mesh.BoundaryTriangles)
        {
            int body = assembled.BodyOfNode(t.A);
            Assert.Equal(body, assembled.BodyOfFace(t.FaceId));
        }
        // No face id is claimed by two bodies.
        var firstIds = assembled.Mesh.BoundaryTriangles
            .Where(t => assembled.BodyOfNode(t.A) == 0).Select(t => t.FaceId).ToHashSet();
        var secondIds = assembled.Mesh.BoundaryTriangles
            .Where(t => assembled.BodyOfNode(t.A) == 1).Select(t => t.FaceId).ToHashSet();
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    [Fact]
    public void Merge_OffsetsBoundaryConditionFaceIds_AndPrefixesNames()
    {
        var bodies = TwoBoxes();
        bodies[0].BoundaryConditions.Add(new FixedTemperature
        {
            Name = "Cold end",
            FaceIds = new[] { StructuredBoxMesh.FaceXMin },
            Kelvin = 300
        });
        bodies[1].BoundaryConditions.Add(new HeatFlux
        {
            Name = "Heater",
            FaceIds = new[] { StructuredBoxMesh.FaceXMax },
            TotalPower = 5
        });

        var assembled = FeMeshAssembler.Assemble(bodies);
        var cold = Assert.IsType<FixedTemperature>(assembled.BoundaryConditions[0]);
        var heater = Assert.IsType<HeatFlux>(assembled.BoundaryConditions[1]);

        Assert.Equal("Base: Cold end", cold.Name);
        Assert.Equal(new[] { StructuredBoxMesh.FaceXMin }, cold.FaceIds);
        Assert.Equal(300, cold.Kelvin);

        Assert.Equal("Lid: Heater", heater.Name);
        Assert.Equal(new[] { 6 + StructuredBoxMesh.FaceXMax }, heater.FaceIds);
        Assert.Equal(5, heater.TotalPower);

        // The merged ids must resolve to real nodes of the right body.
        Assert.NotEmpty(assembled.Mesh.GetFaceNodes(heater.FaceIds));
        foreach (int node in assembled.Mesh.GetFaceNodes(heater.FaceIds))
            Assert.Equal(1, assembled.BodyOfNode(node));
    }

    [Fact]
    public void Merge_KeepsTotalVolume()
    {
        var bodies = TwoBoxes();
        var assembled = FeMeshAssembler.Assemble(bodies);
        double expected = bodies.Sum(b => b.Mesh!.TotalVolume());
        // Relative, not bitwise: the merged sum associates the same terms differently.
        Assert.Equal(expected, assembled.Mesh.TotalVolume(), 12);
        Assert.Equal(0.05 * 0.01 * 0.01, assembled.Mesh.TotalVolume(), 15);
    }

    [Fact]
    public void Merge_NeverWeldsCoincidentNodesAcrossBodies()
    {
        // The two boxes share the plane x = 0.02 with identical node positions there.
        var bodies = TwoBoxes();
        var assembled = FeMeshAssembler.Assemble(bodies);

        var onInterface = Enumerable.Range(0, assembled.Mesh.NodeCount)
            .Where(n => Math.Abs(assembled.Mesh.Nodes[n].X - 0.02) < 1e-15)
            .ToList();
        // Every interface node appears TWICE — once per body. Welding them would fake a
        // perfect joint that the contact model is supposed to price.
        Assert.Equal(2 * 4, onInterface.Count);   // a 1×1 face grid has 4 corners per side
        Assert.Equal(4, onInterface.Count(n => assembled.BodyOfNode(n) == 0));
        Assert.Equal(4, onInterface.Count(n => assembled.BodyOfNode(n) == 1));
    }

    [Fact]
    public void Merge_IsDeterministic()
    {
        var first = FeMeshAssembler.Assemble(TwoBoxes());
        var second = FeMeshAssembler.Assemble(TwoBoxes());
        Assert.Equal(first.Mesh.NodeCount, second.Mesh.NodeCount);
        for (int i = 0; i < first.Mesh.NodeCount; i++)
        {
            Assert.Equal(first.Mesh.Nodes[i].X, second.Mesh.Nodes[i].X);
            Assert.Equal(first.Mesh.Nodes[i].Y, second.Mesh.Nodes[i].Y);
            Assert.Equal(first.Mesh.Nodes[i].Z, second.Mesh.Nodes[i].Z);
        }
        Assert.Equal(first.Mesh.Elements, second.Mesh.Elements);
        Assert.Equal(first.Mesh.BoundaryTriangles, second.Mesh.BoundaryTriangles);
    }

    [Fact]
    public void Merge_RejectsAnUnmeshedBody_NamingIt()
    {
        var bodies = TwoBoxes();
        bodies[1].Mesh = null;
        var ex = Assert.Throws<InvalidOperationException>(() => FeMeshAssembler.Assemble(bodies));
        Assert.Contains("Lid", ex.Message);
        Assert.Contains("no mesh", ex.Message);
    }

    [Fact]
    public void Merge_RejectsAMateriallessBody_NamingIt()
    {
        var bodies = TwoBoxes();
        bodies[0].Material = null;
        var ex = Assert.Throws<InvalidOperationException>(() => FeMeshAssembler.Assemble(bodies));
        Assert.Contains("Base", ex.Message);
        Assert.Contains("material", ex.Message);
    }

    [Fact]
    public void Merge_RejectsAQuadraticBody_NamingIt()
    {
        var bodies = TwoBoxes();
        var linear = bodies[1].Mesh!;
        bodies[1].Mesh = QuadraticMeshBuilder.Upgrade(linear);
        var ex = Assert.Throws<InvalidOperationException>(() => FeMeshAssembler.Assemble(bodies));
        Assert.Contains("Lid", ex.Message);
        Assert.Contains("TET10", ex.Message);
    }

    [Fact]
    public void Merge_RejectsAPreRegionedBody_NamingIt()
    {
        var bodies = TwoBoxes();
        var mesh = bodies[1].Mesh!;
        bodies[1].Mesh = new FeMesh(mesh.Nodes, mesh.Elements, mesh.BoundaryTriangles,
            new int[mesh.ElementCount]);
        var ex = Assert.Throws<InvalidOperationException>(() => FeMeshAssembler.Assemble(bodies));
        Assert.Contains("Lid", ex.Message);
        Assert.Contains("regions", ex.Message);
    }

    [Fact]
    public void Merge_RejectsAnEmptyAssembly()
    {
        Assert.Throws<InvalidOperationException>(() => FeMeshAssembler.Assemble(Array.Empty<Body>()));
    }

    [Fact]
    public void FaceIdStride_CoversConditionsThatNameFacesBeyondTheSkin()
    {
        // A face id the skin does not carry must still widen the stride, or the offset
        // would land inside the next body's range and silently move the condition.
        var bodies = TwoBoxes();
        bodies[0].BoundaryConditions.Add(new FixedTemperature
        {
            Name = "Ghost",
            FaceIds = new[] { 40 },
            Kelvin = 300
        });
        var assembled = FeMeshAssembler.Assemble(bodies);
        Assert.Equal(41, assembled.FaceIdBases[1]);
        Assert.Equal(0, assembled.BodyOfFace(40));
        Assert.Equal(1, assembled.BodyOfFace(41));
    }

    [Fact]
    public void FaceIdBases_AreTheSameBeforeAndAfterMeshing()
    {
        // The pre-solve scene partitions face ids with FaceIdBases so a viewport click
        // resolves to a part; the merge partitions them again. If the two disagreed, the
        // id a user clicked before solving would name a different face after.
        var bodies = TwoBoxes();
        bodies[0].Geometry = FaceTaggedBox(faceCount: 6);
        bodies[1].Geometry = FaceTaggedBox(faceCount: 6);

        var assembled = FeMeshAssembler.Assemble(bodies);
        Assert.Equal(assembled.FaceIdBases, FeMeshAssembler.FaceIdBases(bodies));

        // A face the mesher dropped still reserves its slot: the geometry alone must be
        // able to widen the stride, or an unmeshed body would shift everything after it.
        bodies[0].Geometry = FaceTaggedBox(faceCount: 9);
        Assert.Equal(new[] { 0, 9 }, FeMeshAssembler.FaceIdBases(bodies));
    }

    [Fact]
    public void HeatSource_SpreadsEachBodysPowerOverItsOwnVolume()
    {
        var bodies = TwoBoxes();
        bodies[0].HeatSourcePower = 3.5;
        bodies[1].HeatSourcePower = 0.25;
        var assembled = FeMeshAssembler.Assemble(bodies);
        var source = FeMeshAssembler.BuildElementHeatSource(assembled, bodies);
        Assert.NotNull(source);
        Assert.Equal(assembled.Mesh.ElementCount, source!.Length);

        // The integral of the volumetric source over each body IS that body's power —
        // an exact identity, since q was defined as P divided by the same summed volume.
        for (int b = 0; b < bodies.Count; b++)
        {
            int first = assembled.ElementBases[b];
            int last = b + 1 < bodies.Count ? assembled.ElementBases[b + 1] : assembled.Mesh.ElementCount;
            double integrated = 0;
            for (int e = first; e < last; e++)
                integrated += source[e] * Math.Abs(assembled.Mesh.ElementVolume(e));
            double expected = bodies[b].HeatSourcePower!.Value;
            Assert.InRange(Math.Abs(integrated - expected) / expected, 0, 1e-12);
        }
    }

    [Fact]
    public void HeatSource_LeavesSilentBodiesAtZero_AndIsNullWhenNobodyDissipates()
    {
        var bodies = TwoBoxes();
        Assert.Null(FeMeshAssembler.BuildElementHeatSource(
            FeMeshAssembler.Assemble(bodies), bodies));

        bodies[1].HeatSourcePower = 2.0;
        var assembled = FeMeshAssembler.Assemble(bodies);
        var source = FeMeshAssembler.BuildElementHeatSource(assembled, bodies)!;
        for (int e = 0; e < assembled.ElementBases[1]; e++)
            Assert.Equal(0.0, source[e]);                      // exactly zero, not "small"
        for (int e = assembled.ElementBases[1]; e < source.Length; e++)
            Assert.True(source[e] > 0);
    }

    /// <summary>A cube whose triangles carry face ids 0..<paramref name="faceCount"/>-1,
    /// for the face-id partition gates (the geometry's ids are what widen a stride).</summary>
    private static TriangleMesh FaceTaggedBox(int faceCount)
    {
        var box = OpenSim.Geometry.PrimitiveFactory.CreateBox(0.01, 0.01, 0.01);
        var ids = new int[box.Triangles.Count];
        for (int t = 0; t < ids.Length; t++) ids[t] = t % faceCount;
        return new TriangleMesh(box.Vertices, box.Triangles, ids);
    }
}
