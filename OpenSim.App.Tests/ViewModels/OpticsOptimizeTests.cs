using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-57: the Optimize tool as the Optics workspace runs it. The optimizer's results
/// are the library's (checked in OpenSim.Tests' LocalOptimizationGateTests); here the run must
/// reach the lens through the Lens Data Editor and Undo must put the saved radius back. The
/// singlet's radii, thickness and index are arbitrary inputs.</summary>
public sealed class OpticsOptimizeTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>A singlet (index 1.5, EPD 2 mm): front radius variable, back curvature an F/50
    /// solve, back distance at the paraxial focus; merit: the marginal ray's height on the image.</summary>
    private static OpticsSettings Singlet() => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 2 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new()
            {
                Radius = 100, Thickness = 0.2, Index = 1.5, IsStop = true,
                Flags = new() { new() { Column = "Radius", Kind = "Variable" } },
            },
            new()
            {
                Radius = -100, Thickness = 100,
                Flags = new()
                {
                    new() { Column = "Radius", Kind = "Solve", Solve = "F/#", SolveParameters = new() { 50 } },
                    new() { Column = "Thickness", Kind = "Solve", Solve = "Marginal ray height", SolveParameters = new() { 0, 0 } },
                },
            },
            new(),
        },
        MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new[] { MeritOperandRow.Of("REAY", 3, 1, 0, 1, 0, 0, 0, 1) })),
    };

    [Fact]
    public async Task ARun_IsAppliedToTheLens_AndUndoPutsTheRadiusBack()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var optimize = Optics.Optimize;
        Assert.True(optimize.StartCommand.CanExecute(null));
        Assert.False(optimize.UndoCommand.CanExecute(null));
        optimize.Threads = 2;

        await optimize.StartCommand.ExecuteAsync(null);

        var result = optimize.LastResult;
        Assert.NotNull(result);
        Assert.False(optimize.IsRunning);
        Assert.True(result!.Merit < result.StartMerit);
        Assert.Equal(LocalOptimizationStop.Converged, result.Stop);
        double radius = Optics.ToSettings().Surfaces![1].Radius!.Value;
        Assert.True(Math.Abs(radius * result.Values[0] - 1) < 1e-12, $"radius {radius:R} against curvature {result.Values[0]:R}");
        Assert.NotEmpty(optimize.Log);
        Assert.True(optimize.UndoCommand.CanExecute(null));
        // The solves are still there: the back radius is still driven, not frozen.
        Assert.Equal(2, Optics.ToSettings().Surfaces![2].Flags!.Count);

        optimize.UndoCommand.Execute(null);
        Assert.Equal(100, Optics.ToSettings().Surfaces![1].Radius);
        Assert.False(optimize.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARunWithoutVariables_SaysWhy()
    {
        var singlet = Singlet();
        var surfaces = singlet.Surfaces!.ToList();
        surfaces[1] = surfaces[1] with { Flags = null };
        Assert.Empty(Optics.LoadSettings(singlet with { Surfaces = surfaces }));
        await Optics.Optimize.StartCommand.ExecuteAsync(null);
        Assert.Null(Optics.Optimize.LastResult);
        Assert.Contains("no variables", Optics.Optimize.Status);
    }
}
