using OpenSim.Core.Persistence;
using OpenSim.Core.PostProcessing;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Colormap library semantics, mirroring <see cref="MaterialLibraryTests"/>: user maps
/// ADD/OVERRIDE by name on top of the built-in presets, problems warn instead of silently
/// replacing the library, and an edited map survives a round trip through the JSON file.
/// Every test uses a private temp directory — never the real AppData.
/// </summary>
public sealed class ColormapLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oss-cmap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string UserFile => Path.Combine(_dir, "colormaps.json");

    private void WriteUserFile(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(UserFile, json);
    }

    [Fact]
    public void BuiltIns_AreThePresets_InOrder_AndNoneIsUserDefined()
    {
        var library = new ColormapLibrary(_dir);

        Assert.Empty(library.LoadWarnings);
        Assert.Equal(ColormapDefinition.Presets.Select(p => p.Name), library.Colormaps.Select(c => c.Name));
        foreach (var map in library.Colormaps)
            Assert.False(library.IsUserDefined(map.Name), $"{map.Name} must not count as user-defined.");
    }

    [Fact]
    public void AddOrUpdate_PersistsAndRoundTripsEveryStop()
    {
        // An edited map: a moved stop, a recolored one, and banding on.
        var edited = ColormapDefinition.Viridis
            .WithStopMoved(2, 0.4)
            .WithStopColor(0, 12, 34, 56)
            .WithDiscrete(true)
            .WithName("My map");

        new ColormapLibrary(_dir).AddOrUpdate(edited);
        var reloaded = new ColormapLibrary(_dir);

        Assert.Empty(reloaded.LoadWarnings);
        var loaded = Assert.Single(reloaded.Colormaps, c => c.Name == "My map");
        // Value equality over name, banding AND every stop — the record's own comparer.
        Assert.Equal(edited, loaded);
        Assert.True(reloaded.IsUserDefined("My map"));
        Assert.Equal(ColormapDefinition.Presets.Count + 1, reloaded.Colormaps.Count);
    }

    [Fact]
    public void UserMap_OverridesBuiltInByName_AndRemovingTheOverrideRestoresIt()
    {
        var shadow = ColormapDefinition.Rainbow.WithStopColor(0, 1, 2, 3);
        var library = new ColormapLibrary(_dir);
        library.AddOrUpdate(shadow);

        var reloaded = new ColormapLibrary(_dir);
        Assert.Equal(ColormapDefinition.Presets.Count, reloaded.Colormaps.Count);   // overrode, not appended
        Assert.Equal(shadow, Assert.Single(reloaded.Colormaps, c => c.Name == "Rainbow"));

        reloaded.Remove("Rainbow");
        Assert.Equal(ColormapDefinition.Rainbow, Assert.Single(reloaded.Colormaps, c => c.Name == "Rainbow"));
        Assert.False(reloaded.IsUserDefined("Rainbow"));
        // …and the restoration is what the next session sees, not just this one.
        Assert.Equal(ColormapDefinition.Rainbow,
            Assert.Single(new ColormapLibrary(_dir).Colormaps, c => c.Name == "Rainbow"));
    }

    [Fact]
    public void Remove_RefusesABuiltIn_AndAnUnknownName()
    {
        var library = new ColormapLibrary(_dir);

        var builtIn = Assert.Throws<InvalidOperationException>(() => library.Remove("Viridis"));
        Assert.Contains("built-in", builtIn.Message);
        Assert.Throws<InvalidOperationException>(() => library.Remove("Nothing like this"));
        Assert.Equal(ColormapDefinition.Presets.Count, library.Colormaps.Count);
    }

    [Fact]
    public void MalformedFile_Warns_KeepsBuiltIns_AndIsNotOverwritten()
    {
        const string junk = "{ this is not a colormap list";
        WriteUserFile(junk);

        var library = new ColormapLibrary(_dir);

        Assert.Equal(ColormapDefinition.Presets.Count, library.Colormaps.Count);
        Assert.Contains(library.LoadWarnings, w => w.Contains("malformed"));
        // The user's hand-edited file must survive to be repaired by hand.
        Assert.Equal(junk, File.ReadAllText(UserFile));
    }

    [Fact]
    public void InvalidStops_Warn_RatherThanLoadingAColormapThatDisagreesWithItsLegend()
    {
        // Positions out of order — the constructor's invariant. A silently reordered map
        // would color the mesh differently from the legend built off the same stops.
        WriteUserFile("""
        [
          { "name": "Broken", "stops": [
              { "position": 0, "r": 0, "g": 0, "b": 0 },
              { "position": 0.7, "r": 255, "g": 0, "b": 0 },
              { "position": 0.3, "r": 0, "g": 255, "b": 0 },
              { "position": 1, "r": 255, "g": 255, "b": 255 } ] }
        ]
        """);

        var library = new ColormapLibrary(_dir);

        Assert.Equal(ColormapDefinition.Presets.Count, library.Colormaps.Count);
        Assert.DoesNotContain(library.Colormaps, c => c.Name == "Broken");
        Assert.NotEmpty(library.LoadWarnings);
    }
}
