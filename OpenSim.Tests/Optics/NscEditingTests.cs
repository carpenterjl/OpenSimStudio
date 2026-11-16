using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Editing;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-71: the UI-free parts of the Non-Sequential Component Editor (docs/Optics-Conventions.md
/// §71): the object-type catalogue, reference renumbering on insert/delete/move, Replicate
/// Object (gate: N independent rows at the stated offsets), Modify Reference Object (global
/// placement kept, checked against a rotation product derived here), the check and the
/// single-object meshes. Every position, tilt and size is an arbitrary test input.
/// </summary>
public sealed class NscEditingTests
{
    // ================================================================= catalogue

    [Fact]
    public void Catalogue_HoldsOp70sTypes_InTheOrderAndWithTheFacesOfTheirHelpPages()
    {
        var c = NscObjectCatalogue.Built;
        Assert.Equal(new[] { "Sphere", "Rectangle", "Standard Lens" }, c.Types.Select(t => t.TypeName));
        // The parameter counts the saved scene reader requires (ProjectNonSequential: 2, 2, 9).
        Assert.Equal(new[] { 2, 2, 9 }, c.Types.Select(t => t.Parameters.Count));
        Assert.Equal(new[] { "Radius", "Is Volume?" }, c.Find("sphere")!.Parameters.Select(p => p.Name));
        Assert.Equal(new[] { 0, 1, 2 }, c.Find(" Standard Lens ")!.Faces.Keys.OrderBy(k => k));
        Assert.Null(c.Find("Cylinder Volume"));
        foreach (var name in c.Types.Select(t => t.TypeName)) Assert.Contains(name, ProjectNonSequential.BuiltTypes);
    }

    [Fact]
    public void Catalogue_DefaultsBuildEveryType_WithTheGeometrysOwnFaceNumbers()
    {
        foreach (var d in NscObjectCatalogue.Built.Types)
        {
            var g = d.Build(d.Defaults, LensUnit.Millimeter);
            Assert.Equal(d.TypeName, g.TypeName);
            Assert.Equal(d.Faces.Keys.OrderBy(k => k), g.Faces.Keys.OrderBy(k => k));
            Assert.Equal(d.IsVolumeFor(d.Defaults), g.IsVolume);
        }
        var sphere = NscCoreObjectTypes.Sphere;
        Assert.False(sphere.IsVolumeFor(new[] { 2.0, 0.0 }));
        Assert.True(sphere.IsVolumeFor(new[] { 2.0, -1.0 }));
        // Lengths are converted from the lens unit: radius 5 mm is 0.005 m.
        Assert.Equal(0.005, ((NscSphere)sphere.Build(new[] { 5.0, 1.0 }, LensUnit.Millimeter)).Radius, 15);
    }

    [Fact]
    public void Catalogue_JoinsMoreTypes_RefusesADuplicate_AndNamesThePlanItemOfAnUnbuiltType()
    {
        var extra = new NscObjectTypeDescriptor
        {
            TypeName = "Test Source", Category = NscObjectCategory.Source,
            Parameters = new[] { new NscParameterDescriptor("Layout Rays", 10, NscParameterKind.Integer) },
            Faces = new Dictionary<int, string> { [0] = "All Faces" }, IsVolume = false,
        };
        var joined = NscObjectCatalogue.Built.Join(new[] { extra });
        Assert.Equal(4, joined.Types.Count);
        Assert.Same(extra, joined.Find("test source"));
        var dup = Assert.Throws<ArgumentException>(() => joined.Join(new[] { extra with { TypeName = "SPHERE" } }));
        Assert.Contains("twice", dup.Message);
        var noFaces = Assert.Throws<ArgumentException>(() => new NscObjectCatalogue(new[] { extra with { Faces = new Dictionary<int, string>() } }));
        Assert.Contains("no faces", noFaces.Message);

        var c = NscObjectCatalogue.Built;
        Assert.Contains("OP-77", c.NotBuiltMessage("Source Ellipse"));
        Assert.Contains("OP-79", c.NotBuiltMessage("Detector Rectangle"));
        Assert.Contains("OP-72", c.NotBuiltMessage("Cylinder Volume"));
        Assert.Contains("'Cylinder Volume'", c.NotBuiltMessage("Cylinder Volume"));
    }

    // ================================================================= renumbering

    /// <summary>Objects: 1 (0, 0); 2 ref 1; 3 ref −1 (object 2), inside 1; 4 ref 2, inside −3
    /// (object 1).</summary>
    private static List<OpticsNscObjectSettings> Linked() => new()
    {
        new() { Type = "Standard Lens", Comment = "one" },
        new() { Type = "Sphere", ReferenceObject = 1, Comment = "two" },
        new() { Type = "Rectangle", ReferenceObject = -1, InsideOf = 1, Comment = "three" },
        new() { Type = "Rectangle", ReferenceObject = 2, InsideOf = -3, Comment = "four" },
    };

    private static (int, int)[] LinksOf(IEnumerable<OpticsNscObjectSettings> rows) =>
        rows.Select(r => (r.ReferenceObject ?? 0, r.InsideOf ?? 0)).ToArray();

    [Fact]
    public void Insert_KeepsEveryLinkOnTheSameObject_InItsOwnForm()
    {
        // A row before object 2: objects 2, 3, 4 become 3, 4, 5. Object 3 (now 4) keeps naming
        // object 2 (now 3) relatively: 3 − 4 = −1; object 4 (now 5) names object 2 (now 3)
        // absolutely and object 1 relatively: 1 − 5 = −4.
        var rows = NscObjectTable.Insert(Linked(), 1, new[] { new OpticsNscObjectSettings { Type = "Sphere", Comment = "new" } });
        Assert.Equal(new[] { "one", "new", "two", "three", "four" }, rows.Select(r => r.Comment));
        Assert.Equal(new[] { (0, 0), (0, 0), (1, 0), (-1, 1), (3, -4) }, LinksOf(rows));
    }

    [Fact]
    public void Delete_RenumbersTheRest_AndRefusesToDeleteAReferencedObject()
    {
        // Delete object 3: object 4 becomes 3; inside −3 named object 1, now 1 − 3 = −2.
        var rows = NscObjectTable.Delete(Linked(), new[] { 2 });
        Assert.Equal(new[] { (0, 0), (1, 0), (2, -2) }, LinksOf(rows));

        var refused = Assert.Throws<InvalidOperationException>(() => NscObjectTable.Delete(Linked(), new[] { 1 }));
        Assert.Contains("Object 3", refused.Message);
        Assert.Contains("Object 4", refused.Message);
        Assert.Contains("deleted", refused.Message);
    }

    [Fact]
    public void Move_RenumbersBothWays_AndRefusesToPutAReferenceAfterItsUser()
    {
        // Object 4 up one: objects 3 and 4 swap. Old 3 (now 4) names old 2 (still 2): 2 − 4 = −2;
        // old 4 (now 3) names old 1 relatively: 1 − 3 = −2.
        var rows = NscObjectTable.Move(Linked(), new[] { 3 }, -1);
        Assert.Equal(new[] { "one", "two", "four", "three" }, rows.Select(r => r.Comment));
        Assert.Equal(new[] { (0, 0), (1, 0), (2, -2), (-2, 1) }, LinksOf(rows));

        // Object 2 down one would put it after object 3, which refers to it.
        var refused = Assert.Throws<InvalidOperationException>(() => NscObjectTable.Move(Linked(), new[] { 1 }, +1));
        Assert.Contains("would come after it", refused.Message);
    }

    [Fact]
    public void Remap_LeavesALinkThatWasAlreadyBroken()
    {
        var links = new[] { new NscObjectLinks(0, 0), new NscObjectLinks(5, -7) };
        var remapped = NscObjectTable.Remap(links, new[] { 1, 0 }, out string? refusal);
        Assert.Null(refusal);
        Assert.Equal(new NscObjectLinks(5, -7), remapped[1]);
    }

    // ================================================================= placement (derived)

    /// <summary>The rotation matrices written out here, independent of RigidTransform: Rx(a)
    /// turns +y toward +z, Ry(b) +z toward +x, Rz(c) +x toward +y (right-handed, §4).</summary>
    private static double[,] Rx(double deg) { double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); return new[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } }; }
    private static double[,] Ry(double deg) { double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); return new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } }; }
    private static double[,] Rz(double deg) { double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); return new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } }; }

    private static double[,] Mul(double[,] a, double[,] b)
    {
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) m[i, j] += a[i, k] * b[k, j];
        return m;
    }

    /// <summary>The global frames (rotation, origin) of saved rows, derived from §69: local
    /// R = Rx·Ry·Rz (or Rz·Ry·Rx in the global order), G = O + R·L, and through a reference
    /// object G′ = O′ + R′(O + R·L).</summary>
    private static (double[,] R, double[] O)[] DerivedGlobals(IReadOnlyList<OpticsNscObjectSettings> rows)
    {
        var g = new (double[,] R, double[] O)[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            double a = r.TiltX ?? 0, b = r.TiltY ?? 0, c = r.TiltZ ?? 0;
            var R = r.GlobalXyzRotationOrder == true ? Mul(Rz(c), Mul(Ry(b), Rx(a))) : Mul(Rx(a), Mul(Ry(b), Rz(c)));
            var O = new[] { r.X ?? 0, r.Y ?? 0, r.Z ?? 0 };
            int reference = r.ReferenceObject is int ref0 && ref0 < 0 ? i + 1 + ref0 : r.ReferenceObject ?? 0;
            if (reference > 0)
            {
                var (pr, po) = g[reference - 1];
                var o = new double[3];
                for (int k = 0; k < 3; k++) o[k] = po[k] + pr[k, 0] * O[0] + pr[k, 1] * O[1] + pr[k, 2] * O[2];
                R = Mul(pr, R);
                O = o;
            }
            g[i] = (R, O);
        }
        return g;
    }

    private static void SameGlobals(IReadOnlyList<OpticsNscObjectSettings> before, IReadOnlyList<OpticsNscObjectSettings> after, double tolerance)
    {
        var a = DerivedGlobals(before);
        var b = DerivedGlobals(after);
        for (int i = 0; i < a.Length; i++)
        {
            for (int k = 0; k < 3; k++)
            {
                Assert.True(Math.Abs(a[i].O[k] - b[i].O[k]) <= tolerance, $"Object {i + 1}, origin {k}: {a[i].O[k]:R} before, {b[i].O[k]:R} after.");
                for (int j = 0; j < 3; j++)
                    Assert.True(Math.Abs(a[i].R[k, j] - b[i].R[k, j]) <= tolerance, $"Object {i + 1}, R[{k},{j}]: {a[i].R[k, j]:R} before, {b[i].R[k, j]:R} after.");
            }
        }
    }

    /// <summary>A chain with both rotation orders, a relative reference and a 90° y tilt (where
    /// only x ± z tilt is defined). Lens units; positions under 10, so double rounding of the
    /// products is near 1e-14 and 1e-12 leaves a margin of about a hundred.</summary>
    private static List<OpticsNscObjectSettings> Chain() => new()
    {
        new() { Type = "Sphere", X = 3, Y = -2, Z = 5, TiltX = 10, TiltY = 20, TiltZ = 30 },
        new() { Type = "Sphere", ReferenceObject = 1, X = 1, Y = 2, Z = 3, TiltX = -15, TiltY = 40, TiltZ = 5, GlobalXyzRotationOrder = true },
        new() { Type = "Sphere", ReferenceObject = -1, X = -4, Y = 0.5, Z = 7, TiltX = 25, TiltY = -35, TiltZ = 60 },
        new() { Type = "Sphere", ReferenceObject = 3, X = 2, Y = 2, Z = 2, TiltY = 90, TiltZ = 30 },
        new() { Type = "Sphere", ReferenceObject = 4, X = 0.25, Z = -1, TiltX = 5, GlobalXyzRotationOrder = true },
    };

    [Fact]
    public void Globals_AgreeWithTheDerivedProduct()
    {
        var rows = Chain();
        var derived = DerivedGlobals(rows);
        var globals = NscObjectPlacement.Globals(rows);
        for (int i = 0; i < rows.Count; i++)
        {
            var o = globals[i].Translation;
            Assert.Equal(derived[i].O[0], o.X, 12);
            Assert.Equal(derived[i].O[1], o.Y, 12);
            Assert.Equal(derived[i].O[2], o.Z, 12);
            Assert.Equal(derived[i].R[0, 2], globals[i].Col2.X, 12);
            Assert.Equal(derived[i].R[2, 0], globals[i].Col0.Z, 12);
        }
    }

    [Theory]
    [InlineData(10, 20, 30, false)]
    [InlineData(-170, 89.5, 45, false)]
    [InlineData(10, 20, 30, true)]
    [InlineData(33, -60, -150, true)]
    [InlineData(0, 90, 30, false)]
    [InlineData(20, -90, 0, true)]
    public void FromTransform_ReproducesTheTransform(double a, double b, double c, bool globalOrder)
    {
        var p = new NscPlacement(1.5, -2, 3, a, b, c, globalOrder);
        var t = p.ToTransform();
        var back = NscObjectPlacement.FromTransform(t, globalOrder).ToTransform();
        foreach (var (u, v) in new[] { (t.Col0, back.Col0), (t.Col1, back.Col1), (t.Col2, back.Col2), (t.Translation, back.Translation) })
        {
            Assert.Equal(u.X, v.X, 13);
            Assert.Equal(u.Y, v.Y, 13);
            Assert.Equal(u.Z, v.Z, 13);
        }
    }

    [Fact]
    public void ModifyReference_KeepsEveryGlobalPlacement_AndTheReferenceForm()
    {
        var before = Chain();
        // Objects 3 and 4 re-referenced to object 1: 3 was relative (−1), so it becomes 1 − 3 = −2;
        // 4 was absolute, so it becomes 1. Object 5 still refers to 4, whose global frame is kept.
        var after = NscObjectPlacement.ModifyReference(before, 3, 4, 1);
        Assert.Equal(-2, after[2].ReferenceObject);
        Assert.Equal(1, after[3].ReferenceObject);
        Assert.Equal(before[4], after[4]);
        Assert.Equal(before[1], after[1]);
        SameGlobals(before, after, 1e-12);

        // All of 2..5 to the scene frame.
        var global = NscObjectPlacement.ModifyReference(before, 2, 5, 0);
        Assert.All(global.Skip(1), r => Assert.Equal(0, r.ReferenceObject));
        SameGlobals(before, global, 1e-12);
    }

    [Fact]
    public void ModifyReference_RefusesAReferenceThatDoesNotPrecedeTheRange()
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => NscObjectPlacement.ModifyReference(Chain(), 2, 3, 2));
        Assert.Contains("must precede", e.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => NscObjectPlacement.ModifyReference(Chain(), 4, 6, 1));
    }

    // ================================================================= gate: replicate

    private static List<OpticsNscObjectSettings> ForReplicate() => new()
    {
        new() { Type = "Rectangle", Parameters = new() { 5, 5 }, Material = "ABSORB" },
        new()
        {
            Type = "Standard Lens", Comment = "lenslet", ReferenceObject = 1, X = 1, Y = 2, Z = 10, TiltX = 5,
            Parameters = new() { 20, 0, 2, 2, 1.5, -20, 0, 2, 2 }, Material = "1.5",
            Faces = new() { new() { Face = 1, Coating = "AR" } },
        },
        new() { Type = "Sphere", ReferenceObject = 2, Parameters = new() { 1, 1 } },
        new() { Type = "Sphere", ReferenceObject = -2, Parameters = new() { 1, 1 } },
    };

    [Fact]
    public void Replicate_ThreeByThree_GivesNineIndependentRowsAtTheStatedOffsets()
    {
        var s = new NscReplicateSettings { Object = 2, NumberX = 3, NumberY = 3, DeltaX = 2.5, DeltaY = -4 };
        var before = ForReplicate();
        var rows = NscReplicate.Apply(before, s);
        Assert.Equal(4 + 8, rows.Count);

        // Offsets in insertion order, x fastest: (i − 1)·2.5, (j − 1)·(−4), the centre (the
        // original) left out.
        var expected = new (double X, double Y)[]
        {
            (-2.5, 4), (0, 4), (2.5, 4),
            (-2.5, 0), (2.5, 0),
            (-2.5, -4), (0, -4), (2.5, -4),
        };
        var array = rows.Skip(1).Take(9).ToList();   // the original and its copies: N = 9 rows
        Assert.Equal(9, array.Count);
        Assert.Equal(before[1], array[0]);
        for (int k = 0; k < 8; k++)
        {
            var copy = rows[2 + k];
            Assert.Equal("Standard Lens", copy.Type);
            Assert.Equal("lenslet", copy.Comment);
            Assert.Equal(2, copy.ReferenceObject);               // absolute, to the original
            Assert.Equal((expected[k].X, expected[k].Y, 0.0), (copy.X!.Value, copy.Y!.Value, copy.Z!.Value));
            Assert.Equal((0.0, 0.0, 0.0), (copy.TiltX!.Value, copy.TiltY!.Value, copy.TiltZ!.Value));
            Assert.Equal(before[1].Parameters, copy.Parameters);
            Assert.NotSame(before[1].Parameters, copy.Parameters);   // independent rows
            Assert.NotSame(before[1].Faces, copy.Faces);
            Assert.Equal("AR", copy.Faces![0].Coating);
        }
        // The objects after the original: old 3 (ref 2) is 11 and still names object 2; old 4
        // (ref −2, object 2) is 12 and names it as 2 − 12 = −10.
        Assert.Equal(2, rows[10].ReferenceObject);
        Assert.Equal(-10, rows[11].ReferenceObject);

        // Each copy's global frame is the original's moved by its offset in the original's frame.
        var g = NscObjectPlacement.Globals(rows);
        var original = g[1];
        for (int k = 0; k < 8; k++)
        {
            var want = original.Apply(new Vector3D(expected[k].X, expected[k].Y, 0));
            var got = g[2 + k].Translation;
            Assert.True((want - got).Length <= 1e-13, $"Copy {k + 1}: {got} against {want}.");
        }
    }

    [Fact]
    public void Replicate_RelativeReferences_AndAnOddLineOfFive()
    {
        var s = new NscReplicateSettings { Object = 2, NumberX = 5, DeltaX = 3, RelativeReferences = true };
        var rows = NscReplicate.Apply(ForReplicate(), s);
        Assert.Equal(4 + 4, rows.Count);
        // Copies 3..6 at x = −6, −3, 3, 6 with references 2 − 3 … 2 − 6.
        Assert.Equal(new[] { -6.0, -3.0, 3.0, 6.0 }, rows.Skip(2).Take(4).Select(r => r.X!.Value));
        Assert.Equal(new[] { -1, -2, -3, -4 }, rows.Skip(2).Take(4).Select(r => r.ReferenceObject!.Value));
    }

    [Fact]
    public void Replicate_RefusesAnEvenCount_PickupSolves_AndAMissingObject()
    {
        Assert.Contains("even count", NscReplicate.Check(new NscReplicateSettings { Object = 2, NumberX = 2 }, 4));
        Assert.Contains("Add Pickup Solves", NscReplicate.Check(new NscReplicateSettings { Object = 2, NumberX = 3, AddPickupSolves = true }, 4));
        Assert.Contains("does not exist", NscReplicate.Check(new NscReplicateSettings { Object = 9, NumberX = 3 }, 4));
        Assert.Throws<ArgumentException>(() => NscReplicate.Apply(ForReplicate(), new NscReplicateSettings { Object = 2 }));
    }

    // ================================================================= check, tools, meshes

    [Fact]
    public void Check_UsesTheScenesRules_AndNamesAnUnbuiltType()
    {
        var c = NscObjectCatalogue.Built;
        Assert.Empty(NscObjectCheck.Problems(ForReplicate(), c, LensUnit.Millimeter));

        var hollow = new List<OpticsNscObjectSettings> { new() { Type = "Sphere", Parameters = new() { 1, 0 } } };
        Assert.Contains(NscObjectCheck.Problems(hollow, c, LensUnit.Millimeter), p => p.Contains("hollow sphere"));

        var unknown = new List<OpticsNscObjectSettings> { new() { Type = "Cylinder Volume", Parameters = new() { 1, 2, 3 } } };
        Assert.Contains(NscObjectCheck.Problems(unknown, c, LensUnit.Millimeter), p => p.Contains("OP-72"));

        var coated = ForReplicate();
        Assert.Contains(NscObjectCheck.Problems(coated, c, LensUnit.Millimeter, new[] { "HEAR1" }), p => p.Contains("'AR'"));
    }

    [Fact]
    public void NotBuiltTools_NameTheirPlanItems()
    {
        Assert.Contains("OP-74", NscNotBuiltTools.CombineObjects);
        Assert.Contains("OP-75", NscNotBuiltTools.CombineObjects);
        Assert.Contains("OP-74", NscNotBuiltTools.CreatePolygonObject);
        Assert.Contains("OP-72", NscNotBuiltTools.FreeformZTools);
        foreach (var section in new[] { "Scatter To", "Volume Physics", "Diffraction", "GRIN", "birefringent" })
            Assert.Contains(section, NscNotBuiltTools.PropertySectionsText);
    }

    [Fact]
    public void Meshes_AreClosedWhereTheObjectIsAVolume_AndCarryTheFaceNumbers()
    {
        var lens = NscCoreObjectTypes.StandardLens.Build(new double[] { 20, 0, 4, 5, 3, -30, -1, 4, 5 }, LensUnit.Meter);
        var mesh = NscObjectMesher.Mesh(lens, 8, 24);
        Assert.True(mesh.IsWatertight());
        Assert.Equal(new[] { 0, 1, 2 }, mesh.TriangleFaceIds.Distinct().OrderBy(f => f));
        var box = lens.LocalBounds;
        Assert.All(mesh.Vertices, v => Assert.True(v.X >= box.Min.X - 1e-12 && v.X <= box.Max.X + 1e-12 && v.Z >= box.Min.Z - 1e-12 && v.Z <= box.Max.Z + 1e-12));

        var sphere = NscObjectMesher.Mesh(new NscSphere(2), 6, 16);
        Assert.True(sphere.IsWatertight());
        Assert.All(sphere.Vertices, v => Assert.Equal(2, v.Length, 12));

        var rect = NscObjectMesher.Mesh(new NscRectangle(3, 2));
        Assert.Equal(2, rect.Triangles.Count);
        Assert.Equal(24, rect.TriangleArea(0) + rect.TriangleArea(1), 12);   // 2·3 × 2·2
    }
}
