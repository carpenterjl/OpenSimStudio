using System.IO;
using System.Text.Json;
using OpenSim.App.Analyses;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;

namespace OpenSim.App.Tests.Analyses;

/// <summary>
/// OP-9's gate: two analyses open side by side update after one edit; a long analysis is
/// cancelled by the next edit; settings and the window layout persist in the project; the
/// report page gathers the windows.
/// </summary>
public sealed class OpticsAnalysesViewModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private (OpticsAnalysesViewModel Host, ProjectSession Session) NewHost(params IOpticsAnalysis[] analyses)
    {
        var registry = new OpticsAnalysisRegistry();
        foreach (var analysis in analyses) registry.Register(analysis);
        var session = new ProjectSession(_app.Log);
        var host = new OpticsAnalysesViewModel(session, _app.Log, registry) { Debounce = TimeSpan.Zero };
        return (host, session);
    }

    private static void Edit(ProjectSession session, OpticsMode mode) =>
        session.PublishOptics(new OpticsSettings { Mode = OpticsModes.SavedName(mode) });

    [Fact]
    public async Task TwoAnalysesSideBySide_BothUpdate_AfterOneEdit()
    {
        var a = new CountingAnalysis("A", "Alpha");
        var b = new CountingAnalysis("B", "Beta");
        var (host, session) = NewHost(a, b);
        var left = host.Open(a, DockZone.Bottom);
        var right = host.Open(b, DockZone.Bottom, newPane: true);
        await host.WhenIdleAsync().WaitAsync(Wait);
        Assert.Equal(2, host.Layout.Bottom.Count);              // side by side
        Assert.Equal((1, 1), (a.Calls, b.Calls));

        Edit(session, OpticsMode.NonSequential);
        await host.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal((2, 2), (a.Calls, b.Calls));
        Assert.All(new[] { left, right }, w =>
        {
            Assert.Contains("Non-sequential", w.ResultText);
            Assert.Equal("Up to date", w.Status);
            Assert.False(w.IsComputing);
        });
    }

    [Fact]
    public async Task ALongAnalysis_IsCancelledByTheNextEdit()
    {
        var slow = new SlowAnalysis();
        var (host, session) = NewHost(slow);
        var window = host.Open(slow);
        await slow.FirstStarted.Task.WaitAsync(Wait);
        Assert.True(window.IsComputing);
        var firstRun = window.CurrentRun;

        Edit(session, OpticsMode.NonSequential);
        await Task.WhenAll(firstRun, host.WhenIdleAsync()).WaitAsync(Wait);

        Assert.Equal(1, slow.Cancelled);
        Assert.Equal(1, slow.Finished);
        Assert.Equal(1, window.ComputeCount);
        Assert.Equal("Mode Non-sequential", window.ResultText);
        Assert.Equal("Up to date", window.Status);
    }

    [Fact]
    public async Task EditsWithinTheDebounce_AreCoalesced()
    {
        var counting = new CountingAnalysis();
        var (host, session) = NewHost(counting);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Delay = (_, token) => gate.Task.WaitAsync(token);
        host.Debounce = TimeSpan.FromSeconds(1);
        host.Open(counting);                                    // opening computes at once
        await host.WhenIdleAsync().WaitAsync(Wait);

        for (int i = 0; i < 6; i++) Edit(session, i % 2 == 0 ? OpticsMode.NonSequential : OpticsMode.Sequential);
        gate.SetResult();
        await host.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(2, counting.Calls);
        Assert.Contains("Sequential", host.Windows[0].ResultText);   // the last edit's snapshot
        Assert.DoesNotContain("Non-sequential", host.Windows[0].ResultText);
    }

    [Fact]
    public async Task WithAutoUpdateOff_AnEditMarksTheWindowStale_UntilUpdate()
    {
        var counting = new CountingAnalysis();
        var (host, session) = NewHost(counting);
        var window = host.Open(counting);
        await host.WhenIdleAsync().WaitAsync(Wait);
        window.AutoUpdate = false;

        Edit(session, OpticsMode.NonSequential);
        Assert.True(window.IsStale);
        Assert.Equal(1, counting.Calls);

        window.UpdateCommand.Execute(null);
        await host.WhenIdleAsync().WaitAsync(Wait);
        Assert.Equal(2, counting.Calls);
        Assert.False(window.IsStale);
    }

    [Fact]
    public async Task AppliedSettings_Recompute_AndTheNextWindowStartsFromThem()
    {
        var counting = new CountingAnalysis();
        var (host, _) = NewHost(counting);
        var window = host.Open(counting);
        window.SettingsEditor.Fields.Single(f => f.Name == nameof(CountSettings.Factor)).Text = "4";
        window.ApplySettingsCommand.Execute(null);
        await host.WhenIdleAsync().WaitAsync(Wait);
        Assert.Equal(4, ((CountSettings)window.Settings).Factor);
        Assert.Contains("Factor: 4", window.ResultText);
        Assert.Equal("", window.SettingsError);

        window.SettingsEditor.Fields.Single(f => f.Name == nameof(CountSettings.Factor)).Text = "four";
        window.ApplySettingsCommand.Execute(null);
        Assert.Contains("Factor", window.SettingsError);
        Assert.Equal(4, ((CountSettings)window.Settings).Factor);

        host.Close(window);
        var again = host.Open(counting);
        Assert.Equal(4, ((CountSettings)again.Settings).Factor);
        Assert.Equal("Counting", again.Title);
        Assert.Equal("Counting (2)", host.Open(counting).Title);
        await host.WhenIdleAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task SettingsAndLayout_RoundTripThroughTheSavedForm()
    {
        var a = new CountingAnalysis("A", "Alpha");
        var b = new CountingAnalysis("B", "Beta");
        var (host, _) = NewHost(a, b);
        var right1 = host.Open(a, DockZone.Right);
        var right2 = host.Open(b, DockZone.Right);              // tabbed with right1, on show
        var bottom = host.Open(a, DockZone.Bottom);
        var floating = host.Open(b, DockZone.Float);
        bottom.SetSettings(new CountSettings { Factor = 7, Mode = OpticsMode.NonSequential, Limit = 1.5 });
        bottom.AutoUpdate = false;
        floating.FloatLeft = 100; floating.FloatTop = 50; floating.FloatWidth = 640; floating.FloatHeight = 480;
        host.RightDockWidth = 512;
        await host.WhenIdleAsync().WaitAsync(Wait);

        var saved = host.ToSettings()!;
        var json = JsonSerializer.Serialize(saved);
        var (restored, _) = NewHost(a, b);
        var notes = restored.LoadSettings(JsonSerializer.Deserialize<OpticsAnalysesSettings>(json));
        await restored.WhenIdleAsync().WaitAsync(Wait);

        Assert.Empty(notes);
        Assert.Equal(host.Layout.Placements().Select(p => (p.Item.Title, p.Placement)),
            restored.Layout.Placements().Select(p => (p.Item.Title, p.Placement)));
        Assert.Equal(host.Windows.Select(w => w.Settings), restored.Windows.Select(w => w.Settings));
        Assert.Equal(new[] { true, true, false, true }, restored.Windows.Select(w => w.AutoUpdate));
        var back = restored.Layout.Floating.Single();
        Assert.Equal((100.0, 50.0, 640.0, 480.0), (back.FloatLeft!.Value, back.FloatTop!.Value, back.FloatWidth!.Value, back.FloatHeight!.Value));
        Assert.Equal(512, restored.RightDockWidth);
        Assert.Equal(OpticsAnalysesViewModel.DefaultBottomDockHeight, restored.BottomDockHeight);
        Assert.All(restored.Windows, w => Assert.Equal(1, w.ComputeCount));   // reopened windows compute
        Assert.Equal(json, JsonSerializer.Serialize(restored.ToSettings()));   // and save back the same
    }

    [Fact]
    public void NothingOpen_SavesNothing_AndNullClosesEverything()
    {
        var counting = new CountingAnalysis();
        var (host, _) = NewHost(counting);
        Assert.Null(host.ToSettings());

        host.Open(counting);
        Assert.Empty(host.LoadSettings(null));
        Assert.False(host.HasWindows);
    }

    [Fact]
    public void AnAnalysisThisVersionLacks_IsNamed_KeptForTheNextSave_AndNotOpened()
    {
        var counting = new CountingAnalysis();
        var (host, _) = NewHost(counting);
        var later = JsonDocument.Parse("""{ "rings": 6 }""").RootElement;
        var saved = new OpticsAnalysesSettings
        {
            Windows = new()
            {
                new OpticsAnalysisWindowSettings { Analysis = "HuygensPsf", Dock = "Right", Pane = 0, Settings = later },
                new OpticsAnalysisWindowSettings { Analysis = "Counting", Dock = "Sideways" },
            },
            LastSettings = new() { ["HuygensPsf"] = later },
        };

        var notes = host.LoadSettings(saved);

        Assert.Contains(notes, n => n.Contains("'HuygensPsf'"));
        var window = Assert.Single(host.Windows);
        Assert.Equal(DockZone.Bottom, host.Layout.Find(window)!.Zone);   // an unknown dock reads as bottom
        var again = host.ToSettings()!;
        Assert.Contains(again.Windows!, w => w.Analysis == "HuygensPsf" && w.Settings!.Value.GetProperty("rings").GetInt32() == 6);
        Assert.True(again.LastSettings!.ContainsKey("HuygensPsf"));
    }

    [Fact]
    public async Task TheReport_GathersEveryWindow_InOrder_WithEachAssumptionOnce()
    {
        var a = new CountingAnalysis("A", "Alpha");
        var b = new CountingAnalysis("B", "Beta");
        var (host, session) = NewHost(a, b);
        session.Project.Name = "Triplet";
        Edit(session, OpticsMode.NonSequential);
        host.Open(a, DockZone.Right);
        host.Open(b, DockZone.Bottom);
        await host.WhenIdleAsync().WaitAsync(Wait);

        var report = host.ComposeReport();

        Assert.Equal("Triplet — Optics analyses", report.Title);
        Assert.Contains(("Mode", "Non-sequential"), report.Inputs);
        Assert.Contains(("Analyses", "Alpha, Beta"), report.Inputs);
        Assert.Equal(new[] { "Alpha: Seen", "Beta: Seen" }, report.Sections.Select(s => s.Heading));
        Assert.Equal(new[] { "Counted, not traced." }, report.Assumptions);
        Assert.Contains("## Alpha: Seen", report.ToMarkdown());
    }

    [Fact]
    public void TheReport_ListsAWindowWithNoResult_WithItsStatus()
    {
        var report = OpticsAnalysisReport.Compose("P", OpticsMode.Sequential, new[]
        {
            new OpticsAnalysisReportEntry("Spot", null, "Failed: no rays"),
            new OpticsAnalysisReportEntry("Listing", new OpticsAnalysisResult { Text = "line 1\nline 2\n" }, "Up to date"),
        });
        Assert.Equal(new[] { "Spot", "Listing" }, report.Sections.Select(s => s.Heading));
        Assert.Equal(new[] { "No result: Failed: no rays" }, report.Sections[0].Lines);
        Assert.Equal(new[] { "line 1", "line 2" }, report.Sections[1].Lines);
    }

    [Fact]
    public async Task TheAnalysisWindows_SaveAndReopen_WithTheProject()
    {
        var main = _app.Resolve<MainViewModel>();
        var session = _app.Resolve<ProjectSession>();
        main.EnterWorkspaceCommand.Execute(WorkspaceKind.Optics);
        var summary = main.OpticsAnalyses.Available.Single(a => a.Id == SystemSummaryAnalysis.SavedId);
        var docked = main.OpticsAnalyses.Open(summary, DockZone.Bottom);
        var floated = main.OpticsAnalyses.Open(summary, DockZone.Float);
        floated.SetSettings(new SystemSummarySettings { Units = LensUnit.Inch, ShowConventions = false });
        await main.OpticsAnalyses.WhenIdleAsync().WaitAsync(Wait);
        Assert.Contains("Lens units: mm", docked.ResultText);
        Assert.Contains("Lens units: in", floated.ResultText);

        // A mode change reaches the open windows through the session.
        main.Optics.SelectedMode = main.Optics.Modes.Single(m => m.Mode == OpticsMode.NonSequential);
        await main.OpticsAnalyses.WhenIdleAsync().WaitAsync(Wait);
        Assert.Contains("Mode: Non-sequential", docked.ResultText);

        string path = Path.Combine(_app.UserDirectory, "analyses.ossproj");
        main.CaptureProjectSettings();
        _app.Resolve<ProjectSerializer>().Save(session.Project, path);

        main.NewProjectCommand.Execute(null);
        Assert.False(main.OpticsAnalyses.HasWindows);

        main.LoadProjectFromPath(path);
        await main.OpticsAnalyses.WhenIdleAsync().WaitAsync(Wait);
        var windows = main.OpticsAnalyses.Windows;
        Assert.Equal(2, windows.Count);
        Assert.Equal(DockZone.Bottom, main.OpticsAnalyses.Layout.Find(windows[0])!.Zone);
        Assert.Equal(DockZone.Float, main.OpticsAnalyses.Layout.Find(windows[1])!.Zone);
        Assert.Equal(new SystemSummarySettings { Units = LensUnit.Inch, ShowConventions = false }, windows[1].Settings);
        Assert.Contains("Mode: Non-sequential", windows[0].ResultText);
    }
}
