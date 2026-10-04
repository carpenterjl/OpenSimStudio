using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Net topology as the trace-chain builder sees it. Each case is a small net whose
/// pad-pair resistances are a sum of ρℓ/A terms that can be written down, built so that
/// the builder's old rules gave a different number or none: a via with pads on every
/// layer, a trace ending on the middle of another, a trace through a via, a clearance
/// stroke over a trace, a pour's outline stroke, an arc on a wide trace, and a pad that
/// is not at the end of any drawn trace.
/// </summary>
public class NetTopologyTests
{
    private const double Copper = 35e-6, Gap = 0.2e-3, Plating = 25e-6, Drill = 0.3e-3;
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

    private static ViaBridge Via(double x, double y, params int[] layers) =>
        new(new Via(new Point2(x * 1e-3, y * 1e-3), Drill, Plated: true), layers);

    private static Polygon2 Rect(double x1, double y1, double x2, double y2) => new(new[]
    {
        new Point2(x1 * 1e-3, y1 * 1e-3), new Point2(x2 * 1e-3, y1 * 1e-3),
        new Point2(x2 * 1e-3, y2 * 1e-3), new Point2(x1 * 1e-3, y2 * 1e-3)
    });

    /// <summary>ρℓ/(w·t) of a trace bar, length in mm.</summary>
    private static double BarR(double lengthMm, double width = Width) =>
        lengthMm * 1e-3 / (Sigma * width * Copper);

    /// <summary>ρh/(π·t_p·(d + t_p)) of a barrel between the mid-planes of two layers
    /// <paramref name="layersApart"/> apart.</summary>
    private static double BarrelR(int layersApart) =>
        layersApart * (Gap + Copper) / (Sigma * Math.PI * Plating * (Drill + Plating));

    private static double Rel(double actual, double expected) =>
        Math.Abs(actual - expected) / Math.Abs(expected);

    private static NetResistanceResult Solve(IReadOnlyList<TraceCenterline> centerlines,
        IReadOnlyList<ChainTerminal> pads, IReadOnlyList<ViaBridge>? vias = null,
        IReadOnlyList<CopperIsland>? islands = null) =>
        TraceResistanceNetwork.Solve(
            TraceChainBuilder.BuildGraph(centerlines, vias ?? Array.Empty<ViaBridge>(), Options(), islands),
            pads, Sigma);

    private static double R(NetResistanceResult result, int a, int b)
    {
        var pair = result.Pairs.Single(p => p.PadA == Math.Min(a, b) && p.PadB == Math.Max(a, b));
        Assert.True(pair.ResistanceOhms.HasValue, $"pair ({a},{b}): {pair.Note}");
        return pair.ResistanceOhms!.Value;
    }

    // ------------------------------------------------------------------
    // PCB-06: a via with pads on every layer.
    // ------------------------------------------------------------------

    [Fact]
    public void ThroughViaWithPadsOnAllLayers_JoinsL1ToL2()
    {
        // KiCad's default through via: pads on L1..L4. Routing L1 → via → L2. The barrel
        // used to be kept only when traces ended on L1 AND L4.
        var traces = new[] { Trace(0, 0, 10, 0, layer: 1), Trace(10, 0, 20, 0, layer: 2) };
        var vias = new[] { Via(10, 0, 1, 2, 3, 4) };

        var result = Solve(traces, new[] { Pad(0, 0, 1), Pad(20, 0, 2) }, vias);
        Assert.Null(result.FailureReason);
        Assert.True(Rel(R(result, 0, 1), 2 * BarR(10) + BarrelR(1)) < 1e-9,
            $"R {R(result, 0, 1):g9} vs 2·ρℓ/A + barrel {2 * BarR(10) + BarrelR(1):g9}");

        // The ordered chain: trace, one tube, trace — no stub down to L4.
        var chain = TraceChainBuilder.Build(traces, vias, Options());
        Assert.Null(chain.FailureReason);
        Assert.Equal(3, chain.Chain!.Count);
        var tube = Assert.Single(chain.Chain, s => s.Profile == SegmentProfile.RoundTube);
        Assert.Equal(Gap + Copper, (tube.End - tube.Start).Length, 12);
    }

    [Fact]
    public void ThirdTraceOnAnInnerLayer_IsANodeOfTheBarrel()
    {
        // Traces on L1, L2 and L4 all meet at one via: each pair goes through its own
        // length of barrel.
        var traces = new[]
        {
            Trace(0, 0, 10, 0, layer: 1), Trace(10, 0, 20, 0, layer: 2), Trace(10, 0, 10, 10, layer: 4),
        };
        var result = Solve(traces, new[] { Pad(0, 0, 1), Pad(20, 0, 2), Pad(10, 10, 4) },
            new[] { Via(10, 0, 1, 2, 3, 4) });

        Assert.True(Rel(R(result, 0, 1), 2 * BarR(10) + BarrelR(1)) < 1e-9);
        Assert.True(Rel(R(result, 0, 2), 2 * BarR(10) + BarrelR(3)) < 1e-9);
        Assert.True(Rel(R(result, 1, 2), 2 * BarR(10) + BarrelR(2)) < 1e-9);
    }

    [Fact]
    public void PadOnALayerWithNoTrace_IsReachedThroughTheBarrel()
    {
        // A through-hole pin: trace on L1, the terminal is the pin's pad on L4.
        var traces = new[] { Trace(0, 0, 10, 0, layer: 1) };
        var vias = new[] { Via(10, 0, 1, 2, 3, 4) };

        var result = Solve(traces, new[] { Pad(0, 0, 1), Pad(10, 0, 4) }, vias);
        Assert.True(Rel(R(result, 0, 1), BarR(10) + BarrelR(3)) < 1e-9);

        var chain = TraceChainBuilder.Build(traces, vias, Options(),
            terminals: (Pad(0, 0, 1), Pad(10, 0, 4)));
        Assert.Null(chain.FailureReason);
        Assert.Equal(3 * (Gap + Copper),
            chain.Chain!.Where(s => s.Profile == SegmentProfile.RoundTube)
                .Sum(s => (s.End - s.Start).Length), 12);
    }

    // ------------------------------------------------------------------
    // PCB-07: junctions that are not at a segment end.
    // ------------------------------------------------------------------

    [Fact]
    public void TraceEndingOnTheMiddleOfAnother_IsAThreeWayNode()
    {
        // Main A(0,0)–B(20,0) drawn as ONE segment; stub from (3,0) to C(3,10).
        // The old result bridged the stub's free end to A: R(C,B) = 33 r, 22 % high.
        var traces = new[] { Trace(0, 0, 20, 0), Trace(3, 0, 3, 10) };
        var pads = new[] { Pad(0, 0), Pad(20, 0), Pad(3, 10) };
        var island = new[] { new CopperIsland(0, 1, "L1", Rect(-1, -1, 21, 11)) };

        var graph = TraceChainBuilder.BuildGraph(traces, Array.Empty<ViaBridge>(), Options(), island);
        Assert.Equal(0, graph.CopperBridges);
        Assert.Equal(3, graph.Segments!.Count);

        var result = TraceResistanceNetwork.Solve(graph, pads, Sigma);
        Assert.True(Rel(R(result, 0, 1), BarR(20)) < 1e-12);       // A–B
        Assert.True(Rel(R(result, 0, 2), BarR(13)) < 1e-12);       // A–C
        Assert.True(Rel(R(result, 1, 2), BarR(27)) < 1e-12);       // B–C
        Assert.All(result.Pairs, p => Assert.Null(p.Note));

        // The current path C → B: the stub and 17 mm of the main trace.
        var chain = TraceChainBuilder.Build(traces, Array.Empty<ViaBridge>(), Options(), island,
            terminals: (Pad(3, 10), Pad(20, 0)));
        Assert.Null(chain.FailureReason);
        Assert.Equal(27e-3, chain.Chain!.Sum(s => (s.End - s.Start).Length), 12);
        Assert.Equal(0, chain.CopperBridges);
    }

    [Fact]
    public void CrossingTraces_AreJoinedAtTheCrossing()
    {
        var traces = new[] { Trace(0, 0, 20, 0), Trace(8, -5, 8, 10) };
        var result = Solve(traces, new[] { Pad(0, 0), Pad(20, 0), Pad(8, -5), Pad(8, 10) });

        Assert.True(Rel(R(result, 0, 2), BarR(8 + 5)) < 1e-12);
        Assert.True(Rel(R(result, 1, 3), BarR(12 + 10)) < 1e-12);
        Assert.True(Rel(R(result, 2, 3), BarR(15)) < 1e-12);
    }

    [Fact]
    public void TraceRunningThroughAVia_IsSplitAtTheVia()
    {
        // L1 trace drawn as one segment through the via at x = 8; the L2 trace starts there.
        var traces = new[] { Trace(0, 0, 20, 0, layer: 1), Trace(8, 0, 8, 10, layer: 2) };
        var result = Solve(traces, new[] { Pad(0, 0, 1), Pad(20, 0, 1), Pad(8, 10, 2) },
            new[] { Via(8, 0, 1, 2) });

        Assert.True(Rel(R(result, 0, 2), BarR(8) + BarrelR(1) + BarR(10)) < 1e-9);
        Assert.True(Rel(R(result, 1, 2), BarR(12) + BarrelR(1) + BarR(10)) < 1e-9);
        Assert.True(Rel(R(result, 0, 1), BarR(20)) < 1e-12);
    }

    [Fact]
    public void FreeEndsInOneIsland_AreNotBridgedAcrossAGapInTheCopper()
    {
        // A U-shaped island: two trace ends 3 mm apart face each other across the slot.
        // They are in the same island, but a straight bar between them would cross air.
        var slotIsland = new Polygon2(new[]
        {
            new Point2(-1e-3, -1e-3), new Point2(21e-3, -1e-3), new Point2(21e-3, 9e-3),
            new Point2(11.5e-3, 9e-3), new Point2(11.5e-3, 2e-3), new Point2(8.5e-3, 2e-3),
            new Point2(8.5e-3, 9e-3), new Point2(-1e-3, 9e-3),
        });
        var traces = new[] { Trace(0, 8, 8, 8), Trace(11.9, 8, 20, 8) };   // ends at x = 8 and 11.9, y = 8
        var islands = new[] { new CopperIsland(0, 1, "L1", slotIsland) };

        var graph = TraceChainBuilder.BuildGraph(traces, Array.Empty<ViaBridge>(), Options(), islands);
        Assert.Equal(0, graph.CopperBridges);

        // The same two ends in a solid island ARE bridged (the trace → pad → trace case).
        var solid = new[] { new CopperIsland(0, 1, "L1", Rect(-1, -1, 21, 9)) };
        Assert.Equal(1, TraceChainBuilder.BuildGraph(traces, Array.Empty<ViaBridge>(), Options(), solid).CopperBridges);
    }

    // ------------------------------------------------------------------
    // PCB-08: what counts as a trace centerline.
    // ------------------------------------------------------------------

    private const string Head = "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.6*%\n%ADD11C,0.2*%\n%ADD12C,1.0*%\nG75*\n";

    [Fact]
    public void ClearanceStrokeOverATrace_IsNotACenterline()
    {
        // "Clear a 0.6 mm channel, then draw the 0.2 mm trace in it." The clear stroke
        // used to win the wider-survives de-duplication.
        var doc = new GerberParser().Parse(Head +
            "G36*\nX0Y-5000000D02*\nX30000000Y-5000000D01*\nX30000000Y5000000D01*\nX0Y5000000D01*\nX0Y-5000000D01*\nG37*\n" +
            "%LPC*%\nD10*\nX5000000Y0D02*\nX25000000Y0D01*\n" +
            "%LPD*%\nD11*\nX5000000Y0D02*\nX25000000Y0D01*\nM02*");

        var line = Assert.Single(TraceSegmenter.Centerlines(doc, 1));
        Assert.Equal(0.2e-3, line.Width, 12);
        Assert.Equal(20e-3, line.Length, 12);
    }

    [Fact]
    public void PourOutlineStroke_IsNotATrace_ButATraceLeavingThePourIs()
    {
        // A zone drawn as a region and then stroked around its outline with a 0.2 mm
        // pen (older CAD output), plus one real trace leaving a corner of the pour.
        const string Outline = "X0Y0D02*\nX10000000Y0D01*\nX10000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\n";
        var doc = new GerberParser().Parse(Head +
            "G36*\n" + Outline + "G37*\n" +
            "D11*\n" + Outline +
            "X10000000Y10000000D02*\nX20000000Y10000000D01*\nM02*");

        var line = Assert.Single(TraceSegmenter.Centerlines(doc, 1));
        Assert.Equal(10e-3, line.Start.X, 12);
        Assert.Equal(20e-3, line.End.X, 12);

        // The pour alone has no trace at all.
        var pourOnly = new GerberParser().Parse(Head + "G36*\n" + Outline + "G37*\nD11*\n" + Outline + "M02*");
        Assert.Empty(TraceSegmenter.Centerlines(pourOnly, 1));
    }

    // ------------------------------------------------------------------
    // PCB-10: an arc on a wide trace.
    // ------------------------------------------------------------------

    [Fact]
    public void ArcOnAWideTrace_KeepsItsLength()
    {
        // 1 mm wide: 10 mm straight, a 90° arc of radius 1 mm, 10 mm straight. The arc
        // is tessellated into ~0.2 mm chords, all shorter than the 0.5 mm stub cut — it
        // used to vanish from the chain.
        var doc = new GerberParser().Parse(Head +
            "D12*\nX0Y0D02*\nG01*\nX10000000Y0D01*\n" +
            "G03*\nX11000000Y1000000I0J1000000D01*\n" +
            "G01*\nX11000000Y11000000D01*\nM02*");
        var centerlines = TraceSegmenter.Centerlines(doc, 1);

        Assert.All(centerlines, c => Assert.True(c.Length > 0.5e-3, $"chord {c.Length * 1e3:g3} mm"));
        double truePath = 10e-3 + Math.PI / 2 * 1e-3 + 10e-3;
        Assert.Equal(truePath, centerlines.Sum(c => c.Length), 0.01 * truePath);

        var graph = TraceChainBuilder.BuildGraph(centerlines, Array.Empty<ViaBridge>(), Options(),
            new[] { new CopperIsland(0, 1, "L1", Rect(-1, -1, 12, 12)) });
        Assert.Equal(0, graph.CopperBridges);
        var result = TraceResistanceNetwork.Solve(graph, new[] { Pad(0, 0), Pad(11, 11) }, Sigma);
        Assert.Equal(truePath / (Sigma * 1e-3 * Copper), R(result, 0, 1), 0.01 * truePath / (Sigma * 1e-3 * Copper));

        var chain = TraceChainBuilder.Build(centerlines);
        Assert.Null(chain.FailureReason);
        Assert.Equal(centerlines.Count, chain.Chain!.Count);
    }

    // ------------------------------------------------------------------
    // PCB-11: a pad that is not at a trace end.
    // ------------------------------------------------------------------

    [Fact]
    public void PadAwayFromTheNearestTraceEnd_GetsANote_NotABareNumber()
    {
        var traces = new[] { Trace(0, 0, 40, 0) };

        // Pad 1 sits 3 mm beyond the end of the trace (joined through a pour, say).
        var far = TraceResistanceNetwork.Solve(
            TraceChainBuilder.BuildGraph(traces, Array.Empty<ViaBridge>(), Options()),
            new[] { Pad(0, 0), Pad(43, 0) }, Sigma);
        var pair = Assert.Single(far.Pairs);
        Assert.True(Rel(pair.ResistanceOhms!.Value, BarR(40)) < 1e-12);
        Assert.NotNull(pair.Note);
        Assert.Contains("pad #1 is 2.8 mm from the nearest drawn trace end", pair.Note);
        Assert.Contains("NOT in this resistance", pair.Note);

        // A pad whose own copper reaches the trace end is attached, no note.
        var reaching = TraceResistanceNetwork.Solve(
            TraceChainBuilder.BuildGraph(traces, Array.Empty<ViaBridge>(), Options()),
            new[] { Pad(0, 0), Pad(43, 0) with { Reach = 3.5e-3 } }, Sigma);
        Assert.Null(Assert.Single(reaching.Pairs).Note);
    }
}
