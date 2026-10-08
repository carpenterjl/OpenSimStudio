using OpenSim.Rf.Layered;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Layout;
using Xunit.Abstractions;

namespace OpenSim.Tests.Si;

/// <summary>
/// The layout rule checks and the board crosstalk scan on small boards built by hand, where
/// what should be found — and what should not — is known from the drawing.
/// </summary>
public class LayoutChecksTests
{
    private const double W = 0.2e-3;
    private readonly ITestOutputHelper _output;

    public LayoutChecksTests(ITestOutputHelper output) => _output = output;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    /// <summary>A board put together piece by piece.</summary>
    private sealed class Builder
    {
        private readonly List<CopperIsland> _islands = new();
        private readonly Dictionary<string, List<CopperIsland>> _netIslands = new();
        private readonly Dictionary<string, List<ViaBridge>> _netVias = new();
        private readonly List<string> _order = new();
        private readonly List<TraceCenterline> _traces = new();
        private readonly List<CopperPad> _pads = new();
        private readonly List<Via> _vias = new();
        public List<Polygon2> Outline { get; } = new();

        private void Own(string net, CopperIsland island)
        {
            if (!_netIslands.ContainsKey(net))
            {
                _netIslands[net] = new List<CopperIsland>();
                _netVias[net] = new List<ViaBridge>();
                _order.Add(net);
            }
            _netIslands[net].Add(island);
        }

        /// <summary>Copper of a net (null: copper in no net).</summary>
        public Builder Copper(string? net, int layer, Polygon2 shape)
        {
            var island = new CopperIsland(_islands.Count, layer, $"L{layer}", shape);
            _islands.Add(island);
            if (net is not null) Own(net, island);
            return this;
        }

        /// <summary>A straight trace with its copper.</summary>
        public Builder Trace(string net, int layer, double x0, double y0, double x1, double y1, double width = W)
        {
            _traces.Add(new TraceCenterline(layer, new Point2(x0, y0), new Point2(x1, y1), width));
            double h = width / 2;
            return Copper(net, layer, Rect(Math.Min(x0, x1) - h, Math.Min(y0, y1) - h,
                Math.Max(x0, x1) + h, Math.Max(y0, y1) + h));
        }

        public Builder Via(string net, double x, double y, int[] layers, int from = 0, int to = 0)
        {
            var via = new Via(new Point2(x, y), 0.3e-3, true, from, to);
            _vias.Add(via);
            if (!_netVias.ContainsKey(net)) { _netIslands[net] = new(); _netVias[net] = new(); _order.Add(net); }
            _netVias[net].Add(new ViaBridge(via, layers));
            return this;
        }

        /// <summary>A two-pad capacitor on layer 1, each pad on a small piece of its net.</summary>
        public Builder Capacitor(string refdes, double x, double y, string netA, string netB)
        {
            foreach (var (net, dx, pin) in new[] { (netA, -0.5e-3, "1"), (netB, 0.5e-3, "2") })
            {
                var shape = Rect(x + dx - 0.3e-3, y - 0.3e-3, x + dx + 0.3e-3, y + 0.3e-3);
                Copper(net, 1, shape);
                _pads.Add(new CopperPad(1, new Point2(x + dx, y), shape, 0.6e-3) { ComponentRef = refdes, Pin = pin });
            }
            return this;
        }

        public PcbBoard Build() => new()
        {
            Outline = Outline,
            Islands = _islands,
            Pads = _pads,
            Vias = _vias,
            Nets = _order.Select((name, i) => new CopperNet(i + 1, _netIslands[name])
                { Name = name, StitchingVias = _netVias[name] }).ToList(),
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = _traces,
        };
    }

    private static BoardStackup Stackup(int gaps, double gap = 0.2e-3, double epsR = 4.0) => new()
    {
        GapThickness = Enumerable.Range(1, gaps).ToDictionary(g => g, _ => gap),
        GapPermittivity = Enumerable.Range(1, gaps).ToDictionary(g => g, _ => epsR),
        GapLossTangent = Enumerable.Range(1, gaps).ToDictionary(g => g, _ => 0.0),
        Source = "test",
    };

    private LayoutCheckReport Check(Builder builder, LayoutCheckOptions? options = null)
    {
        var report = LayoutRuleChecker.Check(builder.Build(), options ?? new LayoutCheckOptions { Stackup = Stackup(3) });
        foreach (var f in report.Findings) _output.WriteLine($"[{f.Severity}] {f.Kind}: {f.Message}");
        return report;
    }

    // ------------------------------------------------------------------ plane gaps

    [Fact]
    public void ATraceOverASolidPlane_HasNoFindings()
    {
        var report = Check(new Builder().Trace("SIG", 1, 0, 0, 40e-3, 0)
            .Copper("GND", 2, Rect(-5e-3, -10e-3, 45e-3, 10e-3)));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ATraceAcrossASplit_IsFoundWithItsLengthAndBothSides()
    {
        var report = Check(new Builder().Trace("SIG", 1, 0, 0, 40e-3, 0)
            .Copper("GND", 2, Rect(-5e-3, -10e-3, 18e-3, 10e-3))
            .Copper("VCC", 2, Rect(22e-3, -10e-3, 45e-3, 10e-3)));
        var finding = Assert.Single(report.Findings);
        Assert.Equal(LayoutFindingKind.PlaneGapCrossing, finding.Kind);
        Assert.Equal(LayoutSeverity.Warning, finding.Severity);
        Assert.Equal(4e-3, finding.MeasureMeters, 0.06e-3);
        Assert.Equal(20e-3, finding.At.X, 0.1e-3);
        Assert.Contains("split", finding.Message);
        Assert.Contains("GND", finding.Message);
        Assert.Contains("VCC", finding.Message);
    }

    [Fact]
    public void AJointInsideTheGap_StillGivesOneFinding()
    {
        var report = Check(new Builder().Trace("SIG", 1, 0, 0, 20e-3, 0).Trace("SIG", 1, 40e-3, 0, 20e-3, 0)
            .Copper("GND", 2, Rect(-5e-3, -10e-3, 18e-3, 10e-3))
            .Copper("VCC", 2, Rect(22e-3, -10e-3, 45e-3, 10e-3)));
        var finding = Assert.Single(report.Findings);
        Assert.Equal(4e-3, finding.MeasureMeters, 0.11e-3);
        Assert.Contains("split", finding.Message);
    }

    [Fact]
    public void ASlotInOnePlane_AndATraceRunningOffIt_AreToldApart()
    {
        var plane = new Polygon2(Rect(-5e-3, -10e-3, 30e-3, 10e-3).Outer,
            new[] { Rect(10e-3, -3e-3, 12e-3, 3e-3).Outer });
        var report = Check(new Builder().Trace("SIG", 1, 0, 0, 40e-3, 0).Copper("GND", 2, plane));
        Assert.Equal(2, report.Findings.Count);
        var slot = Assert.Single(report.Findings, f => f.Message.Contains("slot or void"));
        Assert.Equal(2e-3, slot.MeasureMeters, 0.06e-3);
        var edge = Assert.Single(report.Findings, f => f.Message.Contains("past the edge"));
        Assert.Equal(10e-3, edge.MeasureMeters, 0.06e-3);
    }

    [Fact]
    public void BetweenTwoPlanes_AGapInOne_IsANote_AndAViaClearanceIsNotAFinding()
    {
        var split = new Builder().Trace("SIG", 2, 0, 0, 40e-3, 0)
            .Copper("GND", 1, Rect(-5e-3, -10e-3, 18e-3, 10e-3)).Copper("GND", 1, Rect(22e-3, -10e-3, 45e-3, 10e-3))
            .Copper("GND", 3, Rect(-5e-3, -10e-3, 45e-3, 10e-3));
        var finding = Assert.Single(Check(split).Findings);
        Assert.Equal(LayoutSeverity.Note, finding.Severity);
        Assert.Contains("L3, is continuous", finding.Message);

        // The trace ends on its own via, which has a 0.6 mm clearance hole in the plane.
        var hole = new List<Point2>();
        for (int k = 0; k < 24; k++)
            hole.Add(new Point2(40e-3 + 0.6e-3 * Math.Cos(2 * Math.PI * k / 24), 0.6e-3 * Math.Sin(2 * Math.PI * k / 24)));
        var plane = new Polygon2(Rect(-5e-3, -10e-3, 50e-3, 10e-3).Outer, new[] { (IReadOnlyList<Point2>)hole });
        var antipad = new Builder().Trace("SIG", 1, 0, 0, 40e-3, 0).Copper("GND", 2, plane)
            .Via("SIG", 40e-3, 0, new[] { 1, 4 });
        Assert.Empty(Check(antipad).Findings);
    }

    // ------------------------------------------------------------------ reference change

    /// <summary>L1 → via at (20, 0) → L4, planes on L2 and L3.</summary>
    private static Builder Transition(string netL3) => new Builder()
        .Trace("SIG", 1, 0, 0, 20e-3, 0).Trace("SIG", 4, 20e-3, 0, 40e-3, 0)
        .Via("SIG", 20e-3, 0, new[] { 1, 4 })
        .Copper("GND", 2, Rect(-5e-3, -10e-3, 45e-3, 10e-3))
        .Copper(netL3, 3, Rect(-5e-3, -10e-3, 45e-3, 10e-3));

    [Fact]
    public void ALayerChange_NeedsAStitchingViaNearby()
    {
        var far = Transition("GND").Via("GND", 30e-3, 5e-3, new[] { 2, 3 });
        var finding = Assert.Single(Check(far).Findings);
        Assert.Equal(LayoutFindingKind.ReferenceChange, finding.Kind);
        Assert.Equal(LayoutSeverity.Warning, finding.Severity);
        Assert.Contains("11.2 mm away", finding.Message);

        var near = Transition("GND").Via("GND", 21e-3, 0.5e-3, new[] { 2, 3 });
        Assert.Empty(Check(near).Findings);

        var none = Transition("GND");
        Assert.Contains("no via joins GND", Assert.Single(Check(none).Findings).Message);
    }

    [Fact]
    public void BetweenPlanesOfTwoNets_ACapacitorNearbyCarriesTheReturn()
    {
        // Component data present (a resistor elsewhere), but no capacitor: a warning.
        var without = Transition("VCC").Capacitor("R1", 0, 6e-3, "GND", "VCC");
        var finding = Assert.Single(Check(without).Findings);
        Assert.Equal(LayoutSeverity.Warning, finding.Severity);
        Assert.Contains("no capacitor between GND", finding.Message);

        var with = Transition("VCC").Capacitor("C7", 22e-3, 2e-3, "GND", "VCC");
        Assert.Empty(Check(with).Findings);

        var tooFar = Transition("VCC").Capacitor("C7", 35e-3, 8e-3, "GND", "VCC");
        Assert.Single(Check(tooFar).Findings);

        // No component names at all: it cannot be decided, and is said so as a note.
        var blind = Assert.Single(Check(Transition("VCC")).Findings);
        Assert.Equal(LayoutSeverity.Note, blind.Severity);
        Assert.Contains("names no components", blind.Message);
    }

    [Fact]
    public void TwoLayersThatShareTheirPlane_AreNotAReferenceChange()
    {
        // L1 and L3 both reference L2; the via runs L1..L3 only (no stub).
        var builder = new Builder()
            .Trace("SIG", 1, 0, 0, 20e-3, 0).Trace("SIG", 3, 20e-3, 0, 40e-3, 0)
            .Via("SIG", 20e-3, 0, new[] { 1, 3 }, from: 1, to: 3)
            .Copper("GND", 2, Rect(-5e-3, -10e-3, 45e-3, 10e-3));
        Assert.Empty(Check(builder).Findings);
    }

    // ------------------------------------------------------------------ stubs, edge

    [Fact]
    public void AThroughViaUsedForTheTopTwoLayers_LeavesAStub()
    {
        Builder Board(int from, int to) => new Builder()
            .Trace("SIG", 1, 0, 0, 20e-3, 0).Trace("SIG", 3, 20e-3, 0, 40e-3, 0)
            .Via("SIG", 20e-3, 0, new[] { 1, 3 }, from, to)
            .Copper("GND", 2, Rect(-5e-3, -10e-3, 45e-3, 10e-3));
        var options = new LayoutCheckOptions { Stackup = Stackup(5, gap: 0.3e-3), MaxStubMeters = 0.5e-3 };
        // Six layers: the barrel runs on from L3 to L6 — three gaps and two copper layers.
        var board = Board(0, 0).Copper("GND", 6, Rect(-5e-3, -10e-3, 45e-3, 10e-3));
        var stub = Assert.Single(Check(board, options).Findings);
        Assert.Equal(LayoutFindingKind.ViaStub, stub.Kind);
        double expected = 3 * 0.3e-3 + 2 * 35e-6;
        Assert.Equal(expected, stub.MeasureMeters, 1e-9);
        Assert.Contains($"{299792458.0 / (4 * expected * 2) / 1e9:g3} GHz", stub.Message);

        // A blind via that stops at L3 has none; nor has a stub under the limit.
        Assert.Empty(Check(Board(1, 3).Copper("GND", 6, Rect(-5e-3, -10e-3, 45e-3, 10e-3)), options).Findings);
        Assert.Empty(Check(board, options with { MaxStubMeters = 1.5e-3 }).Findings);
    }

    [Fact]
    public void ATraceNearTheBoardEdge_IsReportedWithItsClearance()
    {
        var builder = new Builder().Trace("CLK", 1, 2e-3, 0.4e-3, 30e-3, 0.4e-3).Trace("FAR", 1, 2e-3, 5e-3, 30e-3, 5e-3)
            .Copper("GND", 2, Rect(0, 0, 40e-3, 20e-3));
        builder.Outline.Add(Rect(0, 0, 40e-3, 20e-3));
        var finding = Assert.Single(Check(builder).Findings);
        Assert.Equal(LayoutFindingKind.BoardEdge, finding.Kind);
        Assert.Equal("CLK", finding.Net);
        Assert.Equal(0.4e-3 - W / 2, finding.MeasureMeters, 1e-9);

        // Without an outline the check says it did not run.
        var bare = new Builder().Trace("CLK", 1, 2e-3, 0.4e-3, 30e-3, 0.4e-3).Copper("GND", 2, Rect(0, 0, 40e-3, 20e-3));
        Assert.Contains(Check(bare).Notes, n => n.Contains("no outline"));
    }

    [Fact]
    public void SegmentDistance_CoversCrossingEndpointsAndParallels()
    {
        static Point2 P(double x, double y) => new(x, y);
        Assert.Equal(0, LayoutRuleChecker.SegmentDistance(P(0, 0), P(2, 2), P(0, 2), P(2, 0), out var at));
        Assert.Equal(1, at.X, 1e-12);
        Assert.Equal(1, LayoutRuleChecker.SegmentDistance(P(0, 0), P(4, 0), P(1, 1), P(3, 1), out _), 1e-12);
        Assert.Equal(Math.Sqrt(2), LayoutRuleChecker.SegmentDistance(P(0, 0), P(1, 0), P(2, 1), P(3, 5), out at), 1e-12);
        Assert.Equal(1, at.X, 1e-12);
    }

    // ------------------------------------------------------------------ crosstalk scan

    private static CrosstalkScanOptions ScanOptions(int gaps) => new() { Stackup = Stackup(gaps), RiseTimeSeconds = 0.2e-9 };

    [Fact]
    public void TheScan_PricesAPairWithTheCalculatorsCoupling()
    {
        const double gap = 0.15e-3, length = 40e-3;
        var board = new Builder()
            .Trace("A", 1, 0, 0, length, 0).Trace("B", 1, 0, gap + W, length, gap + W)
            .Copper("GND", 2, Rect(-5e-3, -10e-3, 45e-3, 10e-3)).Build();
        var options = ScanOptions(1);
        var report = CrosstalkScan.Run(board, options);
        var pair = Assert.Single(report.Pairs);
        _output.WriteLine(pair.Describe());
        Assert.Equal(("A", "B"), (pair.NetA, pair.NetB));
        Assert.Equal(length, pair.ParallelLengthMeters, 1e-9);
        Assert.Equal(gap, pair.MinEdgeGapMeters, 1e-9);
        Assert.Null(pair.NotPriced);

        // The same two traces through the calculator, with the scan's model (thickness only).
        var calculator = ImpedanceCalculator.Solve(new LineSpec
        {
            WidthMeters = W, PairGapMeters = gap, HeightMeters = 0.2e-3, RelativePermittivity = 4.0, LossTangent = 0,
            Model = new RlgcModel { ThicknessCorrection = true, SurfaceImpedance = false, WidebandDielectric = false }
        });
        double near = calculator.NearEndCoupling!.Value, far = calculator.FarEndCouplingSecondsPerMeter!.Value;
        var run = Assert.Single(pair.Runs);
        Assert.Equal(near, run.NearEndCoupling, 1e-6 * near);
        Assert.Equal(far, run.FarEndCouplingSecondsPerMeter, 1e-6 * Math.Abs(far));
        // 40 mm is longer than half the 0.2 ns edge travels: the near end is saturated.
        Assert.Equal(near, pair.NearEnd, 1e-6 * near);
        Assert.Equal(Math.Abs(far) * length / options.RiseTimeSeconds, pair.FarEnd, 1e-6 * pair.FarEnd);
        Assert.True(pair.FarEnd > 0.01);
    }

    [Fact]
    public void AGroundGuardBetweenThePair_IsInTheSolve()
    {
        // A 0.2 mm GND trace in the middle of a 0.6 mm gap, the same net as the plane under them:
        // the pair is solved with it, tied to the plane, as the three strips by hand are.
        const double gap = 0.6e-3, length = 40e-3, guard = 0.2e-3;
        double pitch = gap + W;
        var model = new RlgcModel { ThicknessCorrection = true, SurfaceImpedance = false, WidebandDielectric = false };
        Builder Pair() => new Builder()
            .Trace("A", 1, 0, 0, length, 0).Trace("B", 1, 0, pitch, length, pitch)
            .Copper("GND", 2, Rect(-5e-3, -10e-3, 45e-3, 10e-3));
        var guarded = CrosstalkScan.Run(Pair().Trace("GND", 1, 0, pitch / 2, length, pitch / 2, guard).Build(), ScanOptions(1));
        var open = CrosstalkScan.Run(Pair().Build(), ScanOptions(1));
        var run = Assert.Single(guarded.Pairs.Single(p => (p.NetA, p.NetB) == ("A", "B")).Runs);
        Assert.Equal(guard, run.GuardWidthMeters, 1e-12);
        Assert.True(run.GuardIsGround);

        var stack = new LayeredStackup(new[] { new LayeredStackup.Layer(4.0, 0, 0.2e-3) });
        var section = new CoupledLineCrossSection(stack, 0, new[]
        {
            TraceCrossSection.Copper(-pitch / 2, W), TraceCrossSection.Copper(0, guard) with { IsGround = true },
            TraceCrossSection.Copper(pitch / 2, W)
        });
        var expected = CrosstalkScan.Coefficients(BoardCoupledResult.Reduce(section, RlgcExtractor.Extract(section, model)));
        Assert.Equal(expected.Near, run.NearEndCoupling, 1e-6 * expected.Near);

        double without = Assert.Single(open.Pairs).Runs[0].NearEndCoupling;
        _output.WriteLine($"near-end coefficient: {without:e3} open, {run.NearEndCoupling:e3} with the ground guard");
        Assert.True(run.NearEndCoupling < 0.5 * without);
    }

    [Fact]
    public void ARunCutIntoPieces_IsTheSameRun_AndAShortOneIsNotSaturated()
    {
        const double gap = 0.15e-3;
        Builder Base() => new Builder().Copper("GND", 2, Rect(-5e-3, -10e-3, 45e-3, 10e-3));
        var whole = CrosstalkScan.Run(Base().Trace("A", 1, 0, 0, 40e-3, 0)
            .Trace("B", 1, 0, gap + W, 40e-3, gap + W).Build(), ScanOptions(1)).Pairs[0];
        var pieces = Base().Trace("A", 1, 0, 0, 40e-3, 0);
        foreach (double x in new[] { 0.0, 10e-3, 20e-3, 30e-3 })
            pieces.Trace("B", 1, x + 10e-3, gap + W, x, gap + W);        // drawn backwards, too
        var cut = CrosstalkScan.Run(pieces.Build(), ScanOptions(1)).Pairs[0];
        Assert.Equal(whole.ParallelLengthMeters, cut.ParallelLengthMeters, 1e-9);
        Assert.Equal(whole.NearEnd, cut.NearEnd, 1e-9);
        Assert.Equal(whole.FarEnd, cut.FarEnd, 1e-9 + 1e-6 * whole.FarEnd);

        // 5 mm of run against a 1 ns edge: near end = K_b·2T_d/t_r.
        var brief = CrosstalkScan.Run(Base().Trace("A", 1, 0, 0, 40e-3, 0)
            .Trace("B", 1, 10e-3, gap + W, 15e-3, gap + W).Build(),
            ScanOptions(1) with { RiseTimeSeconds = 1e-9 }).Pairs[0];
        var run = brief.Runs[0];
        Assert.Equal(5e-3, brief.ParallelLengthMeters, 1e-9);
        Assert.Equal(run.NearEndCoupling * 2 * 5e-3 * run.DelaySecondsPerMeter / 1e-9, brief.NearEnd, 1e-12);
        Assert.True(brief.NearEnd < 0.2 * run.NearEndCoupling);
    }

    [Fact]
    public void TheScan_RanksCloserPairsFirst_AndIgnoresCrossingsAndDistantTraces()
    {
        var board = new Builder()
            .Trace("A", 1, 0, 0, 40e-3, 0)
            .Trace("B", 1, 0, 0.35e-3, 40e-3, 0.35e-3)             // 0.15 mm from A
            .Trace("C", 1, 0, 1.15e-3, 40e-3, 1.15e-3)             // 0.6 mm from B, 0.95 mm from A
            .Trace("D", 1, 0, 6e-3, 40e-3, 6e-3)                   // far from everything
            .Trace("X", 1, 20e-3, -5e-3, 20e-3, -0.4e-3)           // perpendicular, ends near A
            .Copper("GND", 2, Rect(-5e-3, -10e-3, 45e-3, 10e-3)).Build();
        var report = CrosstalkScan.Run(board, ScanOptions(1));
        foreach (var p in report.Pairs) _output.WriteLine(p.Describe());
        Assert.Equal(new[] { "A/B", "B/C", "A/C" }, report.Pairs.Select(p => $"{p.NetA}/{p.NetB}").ToArray());
        Assert.True(report.Pairs[0].Worst > 3 * report.Pairs[1].Worst);
        Assert.Equal(0, report.PairsNotSolved);

        // With a budget of one solve the other two are counted, not listed.
        var capped = CrosstalkScan.Run(board, ScanOptions(1) with { MaxSolvedPairs = 1 });
        Assert.Equal("B", Assert.Single(capped.Pairs).NetB);
        Assert.Equal(2, capped.PairsNotSolved);
        Assert.Contains(capped.Notes, n => n.Contains("2 more pair"));
    }

    [Fact]
    public void BetweenTwoPlanes_ThereIsNoFarEndCrosstalk_AndWithNoPlaneThePairIsNotPriced()
    {
        var stripline = new Builder()
            .Trace("D_P", 2, 0, 0, 40e-3, 0).Trace("D_N", 2, 0, 0.35e-3, 40e-3, 0.35e-3)
            .Copper("GND", 1, Rect(-5e-3, -10e-3, 45e-3, 10e-3))
            .Copper("GND", 3, Rect(-5e-3, -10e-3, 45e-3, 10e-3)).Build();
        var pair = Assert.Single(CrosstalkScan.Run(stripline, ScanOptions(2)).Pairs);
        _output.WriteLine(pair.Describe());
        Assert.True(pair.LikelyDifferentialPair);
        Assert.True(pair.NearEnd > 0.02);
        Assert.True(pair.FarEnd < 1e-3 * pair.NearEnd, $"far-end {pair.FarEnd} in a homogeneous stripline");

        var bare = new Builder()
            .Trace("A", 1, 0, 0, 40e-3, 0).Trace("B", 1, 0, 0.35e-3, 40e-3, 0.35e-3)
            .Trace("K", 2, 0, 5e-3, 40e-3, 5e-3).Build();
        var unpriced = Assert.Single(CrosstalkScan.Run(bare, ScanOptions(1)).Pairs);
        Assert.NotNull(unpriced.NotPriced);
        Assert.Contains("not priced", unpriced.Describe());
    }

    /// <summary>A bus of 300 traces, each drawn as ten pieces: three thousand centerlines and
    /// about nine hundred coupled pairs. What is asserted is the count and the ordering; the
    /// time is printed, because a scan that is quadratic in the traces would not finish.</summary>
    [Fact]
    public void ABusOfThreeThousandCenterlines_IsCheckedAndScanned()
    {
        var builder = new Builder().Copper("GND", 2, Rect(-5e-3, -5e-3, 105e-3, 130e-3));
        builder.Outline.Add(Rect(-6e-3, -6e-3, 106e-3, 131e-3));
        for (int i = 0; i < 300; i++)
            for (int k = 0; k < 10; k++)
                builder.Trace($"B{i:000}", 1, k * 10e-3, i * 0.4e-3, (k + 1) * 10e-3, i * 0.4e-3);
        var board = builder.Build();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var index = new BoardLayoutIndex(board);
        Assert.Equal(3000, index.Traces.Count);
        var checks = LayoutRuleChecker.Check(index, new LayoutCheckOptions { Stackup = Stackup(1) });
        double checkSeconds = clock.Elapsed.TotalSeconds;
        clock.Restart();
        var scan = CrosstalkScan.Run(index, ScanOptions(1) with { MaxEdgeGapMeters = 0.8e-3 });
        _output.WriteLine($"checks {checkSeconds:f2} s, scan {clock.Elapsed.TotalSeconds:f2} s; "
            + $"{scan.Pairs.Count} pairs listed, {scan.PairsNotSolved} not solved");

        Assert.Empty(checks.Findings);
        // Neighbours at 0.2 mm and at 0.6 mm are inside the 0.8 mm limit: 299 + 298 pairs.
        Assert.Equal(300, scan.Pairs.Count);
        Assert.Equal(299 + 298 - 300, scan.PairsNotSolved);
        Assert.All(scan.Pairs.Take(299), p => Assert.Equal(0.2e-3, p.MinEdgeGapMeters, 1e-9));
        Assert.All(scan.Pairs, p => Assert.Equal(100e-3, p.ParallelLengthMeters, 1e-9));
        Assert.Equal(scan.Pairs[0].Worst, scan.Pairs[298].Worst, 1e-12);
    }

    [Theory]
    [InlineData("USB_P", "USB_N", true)]
    [InlineData("LVDS3+", "LVDS3-", true)]
    [InlineData("d1n", "d1p", true)]
    [InlineData("CLK", "CLR", false)]
    [InlineData("A_P", "B_N", false)]
    [InlineData("TX", "TXD", false)]
    public void DifferentialPairNames_AreRecognised(string a, string b, bool expected) =>
        Assert.Equal(expected, CrosstalkScan.LooksLikeAPair(a, b));
}
