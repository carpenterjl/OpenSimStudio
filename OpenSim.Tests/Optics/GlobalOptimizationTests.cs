using OpenSim.Core.Model;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Optimization.Global;
using static OpenSim.Tests.Optics.LocalOptimizationFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-58: the parts of the Global and Hammer searches that need no long run: the best list's
/// order and diversity rule, the default ranges, refusals and a search stopped before it starts.
/// The searches themselves are checked in Gates/GlobalOptimizationGateTests (Slow). Lenses are
/// the local optimizer's fixtures (arbitrary inputs).
/// </summary>
public sealed class GlobalOptimizationTests
{
    private static GlobalDesign D(double merit, bool met, int trial, double value, string? glass = null) => new()
    {
        Design = new OpticsSettings(), Merit = merit, ConstraintsMet = met, Values = new[] { value },
        Glasses = glass is null ? Array.Empty<string>() : new[] { glass }, Trial = trial, Iteration = 0,
    };

    private static readonly GlobalVariableRange[] Unit = { new(0, 1) };

    [Fact]
    public void TheBestList_PutsConstraintMeetingDesignsFirst_ThenMerit_ThenTheEarlierTrial()
    {
        var list = new GlobalBestList(10, Unit, 0.01);
        Assert.True(list.Offer(D(1, met: false, trial: 0, value: 0.9)));
        Assert.True(list.Offer(D(2, met: true, trial: 1, value: 0.5)));
        Assert.True(list.Offer(D(2, met: true, trial: 2, value: 0.1)));
        Assert.True(list.Offer(D(0.5, met: true, trial: 3, value: 0.3)));
        Assert.Equal(new[] { 3, 1, 2, 0 }, list.Items.Select(d => d.Trial));
    }

    [Fact]
    public void TheBestList_KeepsOneDesignPerForm_TheBetterOne()
    {
        var list = new GlobalBestList(10, Unit, 0.01);
        Assert.True(list.Offer(D(2, true, 0, 0.500)));
        // Within 0.01 of the range width (1): the same form. Better: replaces it.
        Assert.True(list.Offer(D(1.5, true, 1, 0.505)));
        Assert.Equal(new[] { 1 }, list.Items.Select(d => d.Trial));
        // The same form again, worse: dropped.
        Assert.False(list.Offer(D(3, true, 2, 0.509)));
        // An equal merit of the same form keeps the earlier design.
        Assert.False(list.Offer(D(1.5, true, 3, 0.503)));
        // Another form (0.02 away) is kept beside it.
        Assert.True(list.Offer(D(3, true, 4, 0.525)));
        Assert.Equal(new[] { 1, 4 }, list.Items.Select(d => d.Trial));
        // The same values with another glass are another form.
        var glasses = new GlobalBestList(10, Unit, 0.01);
        Assert.True(glasses.Offer(D(1, true, 0, 0.5, "A:x")));
        Assert.True(glasses.Offer(D(2, true, 1, 0.5, "A:y")));
        Assert.Equal(2, glasses.Items.Count);
    }

    [Fact]
    public void TheBestList_KeepsItsCapacity_DroppingTheWorst()
    {
        var list = new GlobalBestList(3, Unit, 0.01);
        for (int i = 0; i < 5; i++) list.Offer(D(10 - i, true, i, 0.1 * i));
        Assert.Equal(new[] { 4, 3, 2 }, list.Items.Select(d => d.Trial));
        Assert.False(list.Offer(D(100, true, 9, 0.95)));
        Assert.Throws<ArgumentException>(() => new GlobalBestList(0, Unit, 0.01));
    }

    [Fact]
    public void DefaultRanges_FollowTheStatedRules()
    {
        var doublet = Doublet();   // variables: surface 2 and 3 curvatures (R −45 and −200)
        var surfaces = doublet.Surfaces!.ToList();
        surfaces[1] = surfaces[1] with { Flags = new() { Variable("Thickness") } };   // thickness 4
        doublet = doublet with { Surfaces = surfaces };
        var variables = LensVariables.Find(doublet);
        Assert.Equal(new[] { LensVariableKind.Thickness, LensVariableKind.Curvature, LensVariableKind.Curvature }, variables.Select(v => v.Kind));

        var ranges = GlobalTrials.DefaultRanges(doublet, NoCatalogs, variables, curvatureLimit: 0.05);
        Assert.Equal(new GlobalVariableRange(2, 6), ranges[0]);              // half to one and a half times 4
        Assert.Equal(new GlobalVariableRange(-0.05, 0.05), ranges[1]);
        Assert.Equal(new GlobalVariableRange(-0.05, 0.05), ranges[2]);

        // A limit smaller than the start's curvature (1/45) is widened to contain it.
        var narrow = GlobalTrials.DefaultRanges(doublet, NoCatalogs, variables, curvatureLimit: 0.01);
        Assert.Equal(-1 / 45.0, narrow[1].Lower);
        Assert.Equal(0.01, narrow[1].Upper);

        // The default limit is 3/|EFL| of the start: symmetric and positive.
        var auto = GlobalTrials.DefaultRanges(doublet, NoCatalogs, variables);
        Assert.True(auto[2].Upper > 0 && auto[2].Lower == -auto[2].Upper);
        Assert.Throws<ArgumentException>(() => GlobalTrials.DefaultRanges(doublet, NoCatalogs, variables, curvatureLimit: -1));
    }

    [Fact]
    public void TheSearches_RefuseWhatTheyCannotRun()
    {
        var noVariables = Doublet() with
        {
            Surfaces = Doublet().Surfaces!.Select(s => s with { Flags = s.Flags?.Where(f => f.Kind != "Variable").ToList() }).ToList(),
        };
        Assert.Throws<ArgumentException>(() => GlobalSearch.Run(noVariables, NoCatalogs));
        Assert.Throws<ArgumentException>(() => HammerSearch.Run(noVariables, NoCatalogs));
        Assert.Throws<ArgumentException>(() => GlobalSearch.Run(Doublet(), NoCatalogs, new GlobalSearchOptions { PopulationSize = 3 }));
        Assert.Throws<ArgumentException>(() => GlobalSearch.Run(Doublet(), NoCatalogs, new GlobalSearchOptions { Crossover = 1.5 }));
        Assert.Throws<ArgumentException>(() => GlobalSearch.Run(Doublet(), NoCatalogs, new GlobalSearchOptions { Ranges = new[] { new GlobalVariableRange(0, 1) } }));
        Assert.Throws<ArgumentException>(() => HammerSearch.Run(Doublet(), NoCatalogs, new HammerSearchOptions { TrialsPerRound = 0 }));
        Assert.Throws<ArgumentException>(() => HammerSearch.Run(Doublet(), NoCatalogs, new HammerSearchOptions { GlassChangeProbability = 2 }));
    }

    [Fact]
    public void ASearchStoppedBeforeItStarts_ReturnsNoDesign_AndSaysSo()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var global = GlobalSearch.Run(Doublet(), NoCatalogs, new GlobalSearchOptions { CancellationToken = cancel.Token, PopulationSize = 4 });
        Assert.Equal(GlobalSearchStop.Cancelled, global.Stop);
        Assert.Empty(global.Best);
        Assert.Equal(0, global.Trials);
        Assert.True(double.IsFinite(global.StartMerit));

        var hammer = HammerSearch.Run(Doublet(), NoCatalogs, new HammerSearchOptions { CancellationToken = cancel.Token });
        Assert.Equal(GlobalSearchStop.Cancelled, hammer.Stop);
        Assert.Empty(hammer.Best);
    }
}
