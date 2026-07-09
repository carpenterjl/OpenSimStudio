using OpenSim.Core.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The stackup-aware 3D chain builder: traces lifted to their copper mid-plane, plated
/// via barrels as vertical tube segments, and the same typed-failure discipline as the
/// planar form.
/// </summary>
public class TraceChain3DTests
{
    private const double Copper = 35e-6, Gap = 1.6e-3, Plating = 25e-6, Drill = 0.3e-3;

    private static NetMeshOptions Options() => new()
    {
        CopperThickness = Copper,
        DefaultDielectricThickness = Gap,
        ViaPlatingThickness = Plating
    };

    private static TraceCenterline Trace(double x1, double y1, double x2, double y2,
        int layer, double width = 4e-4) =>
        new(layer, new Point2(x1 * 1e-3, y1 * 1e-3), new Point2(x2 * 1e-3, y2 * 1e-3), width);

    private static ViaBridge Bridge(double x, double y) =>
        new(new Via(new Point2(x * 1e-3, y * 1e-3), Drill, Plated: true), new[] { 1, 2 });

    [Fact]
    public void TwoLayerNet_BuildsTraceBarrelTrace_AtTheStackupHeights()
    {
        var result = TraceChainBuilder.Build(
            new[] { Trace(0, 0, 8, 0, layer: 1), Trace(8, 0, 16, 0, layer: 2) },
            new[] { Bridge(8, 0) },
            Options());

        Assert.NotNull(result.Chain);
        var chain = result.Chain!;
        Assert.Equal(3, chain.Count);

        // Deterministic start at the lexicographically smallest free endpoint (x = 0).
        Assert.Equal(0, chain[0].Start.X, 1e-12);
        Assert.Equal(SegmentProfile.Bar, chain[0].Profile);
        Assert.Equal(SegmentProfile.RoundTube, chain[1].Profile);
        Assert.Equal(SegmentProfile.Bar, chain[2].Profile);

        // Copper mid-plane separation for equal copper t and one gap g is exactly g + t.
        Assert.Equal(Gap + Copper, chain[1].Length, 1e-12);
        Assert.Equal(Drill + Plating, chain[1].Width, 1e-15);        // mean shell diameter
        Assert.Equal(Copper, chain[0].Thickness, 1e-15);             // stackup thickness
        // Head-to-tail continuity through the barrel.
        for (int i = 1; i < chain.Count; i++)
            Assert.Equal(0, (chain[i].Start - chain[i - 1].End).Length, 1e-12);

        // The builder must emit exactly the intended geometry: composing its chain and a
        // hand-built segment list gives the identical inductance.
        double zTop = chain[0].Start.Z, zBottom = chain[2].Start.Z;
        Assert.True(zTop > zBottom, "Layer 1 sits above layer 2.");
        var hand = new[]
        {
            new TraceSegment3D(new Vector3D(0, 0, zTop), new Vector3D(8e-3, 0, zTop), 4e-4, Copper),
            new TraceSegment3D(new Vector3D(8e-3, 0, zTop), new Vector3D(8e-3, 0, zBottom),
                Drill + Plating, Plating, SegmentProfile.RoundTube),
            new TraceSegment3D(new Vector3D(8e-3, 0, zBottom), new Vector3D(16e-3, 0, zBottom), 4e-4, Copper)
        };
        var composer = new LoopComposer();
        double handL = composer.Compose(hand).LoopInductance;
        // 1e-12 relative, not bitwise: the builder's layer thickness is the stackup's
        // zHi − zLo subtraction, one ulp off the 35 µm literal used here.
        Assert.Equal(handL, composer.Compose(chain).LoopInductance, handL * 1e-12);
    }

    [Fact]
    public void TwoViaOpenChain_Builds_AndClosedLoopFailsTyped()
    {
        // L1 → via → L2 → via → L1: five segments, still one open chain.
        var open = TraceChainBuilder.Build(
            new[]
            {
                Trace(0, 0, 8, 0, layer: 1),
                Trace(8, 0, 16, 0, layer: 2),
                Trace(16, 0, 24, 0, layer: 1)
            },
            new[] { Bridge(8, 0), Bridge(16, 0) },
            Options());
        Assert.NotNull(open.Chain);
        Assert.Equal(5, open.Chain!.Count);
        Assert.Equal(2, open.Chain.Count(s => s.Profile == SegmentProfile.RoundTube));

        // Returning to the start through a second via closes the loop — typed failure.
        var closed = TraceChainBuilder.Build(
            new[] { Trace(0, 0, 8, 0, layer: 1), Trace(8, 0, 0, 0, layer: 2) },
            new[] { Bridge(8, 0), Bridge(0, 0) },
            Options());
        Assert.Null(closed.Chain);
        Assert.Contains("closed loop", closed.FailureReason);
    }

    [Fact]
    public void SingleLayerChain_ThroughThe3DPath_EqualsTheLegacy2DResult()
    {
        var centerlines = new[]
        {
            Trace(0, 0, 8, 0, layer: 1),
            Trace(8, 0, 8, 5, layer: 1)
        };

        var legacy = TraceChainBuilder.Build(centerlines);
        Assert.NotNull(legacy.Chain);
        var legacyReport = NetImpedanceEstimator.Estimate(0.01, legacy.Chain!, Copper, 1e3, 1e8, 3);

        var lifted = TraceChainBuilder.Build(centerlines, Array.Empty<ViaBridge>(), Options());
        Assert.NotNull(lifted.Chain);
        var liftedReport = NetImpedanceEstimator.Estimate(0.01, lifted.Chain!, 1e3, 1e8, 3);

        // A pure z-translation cancels bitwise in every distance the kernel computes.
        Assert.Equal(legacyReport.InductanceHenries, liftedReport.InductanceHenries);
    }

    [Fact]
    public void DeadEndStitchVia_IsDropped_NotABranch()
    {
        // A via tapping the middle of a single-layer run with NO drawn trace on the far
        // layer (it stitches into a pad or pour): under pad-to-pad drive the stub carries
        // no current, so the chain must build exactly as if the via were absent — the
        // behavior real boards depend on (found by the example-board smoke).
        var result = TraceChainBuilder.Build(
            new[] { Trace(0, 0, 8, 0, layer: 1), Trace(8, 0, 16, 0, layer: 1) },
            new[] { Bridge(8, 0) },
            Options());
        Assert.NotNull(result.Chain);
        Assert.Equal(2, result.Chain!.Count);
        Assert.DoesNotContain(result.Chain, s => s.Profile == SegmentProfile.RoundTube);
    }

    private static Polygon2 Rect(double x1, double y1, double x2, double y2) => new(new[]
    {
        new Point2(x1 * 1e-3, y1 * 1e-3), new Point2(x2 * 1e-3, y1 * 1e-3),
        new Point2(x2 * 1e-3, y2 * 1e-3), new Point2(x1 * 1e-3, y2 * 1e-3)
    });

    [Fact]
    public void TraceRunsSplitByConnectingCopper_AreBridgedThroughTheIsland()
    {
        // Routing runs trace → pad/fill → trace: the connecting flash is not a draw
        // record, so the two runs end ~2 mm apart inside continuous copper. With the
        // net's island supplied (the union proves continuity), the builder closes the
        // gap with a straight trace-width bar and reports the bridge count.
        var centerlines = new[] { Trace(0, 0, 8, 0, layer: 1), Trace(10, 0, 18, 0, layer: 1) };
        var island = new CopperIsland(0, 1, "top", Rect(-1, -1, 19, 1));

        // Without the island: two disconnected pieces, loud.
        var bare = TraceChainBuilder.Build(centerlines, Array.Empty<ViaBridge>(), Options());
        Assert.Null(bare.Chain);
        Assert.Contains("disconnected", bare.FailureReason);

        var bridged = TraceChainBuilder.Build(centerlines, Array.Empty<ViaBridge>(), Options(),
            new[] { island });
        Assert.NotNull(bridged.Chain);
        Assert.Equal(3, bridged.Chain!.Count);
        Assert.Equal(1, bridged.CopperBridges);
        // The bridge spans exactly the gap, at the traces' width.
        var bridge = bridged.Chain.Single(s => Math.Abs(s.Length - 2e-3) < 1e-9);
        Assert.Equal(4e-4, bridge.Width, 1e-12);
    }

    [Fact]
    public void SameIslandGapBeyondPadScale_StaysALoudDisconnection()
    {
        // 8 mm apart inside one long pour: a straight bar would misstate the pour's
        // real current path, so the builder must refuse rather than approximate.
        var centerlines = new[] { Trace(0, 0, 8, 0, layer: 1), Trace(16, 0, 24, 0, layer: 1) };
        var island = new CopperIsland(0, 1, "top", Rect(-1, -1, 25, 1));
        var result = TraceChainBuilder.Build(centerlines, Array.Empty<ViaBridge>(), Options(),
            new[] { island });
        Assert.Null(result.Chain);
        Assert.Contains("disconnected", result.FailureReason);
    }

    [Fact]
    public void BranchThroughAVia_StillFailsTyped()
    {
        // Traces on L1, L2 AND a continuation on L1 all meeting at one via: degree 3.
        var result = TraceChainBuilder.Build(
            new[]
            {
                Trace(0, 0, 8, 0, layer: 1),
                Trace(8, 0, 16, 0, layer: 1),
                Trace(8, 0, 8, 5, layer: 2)
            },
            new[] { Bridge(8, 0) },
            Options());
        Assert.Null(result.Chain);
        Assert.Contains("branches", result.FailureReason);
    }

    private static ViaBridge WideBridge(double x, double y, double drill) =>
        new(new Via(new Point2(x * 1e-3, y * 1e-3), drill, Plated: true), new[] { 1, 2 });

    [Fact]
    public void ShortFinalChordIntoAVia_Composes_NotAPhantomBranch()
    {
        // The single-via regression: the L1 run arrives as a long segment plus a final
        // 0.25 mm chord (longer than the width/2 stub tolerance, shorter than the drill
        // radius). First-match junction clustering let the barrel's top endpoint absorb
        // into the chord's FAR junction (0.25 mm away, within the drill-radius
        // tolerance) instead of the via-center junction 0 mm away — that junction then
        // hosted chord-in + chord-out + barrel and the build failed with
        // "3 traces meet there" on a chain that merely passes through one via.
        var result = TraceChainBuilder.Build(
            new[]
            {
                Trace(0, 0, 7.75, 0, layer: 1),
                Trace(7.75, 0, 8, 0, layer: 1),
                Trace(8, 0, 16, 0, layer: 2)
            },
            new[] { WideBridge(8, 0, drill: 0.6e-3) },
            Options());

        Assert.NotNull(result.Chain);
        var chain = result.Chain!;
        Assert.Equal(4, chain.Count);
        Assert.Equal(1, chain.Count(s => s.Profile == SegmentProfile.RoundTube));
        for (int i = 1; i < chain.Count; i++)
            Assert.Equal(0, (chain[i].Start - chain[i - 1].End).Length, 1e-12);
    }

    [Fact]
    public void DuplicateDraws_CollapseToOne_TheWiderWins()
    {
        // Real CAD exports redraw the same centerline; each copy adds a segment-end at
        // the via junction and the chain read as a 3-way branch. The duplicate must
        // collapse before clustering, keeping the wider copy (the copper is the union).
        var result = TraceChainBuilder.Build(
            new[]
            {
                Trace(0, 0, 8, 0, layer: 1, width: 4e-4),
                Trace(0, 0, 8, 0, layer: 1, width: 6e-4),   // duplicate, wider
                Trace(8, 0, 16, 0, layer: 2)
            },
            new[] { Bridge(8, 0) },
            Options());

        Assert.NotNull(result.Chain);
        var chain = result.Chain!;
        Assert.Equal(3, chain.Count);
        Assert.Equal(6e-4, chain[0].Width, 1e-15);
    }

    private static ChainTerminal Pad(double x, double y, int layer) =>
        new(new Point2(x * 1e-3, y * 1e-3), layer);

    [Fact]
    public void BranchedNet_WithTerminals_ExtractsThePadToPadPath_Exactly()
    {
        // The same degree-3 topology that fails without terminals: under pad-to-pad
        // drive the un-driven branch carries zero current, so extracting the unique
        // source→sink path is exact physics — and which branch is "dead" depends on
        // which pads drive the net.
        var centerlines = new[]
        {
            Trace(0, 0, 8, 0, layer: 1),
            Trace(8, 0, 16, 0, layer: 1),
            Trace(8, 0, 8, 5, layer: 2)
        };
        var vias = new[] { Bridge(8, 0) };

        // Drive L1 (0,0) → L2 (8,5): the path runs through the via; the L1 continuation
        // is the dead branch.
        var throughVia = TraceChainBuilder.Build(centerlines, vias, Options(),
            terminals: (Pad(0, 0, 1), Pad(8, 5, 2)));
        Assert.NotNull(throughVia.Chain);
        Assert.Equal(3, throughVia.Chain!.Count);
        Assert.Equal(1, throughVia.Chain.Count(s => s.Profile == SegmentProfile.RoundTube));
        Assert.Equal(1, throughVia.PrunedSegments);
        Assert.Equal(8e-3, throughVia.PrunedLengthMeters, 1e-12);
        for (int i = 1; i < throughVia.Chain.Count; i++)
            Assert.Equal(0, (throughVia.Chain[i].Start - throughVia.Chain[i - 1].End).Length, 1e-12);

        // Same net, driven L1 (0,0) → L1 (16,0): now the via drop is the dead branch.
        var alongL1 = TraceChainBuilder.Build(centerlines, vias, Options(),
            terminals: (Pad(0, 0, 1), Pad(16, 0, 1)));
        Assert.NotNull(alongL1.Chain);
        Assert.Equal(2, alongL1.Chain!.Count);
        Assert.DoesNotContain(alongL1.Chain, s => s.Profile == SegmentProfile.RoundTube);
        Assert.Equal(2, alongL1.PrunedSegments);   // barrel + the L2 trace
    }

    [Fact]
    public void CleanOpenChain_WithTerminalsAtItsEnds_IsIdenticalToTheTerminallessBuild()
    {
        var centerlines = new[] { Trace(0, 0, 8, 0, layer: 1), Trace(8, 0, 16, 0, layer: 2) };
        var vias = new[] { Bridge(8, 0) };

        var plain = TraceChainBuilder.Build(centerlines, vias, Options());
        var anchored = TraceChainBuilder.Build(centerlines, vias, Options(),
            terminals: (Pad(0, 0, 1), Pad(16, 0, 2)));

        Assert.NotNull(plain.Chain);
        Assert.NotNull(anchored.Chain);
        Assert.Equal(plain.Chain!, anchored.Chain!);   // record value equality, segment by segment
        Assert.Equal(0, anchored.PrunedSegments);
    }

    [Fact]
    public void ParallelPathsBetweenTerminals_FailTyped()
    {
        // Go on L1, return on L2 through vias at both ends: a cycle. Current would
        // split between the two paths, so a single-path L is the wrong number.
        var result = TraceChainBuilder.Build(
            new[] { Trace(0, 0, 8, 0, layer: 1), Trace(8, 0, 0, 0, layer: 2) },
            new[] { Bridge(8, 0), Bridge(0, 0) },
            Options(),
            terminals: (Pad(0, 0, 1), Pad(8, 0, 2)));
        Assert.Null(result.Chain);
        Assert.Contains("parallel paths", result.FailureReason);
    }

    [Fact]
    public void DisconnectedFragment_WithTerminals_IsPrunedAsDeadCopper()
    {
        // Two pieces with no island to bridge them; both pads sit on the first piece.
        // Without terminals this is a loud disconnection; with them, the second piece is
        // dead copper (no current path reaches it) and is pruned with the count reported.
        var centerlines = new[] { Trace(0, 0, 8, 0, layer: 1), Trace(12, 0, 20, 0, layer: 1) };

        var plain = TraceChainBuilder.Build(centerlines, Array.Empty<ViaBridge>(), Options());
        Assert.Null(plain.Chain);
        Assert.Contains("disconnected", plain.FailureReason);

        var anchored = TraceChainBuilder.Build(centerlines, Array.Empty<ViaBridge>(), Options(),
            terminals: (Pad(0, 0, 1), Pad(8, 0, 1)));
        Assert.NotNull(anchored.Chain);
        Assert.Single(anchored.Chain!);
        Assert.Equal(1, anchored.PrunedSegments);
        Assert.Equal(8e-3, anchored.PrunedLengthMeters, 1e-12);
    }

    [Fact]
    public void TerminalFarFromAnyJunction_FailsTyped()
    {
        var result = TraceChainBuilder.Build(
            new[] { Trace(0, 0, 8, 0, layer: 1) },
            Array.Empty<ViaBridge>(), Options(),
            terminals: (Pad(0, 0, 1), Pad(50, 50, 1)));
        Assert.Null(result.Chain);
        Assert.Contains("no chain junction within", result.FailureReason);
    }

    [Fact]
    public void SegmentSwallowedByAJunction_IsDropped_NotADegenerateFailure()
    {
        // Chained absorption (found on the example board's RS+ net): a junction's
        // position is the first point seen, so a 0.37 mm jog whose BOTH endpoints lie
        // within the 0.2 mm tolerance of an existing junction — legal, since its length
        // only bounds at 2·tolerance — used to abort the whole build as "degenerate".
        // It is sub-junction-scale copper: drop it like a stub and keep the chain.
        var result = TraceChainBuilder.Build(new[]
        {
            Trace(0, 0, 8, 0, layer: 1),
            Trace(7.82, 0, 8.19, 0, layer: 1),   // both endpoints within 0.2 mm of (8, 0)
            Trace(8.19, 0, 16, 0, layer: 1)
        });
        Assert.NotNull(result.Chain);
        Assert.Equal(2, result.Chain!.Count);

        // Same geometry through the 3D path.
        var lifted = TraceChainBuilder.Build(new[]
        {
            Trace(0, 0, 8, 0, layer: 1),
            Trace(7.82, 0, 8.19, 0, layer: 1),
            Trace(8.19, 0, 16, 0, layer: 1)
        }, Array.Empty<ViaBridge>(), Options());
        Assert.NotNull(lifted.Chain);
        Assert.Equal(2, lifted.Chain!.Count);
    }

    [Fact]
    public void TerminalOnALayerOutsideTheStackupSpan_FailsTyped()
    {
        var result = TraceChainBuilder.Build(
            new[] { Trace(0, 0, 8, 0, layer: 1) },
            Array.Empty<ViaBridge>(), Options(),
            terminals: (Pad(0, 0, 1), Pad(8, 0, 4)));
        Assert.Null(result.Chain);
        Assert.Contains("outside the chain's stackup span", result.FailureReason);
    }
}
