using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Ipc2581;
using OpenSim.Rf.Si;
using Xunit;

namespace OpenSim.Tests.Si;

/// <summary>
/// The board-wide "Evaluate DC nets" sweep. The R network and C extractor carry their
/// own machine-sharp gates (<see cref="TraceResistanceNetworkTests"/>,
/// <see cref="TraceCapacitanceTests"/>) — these gate the COMPOSITION: the component-pin
/// filter (only pairs whose BOTH pads trace back to a package/pin are reported; via
/// landings and Gerber flashes never terminate a row), the complete-rows contract (a row
/// requires BOTH R and C; everything else is counted, never silently dropped), pad
/// naming (refdes.pin + part), the exact R·C = τ product, CSV escaping, bitwise
/// determinism at any DOP, and both the Gerber (no rows) and real IPC-2581 (named rows)
/// sweeps that synthetic fixtures cannot give.
/// </summary>
public class DcNetEvaluatorTests
{
    private const double W = 0.3e-3, H = 0.2e-3, EpsR = 4.4, TanD = 0.02;
    private const double Copper = 35e-6, Sigma = 5.8e7;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    /// <summary>A pad at (xMm, yMm). With refDes/pin set it is a COMPONENT PIN — the only
    /// kind the sweep reports; without them it is an anonymous flash (a via landing or a
    /// Gerber pad) that conducts but never terminates a measurement.</summary>
    private static CopperPad Pad(double xMm, double yMm,
        string? refDes = null, string? pin = null, string? part = null)
    {
        double x = xMm * 1e-3, y = yMm * 1e-3;
        return new CopperPad(1, new Point2(x, y),
            Rect(x - 0.5e-3, y - 0.5e-3, x + 0.5e-3, y + 0.5e-3), 1e-3)
        { ComponentRef = refDes, Pin = pin, PartName = part };
    }

    private static TraceCenterline Trace(double x1, double y1, double x2, double y2) =>
        new(1, new Point2(x1 * 1e-3, y1 * 1e-3), new Point2(x2 * 1e-3, y2 * 1e-3), W);

    private static PcbStackupSettings Stackup() => new()
    {
        DielectricGapThicknesses = new[] { H },
        DielectricGapPermittivities = new[] { EpsR },
        DielectricGapLossTangents = new[] { TanD },
    };

    /// <summary>Five L1 nets in disjoint island bands (containment maps traces/pads to
    /// nets): a 2-pin straight run carrying an extra ANONYMOUS mid pad (a via landing —
    /// excluded from pairs), a 3-pin T-branch whose name carries a comma (the CSV escaping
    /// gate), a 1-pin net (skipped), a 2-pin pour with no centerlines (a failure note),
    /// and an ANON net of two anonymous pads (no component identity ⇒ skipped, the Gerber
    /// case in miniature).</summary>
    private static PcbBoard MultiNetBoard()
    {
        var islands = new[]
        {
            new CopperIsland(0, 1, "L1", Rect(-5e-3, -3e-3, 45e-3, 3e-3)),
            new CopperIsland(1, 1, "L1", Rect(-5e-3, 7e-3, 25e-3, 23e-3)),
            new CopperIsland(2, 1, "L1", Rect(50e-3, -3e-3, 60e-3, 3e-3)),
            new CopperIsland(3, 1, "L1", Rect(50e-3, 7e-3, 80e-3, 23e-3)),
            new CopperIsland(4, 1, "L1", Rect(-5e-3, -23e-3, 45e-3, -17e-3)),
        };
        return new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = islands,
            Pads = new[]
            {
                Pad(0, 0, "H5", "1", "HDR-SMD"), Pad(40, 0, "U1", "3", "SOT-223"), Pad(20, 0), // SIG1 (+via landing)
                Pad(0, 10, "R1", "1", "0402"), Pad(20, 10, "R1", "2", "0402"), Pad(10, 18, "U2", "5", "QFN"), // T,NET
                Pad(55, 0, "D1", "1", "SOD-123"),           // LONELY — 1 pin ⇒ skipped
                Pad(55, 10, "J2", "1", "CONN"), Pad(75, 10, "J2", "2", "CONN"), // POUR — no centerlines ⇒ failure
                Pad(0, -20), Pad(40, -20),                  // ANON — no component identity ⇒ skipped
            },
            Vias = Array.Empty<Via>(),
            Nets = new[]
            {
                new CopperNet(1, new[] { islands[0] }) { Name = "SIG1" },
                new CopperNet(2, new[] { islands[1] }) { Name = "T,NET" },
                new CopperNet(3, new[] { islands[2] }) { Name = "LONELY" },
                new CopperNet(4, new[] { islands[3] }) { Name = "POUR" },
                new CopperNet(5, new[] { islands[4] }) { Name = "ANON" },
            },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = new[]
            {
                Trace(0, 0, 40, 0),
                Trace(0, 10, 10, 10), Trace(10, 10, 20, 10), Trace(10, 10, 10, 18),
                Trace(0, -20, 40, -20),
            },
            Stackup = Stackup(),
        };
    }

    private static DcNetReport Evaluate(PcbBoard board, int? maxDop = null) =>
        DcNetEvaluator.Evaluate(board, new NetMeshOptions { CopperThickness = Copper },
            new BoardCoupledOptions(), "fixture_board.zip", maxDop);

    private static double BarR(double lengthMm) => lengthMm * 1e-3 / (Sigma * W * Copper);

    [Fact]
    public void Sweep_CountsRowsAndFailureNotes_FollowTheComponentPinFilter()
    {
        var report = Evaluate(MultiNetBoard());

        Assert.Equal(2, report.NetsEvaluated);           // SIG1, T,NET
        Assert.Equal(2, report.NetsSkipped);             // LONELY (1 pin), ANON (0 pins)
        Assert.Equal(1, report.NetsFailed);              // POUR
        Assert.Equal(0, report.PairsOmitted);
        // 1 pair (SIG1) + C(3,2) = 3 pairs (T,NET), in board-net order — the pour has NO row.
        Assert.Equal(4, report.Rows.Count);
        Assert.Equal("SIG1", report.Rows[0].Net);
        Assert.All(report.Rows.Skip(1), r => Assert.Equal("T,NET", r.Net));
        Assert.DoesNotContain(report.Rows, r => r.Net is "LONELY" or "POUR" or "ANON");

        // Every reported endpoint is a component pin — no synthesized coordinate labels.
        Assert.All(report.Rows, r =>
        {
            Assert.DoesNotContain("mm)", r.PadA);
            Assert.DoesNotContain("mm)", r.PadB);
        });

        // The pour's reason lives on the failure notes (the R side fails first).
        var note = Assert.Single(report.FailureNotes);
        Assert.StartsWith("POUR — ", note);
        Assert.Contains("pour/region", note);
    }

    [Fact]
    public void AnonymousPad_ConductsButNeverTerminatesARow()
    {
        // SIG1 carries an anonymous via-landing pad at its midpoint (20,0). It sits on the
        // conduction path but has no component identity, so it produces NO pair: SIG1 is
        // exactly the single H5.1 ↔ U1.3 measurement, unaffected in value by the landing.
        var report = Evaluate(MultiNetBoard());
        var sig = report.Rows.Where(r => r.Net == "SIG1").ToList();
        var row = Assert.Single(sig);
        Assert.Equal("H5.1", row.PadA);
        Assert.Equal("U1.3", row.PadB);
        Assert.True(Math.Abs(row.ResistanceOhms - BarR(40)) / BarR(40) < 1e-12);
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
    }

    [Fact]
    public void NamedPads_UseRefDesPin_AndPartName()
    {
        var sig = Evaluate(MultiNetBoard()).Rows[0];
        Assert.Equal("H5.1", sig.PadA);
        Assert.Equal("HDR-SMD", sig.PartA);
        Assert.Equal("U1.3", sig.PadB);
        Assert.Equal("SOT-223", sig.PartB);
    }

    [Fact]
    public void ComponentPadWithoutPin_LabelsByRefDesAlone()
    {
        // A component pad that carries a refdes but no pin number still traces to a
        // package — the label falls back to the refdes rather than a coordinate.
        var board = MultiNetBoard();
        var pads = board.Pads.ToArray();
        pads[1] = pads[1] with { Pin = null };            // U1 pad, pin cleared
        var patched = new PcbBoard
        {
            Outline = board.Outline, Islands = board.Islands, Pads = pads, Vias = board.Vias,
            Nets = board.Nets, Layers = board.Layers, Warnings = board.Warnings,
            TraceCenterlines = board.TraceCenterlines, Stackup = board.Stackup,
        };
        Assert.Equal("U1", Evaluate(patched).Rows[0].PadB);
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
            Pads = new[] { Pad(0, 0, "U1", "1", "IC"), Pad(40, 0, "U2", "1", "IC") },
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
        // Three component pins: two on the trace, one 40 mm off any copper — its two
        // pairs have no computable R and must be counted, leaving exactly the complete pair.
        var island = new CopperIsland(0, 1, "L1", Rect(-5e-3, -3e-3, 85e-3, 45e-3));
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = new[] { island },
            Pads = new[] { Pad(0, 0, "U1", "1", "IC"), Pad(40, 0, "U1", "2", "IC"), Pad(80, 40, "U3", "1", "IC") },
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
        Assert.Equal("U1.1", row.PadA);
        Assert.Equal("U1.2", row.PadB);
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
        Assert.Contains("# nets: 2 evaluated, 2 skipped (<2 component pins), 1 not computable", csv);
        // The pour's reason is a preamble note; deterministic (no timestamp).
        Assert.Contains("# not computable: POUR — ", csv);
        Assert.Contains("Net,Pad A,Part A,Pad B,Part B,R (ohm),C_total (F),Tau (s),Note", csv);
        Assert.Contains("\"T,NET\"", csv);
        Assert.DoesNotContain(",LONELY,", csv);

        var lines = csv.Split("\r\n");
        // The SIG1 data row: every cell is quote-free (component labels have no comma), so
        // a naive split works — and "R" round-trips.
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
    public void GerberBoard_YieldsNoRows_EveryNetSkippedForNoComponentPins()
    {
        // Gerber carries no PinRef, so no pad is a component pin — the report is empty by
        // design, every net skipped (this is why the feature is IPC-2581-specific).
        string zip = System.IO.Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");
        var board = new PcbBoardReader().Read(zip);
        var report = DcNetEvaluator.Evaluate(board, new NetMeshOptions(),
            new BoardCoupledOptions(), "example_board.zip");

        Assert.True(board.Nets.Count > 0);
        Assert.Empty(report.Rows);
        Assert.Equal(0, report.NetsEvaluated);
        Assert.Equal(0, report.NetsFailed);
        Assert.Equal(board.Nets.Count, report.NetsSkipped);
        // The CSV still renders cleanly (header + preamble, no data rows).
        Assert.Contains("Net,Pad A,Part A,Pad B,Part B", DcNetReportCsv.Write(report));
    }

    [Fact]
    public void RealIpc2581Board_ProducesNamedComponentPinRows()
    {
        string? path = OpenSim.Tests.Pcb.Ipc2581IntegrationTests.FindKiCadExampleFile();
        if (path is null) return;                          // example not present in this checkout
        var board = new Ipc2581Reader().Read(path);
        var report = DcNetEvaluator.Evaluate(board, new NetMeshOptions(),
            new BoardCoupledOptions(), "Breakout_Board.xml");

        Assert.True(report.Rows.Count > 0, "The real IPC-2581 board should produce component-pin rows.");
        Assert.True(report.NetsEvaluated > 0);
        Assert.Equal(board.Nets.Count, report.NetsEvaluated + report.NetsSkipped + report.NetsFailed);

        foreach (var row in report.Rows)
        {
            // Every reported endpoint is a component pin — a refdes label, never a
            // synthesized coordinate — with finite, physical R/C/τ.
            Assert.DoesNotContain("mm)", row.PadA);
            Assert.DoesNotContain("mm)", row.PadB);
            Assert.True(double.IsFinite(row.ResistanceOhms) && row.ResistanceOhms >= 0, $"{row.Net}: R");
            Assert.True(double.IsFinite(row.CapacitanceFarads) && row.CapacitanceFarads > 0, $"{row.Net}: C");
            Assert.True(double.IsFinite(row.TimeConstantSeconds) && row.TimeConstantSeconds >= 0, $"{row.Net}: τ");
        }
        // The part column is populated for at least some pins (the file's Component data).
        Assert.Contains(report.Rows, r => r.PartA is not null || r.PartB is not null);
    }
}
