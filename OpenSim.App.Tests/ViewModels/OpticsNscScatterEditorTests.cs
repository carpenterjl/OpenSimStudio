using System.Text.Json;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-80: the editor's Coat/Scatter, Scatter To and ABg catalog fields
/// (<c>OpticsNonSequentialEditorViewModel.Scatter.cs</c>): loading shows the saved values, Apply
/// writes them into the current object and nothing else, the catalog text is parsed and saved,
/// and bad input is refused with a reason. Values are arbitrary test inputs.</summary>
public sealed class OpticsNscScatterEditorTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private static OpticsNonSequentialSettings TwoRectangles() => new()
    {
        AbgCatalog = new() { new OpticsAbgProfileSettings { Name = "POLISH", Wavelength = 0, Rows = new() { new OpticsAbgRowSettings { Angle = 0, A = 0.002, B = 0.0005, G = 2 } } } },
        Objects = new()
        {
            new()
            {
                Type = "Rectangle", Parameters = new() { 20, 20 }, Material = "MIRROR",
                Faces = new() { new() { Face = 0, ScatterModel = "ABg", ScatterRays = 4, ScatterReflect = "POLISH" } },
                ScatterToMethod = "Importance Sampling",
                ScatterTargets = new() { new() { Object = 2, Size = 5, Limit = 0.5 } },
            },
            new() { Type = "Rectangle", Parameters = new() { 5, 5 }, Z = 100, DetectingStandIn = true },
        },
    };

    private OpticsNonSequentialEditorViewModel Loaded(OpticsNonSequentialSettings settings)
    {
        var notes = Optics.LoadSettings(Optics.ToSettings() with { NonSequential = settings });
        Assert.Empty(notes);
        return Optics.NonSequentialEditor;
    }

    [Fact]
    public void Loading_ShowsTheSavedScatterFields_AndLeavesTheFileUnchanged()
    {
        var saved = TwoRectangles();
        var editor = Loaded(saved);
        Assert.Equal("ABg", editor.ScatterModel);
        Assert.Equal(4, editor.ScatterRays);
        Assert.Equal("POLISH", editor.ScatterReflect);
        Assert.Equal("Importance Sampling", editor.ScatterToMethod);
        Assert.Equal("2 5 0.5", editor.ScatterTargets);
        Assert.Contains("POLISH 0 0 0.002 0.0005 2", editor.AbgCatalogText);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(editor.ToSettings()));

        editor.CurrentRow = 1;
        Assert.Equal("None", editor.ScatterModel);
        Assert.Equal("None", editor.ScatterToMethod);
    }

    [Fact]
    public void Apply_WritesTheCurrentObjectsScattering()
    {
        var editor = Loaded(TwoRectangles());
        editor.CurrentRow = 1;
        editor.ScatterFace = 0;
        editor.ScatterModel = "Lambertian";
        editor.ScatterRays = 3;
        editor.ScatterFraction = 0.5;
        editor.ThinWindowScattering = true;
        editor.ScatterToMethod = "Scatter To List";
        editor.ScatterToList = "1";
        Assert.Null(editor.ApplyScatterFields());
        var objects = Optics.ToSettings().NonSequential!.Objects!;
        var face = Assert.Single(objects[1].Faces!);
        Assert.Equal(((string?)"Lambertian", (int?)3, (double?)0.5, (bool?)true), (face.ScatterModel, face.ScatterRays, face.ScatterFraction, face.ThinWindowScattering));
        Assert.Null(face.ScatterSigma);
        Assert.Equal("Scatter To List", objects[1].ScatterToMethod);
        Assert.Equal("1", objects[1].ScatterToList);
        Assert.Null(objects[1].ScatterTargets);
        Assert.Equal(100.0, objects[1].Z);
        Assert.Equal(JsonSerializer.Serialize(TwoRectangles().Objects![0]), JsonSerializer.Serialize(objects[0]));

        // Back to None clears the face's scatter fields.
        editor.ScatterModel = "None";
        editor.ScatterToMethod = "None";
        Assert.Null(editor.ApplyScatterFields());
        var cleared = Optics.ToSettings().NonSequential!.Objects![1];
        Assert.Null(Assert.Single(cleared.Faces!).ScatterModel);
        Assert.Null(cleared.ScatterToMethod);

        editor.ScatterTargets = "2 five 1";
        editor.ScatterToMethod = "Importance Sampling";
        Assert.Contains("line 1", editor.ApplyScatterFields());
        editor.ScatterModel = "Fractal";
        Assert.Contains("not one of", editor.ApplyScatterFields());
    }

    [Fact]
    public void AbgCatalog_IsParsedAndSaved_AndBadRowsAreRefused()
    {
        var editor = Loaded(TwoRectangles());
        editor.AbgCatalogText = "# name wave angle A B g\nROUGH 0.55 0 0.01 0.001 1.5\nROUGH 0.55 45 0.012 0.001 1.5\n";
        Assert.Null(editor.ApplyAbgCatalogText());
        var catalog = Optics.ToSettings().NonSequential!.AbgCatalog!;
        var profile = Assert.Single(catalog);
        Assert.Equal(((string?)"ROUGH", (double?)0.55), (profile.Name, profile.Wavelength));
        Assert.Equal(new double?[] { 0, 45 }, profile.Rows!.Select(r => r.Angle));

        editor.AbgCatalogText = "rough 0 0 0.01 0.001 1.5";
        Assert.Contains("upper case", editor.ApplyAbgCatalogText());
        editor.AbgCatalogText = "ROUGH 0 0 0.01";
        Assert.Contains("6 fields", editor.ApplyAbgCatalogText());
        Assert.Equal("ROUGH", Assert.Single(Optics.ToSettings().NonSequential!.AbgCatalog!).Name);
    }
}
