using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;

namespace OpenSim.Tests.Optics;

/// <summary>OP-1: the optics mode's saved names, and the optics settings in the project file.</summary>
public sealed class OpticsProjectTests
{
    [Fact]
    public void EveryMode_HasALabel_AndASavedNameThatReadsBack()
    {
        Assert.Equal(Enum.GetValues<OpticsMode>(), OpticsModes.All);
        foreach (var mode in OpticsModes.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(OpticsModes.Label(mode)));
            Assert.True(OpticsModes.TryParseSavedName(OpticsModes.SavedName(mode), out var back));
            Assert.Equal(mode, back);
        }
        Assert.Equal(OpticsModes.All.Count, OpticsModes.All.Select(OpticsModes.SavedName).Distinct().Count());
    }

    /// <summary>The names already in project files. A rename of the enum must not change them.</summary>
    [Fact]
    public void SavedNames_AreTheOnesWrittenToFiles()
    {
        Assert.Equal("Sequential", OpticsModes.SavedName(OpticsMode.Sequential));
        Assert.Equal("NonSequential", OpticsModes.SavedName(OpticsMode.NonSequential));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sequential")]      // names are exact: a near miss is a hand edit, and is reported
    [InlineData("Hybrid")]
    public void AnUnknownSavedName_IsRefused(string? name)
    {
        Assert.False(OpticsModes.TryParseSavedName(name, out _));
    }

    [Fact]
    public void OpticsSettings_RoundTrip_AndOlderProjectsLoadWithoutThem()
    {
        var project = new SimProject
        {
            Name = "Lens",
            AnalysisType = "Optics",
            Optics = new OpticsSettings { Mode = OpticsModes.SavedName(OpticsMode.NonSequential) }
        };
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            Assert.True(ProjectSerializer.CurrentFormatVersion >= 4);          // optics arrived in 4
            var loaded = serializer.Load(path);
            Assert.Equal(project.Optics, loaded.Optics);                       // record equality: every field
            Assert.Equal("Optics", loaded.AnalysisType);

            File.WriteAllText(path, """{ "formatVersion": 3, "name": "Older", "bodies": [] }""");
            Assert.Null(serializer.Load(path).Optics);

            // A mode from a later build: the file still loads, and the name is there to report.
            File.WriteAllText(path, """{ "formatVersion": 9, "name": "Newer", "optics": { "mode": "Hybrid" }, "bodies": [] }""");
            Assert.Equal("Hybrid", serializer.Load(path).Optics!.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
