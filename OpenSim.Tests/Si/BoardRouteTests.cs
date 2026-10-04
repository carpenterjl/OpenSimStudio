using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Si;
using Xunit;

namespace OpenSim.Tests.Si;

/// <summary>
/// Fix 6 gates (SI-02, SI-08): the board extraction models the route it claims. The whole
/// pin-to-pin chain of every net is in the network (bends and off-axis legs as uncoupled
/// leads, every parallel stretch coupled), the near end follows the driver pin, and the
/// proximity rebuild keeps every section.
/// </summary>
public class BoardRouteTests
{
    private const double W = 0.3e-3;
    private const double Pitch = 0.6e-3;
    private const double H = 0.2e-3;

    /// <summary>A board of polyline nets on L1 over an L2 plane; each net is its vertex list.</summary>
    private static (PcbBoard Board, List<CopperNet> Nets) RouteBoard(params Point2[][] routes)
    {
        var islands = new List<CopperIsland>();
        var centerlines = new List<TraceCenterline>();
        var nets = new List<CopperNet>();
        int islandId = 0;
        for (int r = 0; r < routes.Length; r++)
        {
            var own = new List<CopperIsland>();
            for (int k = 1; k < routes[r].Length; k++)
            {
                Point2 a = routes[r][k - 1], b = routes[r][k];
                centerlines.Add(new TraceCenterline(1, a, b, W));
                double x0 = Math.Min(a.X, b.X) - W / 2, x1 = Math.Max(a.X, b.X) + W / 2;
                double y0 = Math.Min(a.Y, b.Y) - W / 2, y1 = Math.Max(a.Y, b.Y) + W / 2;
                own.Add(new CopperIsland(islandId++, 1, "L1", new Polygon2(new[]
                    { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) })));
            }
            islands.AddRange(own);
            nets.Add(new CopperNet(r + 1, own) { Name = $"NET{r + 1}" });
        }
        islands.Add(new CopperIsland(islandId, 2, "L2", new Polygon2(new[]
        {
            new Point2(-0.2, -0.2), new Point2(0.2, -0.2), new Point2(0.2, 0.2), new Point2(-0.2, 0.2),
        })));

        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = islands,
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = nets,
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = centerlines,
            Stackup = new PcbStackupSettings
            {
                DielectricGapThicknesses = new[] { H },
                DielectricGapPermittivities = new[] { 4.4 },
                DielectricGapLossTangents = new[] { 0.02 },
            },
        };
        return (board, nets);
    }

    private static Point2 P(double xMm, double yMm) => new(xMm * 1e-3, yMm * 1e-3);

    /// <summary>Through delay of line 0 with both lines terminated in <paramref name="z0"/>.</summary>
    private static double ThroughDelay(MtlNetwork network, double z0, double f)
    {
        var terms = new[] { new LineTermination(z0, z0), new LineTermination(z0, z0) };
        var solution = network.SolveTerminated(f, terms, new[] { Complex.One, Complex.Zero });
        double phase = (solution.FarVoltages[0] / solution.NearVoltages[0]).Phase;
        return -phase / (2 * Math.PI * f);
    }

    [Fact]
    public void LShapedNet_DelayIsTheWholeRoutedLength()
    {
        // NET1: 20 mm, a 90° bend, 60 mm. NET2 runs beside the 60 mm leg only, 3 mm away
        // (inside the spacing limit, far enough that the line is electrically alone).
        var (board, nets) = RouteBoard(
            new[] { P(0, 0), P(20, 0), P(20, 60) },
            new[] { P(23, 0), P(23, 60) });
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);

        Assert.Equal(80e-3, result.RoutedLengthsMeters[0], 6);
        Assert.Equal(60e-3, result.RoutedLengthsMeters[1], 6);
        Assert.Equal(60e-3, result.CoupledLengthMeters, 6);
        Assert.Equal(20e-3, result.LeadLengthsMeters[0], 6);
        Assert.Equal(0.0, result.LeadLengthsMeters[1], 6);

        var single = RlgcExtractor.Extract(result.LeadCrossSections[0]);
        double l = single.InductanceHenriesPerMeter[0, 0], c = single.CapacitanceFaradsPerMeter[0, 0];
        double expected = 80e-3 * Math.Sqrt(l * c);
        double delay = ThroughDelay(result.Network!, Math.Sqrt(l / c), 200e6);
        Assert.InRange(delay, 0.99 * expected, 1.01 * expected);

        Assert.Contains(result.Assumptions, a => a.Contains("80 mm"));
    }

    [Fact]
    public void ShortParallelLegOfALongRoute_IsNotTheWholeModel()
    {
        // The parallel part is the 20 mm leg of an 80 mm route: the old model was 4× short.
        var (board, nets) = RouteBoard(
            new[] { P(0, 0), P(20, 0), P(20, 60) },
            new[] { P(0, 3), P(20, 3) });
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);
        Assert.Equal(20e-3, result.CoupledLengthMeters, 6);
        Assert.Equal(60e-3, result.LeadLengthsMeters[0], 6);
    }

    [Fact]
    public void PairAroundACorner_IsTwoCoupledSectionsSolvedOnce()
    {
        // A pair that turns 90° together: coupled before and after the corner; the outer
        // net is 2·pitch longer, which sits in the lead between the two sections.
        var (board, nets) = RouteBoard(
            new[] { P(0, 0), P(30, 0), P(30, 30) },
            new[] { P(0, -0.6), P(30.6, -0.6), P(30.6, 30) });
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);

        var coupled = result.Sections.Where(s => s.Coupled is not null).ToList();
        Assert.Equal(2, coupled.Count);
        Assert.Same(coupled[0].Coupled, coupled[1].Coupled);
        Assert.Equal(60e-3, result.CoupledLengthMeters, 6);
        Assert.Equal(0.0, result.LeadLengthsMeters[0], 6);
        Assert.Equal(2 * Pitch, result.LeadLengthsMeters[1], 6);
        Assert.Equal(3, result.Sections.Count);            // coupled, lead, coupled
    }

    [Fact]
    public void SwappingTheDriverPin_SwapsNearAndFarEnds()
    {
        // Unequal tails: NET1 0..40 mm, NET2 10..50 mm.
        var (board, nets) = RouteBoard(
            new[] { P(0, 0), P(40, 0) },
            new[] { P(10, 0.6), P(50, 0.6) });
        var a = BoardCoupledExtractor.Extract(board, nets);
        var b = BoardCoupledExtractor.Extract(board, nets, new BoardCoupledOptions { SwapEnds = true });
        var byPin = BoardCoupledExtractor.Extract(board, nets,
            new BoardCoupledOptions { DriverPoints = new Point2?[] { a.FarEnds[0], null } });
        Assert.Null(a.FailureReason);
        Assert.Null(b.FailureReason);

        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(0.0, (a.FarEnds[i] - b.NearEnds[i]).Length, 9);
            Assert.Equal(0.0, (a.NearEnds[i] - b.FarEnds[i]).Length, 9);
            Assert.Equal(0.0, (byPin.NearEnds[i] - b.NearEnds[i]).Length, 9);
        }

        // The network is the same structure seen from the other end: S with near and far
        // ports exchanged. NEXT of one is FEXT-side of the other.
        const double f = 2e9;
        var sa = a.Network!.Scattering(f);
        var sb = b.Network!.Scattering(f);
        int[] swap = { 2, 3, 0, 1 };
        double worst = 0;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                worst = Math.Max(worst, (sb[i, j] - sa[swap[i], swap[j]]).Magnitude);
        Assert.True(worst < 1e-9, $"port-swapped S differs by {worst:g3}");

        // And it is not symmetric to begin with (so the swap is observable).
        Assert.True((sa[0, 0] - sa[2, 2]).Magnitude > 1e-6 || (sa[1, 0] - sa[3, 2]).Magnitude > 1e-6);
    }

    [Fact]
    public void DriverOnTheOtherSide_IsReported()
    {
        var (board, nets) = RouteBoard(
            new[] { P(0, 0), P(40, 0) },
            new[] { P(10, 0.6), P(50, 0.6) });
        var result = BoardCoupledExtractor.Extract(board, nets, new BoardCoupledOptions
        {
            DriverPoints = new Point2?[] { P(0, 0), P(50, 0.6) },
        });
        Assert.Null(result.FailureReason);
        Assert.True(result.DriverAtNearEnd[0]);
        Assert.False(result.DriverAtNearEnd[1]);
        Assert.Contains(result.Assumptions, s => s.Contains("FAR end"));
    }

    [Fact]
    public void ProximityRebuild_KeepsEverySection()
    {
        // SI-08: rebuilding with R(f)/L(f) providers must not shorten the route.
        var (board, nets) = RouteBoard(
            new[] { P(0, 0), P(20, 0), P(20, 60) },
            new[] { P(20.6, 0), P(20.6, 60) });
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);

        int solves = 0;
        var proximity = result.BuildNetwork(section =>
        {
            solves++;
            var rlgc = RlgcExtractor.Extract(section);
            var prox = ProximityExtractor.Extract(section, 1e3, 1e10);
            return rlgc with
            {
                ResistanceMatrixOhmsPerMeter = prox.ResistanceMatrix,
                InternalInductanceHenriesPerMeter = prox.InternalInductance,
            };
        });
        Assert.Equal(3, solves);     // the coupled section + one lead cross-section per net

        var single = RlgcExtractor.Extract(result.LeadCrossSections[0]);
        double z0 = Math.Sqrt(single.InductanceHenriesPerMeter[0, 0] / single.CapacitanceFaradsPerMeter[0, 0]);
        double plain = ThroughDelay(result.Network!, z0, 200e6);
        double withProximity = ThroughDelay(proximity, z0, 200e6);
        Assert.InRange(withProximity, 0.98 * plain, 1.06 * plain);   // internal L only adds delay

        // The default extraction reproduces Network exactly.
        var rebuilt = result.BuildNetwork(s => RlgcExtractor.Extract(s));
        Assert.Equal(plain, ThroughDelay(rebuilt, z0, 200e6), 15);
    }

    [Fact]
    public void ParallelButFarApart_IsRefusedWithTheLimit()
    {
        var (board, nets) = RouteBoard(
            new[] { P(0, 0), P(40, 0) },
            new[] { P(0, 8), P(40, 8) });
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("5 mm", result.FailureReason);
    }
}
