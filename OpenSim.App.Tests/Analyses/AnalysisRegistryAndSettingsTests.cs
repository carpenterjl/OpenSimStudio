using System.Text.Json;
using OpenSim.App.Analyses;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;

namespace OpenSim.App.Tests.Analyses;

/// <summary>OP-9: registering analyses, the generic settings form, and settings in their saved form.</summary>
public sealed class AnalysisRegistryAndSettingsTests
{
    private sealed class WrongDefaults : IOpticsAnalysis
    {
        public string Id => "Wrong";
        public string Name => "Wrong";
        public string Category => "Test";
        public Type SettingsType => typeof(CountSettings);
        public object CreateDefaultSettings() => new SystemSummarySettings();
        public OpticsAnalysisResult Compute(OpticsAnalysisContext c, object s, CancellationToken t) => throw new NotSupportedException();
    }

    [Fact]
    public void TheDefaultRegistry_OffersTheSystemSummary_AndEveryIdIsUnique()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(SystemSummaryAnalysis.SavedId, out var summary));
        Assert.Equal("System summary", summary.Name);
        Assert.Equal(registry.All.Count, registry.All.Select(a => a.Id).Distinct().Count());
        Assert.False(registry.TryGet("NoSuchAnalysis", out _));
        Assert.False(registry.TryGet(null, out _));
    }

    [Fact]
    public void Registration_RefusesADuplicateId_ABlankId_AndDefaultsOfTheWrongType()
    {
        var registry = new OpticsAnalysisRegistry();
        registry.Register(new CountingAnalysis("A"));
        Assert.Throws<ArgumentException>(() => registry.Register(new CountingAnalysis("A", "Another")));
        Assert.Throws<ArgumentException>(() => registry.Register(new CountingAnalysis(" ")));
        Assert.Throws<ArgumentException>(() => registry.Register(new WrongDefaults()));
        Assert.Single(registry.All);
    }

    [Fact]
    public void Compute_RefusesSettingsOfAnotherAnalysis()
    {
        var context = new OpticsAnalysisContext("P", new OpticsSettings());
        Assert.Throws<ArgumentException>(() => new CountingAnalysis().Compute(context, new SystemSummarySettings(), default));
    }

    [Fact]
    public void TheSystemSummary_ListsTheProjectModeAndUnits()
    {
        var context = new OpticsAnalysisContext("Doublet", new OpticsSettings { Mode = "NonSequential" });
        var result = new SystemSummaryAnalysis().Compute(context, new SystemSummarySettings { Units = LensUnit.Inch }, default);
        Assert.Contains("Doublet", result.Text);
        Assert.Contains("Non-sequential", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 in = 0.0254 m", result.Text);
        Assert.NotEmpty(result.Assumptions);

        var brief = new SystemSummaryAnalysis().Compute(context, new SystemSummarySettings { ShowConventions = false }, default);
        // Without the conventions, the System section and the Environment section (OP-13, which
        // names the air index model in use) remain.
        Assert.Equal(new[] { "System", "Environment" }, brief.Sections.Select(s => s.Heading));
        Assert.Contains(result.Sections, s => s.Heading == "Conventions");
    }

    [Fact]
    public void TheSettingsForm_ShowsEveryEditableField_AndBuildsTheEditedRecord()
    {
        var original = new CountSettings { Factor = 3, Note = "n" };
        var editor = AnalysisSettingsEditor.For(original);
        Assert.Equal(new[] { "Factor", "Spacing", "Limit", "Flag", "Mode", "Note" }, editor.Fields.Select(f => f.Name));
        Assert.Equal("3", editor.Fields[0].Text);
        Assert.Equal(SettingKind.Choice, editor.Fields[4].Kind);
        Assert.Equal(Enum.GetNames<OpticsMode>(), editor.Fields[4].Choices);

        editor.Fields[0].Text = "12";
        editor.Fields[1].Text = "1.25e-3";
        editor.Fields[2].Text = "4";
        editor.Fields[3].Flag = true;
        editor.Fields[4].Choice = nameof(OpticsMode.NonSequential);
        editor.Fields[5].Text = "edited";
        Assert.True(editor.TryBuild(original, out var built, out var error), error);
        Assert.Equal(new CountSettings { Factor = 12, Spacing = 1.25e-3, Limit = 4, Flag = true, Mode = OpticsMode.NonSequential, Note = "edited" }, built);

        editor.Fields[2].Text = "";                              // a nullable number may be blank
        Assert.True(editor.TryBuild(original, out built, out _));
        Assert.Null(((CountSettings)built).Limit);

        editor.Show(original);
        Assert.Equal("3", editor.Fields[0].Text);
        Assert.False(editor.Fields[3].Flag);
    }

    [Theory]
    [InlineData(0, "2.5", "whole number")]
    [InlineData(1, "abc", "not a number")]
    [InlineData(1, "NaN", "not a number")]
    [InlineData(0, "99999999999", "")]                           // overflows int: refused by the record
    public void TheSettingsForm_RefusesAValueThatDoesNotParse(int field, string text, string message)
    {
        var original = new CountSettings();
        var editor = AnalysisSettingsEditor.For(original);
        editor.Fields[field].Text = text;
        Assert.False(editor.TryBuild(original, out var built, out var error));
        Assert.Same(original, built);
        Assert.Contains(message, error);
    }

    [Fact]
    public void Settings_RoundTripThroughTheirSavedForm_AndABadSavedValueFallsBackToDefaults()
    {
        var analysis = new CountingAnalysis();
        var settings = new CountSettings { Factor = 5, Limit = 2, Mode = OpticsMode.NonSequential, Note = "x" };
        var saved = OpticsAnalysisSettingsCodec.Save(settings);
        Assert.Equal("NonSequential", saved.GetProperty("mode").GetString());   // enums by name, not number
        Assert.Equal(settings, OpticsAnalysisSettingsCodec.Load(analysis, saved, out var note));
        Assert.Null(note);

        // A property a later build added is ignored; one missing keeps its default.
        var later = JsonDocument.Parse("""{ "factor": 9, "colour": "red" }""").RootElement;
        Assert.Equal(new CountSettings { Factor = 9 }, OpticsAnalysisSettingsCodec.Load(analysis, later, out note));
        Assert.Null(note);

        var broken = JsonDocument.Parse("""{ "factor": "many" }""").RootElement;
        Assert.Equal(new CountSettings(), OpticsAnalysisSettingsCodec.Load(analysis, broken, out note));
        Assert.Contains("Counting", note);

        var notARecord = JsonDocument.Parse("[1, 2]").RootElement;
        Assert.Equal(new CountSettings(), OpticsAnalysisSettingsCodec.Load(analysis, notARecord, out note));
        Assert.NotNull(note);

        Assert.Equal(new CountSettings(), OpticsAnalysisSettingsCodec.Load(analysis, null, out note));
        Assert.Null(note);
    }
}
