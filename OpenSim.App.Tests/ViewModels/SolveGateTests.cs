using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>The Solve buttons follow the rail: live only while its Solve step is open.</summary>
public sealed class SolveGateTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void WithNoMesh_SolveIsDisabled_AndSaysWhy()
    {
        var main = _app.Resolve<MainViewModel>();

        Assert.False(main.SolveActiveCommand.CanExecute(null));
        Assert.Contains("Mesh step", main.StudyRail.SolveHint);
    }

    [Fact]
    public void AStudyThatNeedsNoMesh_CanSolveAtOnce()
    {
        var main = _app.Resolve<MainViewModel>();
        int changes = 0;
        main.SolveActiveCommand.CanExecuteChanged += (_, _) => changes++;

        _app.Resolve<ProjectSession>().ActiveWorkspace = WorkspaceKind.Rf;

        Assert.True(main.SolveActiveCommand.CanExecute(null));
        Assert.True(changes > 0);
    }
}
