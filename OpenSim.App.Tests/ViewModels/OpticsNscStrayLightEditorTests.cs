using System.Text.Json;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.NonSequential.StrayLight;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-84: the editor's path filters and sequences
/// (<c>OpticsNonSequentialEditorViewModel.StrayLight.cs</c>): loading shows the saved lists and
/// leaves the file unchanged, Apply parses and saves them, bad input is refused with a reason, and
/// Generate runs Path Analysis and the straight-through Sequence Generator on the project. The
/// scene is an arbitrary test input.</summary>
public sealed class OpticsNscStrayLightEditorTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>A point source (40° cone) and an absorbing detector 10 mm away.</summary>
    private static OpticsNonSequentialSettings SourceAndDetector() => new()
    {
        Objects = new()
        {
            new OpticsNscObjectSettings { Type = "Source Point", Parameters = new() { 10, 200, 1, 0, 0, 40 } },
            new OpticsNscObjectSettings { Type = "Detector Rectangle", Z = 10, Material = "ABSORB", Parameters = new() { 5, 5, 10, 10 } },
        },
        PathFilters = new() { "1.0, 2.0" },
        Sequences = new() { new OpticsNscSequenceSettings { Name = "SEQ1", Sequence = "1.0, 2.0" }, new OpticsNscSequenceSettings { Name = "Miss", Sequence = "1.0", Enabled = false } },
    };

    private OpticsNonSequentialEditorViewModel Loaded(OpticsNonSequentialSettings settings)
    {
        var notes = Optics.LoadSettings(Optics.ToSettings() with { NonSequential = settings });
        Assert.Empty(notes);
        return Optics.NonSequentialEditor;
    }

    [Fact]
    public void Loading_ShowsTheSavedLists_AndLeavesTheFileUnchanged()
    {
        var saved = SourceAndDetector();
        var editor = Loaded(saved);
        Assert.Equal("1.0, 2.0", editor.PathFiltersText);
        Assert.Equal($"SEQ1; 1.0, 2.0{Environment.NewLine}-Miss; 1.0", editor.SequencesText);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(editor.ToSettings()));
    }

    [Fact]
    public void Apply_SavesTheEditedLists_AndRefusesBadOnes()
    {
        var editor = Loaded(SourceAndDetector());
        editor.PathFiltersText = "1.0, 2.0\n\n1.0 #";
        Assert.Null(editor.ApplyPathFiltersText());
        Assert.Equal(new[] { "1.0, 2.0", "1.0 #" }, Optics.ToSettings().NonSequential!.PathFilters);

        editor.SequencesText = "Direct; 1.0, 2.0";
        Assert.Null(editor.ApplySequencesText());
        var seq = Optics.ToSettings().NonSequential!.Sequences!.Single();
        Assert.Equal("Direct", seq.Name);
        Assert.Null(seq.Enabled);   // on is saved as null

        editor.SequencesText = "A1; 1.0, 2.0";
        Assert.Contains("single letter", editor.ApplySequencesText());
        editor.SequencesText = "NoSemicolon";
        Assert.Contains("name; sequence", editor.ApplySequencesText());
        editor.PathFiltersText = "1.0, two";
        Assert.Contains("_1", editor.ApplyPathFiltersText());
        Assert.Equal("Direct", Optics.ToSettings().NonSequential!.Sequences!.Single().Name);   // refusals change nothing
    }

    [Fact]
    public void Generate_RunsPathAnalysisAndTheSequenceGenerator_OnTheProject()
    {
        var editor = Loaded(SourceAndDetector() with { PathFilters = null, Sequences = null });
        editor.PathAnalysisDetector = 2;
        Assert.Contains("path filter(s) saved", editor.GeneratePathFiltersNow());
        Assert.Contains("1.0, 2.0 #", Optics.ToSettings().NonSequential!.PathFilters!);

        editor.SequenceMethod = NscSequenceMethod.StraightThroughPaths;
        Assert.Contains("1 sequence(s) generated", editor.GenerateSequencesNow());
        Assert.Equal("1.0, 2.0", Optics.ToSettings().NonSequential!.Sequences!.Single().Sequence);
        Assert.Contains("0 sequence(s) generated", editor.GenerateSequencesNow());   // a duplicate is not added

        editor.PathAnalysisDetector = 1;
        Assert.Contains("not a detector", editor.GeneratePathFiltersNow());
    }
}
