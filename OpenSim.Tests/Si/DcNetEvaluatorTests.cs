using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Si;
using Xunit;

namespace OpenSim.Tests.Si;

/// <summary>
/// The board-wide "Evaluate DC nets" sweep. The R network and C extractor carry their
/// own machine-sharp gates (<see cref="TraceResistanceNetworkTests"/>,
/// <see cref="TraceCapacitanceTests"/>) — these gate the COMPOSITION: the ≥2-pad
/// filter, the complete-rows contract (a row requires BOTH R and C; everything else is
/// counted, never silently dropped), pad naming (refdes.pin + part when the file says,
/// synthesized otherwise), the exact R·C = τ product, CSV escaping, bitwise determinism
/// at any DOP, and the real-board sweep that synthetic fixtures cannot give.
/// </summary>
public class DcNetEvaluatorTests
{
    private const double W = 0.3e-3, H = 0.2e-3, EpsR = 4.4, TanD = 0.02;
    private const double Copper = 35e-6, Sigma = 5.8e7;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    private static CopperPad Pad(double xMm, double yMm)
    {
        double x = xMm * 1e-3, y = yMm * 1e-3;
        return new CopperPad(1, new Point2(x, y), Rect(x - 0.5e-3, y - 0.5e-3, x + 0.5e-3, y + 0.5e-3), 1e-3);
    }

    private static TraceCenterline Trace(double x1, double y1, double x2, double y2) =>
        new(1, new Point2(x1 * 1e-3, y1 * 1e-3), new Point2(x2 * 1e-3, y2 * 1e-3), W);

    private static PcbStackupSettings Stackup() => new()
    {
        DielectricGapThicknesses = new[] { H },
        DielectricGapPermittivities = new[] { EpsR },
        DielectricGapLossTangents = new[] { TanD },
    };

    /// <summary>Four L1 nets in disjoint island bands (containment maps traces/pads to
    /// nets): a 2-pad straight run, a 3-pad T-branch whose name carries a comma (the CSV
    /// escaping gate), a 1-pad net (skipped), and a 2-pad pour with no centerlines
    /// (a failure note, no rows).</summary>
    private static PcbBoard MultiNetBoard()
    {
        var islands = new[]
        {
            new CopperIsland(0, 1, "L1", Rect(-5e-3, -3e-3, 45e-3, 3e-3)),
            new CopperIsland(1, 1, "L1", Rect(-5e-3, 7e-3, 25e-3, 23e-3)),
            new CopperIsland(2, 1, "L1", Rect(50e-3, -3e-3, 60e-3, 3e-3)),
            new CopperIsland(3, 1, "L1", Rect(50e-3, 7e-3, 80e-3, 23e-3)),
        };
        return new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = islands,
            Pads = new[]
            {
                Pad(0, 0), Pad(40, 0),                    // SIG1
                Pad(0, 10), Pad(20, 10), Pad(10, 18),     // T,NET
                Pad(55, 0),                               // LONELY — 1 pad ⇒ skipped
                Pad(55, 10), Pad(75, 10),                 // POUR — no centerlines ⇒ failure note
            },
            Vias = Array.Empty<Via>(),
            Nets = new[]
            {
                new CopperNet(1, new[] { islands[0] }) { Name = "SIG1" },
                new CopperNet(2, new[] { islands[1] }) { Name = "T,NET" },
                new CopperNet(3, new[] { islands[2] }) { Name = "LONELY" },
                new CopperNet(4, new[] { islands[3] }) { Name = "POUR" },
            },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = new[]
            {
                Trace(0, 0, 40, 0),
                Trace(0, 10, 10, 10), Trace(10, 10, 20, 10), Trace(10, 10, 10, 18),
            },
            Stackup = Stackup(),
        };
    }

    private static DcNetReport Evaluate(PcbBoard board, int? maxDop = null) =>
        DcNetEvaluator.Evaluate(board, new NetMeshOptions { CopperThickness = Copper },
            new BoardCoupledOptions(), "fixture_board.zip", maxDop);

    private static double BarR(double lengthMm) => lengthMm * 1e-3 / (Sigma * W * Copper);

    [Fact]
    public void Sweep_CountsRowsAndFailureNotes_FollowThePadFilter()
    {
        var report = Evaluate(MultiNetBoard());

        Assert.Equal(2, report.NetsEvaluated);           // SIG1, T,NET
        Assert.Equal(1, report.NetsSkipped);             // LONELY (<2 pads)
        Assert.Equal(1, report.NetsFailed);              // POUR
        Assert.Equal(0, report.PairsOmitted);
        // 1 pair + C(3,2) = 3 pairs, in board-net order — the pour has NO row.
        Assert.Equal(4, report.Rows.Count);
        Assert.Equal("SIG1", report.Rows[0].Net);
        Assert.All(report.Rows.Skip(1), r => Assert.Equal("T,NET", r.Net));
        Assert.DoesNotContain(report.Rows, r => r.Net is "LONELY" or "POUR");

        // The pour's reason lives on the failure notes (the R side fails first).
        var note = Assert.Single(report.FailureNotes);
        Assert.StartsWith("POUR — ", note);
        Assert.Contains("pour/region", note);
    }

    [Fact]
    public void RowValues_MatchTheUnderlyingEngines_Exactly()
    {
        var board = MultiNetBoard();
        var report = Evaluate(board);

        // SIG1: R is the closed form, C is bitwise the S12 extractor, τ is the product.
        var sig = report.Rows[0];
        Assert.True(Math.Abs(sig.ResistanceOhms - BarR(40)) / BarR(40) < 1e-12);
        var c = TraceCapacitanceExtractor.Extract(board, board.Nets[0], new BoardCoupledOptions());
        Assert.Null(c.FailureReason);
        Assert.Equal(c.TotalFarads, sig.CapacitanceFarads);
        Assert.Equal(sig.ResistanceOhms * sig.CapacitanceFarads, sig.TimeConstantSeconds);

        // The T pairs are path sums; every pair of the net carries the SAME net C.
        var t = report.Rows.Where(r => r.Net == "T,NET").ToList();
        Assert.True(Math.Abs(t[0].ResistanceOhms - BarR(20)) / BarR(20) < 1e-12);
        Assert.True(Math.Abs(t[1].ResistanceOhms - BarR(18)) / BarR(18) < 1e-12);
        Assert.True(Math.Abs(t[2].ResistanceOhms - BarR(18)) / BarR(18) < 1e-12);
        Assert.Equal(t[0].CapacitanceFarads, t[1].CapacitanceFarads);
        Assert.Equal(t[0].CapacitanceFarads, t[2].CapacitanceFarads);

        // No component identity on the fixture ⇒ synthesized labels, blank part names.
        Assert.Equal("P0 L1 (0;0)mm", sig.PadA);
        Assert.Equal("P1 L1 (40;0)mm", sig.PadB);
        Assert.Null(sig.PartA);
        Assert.Null(sig.PartB);
    }

    [Fact]
    public void NamedPads_UseRefDesPin_AndPartName()
    {
        var board = MultiNetBoard();
        var pads = board.Pads.ToArray();
        pads[0] = pads[0] with { ComponentRef = "H5", Pin = "1", PartName = "HDR-SMD_HX-PZ2.54" };
        pads[1] = pads[1] with { ComponentRef = "U1", Pin = "3", PartName = "SOT-223" };
        var named = new PcbBoard
        {
            Outline = board.Outline, Islands = board.Islands, Pads = pads, Vias = board.Vias,
            Nets = board.Nets, Layers = board.Layers, Warnings = board.Warnings,
            TraceCenterlines = board.TraceCenterlines, Stackup = board.Stackup,
        };

        var sig = Evaluate(named).Rows[0];
        Assert.Equal("H5.1", sig.PadA);
        Assert.Equal("HDR-SMD_HX-PZ2.54", sig.PartA);
        Assert.Equal("U1.3", sig.PadB);
        Assert.Equal("SOT-223", sig.PartB);
    }

    [Fact]
    public void NoReferenceGap_IsAFailureNote_NotRows()
    {
        // A single-copper-layer board with no stackup: R solves but C has no reference
        // plane — the net can never produce a complete row, so it is a counted failure
        // with the C reason, never a silent drop.
        var island = new CopperIsland(0, 1, "L1", Rect(-5e-3, -3e-3, 45e-3, 3e-3));
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = new[] { island },
            Pads = new[] { Pad(0, 0), Pad(40, 0) },
            Vias = Array.Empty<Via>(),
            Nets = new[] { new CopperNet(1, new[] { island }) { Name = "SIG1" } },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = new[] { Trace(0, 0, 40, 0) },
        };
        var report = Evaluate(board);

        Assert.Empty(report.Rows);
        Assert.Equal(0, report.NetsEvaluated);
        Assert.Equal(1, report.NetsFailed);
        var note = Assert.Single(report.FailureNotes);
        Assert.Contains("reference plane", note);
    }

    [Fact]
    public void PairsWithoutR_AreCounted_NotRows()
    {
        // Three pads: two on the trace, one 40 mm off any copper — its two pairs have
        // no computable R and must be counted, leaving exactly the complete pair.
        var island = new CopperIsland(0, 1, "L1", Rect(-5e-3, -3e-3, 85e-3, 45e-3));
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = new[] { island },
            Pads = new[] { Pad(0, 0), Pad(40, 0), Pad(80, 40) },
            Vias = Array.Empty<Via>(),
            Nets = new[] { new CopperNet(1, new[] { island }) { Name = "SIG1" } },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = new[] { Trace(0, 0, 40, 0) },
            Stackup = Stackup(),
        };
        var report = Evaluate(board);

        Assert.Equal(1, report.NetsEvaluated);
        var row = Assert.Single(report.Rows);
        Assert.Equal("P0 L1 (0;0)mm", row.PadA);
        Assert.Equal("P1 L1 (40;0)mm", row.PadB);
        Assert.Equal(2, report.PairsOmitted);
    }

    [Fact]
    public void Csv_BoardOnceInPreamble_EscapesAndRoundTrips()
    {
        var report = Evaluate(MultiNetBoard());
        string csv = DcNetReportCsv.Write(report);

        // The board name appears ONCE, in the preamble — not on every row.
        Assert.Contains("# board: fixture_board.zip", csv);
        Assert.DoesNotContain("fixture_board.zip,", csv);
        // The pour's reason is a preamble note; deterministic (no timestamp).
        Assert.Contains("# not computable: POUR — ", csv);
        Assert.Contains("Net,Pad A,Part A,Pad B,Part B,R (ohm),C_total (F),Tau (s),Note", csv);
        Assert.Contains("\"T,NET\"", csv);
        Assert.DoesNotContain(",LONELY,", csv);

        var lines = csv.Split("\r\n");
        // The SIG1 data row: every default cell is quote-free, so a naive split works
        // (the pad labels use ';' inside coordinates on purpose) — and "R" round-trips.
        var sig = lines.Single(l => l.StartsWith("SIG1,"));
        var cells = sig.Split(',');
        Assert.Equal(9, cells.Length);
        double r = double.Parse(cells[5], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(report.Rows[0].ResistanceOhms, r);   // "R" format round-trips
    }

    [Fact]
    public void Report_IsBitwiseDeterministic_AtAnyDop()
    {
        var board = MultiNetBoard();
        string sequential = DcNetReportCsv.Write(Evaluate(board, maxDop: 1));
        string parallel = DcNetReportCsv.Write(Evaluate(board));
        Assert.Equal(sequential, parallel);
    }

    [Fact]
    public void RealBoard_Sweep_EveryNetIsRowsOrCountedSkipOrNote()
    {
        string zip = System.IO.Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");
        var board = new PcbBoardReader().Read(zip);
        var report = DcNetEvaluator.Evaluate(board, new NetMeshOptions(),
            new BoardCoupledOptions(), "example_board.zip");

        Assert.Equal(board.Nets.Count, report.NetsEvaluated + report.NetsSkipped + report.NetsFailed);
        Assert.Equal(report.NetsFailed, report.FailureNotes.Count);
        Assert.True(report.Rows.Count > 0, "The example board should produce report rows.");
        Assert.True(report.NetsEvaluated > 0, "Some nets should evaluate cleanly.");

        foreach (var row in report.Rows)
        {
            Assert.True(double.IsFinite(row.ResistanceOhms) && row.ResistanceOhms >= 0,
                $"{row.Net}: R = {row.ResistanceOhms}");
            Assert.True(double.IsFinite(row.CapacitanceFarads) && row.CapacitanceFarads > 0,
                $"{row.Net}: C = {row.CapacitanceFarads}");
            Assert.True(double.IsFinite(row.TimeConstantSeconds) && row.TimeConstantSeconds >= 0,
                $"{row.Net}: τ = {row.TimeConstantSeconds}");
        }

        // The CSV renders the whole sweep without incident.
        string csv = DcNetReportCsv.Write(report);
        Assert.Contains("Net,Pad A,Part A,Pad B,Part B", csv);
    }
}
