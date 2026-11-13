using System.IO;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Persistence;
using OpenSim.Optics;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-1: the Optics workspace shell, and the rule that every workspace and every
/// analysis is registered at each place the shell switches on them.</summary>
public sealed class OpticsWorkspaceTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void EveryWorkspace_HasItsAnalyses_ANavRow_AndAName()
    {
        var main = _app.Resolve<MainViewModel>();
        foreach (var workspace in Enum.GetValues<WorkspaceKind>())
        {
            var options = AnalysisOption.ForWorkspace(workspace);
            Assert.NotEmpty(options);
            Assert.All(options, o => Assert.Equal(workspace, AnalysisOption.WorkspaceOf(o.Kind)));

            var row = Assert.Single(main.WorkspaceNav, n => n.Kind == workspace);
            Assert.False(string.IsNullOrWhiteSpace(row.FullName));   // throws for a workspace with no arm
        }
    }

    [Fact]
    public void EveryAnalysis_IsListed_AndBelongsToTheWorkspaceThatOffersIt()
    {
        foreach (var kind in Enum.GetValues<AnalysisType>())
        {
            Assert.Single(AnalysisOption.All, o => o.Kind == kind);
            var home = AnalysisOption.WorkspaceOf(kind);        // throws for an analysis with no arm
            Assert.Contains(AnalysisOption.ForWorkspace(home), o => o.Kind == kind);
        }
    }

    [Fact]
    public void EveryWorkspace_BuildsItsRail_AndItsStudyLines()
    {
        var main = _app.Resolve<MainViewModel>();
        var session = _app.Resolve<ProjectSession>();
        foreach (var workspace in Enum.GetValues<WorkspaceKind>())
        {
            session.ActiveWorkspace = workspace;
            Assert.NotEmpty(main.StudyRail.Steps);
            Assert.NotNull(main.StudyRail.SelectedStep);
            Assert.NotEmpty(main.StudyRail.StudyLines);         // the units line throws for a workspace with no arm
        }
    }

    [Fact]
    public void TheEmptyOpticsWorkspace_Opens_AndHasNothingToSolve()
    {
        var main = _app.Resolve<MainViewModel>();
        Assert.Same(_app.Resolve<OpticsViewModel>(), main.Optics);

        main.EnterWorkspaceCommand.Execute(WorkspaceKind.Optics);

        var session = _app.Resolve<ProjectSession>();
        Assert.False(session.IsHomeActive);
        Assert.Equal(AnalysisType.Optics, session.SelectedAnalysis.Kind);
        Assert.Equal(new[] { StepKind.Setup, StepKind.Solve, StepKind.Results },
            main.StudyRail.Steps.Select(s => s.Kind));
        Assert.Equal(StepKind.Setup, main.StudyRail.SelectedStep!.Kind);
        Assert.Equal(StepState.Locked, main.StudyRail.Steps[1].State);
        Assert.Equal(StepState.Locked, main.StudyRail.Steps[2].State);
        Assert.False(main.SolveActiveCommand.CanExecute(null));
        Assert.Contains("no optical system", main.StudyRail.SolveHint);
        Assert.True(main.WorkspaceNav.Single(n => n.Kind == WorkspaceKind.Optics).IsActive);
    }

    [Fact]
    public void AnOpticsProject_SavesAndReopens_InItsWorkspaceAndMode()
    {
        var main = _app.Resolve<MainViewModel>();
        var session = _app.Resolve<ProjectSession>();
        main.EnterWorkspaceCommand.Execute(WorkspaceKind.Optics);
        main.Optics.SelectedMode = main.Optics.Modes.Single(m => m.Mode == OpticsMode.NonSequential);

        string path = Path.Combine(_app.UserDirectory, "optics.ossproj");
        main.CaptureProjectSettings();
        _app.Resolve<ProjectSerializer>().Save(session.Project, path);

        // Meanwhile the app moves on, so the restore below is seen to come from the file.
        main.NewProjectCommand.Execute(null);
        Assert.Equal(WorkspaceKind.Structural, session.ActiveWorkspace);
        Assert.Equal(OpticsMode.Sequential, main.Optics.Mode);

        main.LoadProjectFromPath(path);
        Assert.Contains(_app.Log.Entries, e => e.StartsWith("Project loaded from"));
        Assert.Equal(WorkspaceKind.Optics, session.ActiveWorkspace);
        Assert.Equal(AnalysisType.Optics, session.SelectedAnalysis.Kind);
        Assert.Equal(OpticsMode.NonSequential, main.Optics.Mode);
    }

    [Fact]
    public void AModeThisVersionDoesNotKnow_IsKept_AndNamedInTheLog()
    {
        var main = _app.Resolve<MainViewModel>();
        string path = Path.Combine(_app.UserDirectory, "later.ossproj");
        File.WriteAllText(path, """{ "formatVersion": 9, "name": "Later", "analysisType": "Optics", "optics": { "mode": "Hybrid" }, "bodies": [] }""");

        main.LoadProjectFromPath(path);

        Assert.Equal(OpticsMode.Sequential, main.Optics.Mode);
        Assert.Contains(_app.Log.Entries, e => e.Contains("'Hybrid'"));
    }
}
