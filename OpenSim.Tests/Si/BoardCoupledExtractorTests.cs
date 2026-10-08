using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using Xunit;

namespace OpenSim.Tests.Si;

/// <summary>
/// The board multi-trace extraction gates (SI Stage S6). The load-bearing gate is the
/// round-trip: a synthetic parallel-trace board must produce the SAME per-unit-length RLGC
/// as the wizard cross-section for the equivalent geometry — to 1e-6, well below the 2D
/// BEM's own accuracy — because the extracted C is translation-invariant in absolute
/// position, so the only thing that can move it is a wrong width/gap/substrate. The rest
/// are the typed-failure gates: every non-conforming topology names its reason instead of
/// returning a garbage matrix.
/// </summary>
public class BoardCoupledExtractorTests
{
    private const double W = 0.3e-3;   // trace width
    private const double S = 0.3e-3;   // edge-to-edge gap
    private const double Pitch = W + S;
    private const double H = 0.2e-3;   // substrate height
    private const double EpsR = 4.4, TanD = 0.02;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    /// <summary>A board of horizontal strips on L1 over an L2 reference plane, one net per
    /// strip: (xStart, xEnd, yCenter). A per-gap stackup carries the substrate.</summary>
    private static (PcbBoard Board, List<CopperNet> Nets) StripBoard(
        params (double X0, double X1, double Yc)[] strips)
    {
        var islands = new List<CopperIsland>();
        var centerlines = new List<TraceCenterline>();
        var nets = new List<CopperNet>();
        int idx = 0;
        foreach (var (x0, x1, yc) in strips)
        {
            var island = new CopperIsland(idx, 1, "L1", Rect(x0, yc - W / 2, x1, yc + W / 2));
            islands.Add(island);
            centerlines.Add(new TraceCenterline(1, new Point2(x0, yc), new Point2(x1, yc), W));
            nets.Add(new CopperNet(idx + 1, new[] { island }) { Name = $"NET{idx + 1}" });
            idx++;
        }
        // The L2 reference plane (another net's copper): the extractor looks for it now.
        islands.Add(new CopperIsland(idx, 2, "L2", Rect(-100e-3, -100e-3, 100e-3, 100e-3)));

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
                DielectricGapPermittivities = new[] { EpsR },
                DielectricGapLossTangents = new[] { TanD },
            },
        };
        return (board, nets);
    }

    private static RlgcResult WizardRlgc(int n)
    {
        var stack = new LayeredStackup(new[] { new LayeredStackup.Layer(EpsR, TanD, H) });
        var traces = new TraceCrossSection[n];
        double origin = -(n - 1) * Pitch / 2;
        for (int i = 0; i < n; i++)
            traces[i] = TraceCrossSection.Copper(origin + i * Pitch, W);
        return RlgcExtractor.Extract(new CoupledLineCrossSection(stack, 0, traces));
    }

    private static void AssertMatrixClose(double[,] a, double[,] b, double rel, string what)
    {
        int n = a.GetLength(0);
        double scale = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                scale = Math.Max(scale, Math.Abs(a[i, j]));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                Assert.True(Math.Abs(a[i, j] - b[i, j]) <= rel * scale,
                    $"{what}[{i},{j}]: board {b[i, j]:g6} vs wizard {a[i, j]:g6} " +
                    $"(Δ/scale {Math.Abs(a[i, j] - b[i, j]) / scale:g3})");
    }

    // ------------------------------------------------------------------
    // The round-trip gate.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ParallelStrips_MatchWizardRlgc(int n)
    {
        // Strips at an OFFSET y-origin (yc = 5 mm + i·pitch) so the extracted centers differ
        // from the wizard's symmetric ±pitch/2 — the match then proves both fidelity and the
        // translation invariance of the C matrix, not a trivial coordinate coincidence.
        var strips = new (double, double, double)[n];
        for (int i = 0; i < n; i++) strips[i] = (0, 40e-3, 5e-3 + i * Pitch);
        var (board, nets) = StripBoard(strips);

        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);
        Assert.NotNull(result.Rlgc);

        var wizard = WizardRlgc(n);
        AssertMatrixClose(wizard.CapacitanceFaradsPerMeter,
            result.Rlgc!.CapacitanceFaradsPerMeter, 1e-6, "C");
        AssertMatrixClose(wizard.InductanceHenriesPerMeter,
            result.Rlgc.InductanceHenriesPerMeter, 1e-6, "L");
        AssertMatrixClose(wizard.CapacitanceLossFaradsPerMeter,
            result.Rlgc.CapacitanceLossFaradsPerMeter, 1e-6, "C''");

        // The full-length strips overlap entirely → the coupled section is the whole run.
        Assert.Equal(40e-3, result.CoupledLengthMeters, 6);
        Assert.NotNull(result.Network);
    }

    [Fact]
    public void PartialOverlap_SetsCoupledLength_AndReportsLeads()
    {
        // NET1 spans x 0..40 mm, NET2 x 10..50 mm → 30 mm overlap, 10 mm lead each.
        var (board, nets) = StripBoard((0, 40e-3, 0), (10e-3, 50e-3, Pitch));
        var result = BoardCoupledExtractor.Extract(board, nets);

        Assert.Null(result.FailureReason);
        Assert.Equal(30e-3, result.CoupledLengthMeters, 6);
        Assert.Equal(2, result.LeadLengthsMeters.Count);
        Assert.All(result.LeadLengthsMeters, l => Assert.Equal(10e-3, l, 6));
    }

    [Fact]
    public void PartialOverlap_CascadesTheLeadsIntoTheNetwork()
    {
        // Stage B5: the tails are no longer reported-and-dropped. NET1 spans 0..40 mm and NET2
        // 10..50 mm, so each has a 10 mm tail on the opposite side — the low tail belongs to
        // NET1, the high tail to NET2. Cascading them must LENGTHEN the electrical path, which
        // shows as extra delay: the through phase at the far end must lag the coupled section
        // taken alone. (The lead lengths themselves are gated above; this asserts they reach
        // the network rather than only the report.)
        var (board, nets) = StripBoard((0, 40e-3, 0), (10e-3, 50e-3, Pitch));
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);

        // A network built from the coupled section alone, for comparison.
        var coupledOnly = new MtlNetwork(new[]
            { new MtlSection(result.Rlgc!, result.CoupledLengthMeters) });

        const double f = 3e9;
        var terms = new[] { new LineTermination(50, 50), new LineTermination(50, 50) };
        var drive = new[] { Complex.One, Complex.Zero };
        var withLeads = result.Network!.SolveTerminated(f, terms, drive);
        var without = coupledOnly.SolveTerminated(f, terms, drive);

        double phaseWith = withLeads.FarVoltages[0].Phase;
        double phaseWithout = without.FarVoltages[0].Phase;
        Assert.NotEqual(phaseWithout, phaseWith);

        Assert.Contains(result.Assumptions, a => a.Contains("CASCADED"));
    }

    [Fact]
    public void FullOverlap_KeepsTheSingleSectionNetwork()
    {
        // The bitwise pin's precondition: with no tails at all the extractor must build the
        // pre-existing single-section network, so a board without leads is on exactly the
        // arithmetic it always was. The assumption line says so rather than listing 0 mm tails.
        var (board, nets) = StripBoard((0, 40e-3, 0), (0, 40e-3, Pitch));
        var result = BoardCoupledExtractor.Extract(board, nets);

        Assert.Null(result.FailureReason);
        Assert.All(result.LeadLengthsMeters, l => Assert.Equal(0.0, l, 9));
        Assert.Contains(result.Assumptions, a => a.Contains("no lead tails"));
        Assert.DoesNotContain(result.Assumptions, a => a.Contains("CASCADED"));
    }

    // ------------------------------------------------------------------
    // Typed failures — every non-conforming topology names its reason.
    // ------------------------------------------------------------------

    [Fact]
    public void SingleNet_IsTypedFailure()
    {
        var (board, nets) = StripBoard((0, 40e-3, 0), (0, 40e-3, Pitch));
        var result = BoardCoupledExtractor.Extract(board, new[] { nets[0] });
        Assert.NotNull(result.FailureReason);
        Assert.Contains("at least two", result.FailureReason);
    }

    [Fact]
    public void NonParallelNets_AreTypedFailure()
    {
        // NET2 runs diagonally — a non-parallel tangle, not a coupled line.
        var (board, nets) = StripBoard((0, 40e-3, 0));
        var island = new CopperIsland(1, 1, "L1", Rect(0, 2e-3, 40e-3, 40e-3));
        var diagonalNet = new CopperNet(2, new[] { island }) { Name = "DIAG" };
        var board2 = new PcbBoard
        {
            Outline = board.Outline, Islands = new[] { board.Islands[0], island },
            Pads = board.Pads, Vias = board.Vias,
            Nets = new[] { nets[0], diagonalNet }, Layers = board.Layers,
            Warnings = board.Warnings, Stackup = board.Stackup,
            TraceCenterlines = new[]
            {
                board.TraceCenterlines[0],
                new TraceCenterline(1, new Point2(0, 2e-3), new Point2(40e-3, 38e-3), W),
            },
        };
        var result = BoardCoupledExtractor.Extract(board2, new[] { nets[0], diagonalNet });
        Assert.NotNull(result.FailureReason);
        Assert.Contains("parallel", result.FailureReason);
    }

    [Fact]
    public void PourNetWithNoCenterlines_IsTypedFailure()
    {
        // NET2 has an island but no drawn centerline (a pour/region).
        var (board, nets) = StripBoard((0, 40e-3, 0));
        var pourIsland = new CopperIsland(1, 1, "L1", Rect(0, 5e-3, 40e-3, 15e-3));
        var pourNet = new CopperNet(2, new[] { pourIsland }) { Name = "GND" };
        var board2 = new PcbBoard
        {
            Outline = board.Outline, Islands = new[] { board.Islands[0], pourIsland },
            Pads = board.Pads, Vias = board.Vias,
            Nets = new[] { nets[0], pourNet }, Layers = board.Layers,
            Warnings = board.Warnings, Stackup = board.Stackup,
            TraceCenterlines = board.TraceCenterlines,   // only NET1 has a centerline
        };
        var result = BoardCoupledExtractor.Extract(board2, new[] { nets[0], pourNet });
        Assert.NotNull(result.FailureReason);
        Assert.Contains("no trace centerlines", result.FailureReason);
    }

    [Fact]
    public void LaterallyTouchingNets_AreTypedFailure()
    {
        // Two strips whose edges meet (gap 0) — a broadside pair or one net drawn twice.
        var (board, nets) = StripBoard((0, 40e-3, 0), (0, 40e-3, W));   // centers W apart, edges touch
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("overlap laterally", result.FailureReason);
    }

    [Fact]
    public void EndToEndNets_HaveNoOverlap_TypedFailure()
    {
        // NET1 x 0..20 mm, NET2 x 30..50 mm on parallel lines → no longitudinal overlap.
        var (board, nets) = StripBoard((0, 20e-3, 0), (30e-3, 50e-3, Pitch));
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("no longitudinal", result.FailureReason);
    }

    // ------------------------------------------------------------------
    // Coplanar ground copper on the trace layer.
    // ------------------------------------------------------------------

    private const double GroundGap = 0.2e-3, GroundWidth = 2e-3;

    /// <summary>Two strips at y = 5 mm and 5 mm + pitch, x 0..40 mm, between L1 ground pours
    /// <see cref="GroundGap"/> from their outer edges, stitched to the L2 plane (one GND net).
    /// The high-side pour runs only from x = 0 to <paramref name="highPourEnd"/>; a further
    /// island of another net may sit between the low pour and the pair.</summary>
    private static (PcbBoard Board, List<CopperNet> Nets) PairBetweenPours(double highPourEnd = 42e-3,
        bool otherCopperBetween = false)
    {
        const double y0 = 5e-3, y1 = 5e-3 + Pitch;
        var a = new CopperIsland(0, 1, "L1", Rect(0, y0 - W / 2, 40e-3, y0 + W / 2));
        var b = new CopperIsland(1, 1, "L1", Rect(0, y1 - W / 2, 40e-3, y1 + W / 2));
        double lowEdge = y0 - W / 2, highEdge = y1 + W / 2;
        var lowPour = new CopperIsland(2, 1, "L1", Rect(-2e-3, lowEdge - GroundGap - GroundWidth, 42e-3, lowEdge - GroundGap));
        var highPour = new CopperIsland(3, 1, "L1", Rect(-2e-3, highEdge + GroundGap, highPourEnd, highEdge + GroundGap + GroundWidth));
        var plane = new CopperIsland(4, 2, "L2", Rect(-100e-3, -100e-3, 100e-3, 100e-3));
        var islands = new List<CopperIsland> { a, b, lowPour, highPour, plane };
        var nets = new List<CopperNet>
        {
            new(1, new[] { a }) { Name = "A" },
            new(2, new[] { b }) { Name = "B" },
            new(3, new[] { lowPour, highPour, plane }) { Name = "GND" },
        };
        if (otherCopperBetween)
        {
            // A sliver of another net in the low-side gap, over the whole run.
            var other = new CopperIsland(5, 1, "L1", Rect(-2e-3, lowEdge - 0.15e-3, 42e-3, lowEdge - 0.1e-3));
            islands.Add(other);
            nets.Add(new CopperNet(4, new[] { other }) { Name = "OTHER" });
        }
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = islands,
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = nets,
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = new[]
            {
                new TraceCenterline(1, new Point2(0, y0), new Point2(40e-3, y0), W),
                new TraceCenterline(1, new Point2(0, y1), new Point2(40e-3, y1), W),
            },
            Stackup = new PcbStackupSettings
            {
                DielectricGapThicknesses = new[] { H },
                DielectricGapPermittivities = new[] { EpsR },
                DielectricGapLossTangents = new[] { TanD },
            },
        };
        return (board, nets.Take(2).ToList());
    }

    /// <summary>The pair with the given ground strips, solved and reduced by hand.</summary>
    private static RlgcResult PairWithGrounds(bool low, bool high)
    {
        var stack = new LayeredStackup(new[] { new LayeredStackup.Layer(EpsR, TanD, H) });
        var traces = new List<TraceCrossSection> { TraceCrossSection.Copper(0, W), TraceCrossSection.Copper(Pitch, W) };
        if (low) traces.Add(TraceCrossSection.Copper(-W / 2 - GroundGap - GroundWidth / 2, GroundWidth) with { IsGround = true });
        if (high) traces.Add(TraceCrossSection.Copper(Pitch + W / 2 + GroundGap + GroundWidth / 2, GroundWidth) with { IsGround = true });
        var section = new CoupledLineCrossSection(stack, 0, traces);
        return BoardCoupledResult.Reduce(section, RlgcExtractor.Extract(section));
    }

    [Fact]
    public void GroundPoursBesideThePair_AreCoplanarGrounds()
    {
        var (board, nets) = PairBetweenPours();
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);
        Assert.Equal(2, result.CrossSection!.SignalTraces.Count);
        Assert.Equal(2, result.CrossSection.GroundIndices.Count);
        Assert.Equal(2, result.Rlgc!.ConductorCount);

        // The same cross-section built by hand, to the round-trip tolerance.
        var byHand = PairWithGrounds(low: true, high: true);
        AssertMatrixClose(byHand.CapacitanceFaradsPerMeter, result.Rlgc.CapacitanceFaradsPerMeter, 1e-6, "C");
        AssertMatrixClose(byHand.InductanceHenriesPerMeter, result.Rlgc.InductanceHenriesPerMeter, 1e-6, "L");

        // And it is not the pair alone: the grounds take charge and lower the impedance.
        var alone = BoardCoupledExtractor.Extract(board, nets, new BoardCoupledOptions { CoplanarGround = false });
        // Measured: C11 1.0118× the pair's alone, with the ground one substrate height away.
        double rise = result.Rlgc.CapacitanceFaradsPerMeter[0, 0] / alone.Rlgc!.CapacitanceFaradsPerMeter[0, 0];
        Assert.InRange(rise, 1.005, 1.05);
        Assert.Contains(result.Assumptions, a => a.Contains("Coplanar ground") && a.Contains("tied to the reference"));
        Assert.Equal(2, result.Network!.ConductorCount);
    }

    [Fact]
    public void GroundBesideOnlyPartOfTheStretch_IsNotModelled_AndSaysSo()
    {
        // The high-side pour stops halfway: under 90 % of the stretch, so only the low side is
        // carried, and the note says why the other is not.
        var (board, nets) = PairBetweenPours(highPourEnd: 20e-3);
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);
        Assert.Single(result.CrossSection!.GroundIndices);
        AssertMatrixClose(PairWithGrounds(low: true, high: false).CapacitanceFaradsPerMeter,
            result.Rlgc!.CapacitanceFaradsPerMeter, 1e-6, "C");
        Assert.Contains(result.Assumptions, a => a.Contains("NOT modelled"));
    }

    [Fact]
    public void OtherCopperBetweenThePairAndTheGround_IsNotTakenForGround()
    {
        // Another net's copper is the first copper met on the low side: that side is not a
        // coplanar ground (and the other copper, not a return conductor, is not solved either).
        var (board, nets) = PairBetweenPours(otherCopperBetween: true);
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);
        Assert.Single(result.CrossSection!.GroundIndices);
        AssertMatrixClose(PairWithGrounds(low: false, high: true).CapacitanceFaradsPerMeter,
            result.Rlgc!.CapacitanceFaradsPerMeter, 1e-6, "C");
        Assert.Contains(result.Assumptions, a => a.Contains("other copper first"));
    }

    // ------------------------------------------------------------------
    // The network's conductors are the nets, in the order they were selected.
    // ------------------------------------------------------------------

    /// <summary>A board of straight horizontal traces on L1 over an L2 plane, one net each:
    /// (x0, x1, y, width). A per-gap stackup carries the substrate.</summary>
    private static (PcbBoard Board, List<CopperNet> Nets) Traces(params (double X0, double X1, double Y, double W)[] traces)
    {
        var islands = new List<CopperIsland>();
        var centerlines = new List<TraceCenterline>();
        var nets = new List<CopperNet>();
        for (int i = 0; i < traces.Length; i++)
        {
            var (x0, x1, y, w) = traces[i];
            var island = new CopperIsland(i, 1, "L1", Rect(x0, y - w / 2, x1, y + w / 2));
            islands.Add(island);
            centerlines.Add(new TraceCenterline(1, new Point2(x0, y), new Point2(x1, y), w));
            nets.Add(new CopperNet(i + 1, new[] { island }) { Name = $"NET{i + 1}" });
        }
        islands.Add(new CopperIsland(traces.Length, 2, "L2", Rect(-100e-3, -100e-3, 100e-3, 100e-3)));
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(), Islands = islands, Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(), Nets = nets, Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>(),
            TraceCenterlines = centerlines,
            Stackup = new PcbStackupSettings
            {
                DielectricGapThicknesses = new[] { H }, DielectricGapPermittivities = new[] { EpsR },
                DielectricGapLossTangents = new[] { TanD },
            },
        };
        return (board, nets);
    }

    private static double WorstDifference(MtlNetwork a, MtlNetwork b, double f, int lines)
    {
        var sa = a.Scattering(f, 50);
        var sb = b.Scattering(f, 50);
        double worst = 0;
        for (int i = 0; i < sa.GetLength(0); i++)
            for (int j = 0; j < sa.GetLength(1); j++) worst = Math.Max(worst, (sa[i, j] - sb[i, j]).Magnitude);
        return worst;
    }

    [Fact]
    public void ANetBelowTheFirst_IsStillTheSecondConductor()
    {
        // NET1 is 0.3 mm wide at y = 5.6 mm, NET2 0.15 mm wide below it at y = 5 mm and 10 mm
        // longer. The cross-section numbers its traces left to right; the network numbers them
        // by net. Built by hand in net order, the cascade must be the extractor's.
        const double w1 = 0.3e-3, w2 = 0.15e-3;
        var (board, nets) = Traces((0, 40e-3, 5.6e-3, w1), (0, 50e-3, 5e-3, w2));
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);

        var stack = new LayeredStackup(new[] { new LayeredStackup.Layer(EpsR, TanD, H) });
        double gap = 0.6e-3;
        var pair = RlgcExtractor.Extract(new CoupledLineCrossSection(stack, 0, new[]
        {
            TraceCrossSection.Copper(0, w1), TraceCrossSection.Copper(-gap, w2)
        }));
        var inNetOrder = RlgcReduction.Permute(pair, new[] { 1, 0 });     // section order is NET2, NET1
        Assert.True(result.Rlgc!.CapacitanceFaradsPerMeter[0, 0] > result.Rlgc.CapacitanceFaradsPerMeter[1, 1],
            "conductor 0 is NET1, the wide one");
        AssertMatrixClose(inNetOrder.CapacitanceFaradsPerMeter, result.Rlgc.CapacitanceFaradsPerMeter, 1e-6, "C");

        var lead2 = RlgcExtractor.Extract(new CoupledLineCrossSection(stack, 0, new[] { TraceCrossSection.Copper(0, w2) }));
        var lead1 = RlgcExtractor.Extract(new CoupledLineCrossSection(stack, 0, new[] { TraceCrossSection.Copper(0, w1) }));
        var byHand = new MtlNetwork(new MtlSectionBase[]
        {
            new MtlSection(inNetOrder, 40e-3),
            new MtlLeadSection(new[] { (lead1, 0.0), (lead2, 10e-3) })
        });
        double worst = WorstDifference(byHand, result.Network!, 2e9, 2);
        Assert.True(worst < 1e-6, $"S differs by {worst:g3}");
    }

    /// <summary>Like <see cref="Traces"/>, with each net a run of horizontal segments end to end:
    /// (x0, x1, y, width) per segment.</summary>
    private static (PcbBoard Board, List<CopperNet> Nets) Routes(params (double X0, double X1, double Y, double W)[][] routes)
    {
        var islands = new List<CopperIsland>();
        var centerlines = new List<TraceCenterline>();
        var nets = new List<CopperNet>();
        int id = 0;
        foreach (var segments in routes)
        {
            var own = new List<CopperIsland>();
            foreach (var (x0, x1, y, w) in segments)
            {
                var island = new CopperIsland(id++, 1, "L1", Rect(x0, y - w / 2, x1, y + w / 2));
                own.Add(island);
                centerlines.Add(new TraceCenterline(1, new Point2(x0, y), new Point2(x1, y), w));
            }
            islands.AddRange(own);
            nets.Add(new CopperNet(nets.Count + 1, own) { Name = $"NET{nets.Count + 1}" });
        }
        islands.Add(new CopperIsland(id, 2, "L2", Rect(-100e-3, -100e-3, 100e-3, 100e-3)));
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(), Islands = islands, Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(), Nets = nets, Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>(),
            TraceCenterlines = centerlines,
            Stackup = new PcbStackupSettings
            {
                DielectricGapThicknesses = new[] { H }, DielectricGapPermittivities = new[] { EpsR },
                DielectricGapLossTangents = new[] { TanD },
            },
        };
        return (board, nets);
    }

    private static RlgcResult Strips(params (double Center, double Width)[] strips) =>
        RlgcExtractor.Extract(new CoupledLineCrossSection(
            new LayeredStackup(new[] { new LayeredStackup.Layer(EpsR, TanD, H) }), 0,
            strips.Select(s => TraceCrossSection.Copper(s.Center, s.Width)).ToArray()));

    [Fact]
    public void ThreeNets_OfWhichTwoRunOn_AreCoupledInGroups()
    {
        // NET1, NET2, NET3 side by side for 20 mm; NET3 stops, NET1 and NET2 run on together for
        // another 20 mm. The first stretch couples all three, the second the pair, with NET3
        // passing through it. (Before, the second stretch was two lone traces.)
        var (board, nets) = Traces((0, 40e-3, 5e-3, W), (0, 40e-3, 5e-3 + Pitch, W), (0, 20e-3, 5e-3 + 2 * Pitch, W));
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);
        Assert.Equal(40e-3, result.CoupledLengthMeters, 9);
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, result.LeadLengthsMeters.Select(l => Math.Round(l, 9)));

        var byHand = new MtlNetwork(new MtlSectionBase[]
        {
            new MtlSection(Strips((0, W), (Pitch, W), (2 * Pitch, W)), 20e-3),
            new MtlGroupSection(new MtlSection(Strips((0, W), (Pitch, W)), 20e-3), new[] { 0, 1 }, 3),
        });
        double worst = WorstDifference(byHand, result.Network!, 2e9, 3);
        Assert.True(worst < 1e-6, $"S differs by {worst:g3}");
        Assert.Contains(result.Assumptions, a => a.Contains("run alone beside them") && a.Contains("over 20 mm"));
    }

    [Fact]
    public void ALeadThatChangesWidth_IsPricedPieceByPiece()
    {
        // NET1 runs beside NET2 for 30 mm at 0.3 mm, then alone for 20 mm at 0.15 mm and 10 mm
        // at 0.3 mm. Its lead is two lines in series, not 30 mm of the dominant width.
        const double narrow = 0.15e-3;
        var (board, nets) = Routes(
            new[] { (0.0, 30e-3, 5e-3, W), (30e-3, 50e-3, 5e-3, narrow), (50e-3, 60e-3, 5e-3, W) },
            new[] { (0.0, 30e-3, 5e-3 + Pitch, W) });
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);

        var single = Strips((0, W));
        var thin = Strips((0, narrow));
        var byHand = new MtlNetwork(new MtlSectionBase[]
        {
            new MtlSection(Strips((0, W), (Pitch, W)), 30e-3),
            new MtlLeadSection(new[] { (thin, 20e-3), (single, 0.0) }),
            new MtlLeadSection(new[] { (single, 10e-3), (single, 0.0) }),
        });
        double worst = WorstDifference(byHand, result.Network!, 2e9, 2);
        Assert.True(worst < 1e-6, $"S differs by {worst:g3}");
        Assert.Contains(result.Assumptions, a => a.Contains("piece by piece"));
    }

    [Fact]
    public void APairThatChangesLayer_IsMicrostripThenStripline()
    {
        // Four layers, GND planes on L2 and L4. The pair runs 20 mm on L1, drops through a via
        // each to L3 and runs on for 20 mm there: a coupled microstrip over L2, then a coupled
        // stripline between L2 and L4. (Before, a net on two layers was refused.)
        const double h1 = 0.2e-3, h2 = 0.25e-3, h3 = 0.3e-3, e1 = 4.0, e2 = 3.6, e3 = 4.2;
        double y0 = 5e-3, y1 = 5e-3 + Pitch;
        var islands = new List<CopperIsland>();
        var centerlines = new List<TraceCenterline>();
        var nets = new List<CopperNet>();
        var vias = new List<Via>();
        foreach (var y in new[] { y0, y1 })
        {
            var top = new CopperIsland(islands.Count, 1, "L1", Rect(0, y - W / 2, 20e-3 + 0.3e-3, y + W / 2));
            islands.Add(top);
            var inner = new CopperIsland(islands.Count, 3, "L3", Rect(20e-3 - 0.3e-3, y - W / 2, 40e-3, y + W / 2));
            islands.Add(inner);
            centerlines.Add(new TraceCenterline(1, new Point2(0, y), new Point2(20e-3, y), W));
            centerlines.Add(new TraceCenterline(3, new Point2(20e-3, y), new Point2(40e-3, y), W));
            var via = new Via(new Point2(20e-3, y), 0.2e-3, true, 1, 3);
            vias.Add(via);
            nets.Add(new CopperNet(nets.Count + 1, new[] { top, inner })
            {
                Name = $"NET{nets.Count + 1}", StitchingVias = new[] { new ViaBridge(via, new[] { 1, 3 }) }
            });
        }
        var l2 = new CopperIsland(islands.Count, 2, "L2", Rect(-100e-3, -100e-3, 100e-3, 100e-3));
        var l4 = new CopperIsland(islands.Count + 1, 4, "L4", Rect(-100e-3, -100e-3, 100e-3, 100e-3));
        islands.Add(l2);
        islands.Add(l4);
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(), Islands = islands, Pads = Array.Empty<CopperPad>(), Vias = vias,
            Nets = nets.Concat(new[] { new CopperNet(9, new[] { l2, l4 }) { Name = "GND" } }).ToList(),
            Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>(), TraceCenterlines = centerlines,
            Stackup = new PcbStackupSettings
            {
                DielectricGapThicknesses = new[] { h1, h2, h3 }, DielectricGapPermittivities = new[] { e1, e2, e3 },
                DielectricGapLossTangents = new[] { TanD, TanD, TanD },
            },
        };
        var result = BoardCoupledExtractor.Extract(board, nets);
        Assert.Null(result.FailureReason);
        Assert.Equal(40e-3, result.CoupledLengthMeters, 9);

        var microstrip = RlgcExtractor.Extract(new CoupledLineCrossSection(
            new LayeredStackup(new[] { new LayeredStackup.Layer(e1, TanD, h1) }), 0,
            new[] { TraceCrossSection.Copper(0, W), TraceCrossSection.Copper(Pitch, W) }));
        var stripline = RlgcExtractor.Extract(new CoupledLineCrossSection(
            new LayeredStackup(new[] { new LayeredStackup.Layer(e3, TanD, h3), new LayeredStackup.Layer(e2, TanD, h2) }), 0,
            new[] { TraceCrossSection.Copper(0, W), TraceCrossSection.Copper(Pitch, W) }, topGround: true));
        var byHand = new MtlNetwork(new MtlSectionBase[] { new MtlSection(microstrip, 20e-3), new MtlSection(stripline, 20e-3) });
        double worst = WorstDifference(byHand, result.Network!, 2e9, 2);
        Assert.True(worst < 1e-6, $"S differs by {worst:g3}");
        Assert.Contains(result.Assumptions, a => a.Contains("change layer"));
    }

    // ------------------------------------------------------------------
    // Real-board robustness: the extractor must return a WELL-FORMED typed result
    // (a valid cross-section, or a failure with a reason) on the messy real centerlines
    // of arbitrary net pairs — never throw, never a half-built garbage matrix. This is the
    // gate the synthetic fixtures cannot give (real traces jog, branch, and self-cross).
    // ------------------------------------------------------------------

    [Fact]
    public void RealBoard_ArbitraryNetPairs_AlwaysTypedResult()
    {
        string zip = System.IO.Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");
        var board = new PcbBoardReader().Read(zip);

        // Trace-scale single-layer signal nets (not planes/pours), largest first.
        var signalNets = board.Nets
            .Where(n => n.IsSingleLayer && n.Area is > 1e-7 and < 1e-5)
            .OrderByDescending(n => n.Area)
            .Take(8)
            .ToList();
        Assert.True(signalNets.Count >= 2, "The fixture should carry several trace-scale nets.");

        int wellFormed = 0;
        for (int i = 0; i < signalNets.Count; i++)
            for (int j = i + 1; j < signalNets.Count; j++)
            {
                var result = BoardCoupledExtractor.Extract(board, new[] { signalNets[i], signalNets[j] });
                if (result.FailureReason is not null)
                    Assert.Null(result.CrossSection);           // failure ⇒ no partial section
                else
                {
                    Assert.NotNull(result.Rlgc);
                    Assert.NotNull(result.Network);
                    Assert.Equal(2, result.CrossSection!.SignalTraces.Count);
                    Assert.True(result.CoupledLengthMeters > 0);
                }
                wellFormed++;
            }
        Assert.True(wellFormed > 0);
    }
}
