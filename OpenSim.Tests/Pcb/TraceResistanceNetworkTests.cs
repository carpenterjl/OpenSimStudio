using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The DC pad-pair resistance network — nodal analysis on the trace graph. The gates
/// are composition identities against the closed form ρℓ/A (the solve is exact for the
/// lumped model by construction, so machine-sharp tolerances are the honest claim), and
/// the headline is the parallel-routes case the chain extraction refuses by design.
/// </summary>
public class TraceResistanceNetworkTests
{
    private const double Copper = 35e-6, Gap = 1.6e-3, Plating = 25e-6, Drill = 0.3e-3;
    private const double Sigma = 5.8e7;
    private const double Width = 0.4e-3;

    private static NetMeshOptions Options() => new()
    {
        CopperThickness = Copper,
        DefaultDielectricThickness = Gap,
        ViaPlatingThickness = Plating
    };

    private static TraceCenterline Trace(double x1, double y1, double x2, double y2,
        int layer = 1, double width = Width) =>
        new(layer, new Point2(x1 * 1e-3, y1 * 1e-3), new Point2(x2 * 1e-3, y2 * 1e-3), width);

    private static ChainTerminal Pad(double x, double y, int layer = 1) =>
        new(new Point2(x * 1e-3, y * 1e-3), layer);

    private static NetResistanceResult Solve(
        IReadOnlyList<TraceCenterline> centerlines, IReadOnlyList<ChainTerminal> pads,
        IReadOnlyList<ViaBridge>? vias = null)
    {
        var graph = TraceChainBuilder.BuildGraph(
            centerlines, vias ?? Array.Empty<ViaBridge>(), Options());
        return TraceResistanceNetwork.Solve(graph, pads, Sigma);
    }

    /// <summary>Bar-segment resistance ρℓ/(w·t) for a length in mm.</summary>
    private static double BarR(double lengthMm, double width = Width) =>
        lengthMm * 1e-3 / (Sigma * width * Copper);

    private static double Rel(double actual, double expected) =>
        Math.Abs(actual - expected) / Math.Abs(expected);

    [Fact]
    public void StraightTrace_TwoPads_IsRhoLOverA()
    {
        var result = Solve(
            new[] { Trace(0, 0, 40, 0) },
            new[] { Pad(0, 0), Pad(40, 0) });

        Assert.Null(result.FailureReason);
        var pair = Assert.Single(result.Pairs);
        Assert.Null(pair.Note);
        Assert.True(Rel(pair.ResistanceOhms!.Value, BarR(40)) < 1e-12,
            $"R = {pair.ResistanceOhms} vs ρℓ/A = {BarR(40)}");
    }

    [Fact]
    public void SeriesOfTwoWidths_IsTheSum()
    {
        var result = Solve(
            new[] { Trace(0, 0, 30, 0), Trace(30, 0, 50, 0, width: 0.8e-3) },
            new[] { Pad(0, 0), Pad(50, 0) });

        double expected = BarR(30) + BarR(20, 0.8e-3);
        var pair = Assert.Single(result.Pairs);
        Assert.True(Rel(pair.ResistanceOhms!.Value, expected) < 1e-12);
    }

    [Fact]
    public void TBranch_EveryPair_IsThePathSum()
    {
        // A branch is fatal to the plain chain build; the network prices every pair.
        var centerlines = new[]
        {
            Trace(0, 0, 10, 0), Trace(10, 0, 20, 0), Trace(10, 0, 10, 8),
        };
        var pads = new[] { Pad(0, 0), Pad(20, 0), Pad(10, 8) };
        var result = Solve(centerlines, pads);

        Assert.Null(result.FailureReason);
        Assert.Equal(3, result.Pairs.Count);
        Assert.True(Rel(R(result, 0, 1), BarR(10) + BarR(10)) < 1e-12);
        Assert.True(Rel(R(result, 0, 2), BarR(10) + BarR(8)) < 1e-12);
        Assert.True(Rel(R(result, 1, 2), BarR(10) + BarR(8)) < 1e-12);
        Assert.All(result.Pairs, p => Assert.True(p.ResistanceOhms > 0));
    }

    [Fact]
    public void TBranch_PadPair_MatchesTheChainPathComposition()
    {
        // Two implementations agree on a tree: the network's two-point resistance ≡
        // Σρℓ/A over the terminal path the chain builder itself extracts (exact —
        // both consume the SAME PrepareSegments recipe).
        var centerlines = new[]
        {
            Trace(0, 0, 10, 0), Trace(10, 0, 20, 0), Trace(10, 0, 10, 8),
        };
        var terminals = (Pad(0, 0), Pad(20, 0));
        var chain = TraceChainBuilder.Build(centerlines, Array.Empty<ViaBridge>(), Options(),
            terminals: terminals);
        Assert.NotNull(chain.Chain);
        double pathR = chain.Chain!.Sum(s => s.Length / (Sigma * s.Width * s.Thickness));

        var network = Solve(centerlines, new[] { Pad(0, 0), Pad(20, 0), Pad(10, 8) });
        Assert.True(Rel(R(network, 0, 1), pathR) < 1e-12);
    }

    [Fact]
    public void ParallelRoutes_AreExactlyHalfTheSingleRoute()
    {
        // THE case the chain extraction refuses ("parallel paths exist … a network
        // solve is required"): two identical-length mirror detours between the same
        // pads carry exactly half the single-route resistance.
        var routeA = new[] { Trace(0, 0, 0, 2), Trace(0, 2, 10, 2), Trace(10, 2, 10, 0) };
        var routeB = new[] { Trace(0, 0, 0, -2), Trace(0, -2, 10, -2), Trace(10, -2, 10, 0) };
        var pads = new[] { Pad(0, 0), Pad(10, 0) };

        var single = Solve(routeA, pads);
        var both = Solve(routeA.Concat(routeB).ToArray(), pads);

        double rSingle = R(single, 0, 1);
        Assert.True(Rel(rSingle, BarR(2 + 10 + 2)) < 1e-12);
        Assert.True(Rel(R(both, 0, 1), rSingle / 2) < 1e-12,
            $"parallel {R(both, 0, 1)} vs half of {rSingle}");

        // And the chain path indeed refuses this exact topology.
        var chain = TraceChainBuilder.Build(routeA.Concat(routeB).ToArray(),
            Array.Empty<ViaBridge>(), Options(), terminals: (pads[0], pads[1]));
        Assert.NotNull(chain.FailureReason);
        Assert.Contains("network solve", chain.FailureReason);
    }

    [Fact]
    public void ViaBarrel_TubeTerm_IsTheExactAnnulus()
    {
        // L1 trace → barrel → L2 trace. The barrel length is the mid-plane separation
        // (exactly gap + copper for equal copper thicknesses — pinned by the chain
        // tests) and its area the exact annulus: Width is the MEAN SHELL diameter, so
        // π·t·W ≡ π((r+t)² − r²) with r = bore/2 — algebraically, not approximately.
        double shellW = Drill + Plating;
        Assert.Equal(Math.PI * Plating * shellW,
            Math.PI * (Math.Pow(Drill / 2 + Plating, 2) - Math.Pow(Drill / 2, 2)), 15);

        var result = Solve(
            new[] { Trace(0, 0, 8, 0, layer: 1), Trace(8, 0, 16, 0, layer: 2) },
            new[] { Pad(0, 0, layer: 1), Pad(16, 0, layer: 2) },
            new[] { new ViaBridge(new Via(new Point2(8e-3, 0), Drill, Plated: true), new[] { 1, 2 }) });

        double barrel = (Gap + Copper) / (Sigma * Math.PI * Plating * shellW);
        double expected = BarR(8) + barrel + BarR(8);
        var pair = Assert.Single(result.Pairs);
        Assert.True(Rel(pair.ResistanceOhms!.Value, expected) < 1e-12,
            $"R = {pair.ResistanceOhms} vs trace+barrel+trace = {expected}");
    }

    [Fact]
    public void DuplicateDraw_CountsOnce()
    {
        // Copper drawn twice is one copper: dedup (the chain builder's own rule) keeps
        // the parallel-halving from firing on a redraw of the SAME centerline.
        var once = Solve(new[] { Trace(0, 0, 40, 0) }, new[] { Pad(0, 0), Pad(40, 0) });
        var twice = Solve(new[] { Trace(0, 0, 40, 0), Trace(0, 0, 40, 0) },
            new[] { Pad(0, 0), Pad(40, 0) });

        Assert.Equal(once.Pairs[0].ResistanceOhms!.Value, twice.Pairs[0].ResistanceOhms!.Value);
    }

    [Fact]
    public void DisconnectedPads_AreATypedNote()
    {
        var result = Solve(
            new[] { Trace(0, 0, 10, 0), Trace(30, 0, 40, 0) },
            new[] { Pad(0, 0), Pad(40, 0) });

        var pair = Assert.Single(result.Pairs);
        Assert.Null(pair.ResistanceOhms);
        Assert.Contains("not connected", pair.Note);
    }

    [Fact]
    public void UnmappedPad_IsATypedNote()
    {
        var result = Solve(
            new[] { Trace(0, 0, 10, 0) },
            new[] { Pad(0, 0), Pad(50, 50) });      // 50 mm from any copper — beyond the pad span

        var pair = Assert.Single(result.Pairs);
        Assert.Null(pair.ResistanceOhms);
        Assert.Contains("no chain junction within", pair.Note);
    }

    [Fact]
    public void CoincidentPads_ReadZeroWithANote()
    {
        var result = Solve(
            new[] { Trace(0, 0, 10, 0) },
            new[] { Pad(0, 0), Pad(0.05, 0), Pad(10, 0) });   // 0.05 mm < the 0.2 mm tolerance

        Assert.Equal(3, result.Pairs.Count);
        var coincident = result.Pairs.Single(p => p.PadA == 0 && p.PadB == 1);
        Assert.Equal(0, coincident.ResistanceOhms);
        Assert.Contains("coincide", coincident.Note);
        Assert.True(result.Pairs.Single(p => p.PadA == 0 && p.PadB == 2).ResistanceOhms > 0);
    }

    [Fact]
    public void PourNet_FailurePassesThrough()
    {
        var graph = TraceChainBuilder.BuildGraph(
            Array.Empty<TraceCenterline>(), Array.Empty<ViaBridge>(), Options());
        var result = TraceResistanceNetwork.Solve(graph, new[] { Pad(0, 0), Pad(1, 0) }, Sigma);

        Assert.NotNull(result.FailureReason);
        Assert.Contains("pour/region", result.FailureReason);
    }

    private static double R(NetResistanceResult result, int a, int b) =>
        result.Pairs.Single(p => p.PadA == a && p.PadB == b).ResistanceOhms!.Value;
}
