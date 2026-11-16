using System.Text.Json;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.NonSequential.Editing;
using S = OpenSim.App.ViewModels.OpticsNonSequentialEditorSchema;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-71: the Non-Sequential Component Editor as the Optics workspace edits it. Gate:
/// every object type round-trips through the editor unchanged; replicating an object gives
/// independent rows at the stated offsets. Plus insert/delete/move renumbering (from the
/// commands and from the grid's own row edits, with undo) and Modify Reference Object, whose
/// global placement before and after is compared through a rotation product derived in the test.
/// Positions, sizes and names are arbitrary test inputs.</summary>
public sealed class OpticsNonSequentialEditorTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>One row of every catalogue type and one of a type the catalogue does not know,
    /// with values left out (null), values the editor shows differently (a lower-case type, a
    /// lower-case Face Is, a short parameter list), faces of numbers the type lacks, and the
    /// system controls around the list.</summary>
    private static OpticsNonSequentialSettings EveryType() => new()
    {
        MaxIntersectionsPerRay = 1234,
        GlueDistance = 1e-6,
        BackgroundMaterial = "1.0003",
        Objects = new()
        {
            new() { Type = "Sphere", Parameters = new() { 2.5, 1 }, Material = "N-BK7", Comment = "ball", X = 1, Z = -3 },
            new()
            {
                Type = "rectangle", Parameters = new() { 4 }, ReferenceObject = 1, Y = 2.25, TiltX = 45,
                Material = "MIRROR", DetectingStandIn = false,
                Faces = new() { new() { Face = 0, FaceIs = "reflective" }, new() { Face = 7, Coating = "KEEP" } },
            },
            new()
            {
                Type = "Standard Lens", Parameters = new() { 50, -0.5, 10, 12, 4, -80, 0, 10, 12, 99 },
                ReferenceObject = -1, InsideOf = 0, X = 0.125, TiltY = -10, TiltZ = 3, GlobalXyzRotationOrder = true,
                Faces = new() { new() { Face = 1, FaceIs = "Object Default", Coating = "AR" }, new() { Face = 2, Coating = "" } },
            },
            new() { Type = "Cylinder Volume", Parameters = Enumerable.Range(1, 40).Select(i => i * 0.5).ToList(), Comment = "not built", InsideOf = 3 },
            new() { Type = "Sphere" },
        },
    };

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    private OpticsNonSequentialEditorViewModel Loaded(OpticsNonSequentialSettings settings)
    {
        var notes = Optics.LoadSettings(Optics.ToSettings() with { NonSequential = settings });
        Assert.Empty(notes);
        return Optics.NonSequentialEditor;
    }

    private static void Type(SheetDocument doc, int row, int slot, string text)
    {
        var result = doc.SetText(row, slot + 1, text);
        Assert.True(result.Succeeded, result.Message);
    }

    private static int[] References(OpticsNonSequentialEditorViewModel e) => e.Rows().Select(r => r.ReferenceObject ?? 0).ToArray();
    private static int[] Insides(OpticsNonSequentialEditorViewModel e) => e.Rows().Select(r => r.InsideOf ?? 0).ToArray();
    private static string?[] Comments(OpticsNonSequentialEditorViewModel e) => e.Rows().Select(r => r.Comment).ToArray();

    // ================================================================= gate: round trip

    [Fact]
    public void EveryObjectType_RoundTripsThroughTheEditorUnchanged()
    {
        var saved = EveryType();
        var editor = Loaded(saved);
        Assert.Equal(5, editor.ObjectCount);
        Assert.Equal(Json(saved), Json(editor.ToSettings()));
        Assert.Equal(Json(saved), Json(Optics.ToSettings().NonSequential));

        // The unknown type is shown under its own name with its raw parameters by number.
        var row = editor.Document.Rows[3];
        Assert.Equal("Cylinder Volume", row.TypeName);
        Assert.Equal(0.5, row.CellFor(S.ParameterKey(1))!.Value.Number);
        Assert.Equal(16, row.CellFor(S.ParameterKey(32))!.Value.Number);

        // An edit and its undo: the undone table is the loaded one again, to the byte.
        Type(editor.Document, 0, S.XSlot, "7");
        Assert.Equal(7, Optics.ToSettings().NonSequential!.Objects![0].X);
        Assert.True(editor.Document.Undo());
        Assert.Equal(Json(saved), Json(editor.ToSettings()));
    }

    [Fact]
    public void Edits_ReachTheSavedSystem_WithOnlyTheEditedValuesWritten()
    {
        var editor = Loaded(EveryType());
        var doc = editor.Document;
        Type(doc, 2, S.FirstParameterSlot, "55");                          // Radius 1
        Assert.True(doc.SetProperty(2, S.CoatingProperty(2), "HR").Succeeded);
        Assert.True(doc.SetProperty(0, S.FaceIsProperty(0), "Absorbing").Succeeded);
        var lens = Optics.ToSettings().NonSequential!.Objects![2];
        Assert.Equal(new double[] { 55, -0.5, 10, 12, 4, -80, 0, 10, 12, 99 }, lens.Parameters);   // the tenth, past the type's nine, kept
        Assert.Equal("HR", lens.Faces!.Single(f => f.Face == 2).Coating);
        Assert.Equal("AR", lens.Faces!.Single(f => f.Face == 1).Coating);
        var ball = Optics.ToSettings().NonSequential!.Objects![0];
        Assert.Equal("Absorbing", ball.Faces!.Single().FaceIs);
        Assert.Null(ball.Y);                                                   // never set, never written
    }

    // ================================================================= renumbering

    /// <summary>Objects: 1; 2 ref 1; 3 ref −1 (object 2), inside 1; 4 ref 2, inside −3 (object 1).</summary>
    private static OpticsNonSequentialSettings Linked() => new()
    {
        Objects = new()
        {
            new() { Type = "Standard Lens", Comment = "one" },
            new() { Type = "Sphere", ReferenceObject = 1, Comment = "two" },
            new() { Type = "Rectangle", ReferenceObject = -1, InsideOf = 1, Comment = "three" },
            new() { Type = "Rectangle", ReferenceObject = 2, InsideOf = -3, Comment = "four" },
        },
    };

    [Fact]
    public void Insert_FromTheCommand_KeepsTheLinksOnTheSameObjects_AsOneUndoStep()
    {
        var e = Loaded(Linked());
        e.CurrentRow = 1;
        e.NewObjectType = "Rectangle";
        e.InsertObjectCommand.Execute(null);
        // Objects 2, 3, 4 are now 3, 4, 5: 3 → 4 keeps −1 (object 3); 4 → 5 names 3 and 1 − 5 = −4.
        Assert.Equal(new string?[] { "one", null, "two", "three", "four" }, Comments(e));
        Assert.Equal(new[] { 0, 0, 1, -1, 3 }, References(e));
        Assert.Equal(new[] { 0, 0, 0, 1, -4 }, Insides(e));
        Assert.Equal("Rectangle", e.Rows()[1].Type);

        Assert.True(e.Document.Undo());
        Assert.Equal(Json(Linked()), Json(e.ToSettings()));
    }

    [Fact]
    public void Delete_RenumbersTheRest_AndIsRefusedForAReferencedObject()
    {
        var e = Loaded(Linked());
        e.CurrentRow = 1;
        e.DeleteObjectCommand.Execute(null);
        Assert.Contains("Object 3", e.Status);
        Assert.Contains("deleted", e.Status);
        Assert.Equal(4, e.ObjectCount);

        e.CurrentRow = 2;
        e.DeleteObjectCommand.Execute(null);
        Assert.Equal(new string?[] { "one", "two", "four" }, Comments(e));
        Assert.Equal(new[] { 0, 1, 2 }, References(e));
        Assert.Equal(new[] { 0, 0, -2 }, Insides(e));   // object 1 from object 3: 1 − 3

        Assert.True(e.Document.Undo());
        Assert.Equal(Json(Linked()), Json(e.ToSettings()));
    }

    [Fact]
    public void Move_FromTheGridsOwnRowEdit_Renumbers_AndUndoRestores()
    {
        var e = Loaded(Linked());
        // The grid's Alt+Up calls the sheet directly; the hook renumbers in the same step.
        Assert.True(e.Document.MoveRows(new[] { 3 }, -1).Succeeded);
        Assert.Equal(new string?[] { "one", "two", "four", "three" }, Comments(e));
        Assert.Equal(new[] { 0, 1, 2, -2 }, References(e));
        Assert.Equal(new[] { 0, 0, -2, 1 }, Insides(e));
        Assert.True(e.Document.Undo());
        Assert.Equal(Json(Linked()), Json(e.ToSettings()));
        Assert.True(e.Document.Redo());
        Assert.Equal(new[] { 0, 1, 2, -2 }, References(e));

        // Object 2 below object 3, which refers to it: refused, nothing changed.
        var refused = Loaded(Linked()).Document.MoveRows(new[] { 1 }, +1);
        Assert.False(refused.Succeeded);
        Assert.Contains("would come after it", refused.Message);
    }

    [Fact]
    public void Insert_FromTheGridsOwnRowEdit_AlsoRenumbers()
    {
        var e = Loaded(Linked());
        Assert.True(e.Document.InsertRows(0).Succeeded);
        Assert.Equal(new[] { 0, 0, 2, -1, 3 }, References(e));
        Assert.Equal(new[] { 0, 0, 0, 2, -3 }, Insides(e));
    }

    // ================================================================= refusals

    [Fact]
    public void ObjectsOfATypeNotBuilt_AreRefused_ByName()
    {
        var e = Loaded(EveryType());
        // Source File is in OP-77's list but refused (ray files are OP-78): its own reason is given.
        e.NewObjectType = "Source File";
        e.InsertObjectCommand.Execute(null);
        Assert.Contains("OP-78", e.Status);
        Assert.Equal(5, e.ObjectCount);

        var typed = e.Document.SetText(0, SheetSchema.TypeColumn, "Cylinder Pipe");
        Assert.False(typed.Succeeded);
        Assert.Contains("OP-72", typed.Message);

        // The saved unknown type has a row type, but no other row may take it.
        Assert.True(e.Document.SetText(4, SheetSchema.TypeColumn, "Cylinder Volume").Succeeded);
        Assert.Equal("Sphere", e.Rows()[4].Type);
        Assert.Contains("not built", e.Status);

        e.CombineObjectsCommand.Execute(null);
        Assert.Contains("OP-74", e.Status);
        e.CreatePolygonObjectCommand.Execute(null);
        Assert.Contains("OP-74", e.Status);
    }

    // ================================================================= gate: replicate

    [Fact]
    public void Replicate_GivesIndependentRowsAtTheStatedOffsets_AsOneUndoStep()
    {
        var e = Loaded(Linked());
        e.CurrentRow = 1;   // object 2, the sphere
        e.ReplicateNumberX = 3;
        e.ReplicateNumberY = 1;
        e.ReplicateNumberZ = 3;
        e.ReplicateDeltaX = 1.5;
        e.ReplicateDeltaZ = 2;
        e.ReplicateObjectCommand.Execute(null);
        Assert.Contains("8 copies", e.Status);

        var rows = e.Rows();
        Assert.Equal(4 + 8, rows.Count);
        // x fastest, then z; the centre (the original) left out.
        var expected = new (double X, double Z)[] { (-1.5, -2), (0, -2), (1.5, -2), (-1.5, 0), (1.5, 0), (-1.5, 2), (0, 2), (1.5, 2) };
        for (int k = 0; k < 8; k++)
        {
            var copy = rows[2 + k];
            Assert.Equal("Sphere", copy.Type);
            Assert.Equal("two", copy.Comment);
            Assert.Equal(2, copy.ReferenceObject);
            Assert.Equal((expected[k].X, 0.0, expected[k].Z), (copy.X ?? 0, copy.Y ?? 0, copy.Z ?? 0));
        }
        // The objects after: "three" (ref −1 to object 2) is now 11: 2 − 11 = −9; "four" is 12.
        Assert.Equal(-9, rows[10].ReferenceObject);
        Assert.Equal(1, rows[10].InsideOf);
        Assert.Equal(2, rows[11].ReferenceObject);
        Assert.Equal(1 - 12, rows[11].InsideOf);

        // Independent: a copy's radius changes alone.
        Type(e.Document, 4, S.FirstParameterSlot, "9");
        Assert.Equal(9, e.Rows()[4].Parameters![0]);
        Assert.Equal(1, e.Document.Rows[1].CellFor(S.ParameterKey(1))!.Value.Number);   // the original's radius, the default 1

        Assert.True(e.Document.Undo());   // the edit
        Assert.True(e.Document.Undo());   // the whole replication
        Assert.Equal(Json(Linked()), Json(e.ToSettings()));
    }

    // ================================================================= modify reference

    private static double[,] Rx(double deg) { double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); return new[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } }; }
    private static double[,] Ry(double deg) { double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); return new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } }; }
    private static double[,] Rz(double deg) { double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); return new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } }; }

    private static double[,] Mul(double[,] a, double[,] b)
    {
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) m[i, j] += a[i, k] * b[k, j];
        return m;
    }

    /// <summary>Global frames derived from Conventions §69 (G = O + R·L, R = Rx·Ry·Rz or, in the
    /// global order, Rz·Ry·Rx; through a reference G′ = O′ + R′G), independent of the code.</summary>
    private static (double[,] R, double[] O)[] Globals(IReadOnlyList<OpticsNscObjectSettings> rows)
    {
        var g = new (double[,] R, double[] O)[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            double a = r.TiltX ?? 0, b = r.TiltY ?? 0, c = r.TiltZ ?? 0;
            var R = r.GlobalXyzRotationOrder == true ? Mul(Rz(c), Mul(Ry(b), Rx(a))) : Mul(Rx(a), Mul(Ry(b), Rz(c)));
            var O = new[] { r.X ?? 0, r.Y ?? 0, r.Z ?? 0 };
            int reference = (r.ReferenceObject ?? 0) < 0 ? i + 1 + r.ReferenceObject!.Value : r.ReferenceObject ?? 0;
            if (reference > 0)
            {
                var (pr, po) = g[reference - 1];
                O = new[]
                {
                    po[0] + pr[0, 0] * O[0] + pr[0, 1] * O[1] + pr[0, 2] * O[2],
                    po[1] + pr[1, 0] * O[0] + pr[1, 1] * O[1] + pr[1, 2] * O[2],
                    po[2] + pr[2, 0] * O[0] + pr[2, 1] * O[1] + pr[2, 2] * O[2],
                };
                R = Mul(pr, R);
            }
            g[i] = (R, O);
        }
        return g;
    }

    /// <summary>Modify Reference Object on objects 3 and 4 of a tilted chain (both rotation
    /// orders, a relative reference): every object's global origin and rotation before and after
    /// agree to 1e-12 (positions under 10 lens units: double rounding of the products is near
    /// 1e-14), each keeps its rotation order, and the reference form is kept.</summary>
    [Fact]
    public void ModifyReference_KeepsTheGlobalPlacement_AsOneUndoStep()
    {
        var chain = new OpticsNonSequentialSettings
        {
            Objects = new()
            {
                new() { Type = "Sphere", X = 3, Y = -2, Z = 5, TiltX = 10, TiltY = 20, TiltZ = 30 },
                new() { Type = "Sphere", ReferenceObject = 1, X = 1, Y = 2, Z = 3, TiltX = -15, TiltY = 40, TiltZ = 5, GlobalXyzRotationOrder = true },
                new() { Type = "Sphere", ReferenceObject = -1, X = -4, Y = 0.5, Z = 7, TiltX = 25, TiltY = -35, TiltZ = 60, GlobalXyzRotationOrder = true },
                new() { Type = "Sphere", ReferenceObject = 3, X = 2, Y = 2, Z = 2, TiltY = 90, TiltZ = 30 },
                new() { Type = "Sphere", ReferenceObject = 4, X = 0.25, Z = -1, TiltX = 5 },
            },
        };
        var e = Loaded(chain);
        var before = e.Rows();
        e.ModifyFirstObject = 3;
        e.ModifyLastObject = 4;
        e.ModifyReferTo = 1;
        e.ModifyReferenceObjectCommand.Execute(null);
        Assert.Contains("same global place", e.Status);
        var after = e.Rows();
        Assert.Equal(new[] { 0, 1, 1 - 3, 1, 4 }, after.Select(r => r.ReferenceObject ?? 0));
        Assert.Equal(new bool?[] { null, true, true, null, null }, after.Select(r => r.GlobalXyzRotationOrder));

        var g0 = Globals(before);
        var g1 = Globals(after);
        for (int i = 0; i < g0.Length; i++)
            for (int k = 0; k < 3; k++)
            {
                Assert.True(Math.Abs(g0[i].O[k] - g1[i].O[k]) <= 1e-12, $"Object {i + 1}, origin {k}: {g0[i].O[k]:R} before, {g1[i].O[k]:R} after.");
                for (int j = 0; j < 3; j++)
                    Assert.True(Math.Abs(g0[i].R[k, j] - g1[i].R[k, j]) <= 1e-12, $"Object {i + 1}, R[{k},{j}].");
            }

        Assert.True(e.Document.Undo());
        Assert.Equal(Json(chain), Json(e.ToSettings()));

        Assert.Contains("must precede", e.ModifyReference(2, 3, 2));
    }

    [Fact]
    public void ViewCurrentObject_MeshesTheObject_AndRefusesAnUnbuiltType()
    {
        var e = Loaded(EveryType());
        e.CurrentRow = 2;
        var view = e.ViewCurrentObject(out string? refusal);
        Assert.Null(refusal);
        Assert.NotNull(view);
        Assert.Equal("Object 3: Standard Lens", view!.Title);
        Assert.Equal(new[] { 0, 1, 2 }, view.Mesh.TriangleFaceIds.Distinct().OrderBy(f => f));
        int coarse = view.Mesh.Triangles.Count;
        view.Resolution = NscDrawResolution.All[3];
        Assert.True(view.Mesh.Triangles.Count > coarse);

        e.CurrentRow = 3;
        Assert.Null(e.ViewCurrentObject(out refusal));
        Assert.Contains("OP-72", refusal);
    }

    [Fact]
    public void Check_ShowsTheProblems_AndThePanelCount()
    {
        var e = Loaded(EveryType());
        Assert.True(e.HasProblems);
        Assert.Contains(e.Problems, p => p.Contains("Object 4") && p.Contains("Cylinder Volume"));
        Optics.Reset();
        Assert.Equal(0, Optics.NonSequentialEditor.ObjectCount);
        Assert.Null(Optics.ToSettings().NonSequential);
        Assert.False(Optics.NonSequentialEditor.HasProblems);
    }
}
