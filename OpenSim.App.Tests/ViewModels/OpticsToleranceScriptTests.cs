using System.IO;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-67: the tolerance script block of the Tolerance Data Editor: its fields saved with
/// the run settings, Check naming a line, the merit function saved as a file LOADMERIT reads, and a
/// run with the User script criterion. The lens data are arbitrary inputs.</summary>
public sealed class OpticsToleranceScriptTests : IDisposable
{
    private readonly AppComposition _app = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opensim-op67-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _app.Dispose();
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>A singlet in air (mm): n = 1.5, front radius 40, back −120, with a two-row merit function.</summary>
    private OpticsToleranceEditorViewModel WithSinglet()
    {
        var notes = new List<string>();
        Optics.LensEditor.LoadSettings(new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true },
            new() { Radius = -120, Thickness = 90 },
            new(),
        }, notes);
        Optics.MeritEditor.LoadSettings(new OpticsMeritFunctionSettings
        {
            Operands = new()
            {
                new() { Type = "REAY", Int1 = 3, Int2 = 1, Data = new List<double> { 0, 0, 0, 1 }, Weight = 1 },
                new() { Type = "REAY", Int1 = 3, Int2 = 1, Data = new List<double> { 0, 0, 0, 0.5 }, Weight = 1 },
            },
        }, notes);
        var t = Optics.ToleranceEditor;
        t.Load(new ToleranceTable(new[] { new ToleranceOperandRow { Code = "TRAD", Int1 = 1, Min = -0.5, Max = 0.5 } }), notes);
        Assert.Empty(notes);
        return t;
    }

    [Fact]
    public void ScriptFields_AreSavedWithTheRunSettings_AndGoIntoTheRun()
    {
        var t = WithSinglet();
        t.SelectedCriterion = t.Criteria.Single(c => c.Value == ToleranceCriterion.UserScript);
        t.ScriptName = "ALIGN.TSC";
        t.ScriptText = "GETMERIT";
        t.ScriptMeritFolder = @"C:\merit";
        t.ScriptOutputFolder = @"C:\out";
        var saved = t.ToSettings()!.Run!;
        Assert.Equal("UserScript", saved.Criterion);
        Assert.Equal("ALIGN.TSC", saved.ScriptName);
        Assert.Equal("GETMERIT", saved.ScriptText);
        Assert.Equal(@"C:\merit", saved.ScriptMeritFolder);

        t.Reset();
        Assert.Equal("", t.ScriptText);
        var notes = new List<string>();
        t.LoadSettings(new OpticsToleranceSettings { Run = saved }, notes);
        var o = t.RunOptions();
        Assert.Equal(ToleranceCriterion.UserScript, o.Criterion);
        Assert.Equal("GETMERIT", o.Script!.Text);
        Assert.Equal(@"C:\out", o.Script.OutputFolder);
        Assert.DoesNotContain("User script", t.CriteriaNotBuilt);
    }

    [Fact]
    public void Check_NamesTheLine()
    {
        var t = WithSinglet();
        t.ScriptName = "BAD.TSC";
        t.ScriptText = "GETMERIT\nCEDV 3 1";
        t.CheckScriptCommand.Execute(null);
        Assert.Contains("BAD.TSC, line 2", t.ScriptStatus);
        t.ScriptText = "LOADMERIT SPOT\nREPORT \"merit \" 0";
        t.CheckScriptCommand.Execute(null);
        Assert.Contains("every command it names runs", t.ScriptStatus);
    }

    [Fact]
    public async Task TheSavedMeritFunction_IsLoadedByTheScript_InARun()
    {
        var t = WithSinglet();
        Directory.CreateDirectory(_folder);
        t.ScriptMeritFolder = _folder;
        t.ScriptMeritName = "SPOT";
        t.SaveMeritForScriptCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(_folder, "SPOT.json")), t.ScriptStatus);

        t.SelectedCriterion = t.Criteria.Single(c => c.Value == ToleranceCriterion.UserScript);
        t.ScriptText = "LOADMERIT SPOT\nREPORT \"merit = \" 0";
        await t.RunToleranceCommand.ExecuteAsync(null);
        var row = Assert.Single(t.RunRows);
        Assert.Equal("TRAD", row.Type);
        Assert.Contains("Script output:", t.ReportText);
        Assert.Contains("merit = ", t.ReportText);
    }
}
