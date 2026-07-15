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
                    Assert.Equal(2, result.CrossSection!.Traces.Count);
                    Assert.True(result.CoupledLengthMeters > 0);
                }
                wellFormed++;
            }
        Assert.True(wellFormed > 0);
    }
}
