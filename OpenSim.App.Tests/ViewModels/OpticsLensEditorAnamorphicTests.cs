using OpenSim.App.Controls.Spreadsheet;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-24: the Lens Data Editor offers the Toroidal surface with OpticStudio's parameter
/// 0 (the extrapolate flag), the first type with a parameter numbered 0, and the edited row
/// reaches the traced lens in SI. Values are arbitrary inputs.</summary>
public sealed class OpticsLensEditorAnamorphicTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private static int Slot(SheetRow row, string key) =>
        Enumerable.Range(0, row.Cells.Count).Single(i => row.Cells[i].Definition?.Key == key);

    [Fact]
    public void AToroidalRow_SavesParameterZero_AndTracesAsTheToroid()
    {
        var editor = Optics.LensEditor;
        var notes = new List<string>();
        editor.LoadSettings(new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true },
            new() { Radius = -120, Thickness = 90 },
            new(),
        }, notes);
        Assert.Empty(notes);
        var doc = editor.Document;
        Assert.True(doc.SetText(1, 0, "Toroidal").Succeeded);
        Assert.Equal(CellKind.Integer, doc.Rows[1].Cells[Slot(doc.Rows[1], "Par0")].Definition!.Kind);
        Assert.True(doc.SetText(1, Slot(doc.Rows[1], "Par0") + 1, "1").Succeeded);
        Assert.True(doc.SetText(1, Slot(doc.Rows[1], "Par1") + 1, "30").Succeeded);
        Assert.True(doc.SetText(1, Slot(doc.Rows[1], "Par2") + 1, "0.002").Succeeded);
        Assert.Empty(editor.Problems);

        var saved = editor.ToSettings()[1];
        Assert.Equal("Toroidal", saved.Type);
        Assert.Equal(1, SavedSurfaces.Value(saved, 0));
        Assert.Equal(30, SavedSurfaces.Value(saved, 1));

        // In SI: 30 mm is 0.03 m; α1 on y² is in lens units^-1, so 0.002/mm is 2/m.
        var context = new LensEditContext(Optics.ToSettings().SystemData, Array.Empty<OpticalCatalog>());
        var shape = Assert.IsType<ToroidalSurface>(context.ToLens(editor.ToSettings()).Surfaces[1].Shape);
        Assert.True(shape.Extrapolate);
        Assert.True(Math.Abs(shape.RadiusOfRotation - 0.03) <= 1e-17);
        Assert.True(Math.Abs(shape.Alpha[0] - 2) <= 1e-15);
    }
}
