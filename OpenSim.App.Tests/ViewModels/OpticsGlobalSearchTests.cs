using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization.Global;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-58: the Global and Hammer section of the Optimize tool as the Optics workspace
/// runs it. The searches' results are the library's (OpenSim.Tests' GlobalOptimizationGateTests);
/// here a search must list its designs without touching the lens, and Apply design must write
/// the selected one through the Lens Data Editor. The singlet (OpticsOptimizeTests' fixture) is
/// an arbitrary input.</summary>
public sealed class OpticsGlobalSearchTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

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
    public async Task AGlobalSearch_ListsDesigns_AndApplyWritesTheSelectedOne()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var optimize = Optics.Optimize;
        optimize.Threads = 2;
        optimize.GlobalPopulation = 4;
        optimize.GlobalGenerations = 0;
        Assert.True(optimize.StartGlobalCommand.CanExecute(null));
        Assert.False(optimize.ApplyBestDesignCommand.CanExecute(null));

        await optimize.StartGlobalCommand.ExecuteAsync(null);

        Assert.False(optimize.IsRunning);
        var search = optimize.LastSearch;
        Assert.NotNull(search);
        Assert.Equal(GlobalSearchKind.Global, search!.Kind);
        Assert.NotEmpty(optimize.BestDesigns);
        Assert.Equal(search.Best.Count, optimize.BestDesigns.Count);
        Assert.Equal(1, optimize.BestDesigns[0].Rank);
        // The lens is untouched until a design is applied.
        Assert.Equal(100, Optics.ToSettings().Surfaces![1].Radius);

        optimize.SelectedBestDesign = optimize.BestDesigns[0];
        Assert.True(optimize.ApplyBestDesignCommand.CanExecute(null));
        optimize.ApplyBestDesignCommand.Execute(null);
        double radius = Optics.ToSettings().Surfaces![1].Radius!.Value;
        double curvature = optimize.BestDesigns[0].Design.Values[0];
        Assert.True(Math.Abs(radius * curvature - 1) < 1e-12, $"radius {radius:R} against curvature {curvature:R}");
        Assert.Equal(2, Optics.ToSettings().Surfaces![2].Flags!.Count);   // the solves are kept
    }

    [Fact]
    public async Task AHammerSearch_ListsDesigns_StartingWithTheReoptimizedStart()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var optimize = Optics.Optimize;
        optimize.Threads = 2;
        optimize.HammerRounds = 1;
        optimize.HammerTrialsPerRound = 2;

        await optimize.StartHammerCommand.ExecuteAsync(null);

        var search = optimize.LastSearch;
        Assert.NotNull(search);
        Assert.Equal(GlobalSearchKind.Hammer, search!.Kind);
        Assert.Equal(3, search.Trials);
        Assert.NotEmpty(optimize.BestDesigns);
        Assert.Equal(100, Optics.ToSettings().Surfaces![1].Radius);
    }

    [Fact]
    public async Task ASearchWithBadSettings_SaysWhy_AndListsNothing()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var optimize = Optics.Optimize;
        optimize.GlobalPopulation = 4;
        optimize.GlobalGenerations = -1;

        await optimize.StartGlobalCommand.ExecuteAsync(null);

        Assert.Null(optimize.LastSearch);
        Assert.Empty(optimize.BestDesigns);
        Assert.Contains("generations", optimize.Status);
    }
}
