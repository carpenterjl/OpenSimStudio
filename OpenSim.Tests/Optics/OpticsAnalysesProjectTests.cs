using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;

namespace OpenSim.Tests.Optics;

/// <summary>OP-9: the analysis windows' saved form in the project file.</summary>
public sealed class OpticsAnalysesProjectTests
{
    [Fact]
    public void AnalysisWindows_RoundTripEveryField_AndOlderProjectsLoadWithoutThem()
    {
        var settings = OpticsAnalysisSettingsCodec.Save(new SystemSummarySettings { Units = LensUnit.Inch, ShowConventions = false });
        var analyses = new OpticsAnalysesSettings
        {
            Windows = new()
            {
                new OpticsAnalysisWindowSettings
                {
                    Analysis = SystemSummaryAnalysis.SavedId, Dock = "Right", Pane = 1, Selected = true,
                    AutoUpdate = false, Settings = settings,
                },
                new OpticsAnalysisWindowSettings
                {
                    Analysis = "Later", Dock = "Float", FloatLeft = -120.5, FloatTop = 40, FloatWidth = 600, FloatHeight = 400,
                },
            },
            LastSettings = new() { [SystemSummaryAnalysis.SavedId] = settings },
            RightDockWidth = 480,
            BottomDockHeight = 260,
        };
        var project = new SimProject
        {
            Name = "Lens", AnalysisType = "Optics",
            Optics = new OpticsSettings { Mode = OpticsModes.SavedName(OpticsMode.Sequential), Analyses = analyses },
        };
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = serializer.Load(path).Optics!.Analyses!;

            // JsonElement has no value equality: compare the saved forms field by field.
            Assert.Equal(JsonSerializer.Serialize(analyses), JsonSerializer.Serialize(loaded));
            Assert.Equal(2, loaded.Windows!.Count);
            var first = loaded.Windows[0];
            Assert.Equal(SystemSummaryAnalysis.SavedId, first.Analysis);
            Assert.Equal("Right", first.Dock);
            Assert.Equal(1, first.Pane);
            Assert.True(first.Selected);
            Assert.False(first.AutoUpdate);
            Assert.Equal(new SystemSummarySettings { Units = LensUnit.Inch, ShowConventions = false },
                OpticsAnalysisSettingsCodec.Load(new SystemSummaryAnalysis(), first.Settings, out var note));
            Assert.Null(note);
            var floating = loaded.Windows[1];
            Assert.Equal(new double?[] { -120.5, 40, 600, 400 },
                new[] { floating.FloatLeft, floating.FloatTop, floating.FloatWidth, floating.FloatHeight });
            Assert.Equal(new double?[] { 480, 260 }, new[] { loaded.RightDockWidth, loaded.BottomDockHeight });

            // The settings are stored as JSON in the file, not as an escaped string.
            Assert.Contains("\"units\": \"Inch\"", File.ReadAllText(path));

            // An OP-1 file (optics without analyses) and an older one still load.
            File.WriteAllText(path, """{ "formatVersion": 4, "name": "OP1", "optics": { "mode": "Sequential" }, "bodies": [] }""");
            Assert.Null(serializer.Load(path).Optics!.Analyses);
            File.WriteAllText(path, """{ "formatVersion": 3, "name": "Older", "bodies": [] }""");
            Assert.Null(serializer.Load(path).Optics);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
