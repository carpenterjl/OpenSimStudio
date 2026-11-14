using System.Text.Json;
using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.LensEditing;
using S = OpenSim.App.ViewModels.OpticsLensEditorSchema;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-31: the Lens Data Editor as the Optics workspace edits it. Radii, thicknesses
/// and indices are arbitrary inputs; the focal length after Make Focal is checked against the
/// target, as in the library's gate.</summary>
public sealed class OpticsLensEditorTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private static string Json(object o) => JsonSerializer.Serialize(o);

    /// <summary>A singlet in air (mm): n = 1.5, front radius 40, back −120.</summary>
    private static List<OpticsSurfaceSettings> Singlet() => new()
    {
        new() { ThicknessIsInfinite = true },
        new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true },
        new() { Radius = -120, Thickness = 90 },
        new(),
    };

    private OpticsLensEditorViewModel Loaded(List<OpticsSurfaceSettings> surfaces)
    {
        var editor = Optics.LensEditor;
        var notes = new List<string>();
        editor.LoadSettings(surfaces, notes);
        Assert.Empty(notes);
        return editor;
    }

    [Fact]
    public void TheEditor_StartsWithObjectStopAndImage_AndPublishesThem()
    {
        var editor = Optics.LensEditor;
        var doc = editor.Document;
        Assert.Equal(3, doc.RowCount);
        Assert.True(doc.Rows[0].IsLocked);
        Assert.False(doc.Rows[1].IsLocked);
        Assert.True(doc.Rows[2].IsLocked);
        Assert.Equal(Json(OpticsLensEditorViewModel.DefaultSurfaces()), Json(editor.ToSettings()));
        Assert.Equal(Json(editor.ToSettings()), Json(Optics.ToSettings().Surfaces!));
        Assert.Equal(3, _app.Resolve<ProjectSession>().OpticsSystem!.Surfaces!.Count);
        // The clear semi-diameter starts automatic.
        Assert.Equal(S.AutomaticSolve, doc.Rows[1].Cells[S.SemiDiameterSlot].Flag.SolveCode);
        Assert.Equal(0, doc.History.UndoCount);
    }

    [Fact]
    public void ATypedValue_ReachesTheProject_AndUndoes()
    {
        var editor = Optics.LensEditor;
        var session = _app.Resolve<ProjectSession>();
        var doc = editor.Document;
        Assert.True(doc.SetText(1, S.RadiusSlot + 1, "50").Succeeded);
        Assert.Equal(50, editor.ToSettings()[1].Radius);
        Assert.Equal(50, session.OpticsSystem!.Surfaces![1].Radius);
        Assert.True(doc.SetText(1, S.MaterialSlot + 1, "1.6").Succeeded);
        Assert.Equal(1.6, editor.ToSettings()[1].Index);
        Assert.True(doc.SetText(1, S.SemiDiameterSlot + 1, "4").Succeeded);   // typing fixes it
        Assert.Equal(4, editor.ToSettings()[1].SemiDiameter);

        editor.UndoCommand.Execute(null);
        Assert.Null(editor.ToSettings()[1].SemiDiameter);
        editor.UndoCommand.Execute(null);
        editor.UndoCommand.Execute(null);
        Assert.Null(editor.ToSettings()[1].Radius);
        Assert.Null(session.OpticsSystem!.Surfaces![1].Radius);
        editor.RedoCommand.Execute(null);
        Assert.Equal(50, editor.ToSettings()[1].Radius);
    }

    [Fact]
    public void Surfaces_AreInsertedAndDeleted_BetweenTheObjectAndTheImage()
    {
        var editor = Optics.LensEditor;
        var doc = editor.Document;
        editor.CurrentRow = 1;
        editor.InsertSurfaceCommand.Execute(null);
        Assert.Equal(4, doc.RowCount);
        Assert.True(editor.ToSettings()[2].IsStop);                         // the stop moved down
        Assert.True(doc.Rows[3].IsLocked);
        Assert.False(doc.Rows[2].IsLocked);

        Assert.False(doc.InsertRows(0).Succeeded);                          // not before the object
        Assert.False(doc.InsertRows(doc.RowCount).Succeeded);               // not after the image
        Assert.False(doc.DeleteRows(new[] { 0 }).Succeeded);
        Assert.False(doc.DeleteRows(new[] { doc.RowCount - 1 }).Succeeded);

        editor.CurrentRow = 1;
        editor.DeleteSurfaceCommand.Execute(null);
        Assert.Equal(3, doc.RowCount);
        Assert.True(editor.ToSettings()[1].IsStop);

        // Make stop moves the flag, as one step.
        editor.CurrentRow = 1;
        editor.InsertSurfaceAfterCommand.Execute(null);
        editor.CurrentRow = 2;
        editor.MakeStopCommand.Execute(null);
        Assert.Equal(new[] { false, false, true, false }, editor.ToSettings().Select(s => s.IsStop));
        Assert.Equal("Make surface stop", doc.History.UndoDescription);
    }

    /// <summary>The saved-surface round trip through the editor: every field the sheet shows,
    /// flags of each kind, and a type that is not registered with its parameters past the last
    /// column and its extra data.</summary>
    [Fact]
    public void SavedSurfaces_RoundTripThroughTheEditor()
    {
        var surfaces = new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new()
            {
                Comment = "front", Radius = 50, Thickness = 6, Index = 1.5, IsStop = true, SemiDiameter = 8, Conic = -0.25, Coating = "AR",
                Tce = 7.1, HideRaysTo = true,
                Aperture = new() { Type = SavedSurfaces.CircularAperture, MinRadius = 0, MaxRadius = 7.5, DecenterX = 0.1 },
                Flags = new() { new() { Column = SavedSurfaces.RadiusColumn, Kind = SavedSurfaces.VariableFlag } },
            },
            new()
            {
                Radius = -50, Thickness = 40, ChipZone = 0.5, MechanicalSemiDiameter = 9.5, RowColor = 2, SkipRaysTo = true, DoNotDraw = true,
                Aperture = new() { Type = SavedSurfaces.UserDefinedAperture, IsObscuration = true, Vertices = new() { new() { X = 0, Y = 0 }, new() { X = 1.0 / 3, Y = 0 }, new() { X = 0, Y = 2 } } },
                Flags = new()
                {
                    new() { Column = SavedSurfaces.ThicknessColumn, Kind = SavedSurfaces.PickupFlag, PickupSurface = 1, PickupColumn = SavedSurfaces.ThicknessColumn, PickupScale = -1, PickupOffset = 0.5 },
                },
            },
            new()
            {
                Type = "Zernike Standard Sag", Radius = 100, Thickness = 5, Material = "N-BK7", SemiDiameter = 4, IsIgnored = false,
                Parameters = new() { new() { Number = 1, Value = 1e-5 }, new() { Number = 40, Value = 3 } },
                ExtraData = new() { new() { Number = 1, Value = 2 } },
                Flags = new()
                {
                    new() { Column = SavedSurfaces.SemiDiameterColumn, Kind = SavedSurfaces.SolveFlag, Solve = ApertureTools.MaximumSolve },
                    new() { Column = SavedSurfaces.ParameterColumn(2), Kind = SavedSurfaces.VariableFlag },
                    new() { Column = SavedSurfaces.ThicknessColumn, Kind = SavedSurfaces.SolveFlag, Solve = "Marginal ray height", SolveParameters = new() { 0, 0.7 } },
                },
            },
            new(),
        };
        // The sheet lists a row's flags in column order; the saved list above is in that order
        // except the thickness solve, which comes before the parameter column.
        var expected = surfaces.ToList();
        expected[3] = expected[3] with { Flags = new() { surfaces[3].Flags![2], surfaces[3].Flags![0], surfaces[3].Flags![1] } };

        var editor = Loaded(surfaces);
        Assert.Equal(5, editor.Document.RowCount);
        Assert.Equal("Zernike Standard Sag", editor.Document.Rows[3].TypeName);
        Assert.Equal(Json(expected), Json(editor.ToSettings()));
        Assert.Contains(editor.Problems, p => p.Contains("Zernike Standard Sag"));
        Assert.Equal(0, editor.Document.History.UndoCount);

        // Through the project's optics record and back.
        Assert.Equal(Json(expected), Json(Optics.ToSettings().Surfaces!));

        // A flag the editor cannot take is named, not dropped silently.
        var bad = Singlet();
        bad[2] = bad[2] with { Flags = new() { new() { Column = SavedSurfaces.RadiusColumn, Kind = SavedSurfaces.SolveFlag, Solve = "Not a solve" } } };
        var notes = new List<string>();
        editor.LoadSettings(bad, notes);
        Assert.Contains(notes, n => n.Contains("Surface 2") && n.Contains("Not a solve"));
        // A list without an object and an image is refused, keeping the lens.
        editor.LoadSettings(new List<OpticsSurfaceSettings> { new() }, notes);
        Assert.Equal(4, editor.Document.RowCount);
        // An older file (no surfaces) leaves the editor as it is.
        editor.LoadSettings(null, notes);
        Assert.Equal(4, editor.Document.RowCount);
    }

    [Fact]
    public void MakeFocal_IsOneUndoStep_AndHitsTheTarget()
    {
        var editor = Loaded(Singlet());
        editor.TargetFocalLength = 80;
        editor.MakeFocalCommand.Execute(null);
        var context = new LensEditContext(Optics.ToSettings().SystemData, Array.Empty<OpticalCatalog>());
        double efl = MakeFocal.EffectiveFocalLength(editor.ToSettings(), context);
        Assert.True(Math.Abs(efl / 80 - 1) <= 1e-12, $"focal length {efl:R}");
        Assert.Equal(1, editor.Document.History.UndoCount);
        Assert.Equal("Make Focal", editor.Document.History.UndoDescription);
        Assert.StartsWith("Make Focal:", editor.Status);

        editor.UndoCommand.Execute(null);
        Assert.Equal(Json(Singlet()), Json(editor.ToSettings()));
        editor.RedoCommand.Execute(null);
        Assert.Equal(efl, MakeFocal.EffectiveFocalLength(editor.ToSettings(), context));
    }

    [Fact]
    public void TheGeometryTools_RunFromTheEditor()
    {
        var editor = Loaded(Singlet());
        editor.FirstSurface = 1;
        editor.LastSurface = 2;
        editor.ReverseElementsCommand.Execute(null);
        Assert.Equal(120, editor.ToSettings()[1].Radius);
        Assert.Equal(-40, editor.ToSettings()[2].Radius);

        // The monocentric double-pass lens of the library's gate.
        editor = Loaded(new List<OpticsSurfaceSettings>
        {
            new() { Thickness = 10 },
            new() { Radius = -10, Thickness = 5, Index = 1.5, IsStop = true },
            new() { Radius = -15, Thickness = 35 },
            new() { Radius = -50, Thickness = 0 },
            new(),
        });
        editor.ReflectAtSurface = 3;
        editor.MakeDoublePassCommand.Execute(null);
        var doc = editor.Document;
        Assert.Equal(7, doc.RowCount);
        Assert.True(doc.Rows[6].IsLocked);
        Assert.False(doc.Rows[4].IsLocked);
        Assert.Equal(CellFlagKind.Pickup, doc.Rows[4].Cells[S.ThicknessSlot].Flag.Kind);
        Assert.Equal(SavedSurfaces.Mirror, editor.ToSettings()[3].Material);
        Assert.Equal(-10, editor.ToSettings()[5].Thickness);
        Assert.Empty(editor.Problems);

        // A refused tool changes nothing and says why.
        editor.ReflectAtSurface = 0;
        editor.MakeDoublePassCommand.Execute(null);
        Assert.Equal(7, doc.RowCount);
        Assert.Contains("Make Double Pass", editor.Status);

        // The bulk variable tools and System Check.
        editor.SetAllRadiiVariableCommand.Execute(null);
        Assert.Equal(CellFlagKind.Variable, doc.Rows[1].Cells[S.RadiusSlot].Flag.Kind);
        editor.RemoveAllVariablesCommand.Execute(null);
        Assert.Equal(CellFlagKind.Fixed, doc.Rows[1].Cells[S.RadiusSlot].Flag.Kind);
        editor.RunSystemCheckCommand.Execute(null);
        Assert.DoesNotContain("Error:", editor.SystemCheckReport);
    }

    [Fact]
    public void TheApertureTools_RunFromTheEditor()
    {
        var editor = Loaded(Singlet());
        editor.ConvertToFloatingAperturesCommand.Execute(null);
        Assert.Equal(S.ApertureChoices.Single(c => c == SavedSurfaces.FloatingAperture),
            editor.Document.Rows[1].Properties[S.ApertureProperty].Text);
        editor.RemoveAllAperturesCommand.Execute(null);
        Assert.All(editor.ToSettings(), s => Assert.Null(s.Aperture));
        editor.ConvertToCircularAperturesCommand.Execute(null);
        var circular = editor.ToSettings();
        Assert.Equal(SavedSurfaces.CircularAperture, circular[1].Aperture!.Type);
        Assert.Equal(circular[1].SemiDiameter, circular[1].Aperture!.MaxRadius);
        Assert.Equal(CellFlagKind.Fixed, editor.Document.Rows[1].Cells[S.SemiDiameterSlot].Flag.Kind);
    }

    [Fact]
    public void ChangingTheLensUnit_KeepsTheLensItsSize()
    {
        var editor = Loaded(Singlet());
        Optics.SelectedLensUnit = Optics.LensUnitChoices.Single(c => c.Value == LensUnit.Inch);
        var inches = editor.ToSettings();
        Assert.True(Math.Abs(inches[1].Radius!.Value - 40 / 25.4) <= 1e-15 * 40 / 25.4);
        Assert.True(Math.Abs(inches[2].Thickness - 90 / 25.4) <= 1e-15 * 90 / 25.4);
        Assert.Equal(0, editor.Document.History.UndoCount);
    }

    [Fact]
    public void GoToSurface_FindsANumberATypeOrAComment()
    {
        var surfaces = Singlet();
        surfaces[2] = surfaces[2] with { Comment = "rear face" };
        var editor = Loaded(surfaces);
        editor.CurrentRow = 0;
        editor.GoToText = "rear";
        editor.GoToSurfaceCommand.Execute(null);
        Assert.Equal(2, editor.CurrentRow);
        editor.GoToText = "3";
        editor.GoToSurfaceCommand.Execute(null);
        Assert.Equal(3, editor.CurrentRow);
        editor.GoToText = "nothing like it";
        editor.GoToSurfaceCommand.Execute(null);
        Assert.Contains("No surface", editor.Status);
    }

    // ---- TC-6: surface types, tilts, coordinate breaks and evaluated solves ----

    private static int Slot(SheetRow row, string key) =>
        Enumerable.Range(0, row.Cells.Count).Single(i => row.Cells[i].Definition?.Key == key);

    [Fact]
    public void EveryRegisteredType_IsOffered_WithItsOwnParameterColumns()
    {
        var doc = Optics.LensEditor.Document;
        foreach (var layout in SurfaceTypes.All)
            Assert.NotNull(doc.Schema.FindRowType(layout.Name));
        var even = doc.Schema.FindRowType("Even Asphere")!;
        Assert.Equal("α1", even.ColumnAt(S.FirstParameterSlot)!.Header);
        Assert.Equal(SavedSurfaces.ParameterColumn(1), even.ColumnAt(S.FirstParameterSlot)!.Key);
        Assert.Contains("lens units^-3", even.ColumnAt(S.FirstParameterSlot + 1)!.ToolTip);
        var q = doc.Schema.FindRowType("Q-Type Asphere")!;
        Assert.Equal(SavedSurfaces.ParameterColumn(13), q.ColumnAt(S.FirstParameterSlot)!.Key);   // the type's first column
        Assert.Equal(CellKind.Integer, q.ColumnAt(S.FirstParameterSlot)!.Kind);
        var paraxial = doc.Schema.FindRowType("Paraxial")!;
        Assert.Null(paraxial.ColumnAt(S.RadiusSlot));                    // a Paraxial surface has no radius
        var cb = doc.Schema.FindRowType(SurfaceTypes.CoordinateBreak)!;
        Assert.Null(cb.ColumnAt(S.MaterialSlot));
        Assert.Equal("Decenter X", cb.ColumnAt(S.FirstParameterSlot)!.Header);
        Assert.NotNull(cb.ColumnAt(S.FirstParameterSlot)!.FindSolveCode("Chief ray"));
        Assert.NotNull(cb.FindProperty(S.ReturnModeProperty));
        Assert.Null(cb.FindProperty(S.BeforeTiltXProperty));
    }

    [Fact]
    public void PickingATypeAndEditingAParameter_ReachesTheSavedFile_AndATypeChangeResetsTheParameters()
    {
        var editor = Loaded(Singlet());
        var doc = editor.Document;
        Assert.True(doc.SetText(2, 0, "Even Asphere").Succeeded);
        Assert.Equal(-120, editor.ToSettings()[2].Radius);                 // shared columns carry over
        int par2 = Slot(doc.Rows[2], "Par2");
        Assert.True(doc.SetText(2, par2 + 1, "1e-6").Succeeded);
        var saved = editor.ToSettings()[2];
        Assert.Equal("Even Asphere", saved.Type);
        Assert.Equal(1e-6, SavedSurfaces.Value(saved, 2));
        Assert.Empty(editor.Problems);
        Assert.Equal(Json(editor.ToSettings()), Json(Optics.ToSettings().Surfaces!));

        // The traced lens holds the asphere in SI: 1e-6 mm^-3 is 1e-6 / (1e-3)^3 m^-3.
        var context = new LensEditContext(Optics.ToSettings().SystemData, Array.Empty<OpticalCatalog>());
        var shape = Assert.IsType<OpenSim.Optics.Sequential.Aspheres.EvenAsphere>(context.ToLens(editor.ToSettings()).Surfaces[2].Shape);
        double alpha2 = 1e-6 / (1e-3 * 1e-3 * 1e-3);
        Assert.True(Math.Abs(shape.Alpha[1] - alpha2) <= 1e-15 * alpha2, $"{shape.Alpha[1]:R}");

        // Another type: the radius stays, the parameters reset, as one undo step.
        Assert.True(doc.SetText(2, 0, "Odd Asphere").Succeeded);
        Assert.Equal(-120, editor.ToSettings()[2].Radius);
        Assert.Null(editor.ToSettings()[2].Parameters);
        editor.UndoCommand.Execute(null);
        Assert.Equal(1e-6, SavedSurfaces.Value(editor.ToSettings()[2], 2));

        // An extra-data column of a Q-type is saved as extra data.
        Assert.True(doc.SetText(2, 0, "Q-Type Asphere").Succeeded);
        Assert.True(doc.SetText(2, Slot(doc.Rows[2], "Par15") + 1, "6").Succeeded);
        Assert.Equal(6, editor.ToSettings()[2].ExtraData!.Single(p => p.Number == 15).Value);
        Assert.Null(editor.ToSettings()[2].Parameters);
    }

    [Fact]
    public void TiltsAndCoordinateBreaks_AreEditedAndSaved()
    {
        var editor = Loaded(Singlet());
        var doc = editor.Document;
        Assert.True(doc.SetProperty(2, S.BeforeDecenterYProperty, "0.5").Succeeded);
        Assert.True(doc.SetProperty(2, S.BeforeTiltXProperty, "2").Succeeded);
        Assert.True(doc.SetProperty(2, S.BeforeOrderProperty, S.TiltThenDecenter).Succeeded);
        Assert.True(doc.SetProperty(2, S.AfterModeProperty, S.AfterReverseThis).Succeeded);
        var saved = editor.ToSettings()[2];
        Assert.Equal((0.5, 2.0, "TiltThenDecenter"), (saved.TiltBefore!.DecenterY, saved.TiltBefore.TiltX, saved.TiltBefore.Order!));
        Assert.Equal("ReverseThisSurface", saved.TiltAfter!.Mode);
        // The traced lens has them, decenters in meters.
        var context = new LensEditContext(Optics.ToSettings().SystemData, Array.Empty<OpticalCatalog>());
        var lens = context.Solve(editor.ToSettings()).Entered;
        Assert.Equal(new TiltDecenter(0, 0.5e-3, 2, 0, 0, TiltDecenterOrder.TiltThenDecenter), lens.Surfaces[2].TiltBefore);
        Assert.Equal(TiltAfterKind.ReverseThisSurface, lens.Surfaces[2].TiltAfter!.Kind);

        // A coordinate break with its own columns and a return.
        editor.CurrentRow = 2;
        editor.InsertSurfaceCommand.Execute(null);
        Assert.True(doc.SetText(2, 0, SurfaceTypes.CoordinateBreak).Succeeded);
        Assert.True(doc.SetText(2, Slot(doc.Rows[2], "Par3") + 1, "5").Succeeded);
        Assert.True(doc.SetProperty(2, S.ReturnModeProperty, S.ReturnXYZ).Succeeded);
        Assert.True(doc.SetProperty(2, S.ReturnSurfaceProperty, "1").Succeeded);
        var cb = editor.ToSettings()[2];
        Assert.Equal(SurfaceTypes.CoordinateBreak, cb.Type);
        Assert.Equal(5, SavedSurfaces.Value(cb, 3));
        Assert.Equal(("OrientationXYZ", 1), (cb.CoordinateReturn!.Mode!, cb.CoordinateReturn.Surface));
        Assert.Null(cb.TiltBefore);
        var traced = context.Solve(editor.ToSettings()).Entered.Surfaces[2].CoordinateBreak!;
        Assert.Equal((5.0, CoordinateReturnMode.OrientationXYZ, 1), (traced.Values.TiltXDegrees, traced.Return, traced.ReturnToSurface));

        // Reloading the saved surfaces shows the same values.
        var all = editor.ToSettings();
        Assert.Equal(Json(all), Json(Loaded(all).ToSettings()));
    }

    [Fact]
    public void ASolve_ShowsItsSolvedValue_AndAFailureOnItsCell()
    {
        var editor = Loaded(Singlet());
        var doc = editor.Document;
        // F/# 4 on the back radius: the cell shows the solved radius, the value ProjectLens gives.
        Assert.True(doc.SetFlag(2, S.RadiusSlot + 1, CellFlag.Solve("F/#", 4)).Succeeded);
        var project = Optics.ToSettings();
        var solution = ProjectLens.Solve(project, Array.Empty<OpticalCatalog>())!;
        Assert.True(solution.Succeeded, string.Join(" ", solution.Errors));
        double solvedRadius = 1 / solution.Lens.Surfaces[2].Curvature / 1e-3;
        Assert.Equal(solvedRadius, doc.Rows[2].Cells[S.RadiusSlot].Value.Number);
        Assert.NotEqual(-120, solvedRadius);
        Assert.Equal("F", doc.Rows[2].Cells[S.RadiusSlot].Badge);
        Assert.Empty(editor.Problems);
        // The saved file keeps the solve with its parameter, and the solved value.
        var flag = project.Surfaces![2].Flags!.Single();
        Assert.Equal((SavedSurfaces.SolveFlag, "F/#", 4.0), (flag.Kind!, flag.Solve!, flag.SolveParameters!.Single()));
        Assert.Equal(solvedRadius, project.Surfaces[2].Radius);
        // The marginal ray leaves at -1/(2F), so the solved lens works at F/4: EFL / EPD = 4.
        var system = new LensEditContext(project.SystemData, Array.Empty<OpticalCatalog>());
        double efl = MakeFocal.EffectiveFocalLength(editor.ToSettings(), system);
        Assert.True(Math.Abs(efl / project.SystemData!.ApertureValue - 4) <= 1e-9, $"F/# {efl / project.SystemData.ApertureValue:R}");

        // A pickup shows its value too: 10 x 6 + 2.
        Assert.True(doc.SetFlag(2, S.ThicknessSlot + 1, CellFlag.PickupFrom(doc.Rows[1], S.ThicknessSlot + 1, 10, 2)).Succeeded);
        Assert.True(Math.Abs(doc.Rows[2].Cells[S.ThicknessSlot].Value.Number - 62) <= 1e-12);

        // F/# 0 cannot be met: the cell keeps its value and carries the message.
        Assert.True(doc.SetFlag(2, S.RadiusSlot + 1, CellFlag.Solve("F/#", 0)).Succeeded);
        var cell = doc.Rows[2].Cells[S.RadiusSlot];
        Assert.Equal("F!", cell.Badge);
        Assert.Contains("Surface 2, curvature, f/# solve", cell.ToolTip);
        Assert.Contains(editor.Problems, p => p.StartsWith("Surface 2, curvature"));
        // Fixing it clears the note.
        Assert.True(doc.SetFlag(2, S.RadiusSlot + 1, CellFlag.Fixed).Succeeded);
        Assert.Null(doc.Rows[2].Cells[S.RadiusSlot].Note);
        Assert.Empty(editor.Problems);
    }
}
