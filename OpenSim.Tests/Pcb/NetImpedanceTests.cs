using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class TraceChainBuilderTests
{
    private static TraceCenterline Seg(double x1, double y1, double x2, double y2,
        double width = 4e-4, int layer = 1) =>
        new(layer, new Point2(x1 * 1e-3, y1 * 1e-3), new Point2(x2 * 1e-3, y2 * 1e-3), width);

    // An L-shaped three-segment trace (coordinates in mm).
    private static TraceCenterline[] LTrace() => new[]
    {
        Seg(0, 0, 4, 0),
        Seg(4, 0, 8, 0),
        Seg(8, 0, 8, 5)
    };

    [Fact]
    public void OrderedReversedAndShuffledInputs_YieldTheSameOrientedChain()
    {
        var ordered = LTrace();
        var reversed = new[]
        {
            // Every segment flipped AND the list order scrambled.
            Seg(8, 5, 8, 0),
            Seg(4, 0, 0, 0),
            Seg(8, 0, 4, 0)
        };

        var a = TraceChainBuilder.Build(ordered);
        var b = TraceChainBuilder.Build(reversed);
        Assert.NotNull(a.Chain);
        Assert.NotNull(b.Chain);

        foreach (var result in new[] { a, b })
        {
            var chain = result.Chain!;
            Assert.Equal(3, chain.Count);
            // Deterministic start (lexicographically smallest free endpoint = origin)
            // and head-to-tail orientation throughout.
            Assert.Equal(0, chain[0].Start.X, 1e-12);
            Assert.Equal(0, chain[0].Start.Y, 1e-12);
            for (int i = 1; i < chain.Count; i++)
            {
                Assert.Equal(chain[i - 1].End.X, chain[i].Start.X, 1e-9);
                Assert.Equal(chain[i - 1].End.Y, chain[i].Start.Y, 1e-9);
            }
            Assert.Equal(8e-3, chain[^1].End.X, 1e-9);
            Assert.Equal(5e-3, chain[^1].End.Y, 1e-9);
        }

        // Same oriented geometry ⇒ identical PEEC inductance from either input order.
        double L(TraceChainResult r) => new LoopComposer().Compose(
            r.Chain!.Select(c => new TraceSegment(c.Start, c.End, c.Width, 35e-6)).ToList()).LoopInductance;
        Assert.Equal(L(a), L(b), 1e-18);
    }

    [Fact]
    public void Branch_FailsNamingTheJunction()
    {
        var branch = LTrace().Append(Seg(4, 0, 4, 3)).ToArray();   // T at (4, 0)
        var result = TraceChainBuilder.Build(branch);
        Assert.Null(result.Chain);
        Assert.Contains("branches at (4", result.FailureReason);
    }

    [Fact]
    public void DisconnectedPieces_Fail()
    {
        var pieces = new[] { Seg(0, 0, 4, 0), Seg(10, 10, 14, 10) };
        var result = TraceChainBuilder.Build(pieces);
        Assert.Null(result.Chain);
        Assert.Contains("disconnected", result.FailureReason);
    }

    [Fact]
    public void ClosedLoop_Fails()
    {
        var square = new[]
        {
            Seg(0, 0, 5, 0), Seg(5, 0, 5, 5), Seg(5, 5, 0, 5), Seg(0, 5, 0, 0)
        };
        var result = TraceChainBuilder.Build(square);
        Assert.Null(result.Chain);
        Assert.Contains("closed loop", result.FailureReason);
    }

    [Fact]
    public void MultiLayer_Fails()
    {
        var twoLayers = new[] { Seg(0, 0, 4, 0, layer: 1), Seg(4, 0, 8, 0, layer: 2) };
        var result = TraceChainBuilder.Build(twoLayers);
        Assert.Null(result.Chain);
        Assert.Contains("layers", result.FailureReason);
    }

    [Fact]
    public void Empty_FailsWithPourExplanation()
    {
        var result = TraceChainBuilder.Build(Array.Empty<TraceCenterline>());
        Assert.Null(result.Chain);
        Assert.Contains("pour", result.FailureReason);
    }
}

public class NetTraceExtractorTests
{
    private static Polygon2 Rect(double x1, double y1, double x2, double y2) =>
        new(new[]
        {
            new Point2(x1, y1), new Point2(x2, y1), new Point2(x2, y2), new Point2(x1, y2)
        });

    [Fact]
    public void CenterlinesAreAssignedToTheContainingNetOnly()
    {
        // Two nets on layer 1: one around y = 0, one around y = 10 mm.
        var islandA = new CopperIsland(0, 1, "top", Rect(-1e-3, -1e-3, 9e-3, 1e-3));
        var islandB = new CopperIsland(1, 1, "top", Rect(-1e-3, 9e-3, 9e-3, 11e-3));
        var netA = new CopperNet(0, new[] { islandA });
        var netB = new CopperNet(1, new[] { islandB });
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = new[] { islandA, islandB },
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = new[] { netA, netB },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = new[]
            {
                new TraceCenterline(1, new Point2(0, 0), new Point2(8e-3, 0), 4e-4),
                new TraceCenterline(1, new Point2(0, 10e-3), new Point2(8e-3, 10e-3), 4e-4),
                new TraceCenterline(2, new Point2(0, 0), new Point2(8e-3, 0), 4e-4)   // wrong layer
            }
        };

        var forA = NetTraceExtractor.ForNet(board, netA);
        var forB = NetTraceExtractor.ForNet(board, netB);
        Assert.Single(forA);
        Assert.Equal(0, forA[0].Start.Y, 1e-12);
        Assert.Single(forB);
        Assert.Equal(10e-3, forB[0].Start.Y, 1e-12);
    }
}

public class NetImpedanceEstimatorTests
{
    [Fact]
    public void Sweep_IsSelfConsistentWithComposedInductance()
    {
        var chain = new[]
        {
            new TraceCenterline(1, new Point2(0, 0), new Point2(10e-3, 0), 2e-4)
        };
        const double r = 0.01, thickness = 35e-6;

        var report = NetImpedanceEstimator.Estimate(r, chain, thickness, 1e3, 1e8, 6);

        // L must be exactly the LoopComposer value for the same segment geometry.
        var reference = new LoopComposer().Compose(new[]
        {
            new TraceSegment(new Point2(0, 0), new Point2(10e-3, 0), 2e-4, thickness)
        });
        Assert.Equal(reference.LoopInductance, report.InductanceHenries, 1e-18);
        Assert.Equal(r, report.ResistanceOhms);

        double previous = 0;
        foreach (var point in report.Points)
        {
            double expected = Math.Sqrt(r * r
                + Math.Pow(2 * Math.PI * point.Frequency * report.InductanceHenries, 2));
            Assert.Equal(expected, point.Magnitude, expected * 1e-12);
            Assert.True(point.Magnitude >= previous, "|Z| must grow with frequency for R + jωL.");
            previous = point.Magnitude;
        }
        // A 10 mm trace has ~7 nH: at 100 MHz ωL ≈ 4 Ω ≫ 10 mΩ ⇒ nearly inductive.
        Assert.InRange(report.Points[^1].PhaseDegrees, 89.0, 90.0);
        Assert.Contains(report.Assumptions, a => a.Contains("PARTIAL inductance"));
        Assert.Contains(report.Assumptions, a => a.Contains("skin"));
    }

    [Fact]
    public void BadInputs_ThrowActionable()
    {
        var chain = new[]
        {
            new TraceCenterline(1, new Point2(0, 0), new Point2(10e-3, 0), 2e-4)
        };
        Assert.Throws<InvalidOperationException>(() =>
            NetImpedanceEstimator.Estimate(0, chain, 35e-6, 1e3, 1e8, 5));       // no DC solve yet
        Assert.Throws<InvalidOperationException>(() =>
            NetImpedanceEstimator.Estimate(0.01, Array.Empty<TraceCenterline>(), 35e-6, 1e3, 1e8, 5));
        Assert.Throws<InvalidOperationException>(() =>
            NetImpedanceEstimator.Estimate(0.01, chain, 35e-6, 1e8, 1e3, 5));    // inverted range
    }
}

public class BoardCenterlineRetentionTests
{
    // A minimal single-layer copper file: two pads joined by one 0.4 mm round-aperture
    // trace, tagged with the FileFunction attribute the layer classifier keys on.
    private const string CopperLayer = """
        %TF.FileFunction,Copper,L1,Top*%
        %FSLAX46Y46*%
        %MOMM*%
        %ADD10C,1.000000*%
        %ADD11C,0.400000*%
        G01*
        D10*
        X1000000Y1000000D03*
        X9000000Y1000000D03*
        D11*
        X1000000Y1000000D02*
        X9000000Y1000000D01*
        M02*
        """;

    [Fact]
    public void Reader_RetainsCenterlines_AndTheFullPipelineEstimatesImpedance()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oss-centerline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "copper_l1.gbr"), CopperLayer);
            var board = new PcbBoardReader().Read(dir);

            // The union destroyed the draw, but the centerline was captured at read time.
            var centerline = Assert.Single(board.TraceCenterlines);
            Assert.Equal(1, centerline.LayerOrder);
            Assert.Equal(4e-4, centerline.Width, 1e-10);
            Assert.Equal(8e-3, centerline.Length, 1e-9);

            // Pads + trace union into one net; the extractor and chain builder complete
            // the pipeline the app runs after "Mesh selected net".
            var net = Assert.Single(board.Nets);
            var chain = TraceChainBuilder.Build(NetTraceExtractor.ForNet(board, net));
            Assert.NotNull(chain.Chain);
            var report = NetImpedanceEstimator.Estimate(0.005, chain.Chain!, 35e-6, 1e3, 1e8, 3);
            Assert.True(report.InductanceHenries > 1e-9,
                "An 8 mm trace should compose to a few nanohenries.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
