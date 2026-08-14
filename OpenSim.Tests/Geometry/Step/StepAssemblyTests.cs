using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step;
using OpenSim.Geometry.Step.Schema;
using Frame = OpenSim.Tests.Geometry.Step.StepAssemblyFixtures.Frame;

namespace OpenSim.Tests.Geometry.Step;

/// <summary>
/// Multi-body STEP import: the product structure is read and every occurrence is placed.
/// <para>
/// The placements are gated EXACTLY, not to a tolerance. Assembly frames in real exporters
/// are axis-aligned, so their direction cosines are exact 0/±1 and the rotation is applied
/// with exact arithmetic — a placement bug therefore shows up as an exact mismatch, and a
/// loose tolerance here would hide precisely the class of error (a swapped axis, a sign, a
/// missing composition) this code can make.
/// </para>
/// </summary>
public class StepAssemblyTests
{
    private static StepAssemblyReport Import(string text) =>
        new StepImporter().ImportAssemblyText(text);

    [Fact]
    public void TwoParts_AreBothImported_AndPlacedWhereTheAssemblySaysExactly()
    {
        var report = Import(StepAssemblyFixtures.Boxes(StepFixtures.Unit.Millimetre,
            ("Small", 1, Frame.At(10, 0, 0)),
            ("Large", 2, Frame.At(0, 20, 0))));

        Assert.Equal(2, report.Bodies.Count);
        Assert.Equal(new[] { "Small", "Large" }, report.Bodies.Select(b => b.Name));
        Assert.All(report.Bodies, b => Assert.True(b.PlacedByAssembly));

        // A part authored at its local origin lands at [t, t + size]; both ends are the
        // same double the transform computes, so equality is exact.
        AssertBounds(report.Bodies[0].Mesh, (0.010, 0.0, 0.0), 0.001);
        AssertBounds(report.Bodies[1].Mesh, (0.0, 0.020, 0.0), 0.002);

        // Placement moves a part; it must not resize it.
        Assert.Equal(1e-9, report.Bodies[0].SignedVolume, 1e-20);
        Assert.Equal(8e-9, report.Bodies[1].SignedVolume, 1e-20);
    }

    [Fact]
    public void Rotation_IsAppliedExactly_VertexByVertex()
    {
        // The SAME fixture under two frames: identity, and +90° about Z with a shift.
        var reference = Import(StepAssemblyFixtures.SingleBox(Frame.At(0, 0, 0))).Bodies.Single().Mesh;
        var rotated = Import(StepAssemblyFixtures.SingleBox(Frame.RotatedZ90(5, 7, 0))).Bodies.Single().Mesh;

        Assert.Equal(reference.Vertices.Count, rotated.Vertices.Count);
        for (int i = 0; i < reference.Vertices.Count; i++)
        {
            var p = reference.Vertices[i];
            var expected = new Vector3D(-p.Y + 0.005, p.X + 0.007, p.Z);
            Assert.True(expected.Equals(rotated.Vertices[i]),
                $"vertex {i}: expected {expected}, got {rotated.Vertices[i]}");
        }
        Assert.Equal(reference.Triangles, rotated.Triangles);
    }

    [Fact]
    public void InstancedPart_IsTessellatedOnce_AndSharesItsTopology()
    {
        var report = Import(StepAssemblyFixtures.InstancedPart(4));
        Assert.Equal(4, report.Bodies.Count);
        Assert.Single(report.Bodies.Select(b => b.SolidId).Distinct());

        var first = report.Bodies[0].Mesh;
        foreach (var body in report.Bodies.Skip(1))
        {
            // Topology is SHARED, not copied: instancing must not re-tessellate.
            Assert.Same(first.Triangles, body.Mesh.Triangles);
            Assert.Same(first.TriangleFaceIds, body.Mesh.TriangleFaceIds);
        }
        for (int i = 0; i < report.Bodies.Count; i++)
        {
            double offset = 0.010 * i;
            for (int v = 0; v < first.Vertices.Count; v++)
            {
                var expected = new Vector3D(first.Vertices[v].X - 0.0 + offset,
                    first.Vertices[v].Y, first.Vertices[v].Z);
                Assert.Equal(expected.X, report.Bodies[i].Mesh.Vertices[v].X, 15);
            }
        }
        Assert.Contains(report.Notes, n => n.Contains("instanced"));
    }

    [Fact]
    public void NestedAssembly_ComposesTheTransformsInOrder()
    {
        // Outer: +90° about Z at (5, 0, 0). Inner: translate (0, 3, 0) in the sub-assembly.
        // The inner translation must be ROTATED by the outer frame → (−3, 0, 0) + (5, 0, 0).
        var nested = Import(StepAssemblyFixtures.Nested(
            Frame.RotatedZ90(5, 0, 0), Frame.At(0, 3, 0))).Bodies.Single();
        var reference = Import(StepAssemblyFixtures.SingleBox(Frame.At(0, 0, 0))).Bodies.Single().Mesh;

        Assert.Equal("BLK", nested.Name);
        for (int i = 0; i < reference.Vertices.Count; i++)
        {
            var p = reference.Vertices[i];
            // outer ∘ inner applied to p: rotate(p + (0,0.003,0)) + (0.005,0,0)
            var inner = new Vector3D(p.X, p.Y + 0.003, p.Z);
            var expected = new Vector3D(-inner.Y + 0.005, inner.X, inner.Z);
            var actual = nested.Mesh.Vertices[i];
            Assert.Equal(expected.X, actual.X, 15);
            Assert.Equal(expected.Y, actual.Y, 15);
            Assert.Equal(expected.Z, actual.Z, 15);
        }
    }

    [Fact]
    public void ReferenceDesignators_NameTheOccurrences_AndDuplicatesAreDisambiguated()
    {
        var named = Import(StepAssemblyFixtures.WithDesignators("U1", "U2"));
        Assert.Equal(new[] { "U1", "U2" }, named.Bodies.Select(b => b.Name));

        var duplicated = Import(StepAssemblyFixtures.WithDesignators("U1", "U1"));
        Assert.Equal(new[] { "U1", "U1:2" }, duplicated.Bodies.Select(b => b.Name));
    }

    [Fact]
    public void PartWithALinkedBrepRepresentation_StillFindsItsSolid()
    {
        // The product's own SHAPE_REPRESENTATION holds no geometry; the solid lives in a
        // linked ADVANCED_BREP_SHAPE_REPRESENTATION. Missing this link would import an
        // empty assembly, so it is gated on placement AND on volume.
        var body = Import(StepAssemblyFixtures.LinkedRepresentation(Frame.At(4, 0, 0))).Bodies.Single();
        Assert.Equal(6e-9, body.SignedVolume, 1e-20);   // 1 × 2 × 3 mm³
        AssertBounds(body.Mesh, (0.004, 0.0, 0.0), 0.001, 0.002, 0.003);
    }

    [Fact]
    public void MappedItem_PlacesTheInstancedRepresentation()
    {
        var body = Import(StepAssemblyFixtures.MappedItem(Frame.At(0, 0, 6))).Bodies.Single();
        AssertBounds(body.Mesh, (0.0, 0.0, 0.006), 0.001, 0.002, 0.003);
    }

    [Fact]
    public void NoProductStructure_ImportsEverySolidInTheGlobalFrame()
    {
        // Two boxes authored at their true positions with no assembly records at all: the
        // file's own coordinates ARE the placement, which is a different claim from
        // "the placement is missing" and gets a note rather than a failure.
        var report = Import(StepFixtures.TwoBoxes());
        Assert.Equal(2, report.Bodies.Count);
        Assert.All(report.Bodies, b => Assert.False(b.PlacedByAssembly));
        Assert.Contains(report.Notes, n => n.Contains("no assembly structure"));
        AssertBounds(report.Bodies[0].Mesh, (0.0, 0.0, 0.0), 0.001);
        AssertBounds(report.Bodies[1].Mesh, (0.010, 0.0, 0.0), 0.002);
    }

    [Fact]
    public void SingleSolidFile_ImportsAsOneBody()
    {
        var report = Import(StepFixtures.Box(2, 2, 2));
        Assert.Single(report.Bodies);
        Assert.Equal(8e-9, report.Bodies[0].SignedVolume, 1e-20);
    }

    [Fact]
    public void OccurrenceWithoutAPlacementChain_FailsNamingIt()
    {
        var ex = Assert.Throws<StepImportException>(() => Import(StepAssemblyFixtures.MissingPlacement()));
        Assert.Contains("NEXT_ASSEMBLY_USAGE_OCCURRENCE", ex.Message);
        Assert.Contains("cannot be guessed", ex.Message);
    }

    [Fact]
    public void ScaledPlacementOperator_IsRefused()
    {
        var ex = Assert.Throws<StepUnsupportedEntityException>(
            () => Import(StepAssemblyFixtures.ScaledPlacement()));
        Assert.Contains("CARTESIAN_TRANSFORMATION_OPERATOR_3D", ex.Message);
    }

    [Fact]
    public void InchUnits_ScaleThePlacementToo()
    {
        // A placement is a LENGTH: 1 inch of assembly offset is 25.4 mm, not 1 mm.
        var body = Import(StepAssemblyFixtures.Boxes(StepFixtures.Unit.Inch,
            ("Block", 1, Frame.At(2, 0, 0)))).Bodies.Single();
        Assert.Equal(2 * 0.0254, body.Mesh.Bounds.Min.X, 12);
        Assert.Equal(3 * 0.0254, body.Mesh.Bounds.Max.X, 12);
    }

    [Fact]
    public void AssemblyImport_IsDeterministic_Bitwise()
    {
        string text = StepAssemblyFixtures.Boxes(StepFixtures.Unit.Millimetre,
            ("Small", 1, Frame.At(10, 0, 0)), ("Large", 2, Frame.RotatedZ90(0, 20, 0)));
        var a = Import(text);
        var b = Import(text);
        Assert.Equal(a.Bodies.Count, b.Bodies.Count);
        for (int i = 0; i < a.Bodies.Count; i++)
        {
            Assert.Equal(a.Bodies[i].Name, b.Bodies[i].Name);
            Assert.Equal(a.Bodies[i].Mesh.Vertices.Count, b.Bodies[i].Mesh.Vertices.Count);
            for (int v = 0; v < a.Bodies[i].Mesh.Vertices.Count; v++)
                Assert.True(a.Bodies[i].Mesh.Vertices[v].Equals(b.Bodies[i].Mesh.Vertices[v]));
            Assert.Equal(a.Bodies[i].Mesh.Triangles, b.Bodies[i].Mesh.Triangles);
        }
    }

    [Fact]
    public void PlacedBodies_StayWatertight()
    {
        // A rigid motion preserves every distance the welder's tolerances are expressed
        // in, so the watertightness proven at tessellation time must survive placement.
        var report = Import(StepAssemblyFixtures.Boxes(StepFixtures.Unit.Millimetre,
            ("A", 1, Frame.RotatedZ90(3, 4, 5)), ("B", 2, Frame.At(-7, 0, 0))));
        Assert.All(report.Bodies, b => Assert.True(b.Mesh.IsWatertight(), b.Name));
    }

    private static void AssertBounds(TriangleMesh mesh, (double x, double y, double z) origin,
        double size) => AssertBounds(mesh, origin, size, size, size);

    private static void AssertBounds(TriangleMesh mesh, (double x, double y, double z) origin,
        double dx, double dy, double dz)
    {
        var bounds = mesh.Bounds;
        Assert.Equal(origin.x, bounds.Min.X, 15);
        Assert.Equal(origin.y, bounds.Min.Y, 15);
        Assert.Equal(origin.z, bounds.Min.Z, 15);
        Assert.Equal(origin.x + dx, bounds.Max.X, 15);
        Assert.Equal(origin.y + dy, bounds.Max.Y, 15);
        Assert.Equal(origin.z + dz, bounds.Max.Z, 15);
    }
}

/// <summary>
/// The rigid-placement primitive on its own: the algebra the assembly resolver depends on.
/// </summary>
public class StepRigidTransformTests
{
    private static Axis2Placement3D Frame(double x, double y, double z,
        Vector3D? axis = null, Vector3D? refDir = null) =>
        Axis2Placement3D.FromAxes(new Vector3D(x, y, z), axis, refDir, 1);

    [Fact]
    public void IdenticalFrames_GiveTheIdentity()
    {
        var f = Frame(3, 4, 5);
        var t = StepRigidTransform.FromFrames(f, f, 1);
        Assert.True(t.IsIdentity);
        var p = new Vector3D(0.1, -0.2, 0.3);
        Assert.True(p.Equals(t.Apply(p)));
    }

    [Fact]
    public void TranslationOnly_IsExact()
    {
        var t = StepRigidTransform.FromFrames(Frame(0, 0, 0), Frame(1, 2, 3), 1);
        Assert.Equal(1.0, t.Determinant, 15);
        var p = new Vector3D(0.5, 0.5, 0.5);
        Assert.True(new Vector3D(1.5, 2.5, 3.5).Equals(t.Apply(p)));
        // A direction is rotated but never translated.
        Assert.True(p.Equals(t.ApplyDirection(p)));
    }

    [Fact]
    public void QuarterTurnAboutZ_MapsXToY_Exactly()
    {
        var t = StepRigidTransform.FromFrames(
            Frame(0, 0, 0),
            Frame(0, 0, 0, Vector3D.UnitZ, Vector3D.UnitY), 1);
        Assert.True(Vector3D.UnitY.Equals(t.ApplyDirection(Vector3D.UnitX)));
        Assert.True((-Vector3D.UnitX).Equals(t.ApplyDirection(Vector3D.UnitY)));
        Assert.True(Vector3D.UnitZ.Equals(t.ApplyDirection(Vector3D.UnitZ)));
        Assert.Equal(1.0, t.Determinant, 15);
    }

    [Fact]
    public void Compose_IsOuterAfterInner()
    {
        var inner = StepRigidTransform.FromFrames(Frame(0, 0, 0), Frame(0, 3, 0), 1);
        var outer = StepRigidTransform.FromFrames(
            Frame(0, 0, 0), Frame(5, 0, 0, Vector3D.UnitZ, Vector3D.UnitY), 1);
        var composed = StepRigidTransform.Compose(outer, inner);

        var p = new Vector3D(1, 0, 0);
        Assert.True(outer.Apply(inner.Apply(p)).Equals(composed.Apply(p)));
        Assert.Equal(1.0, composed.Determinant, 15);
        Assert.True(new Vector3D(2, 1, 0).Equals(composed.Apply(p)));  // (1,3,0) → (−3,1,0)+(5,0,0)
    }

    [Fact]
    public void ComposingWithTheIdentity_ChangesNothing()
    {
        var t = StepRigidTransform.FromFrames(
            Frame(1, 1, 1), Frame(2, 0, 0, Vector3D.UnitX, Vector3D.UnitZ), 1);
        Assert.Same(t, StepRigidTransform.Compose(StepRigidTransform.Identity, t));
        Assert.Same(t, StepRigidTransform.Compose(t, StepRigidTransform.Identity));
    }

    [Fact]
    public void GeneralRotation_IsOrthonormal_AndInvertible()
    {
        // A frame that is not axis aligned: the columns must still be orthonormal and the
        // determinant +1, which is what makes the placement rigid.
        var axis = new Vector3D(1, 2, 3).Normalized();
        var t = StepRigidTransform.FromFrames(Frame(0, 0, 0), Frame(0.4, -0.2, 0.9, axis), 1);
        Assert.Equal(1.0, t.Determinant, 12);
        Assert.Equal(1.0, t.Col0.Length, 12);
        Assert.Equal(1.0, t.Col1.Length, 12);
        Assert.Equal(0.0, Vector3D.Dot(t.Col0, t.Col1), 12);
        Assert.Equal(0.0, Vector3D.Dot(t.Col1, t.Col2), 12);

        // Rigid ⇒ distance preserving.
        var a = new Vector3D(0.3, -0.7, 1.1);
        var b = new Vector3D(-0.2, 0.4, 0.05);
        Assert.Equal(Vector3D.Distance(a, b), Vector3D.Distance(t.Apply(a), t.Apply(b)), 12);
    }
}
