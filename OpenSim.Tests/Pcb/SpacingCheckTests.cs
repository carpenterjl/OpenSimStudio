using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Rules;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Feature 11: clearance and creepage against a spacing table, on boards drawn by hand where
/// every distance is known. The table itself is data the program ships; these gates check
/// that the checker reads it and measures the copper right, not the standard's numbers.
/// </summary>
public class SpacingCheckTests
{
    private readonly ITestOutputHelper _out;
    public SpacingCheckTests(ITestOutputHelper output) => _out = output;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    private sealed class Builder
    {
        private readonly List<CopperIsland> _islands = new();
        private readonly Dictionary<string, List<CopperIsland>> _nets = new();
        private readonly List<string> _order = new();
        public List<Polygon2> Outline { get; } = new();

        public Builder Copper(string? net, int layer, Polygon2 shape)
        {
            var island = new CopperIsland(_islands.Count, layer, $"L{layer}", shape);
            _islands.Add(island);
            if (net is null) return this;
            if (!_nets.ContainsKey(net)) { _nets[net] = new List<CopperIsland>(); _order.Add(net); }
            _nets[net].Add(island);
            return this;
        }

        public PcbBoard Build(int layers = 2) => new()
        {
            Outline = Outline, Islands = _islands, Pads = Array.Empty<CopperPad>(), Vias = Array.Empty<Via>(),
            Nets = _order.Select((n, i) => new CopperNet(i + 1, _nets[n]) { Name = n }).ToList(),
            Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>(),
            Stackup = new OpenSim.Core.Model.PcbStackupSettings
            {
                DielectricGapThicknesses = Enumerable.Repeat(0.2e-3, layers - 1).ToList(),
                CopperLayerThicknesses = Enumerable.Repeat(35e-6, layers).ToList()
            }
        };
    }

    private static readonly double Mm = 1e-3;

    [Fact]
    public void Ipc2221Table_ReadsRowsAndTheSlopeAboveIt()
    {
        var t = Ipc2221Spacing.Table6_1;
        t.Validate();
        Assert.Equal(7, t.Columns.Count);
        Assert.Equal(1.25 * Mm, Ipc2221Spacing.Required(300, Ipc2221Column.B2ExternalUncoated), 1e-12);
        Assert.Equal(0.6 * Mm, Ipc2221Spacing.Required(48, Ipc2221Column.B2ExternalUncoated), 1e-12);
        Assert.Equal(0.1 * Mm, Ipc2221Spacing.Required(30, Ipc2221Column.B2ExternalUncoated), 1e-12);
        Assert.Equal(0.2 * Mm, Ipc2221Spacing.Required(300, Ipc2221Column.B1Internal), 1e-12);
        Assert.Equal(0.4 * Mm, Ipc2221Spacing.Required(300, Ipc2221Column.B4ExternalCoated), 1e-12);
        // 1000 V, B2: 2.5 mm + 0.005 mm/V × 500 V = 5.0 mm.
        Assert.Equal(5.0 * Mm, Ipc2221Spacing.Required(1000, Ipc2221Column.B2ExternalUncoated), 1e-12);
        Assert.Equal(0.25 * Mm + 0.0025 * Mm * 500, Ipc2221Spacing.Required(1000, Ipc2221Column.B1Internal), 1e-12);
        Assert.Contains("verify", t.Source);
    }

    [Fact]
    public void SpacingTable_RoundTripsThroughCsv_AndRefusesBadInput()
    {
        var csv = Ipc2221Spacing.Table6_1.ToCsv();
        _out.WriteLine(csv);
        var back = SpacingTable.ParseCsv(csv, "copy", "test");
        Assert.Equal(Ipc2221Spacing.Table6_1.Columns, back.Columns);
        for (double v = 0; v <= 1200; v += 7)
            for (int c = 0; c < 7; c++)
                Assert.Equal(Ipc2221Spacing.Table6_1.Required(v, c), back.Required(v, c), 1e-15);

        var iec = SpacingTable.ParseCsv(Iec60664Spacing.CreepageHeader + "\n50,0.6,1.2,1.5,1.9,1.5,1.7,1.9\n100,0.7,1.4,1.8,2.2,1.8,2.0,2.2\n",
            "creepage", Iec60664Spacing.Source("2020"));
        Assert.Equal(1.5 * Mm, iec.Required(50, "PD2 group II"), 1e-12);
        Assert.Equal(2.2 * Mm, iec.Required(80, "PD3 group III"), 1e-12);
        Assert.Throws<InvalidOperationException>(() => iec.Required(200, "PD1"));   // no rule beyond the table
        Assert.Throws<ArgumentException>(() => iec.Required(50, "PD9"));

        Assert.Throws<InvalidDataException>(() => SpacingTable.ParseCsv("Max volts,A\n10,x\n", "bad", "t"));
        Assert.Throws<InvalidOperationException>(() => SpacingTable.ParseCsv("Max volts,A\n20,0.1\n10,0.2\n", "bad", "t"));
    }

    [Fact]
    public void TwoNetsOnTheOuterLayer_ClearanceAgainstTheOuterColumn()
    {
        // A: x 0..10, B: x 10.5..20 — a 0.5 mm gap on L1 (outer, uncoated: B2).
        var builder = new Builder()
            .Copper("A", 1, Rect(0, 0, 10 * Mm, 5 * Mm))
            .Copper("B", 1, Rect(10.5 * Mm, 0, 20 * Mm, 5 * Mm));
        builder.Outline.Add(Rect(-1 * Mm, -1 * Mm, 21 * Mm, 6 * Mm));
        var board = builder.Build();

        var fail = SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0, ["B"] = 300 });
        _out.WriteLine(string.Join("\n", fail.Describe()));
        var f = Assert.Single(fail.Findings);
        Assert.Equal(SpacingKind.Clearance, f.Kind);
        Assert.Equal(0.5 * Mm, f.MeasuredMeters, 1e-12);
        Assert.Equal(1.25 * Mm, f.RequiredMeters, 1e-12);
        Assert.Equal(300, f.Volts);
        Assert.Equal("B2", f.Column);
        Assert.False(f.Passes);
        Assert.Equal(10 * Mm, f.At.X, 1e-12);

        // 30 V asks 0.1 mm: the pair passes, and is listed only when the window reaches 5× the requirement.
        var pass = SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0, ["B"] = 30 },
            new SpacingCheckOptions { ReportWithinFactor = 10 });
        f = Assert.Single(pass.Findings);
        Assert.True(f.Passes);
        Assert.Equal(0.1 * Mm, f.RequiredMeters, 1e-12);
        Assert.Empty(SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0, ["B"] = 30 }).Findings);

        // Coated outer layers at 300 V: B4 is 0.4 mm, so 0.5 mm passes.
        var coated = SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0, ["B"] = 300 },
            new SpacingCheckOptions { OuterColumn = "B4" });
        Assert.True(Assert.Single(coated.Findings).Passes);

        // A net without a voltage is left out, and said so.
        var partial = SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0 });
        Assert.Empty(partial.Findings);
        Assert.Contains(partial.Notes, n => n.Contains("no working voltage") && n.Contains("B"));
    }

    [Fact]
    public void InnerLayer_UsesTheInnerColumn_AndTheListingWindowApplies()
    {
        // The same 0.5 mm gap on L2 of a 4-layer board: B1 at 300 V is 0.2 mm → passes.
        var board = new Builder()
            .Copper("A", 2, Rect(0, 0, 10 * Mm, 5 * Mm))
            .Copper("B", 2, Rect(10.5 * Mm, 0, 20 * Mm, 5 * Mm))
            .Copper("G", 4, Rect(-5 * Mm, -5 * Mm, -4 * Mm, -4 * Mm))      // something on L4 so it is the bottom
            .Build(layers: 4);
        var report = SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0, ["B"] = 300, ["G"] = 0 },
            new SpacingCheckOptions { ReportWithinFactor = 3 });
        _out.WriteLine(string.Join("\n", report.Describe()));
        var f = Assert.Single(report.Findings);
        Assert.Equal("B1", f.Column);
        Assert.Equal(0.2 * Mm, f.RequiredMeters, 1e-12);
        Assert.True(f.Passes);
        Assert.Equal(2, f.Layer);
        // Within 2× it is not listed (0.5 mm against 0.2 mm is 2.5×).
        Assert.Empty(SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0, ["B"] = 300, ["G"] = 0 }).Findings);
    }

    [Fact]
    public void Creepage_FollowsTheSurfaceAroundASlot()
    {
        // Pads A (x 0..2) and B (x 6..8), y 0..2; a slot x 3.5..4.5, y −3..5 between them. The
        // straight line is 4 mm; the shortest surface path leaves a pad corner, rounds a slot
        // end and comes back: 2·√(1.5² + 3²) + 1 = 7.708 mm.
        var builder = new Builder()
            .Copper("A", 1, Rect(0, 0, 2 * Mm, 2 * Mm))
            .Copper("B", 1, Rect(6 * Mm, 0, 8 * Mm, 2 * Mm));
        builder.Outline.Add(new Polygon2(Rect(-5 * Mm, -7 * Mm, 13 * Mm, 9 * Mm).Outer,
            new[] { Rect(3.5 * Mm, -3 * Mm, 4.5 * Mm, 5 * Mm).Outer.Reverse().ToList() }));
        var board = builder.Build();

        // 1000 V: B2 asks 5.0 mm — the straight 4 mm clearance fails, the 7.7 mm creepage passes.
        var report = SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0, ["B"] = 1000 });
        _out.WriteLine(string.Join("\n", report.Describe()));
        Assert.Equal(2, report.Findings.Count);
        var clearance = report.Findings.Single(f => f.Kind == SpacingKind.Clearance);
        var creepage = report.Findings.Single(f => f.Kind == SpacingKind.Creepage);
        Assert.Equal(4 * Mm, clearance.MeasuredMeters, 1e-12);
        Assert.False(clearance.Passes);
        Assert.Equal((1 + 2 * Math.Sqrt(1.5 * 1.5 + 9)) * Mm, creepage.MeasuredMeters, 1e-9 * Mm);
        Assert.True(creepage.Passes);

        // A shorter slot (y −1..3): the path is the minimum over the copper, not the path from
        // the clearance's closest points — 1 + 2·√(1.5² + 1²) = 4.606 mm, not 6 mm from mid-height.
        var shorter = new Builder()
            .Copper("A", 1, Rect(0, 0, 2 * Mm, 2 * Mm))
            .Copper("B", 1, Rect(6 * Mm, 0, 8 * Mm, 2 * Mm));
        shorter.Outline.Add(new Polygon2(Rect(-5 * Mm, -5 * Mm, 13 * Mm, 7 * Mm).Outer,
            new[] { Rect(3.5 * Mm, -1 * Mm, 4.5 * Mm, 3 * Mm).Outer.Reverse().ToList() }));
        var shortReport = SpacingChecker.Check(shorter.Build(), new Dictionary<string, double> { ["A"] = 0, ["B"] = 1000 });
        Assert.Equal((1 + 2 * Math.Sqrt(1.5 * 1.5 + 1)) * Mm,
            shortReport.Findings.Single(f => f.Kind == SpacingKind.Creepage).MeasuredMeters, 1e-9 * Mm);

        // The slot elsewhere: the straight line is the surface path and no creepage line appears.
        var away = new Builder()
            .Copper("A", 1, Rect(0, 0, 2 * Mm, 2 * Mm))
            .Copper("B", 1, Rect(6 * Mm, 0, 8 * Mm, 2 * Mm));
        away.Outline.Add(new Polygon2(Rect(-5 * Mm, -5 * Mm, 13 * Mm, 7 * Mm).Outer,
            new[] { Rect(3.5 * Mm, 4 * Mm, 4.5 * Mm, 6 * Mm).Outer.Reverse().ToList() }));
        var none = SpacingChecker.Check(away.Build(), new Dictionary<string, double> { ["A"] = 0, ["B"] = 1000 });
        Assert.Single(none.Findings);
        Assert.Equal(SpacingKind.Clearance, none.Findings[0].Kind);

        // The path primitive on its own: a point pair with a diamond-shaped cutout in the way.
        var diamond = new[] { new Point2(2, 0), new Point2(3, 1), new Point2(2, 2), new Point2(1, 1) };
        double path = SpacingChecker.SurfacePath(new Point2(0, 1), new Point2(4, 1), new[] { diamond });
        Assert.Equal(2 * Math.Sqrt(5), path, 1e-12);            // (0,1)→(2,0)→(4,1), grazing the lower vertex
        Assert.Equal(4, SpacingChecker.SurfacePath(new Point2(0, 3), new Point2(4, 3), new[] { diamond }), 1e-12);
    }

    [Fact]
    public void LayerToLayer_UsesTheGapThickness()
    {
        // Net A on L1 over net B on L2, overlapping in plan; the gap is 0.1 mm.
        var board = new Builder()
            .Copper("A", 1, Rect(0, 0, 10 * Mm, 10 * Mm))
            .Copper("B", 2, Rect(5 * Mm, 5 * Mm, 15 * Mm, 15 * Mm))
            .Build();
        var stackup = new BoardStackup { GapThickness = new Dictionary<int, double> { [1] = 0.1 * Mm } };

        var fail = SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0, ["B"] = 300 },
            new SpacingCheckOptions { Stackup = stackup });
        _out.WriteLine(string.Join("\n", fail.Describe()));
        var f = Assert.Single(fail.Findings);
        Assert.Equal(SpacingKind.LayerToLayer, f.Kind);
        Assert.Equal(0.1 * Mm, f.MeasuredMeters, 1e-12);
        Assert.Equal(0.2 * Mm, f.RequiredMeters, 1e-12);
        Assert.False(f.Passes);
        Assert.Equal(1, f.Layer);

        var pass = SpacingChecker.Check(board, new Dictionary<string, double> { ["A"] = 0, ["B"] = 30 },
            new SpacingCheckOptions { Stackup = stackup, ReportWithinFactor = 3 });
        Assert.True(Assert.Single(pass.Findings).Passes);                  // 0.05 mm at 30 V

        // Not overlapping in plan: nothing to report.
        var apart = new Builder()
            .Copper("A", 1, Rect(0, 0, 10 * Mm, 10 * Mm))
            .Copper("B", 2, Rect(11 * Mm, 11 * Mm, 15 * Mm, 15 * Mm))
            .Build();
        Assert.Empty(SpacingChecker.Check(apart, new Dictionary<string, double> { ["A"] = 0, ["B"] = 300 },
            new SpacingCheckOptions { Stackup = stackup }).Findings);
    }

    [Fact]
    public void ManyIslands_TheClosestPairPerNetPairIsReported()
    {
        // Three pieces of A along the bottom and two of B above; the nearest pair sets the finding.
        var b = new Builder();
        for (int i = 0; i < 3; i++) b.Copper("A", 1, Rect(i * 5 * Mm, 0, i * 5 * Mm + 4 * Mm, 1 * Mm));
        b.Copper("B", 1, Rect(0, 2 * Mm, 4 * Mm, 3 * Mm));               // 1.0 mm above A0
        b.Copper("B", 1, Rect(5 * Mm, 1.3 * Mm, 9 * Mm, 3 * Mm));        // 0.3 mm above A1
        var report = SpacingChecker.Check(b.Build(), new Dictionary<string, double> { ["A"] = 0, ["B"] = 100 });
        _out.WriteLine(string.Join("\n", report.Describe()));
        var f = Assert.Single(report.Findings);
        Assert.Equal(0.3 * Mm, f.MeasuredMeters, 1e-12);
        Assert.Equal(0.6 * Mm, f.RequiredMeters, 1e-12);
        Assert.False(f.Passes);
    }
}
