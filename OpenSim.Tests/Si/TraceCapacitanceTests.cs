using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using Xunit;

namespace OpenSim.Tests.Si;

/// <summary>
/// Per-trace capacitance-to-ground gates. The BEM itself is gated in
/// <see cref="RlgcExtractorTests"/> (Hammerstad–Jensen, FD-oracle Richardson limits,
/// panel convergence) — these are COMPOSITION identities: the extractor's grouping,
/// dedup, and per-layer substrate plumbing must reproduce C′ × length exactly, because
/// the 1-conductor cross-section it builds is the SAME solve the wizard runs (center 0,
/// same defaults), so any deviation is a plumbing bug, not BEM noise. Physics enters
/// only through the wide-strip parallel-plate band and the exact pad plate term.
/// </summary>
public class TraceCapacitanceTests
{
    private const double W = 0.3e-3;    // trace width
    private const double H = 0.2e-3;    // substrate height
    private const double EpsR = 4.4, TanD = 0.02;
    private const double Epsilon0 = 8.8541878128e-12;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    /// <summary>A one-net board: given centerlines (all inside one large island per layer),
    /// optional pads, and a per-gap stackup. The island is the bounding box per layer —
    /// containment is all <see cref="OpenSim.Pcb.Inductance.NetTraceExtractor"/> needs.
    /// <paramref name="planeLayers"/> carry a solid copper plane of ANOTHER net — the
    /// reference the extractor now looks for instead of assuming; the default is one
    /// plane on the layer below the net's lowest.</summary>
    private static (PcbBoard Board, CopperNet Net) OneNetBoard(
        IReadOnlyList<TraceCenterline> centerlines,
        IReadOnlyList<CopperPad>? pads = null,
        PcbStackupSettings? stackup = null,
        int[]? planeLayers = null)
    {
        var islands = new List<CopperIsland>();
        int id = 0;
        foreach (var layer in centerlines.Select(c => c.LayerOrder)
                     .Concat((pads ?? Array.Empty<CopperPad>()).Select(p => p.LayerOrder))
                     .Distinct().OrderBy(l => l))
        {
            islands.Add(new CopperIsland(id++, layer, $"L{layer}",
                Rect(-100e-3, -100e-3, 100e-3, 100e-3)));
        }
        var net = new CopperNet(1, islands) { Name = "NET1" };
        var boardIslands = new List<CopperIsland>(islands);
        foreach (int layer in planeLayers ?? new[] { islands.Max(i => i.LayerOrder) + 1 })
            boardIslands.Add(new CopperIsland(id++, layer, $"L{layer} plane",
                Rect(-150e-3, -150e-3, 150e-3, 150e-3)));
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = boardIslands,
            Pads = pads ?? Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = new[] { net },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = centerlines,
            Stackup = stackup ?? new PcbStackupSettings
            {
                DielectricGapThicknesses = new[] { H },
                DielectricGapPermittivities = new[] { EpsR },
                DielectricGapLossTangents = new[] { TanD },
            },
        };
        return (board, net);
    }

    /// <summary>The wizard's 1-conductor C′ [F/m] at width w over (εr, tanδ, h) — built at
    /// center 0 with the extractor's own defaults, so it is the IDENTICAL solve and the
    /// composition identities below hold to machine precision, not BEM tolerance.</summary>
    private static double WizardCPerMeter(double width, double epsR = EpsR,
        double tanD = TanD, double h = H)
    {
        var stack = new LayeredStackup(new[] { new LayeredStackup.Layer(epsR, tanD, h) });
        var options = new BoardCoupledOptions();
        var section = new CoupledLineCrossSection(stack, 0, new[]
        {
            new TraceCrossSection(0, width, options.CopperThicknessMeters,
                options.ConductivitySiemensPerMeter),
        });
        return RlgcExtractor.Extract(section).CapacitanceFaradsPerMeter[0, 0];
    }

    // ------------------------------------------------------------------
    // The round-trip / composition identities.
    // ------------------------------------------------------------------

    [Fact]
    public void StraightTrace_MatchesWizardCTimesLength()
    {
        // At an offset y so board coordinates differ from anything canonical — the
        // cross-section is position-free, so the match must be machine-exact.
        var (board, net) = OneNetBoard(new[]
        {
            new TraceCenterline(1, new Point2(0, 5e-3), new Point2(40e-3, 5e-3), W),
        });
        var result = TraceCapacitanceExtractor.Extract(board, net);

        Assert.Null(result.FailureReason);
        double want = WizardCPerMeter(W) * 40e-3;
        Assert.True(Math.Abs(result.TotalFarads - want) <= 1e-12 * want,
            $"total {result.TotalFarads:g9} vs wizard C'·l {want:g9}");
        Assert.Equal(0, result.PadFarads);

        var g = Assert.Single(result.Groups);
        Assert.Equal(1, g.LayerOrder);
        Assert.Equal(W, g.WidthMeters, 12);
        Assert.Equal(40e-3, g.LengthMeters, 12);
        Assert.InRange(g.EffectivePermittivity, 1.0, EpsR);   // microstrip: air < ε_eff < εr
    }

    [Fact]
    public void BentTrace_CountsFullRoutedLength()
    {
        // An L-bend: 30 mm horizontal + 20 mm vertical, same width ⇒ one group,
        // C = C′ × 50 mm exactly (translational invariance; corners stated-ignored).
        var (board, net) = OneNetBoard(new[]
        {
            new TraceCenterline(1, new Point2(0, 0), new Point2(30e-3, 0), W),
            new TraceCenterline(1, new Point2(30e-3, 0), new Point2(30e-3, 20e-3), W),
        });
        var result = TraceCapacitanceExtractor.Extract(board, net);

        Assert.Null(result.FailureReason);
        double want = WizardCPerMeter(W) * 50e-3;
        Assert.True(Math.Abs(result.TotalFarads - want) <= 1e-12 * want,
            $"bent-trace total {result.TotalFarads:g9} vs C'·(30+20)mm {want:g9}");
    }

    [Fact]
    public void BranchedNet_CountsEveryArm()
    {
        // A T: capacitance is electrostatic — ALL copper holds charge, so unlike the
        // inductance chain (branches pruned: zero current) every arm counts.
        var (board, net) = OneNetBoard(new[]
        {
            new TraceCenterline(1, new Point2(-15e-3, 0), new Point2(0, 0), W),
            new TraceCenterline(1, new Point2(0, 0), new Point2(15e-3, 0), W),
            new TraceCenterline(1, new Point2(0, 0), new Point2(0, 10e-3), W),
        });
        var result = TraceCapacitanceExtractor.Extract(board, net);

        Assert.Null(result.FailureReason);
        double want = WizardCPerMeter(W) * 40e-3;
        Assert.True(Math.Abs(result.TotalFarads - want) <= 1e-12 * want,
            $"T-net total {result.TotalFarads:g9} vs C'·(15+15+10)mm {want:g9}");
    }

    [Fact]
    public void MultiWidthNet_SumsPerWidthGroups()
    {
        var wide = 0.6e-3;
        var (board, net) = OneNetBoard(new[]
        {
            new TraceCenterline(1, new Point2(0, 0), new Point2(20e-3, 0), W),
            new TraceCenterline(1, new Point2(20e-3, 0), new Point2(50e-3, 0), wide),
        });
        var result = TraceCapacitanceExtractor.Extract(board, net);

        Assert.Null(result.FailureReason);
        Assert.Equal(2, result.Groups.Count);
        double want = WizardCPerMeter(W) * 20e-3 + WizardCPerMeter(wide) * 30e-3;
        Assert.True(Math.Abs(result.TotalFarads - want) <= 1e-12 * want,
            $"multi-width total {result.TotalFarads:g9} vs Σ C'(w)·l {want:g9}");
    }

    /// <summary>C′ [F/m] of one trace of width <paramref name="width"/> in a hand-built
    /// cross-section — the reference each board case below must reproduce exactly.</summary>
    private static double HandBuiltCPerMeter(double width, int metalInterface, bool topGround,
        params (double EpsR, double TanD, double H)[] layersGroundUp)
    {
        var stack = new LayeredStackup(layersGroundUp
            .Select(l => new LayeredStackup.Layer(l.EpsR, l.TanD, l.H)).ToArray());
        var options = new BoardCoupledOptions();
        var section = new CoupledLineCrossSection(stack, metalInterface, new[]
        {
            new TraceCrossSection(0, width, options.CopperThicknessMeters,
                options.ConductivitySiemensPerMeter),
        }, topGround);
        return RlgcExtractor.Extract(section).CapacitanceFaradsPerMeter[0, 0];
    }

    // Gap 1 (L1–L2): 0.2 mm, εr 4.4. Gap 2 (L2–L3): 0.4 mm, εr 3.0.
    private static readonly PcbStackupSettings ThreeLayer = new()
    {
        DielectricGapThicknesses = new[] { H, 0.4e-3 },
        DielectricGapPermittivities = new[] { EpsR, 3.0 },
        DielectricGapLossTangents = new[] { TanD, 0.01 },
    };

    [Fact]
    public void InnerLayerTrace_BetweenTwoPlanes_IsAStripline()
    {
        // L2 trace with planes on L1 and L3. This used to be priced as a microstrip on
        // the gap below with AIR above (the test that stood here pinned exactly that).
        var (board, net) = OneNetBoard(new[]
        {
            new TraceCenterline(2, new Point2(0, 5e-3), new Point2(30e-3, 5e-3), W),
        }, stackup: ThreeLayer, planeLayers: new[] { 1, 3 });
        var result = TraceCapacitanceExtractor.Extract(board, net);

        Assert.Null(result.FailureReason);
        // Ground-up: gap 2 under the trace, gap 1 over it, a plane closing each end.
        double stripline = HandBuiltCPerMeter(W, 0, topGround: true, (3.0, 0.01, 0.4e-3), (EpsR, TanD, H));
        double want = stripline * 30e-3;
        Assert.True(Math.Abs(result.TotalFarads - want) <= 1e-12 * want,
            $"inner-layer total {result.TotalFarads:g9} vs stripline C'·l {want:g9}");
        Assert.Contains(result.Assumptions, a => a.StartsWith("Stripline: trace layer L2") && a.Contains("L1 above") && a.Contains("L3 below"));

        // The size of the old error: the microstrip-on-the-gap-below number is far too low.
        double oldMicrostrip = WizardCPerMeter(W, epsR: 3.0, tanD: 0.01, h: 0.4e-3);
        Assert.True(stripline > 2 * oldMicrostrip,
            $"stripline C' {stripline:g4} F/m should be more than twice the old {oldMicrostrip:g4} F/m");
        // And it is at least the two parallel plates.
        Assert.True(stripline > Epsilon0 * W * (3.0 / 0.4e-3 + EpsR / H));
    }

    [Fact]
    public void MultiLayerNet_EachLayerIsSolvedAgainstTheBoardsRealPlane()
    {
        // The net routes on L1 and L2; the only plane is L3. L1 is then a microstrip over
        // BOTH gaps (the net's own L2 copper is not its reference), and L2 is an embedded
        // microstrip: gap 2 to the plane, gap 1 as dielectric cover, air above.
        var (board, net) = OneNetBoard(new[]
        {
            new TraceCenterline(1, new Point2(0, 0), new Point2(25e-3, 0), W),
            new TraceCenterline(2, new Point2(0, 5e-3), new Point2(30e-3, 5e-3), W),
        }, stackup: ThreeLayer);
        var result = TraceCapacitanceExtractor.Extract(board, net);

        Assert.Null(result.FailureReason);
        var layers = new[] { (3.0, 0.01, 0.4e-3), (EpsR, TanD, H) };       // ground-up from L3
        double want = HandBuiltCPerMeter(W, 1, topGround: false, layers) * 25e-3
                    + HandBuiltCPerMeter(W, 0, topGround: false, layers) * 30e-3;
        Assert.True(Math.Abs(result.TotalFarads - want) <= 1e-12 * want,
            $"multi-layer total {result.TotalFarads:g9} vs per-layer sum {want:g9}");
        Assert.Contains(result.Assumptions, a => a.Contains("Reference plane L3 below trace layer L1"));
        Assert.Contains(result.Assumptions, a => a.Contains("Reference plane L3 below trace layer L2") && a.Contains("embedded microstrip"));
    }

    [Fact]
    public void BottomLayerTrace_UnderAPlane_IsTheMirroredMicrostrip()
    {
        var (board, net) = OneNetBoard(new[]
        {
            new TraceCenterline(3, new Point2(0, 0), new Point2(20e-3, 0), W),
        }, stackup: ThreeLayer, planeLayers: new[] { 2 });
        var result = TraceCapacitanceExtractor.Extract(board, net);

        Assert.Null(result.FailureReason);
        double want = WizardCPerMeter(W, epsR: 3.0, tanD: 0.01, h: 0.4e-3) * 20e-3;
        Assert.True(Math.Abs(result.TotalFarads - want) <= 1e-12 * want);
        Assert.Contains(result.Assumptions, a => a.Contains("the dielectric gap above trace layer L3") && a.Contains("L2"));
    }

    [Fact]
    public void NoCopperUnderTheTrace_IsRefused_WithTheCoverageFound()
    {
        // A two-layer board whose L2 "plane" stops well short of the trace: 40 mm of trace,
        // copper under the first 10 mm only.
        var trace = new TraceCenterline(1, new Point2(0, 0), new Point2(40e-3, 0), W);
        var own = new CopperIsland(0, 1, "L1", Rect(-1e-3, -1e-3, 41e-3, 1e-3));
        var partial = new CopperIsland(1, 2, "L2", Rect(-5e-3, -5e-3, 10e-3, 5e-3));
        var net = new CopperNet(1, new[] { own }) { Name = "NET1" };
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = new[] { own, partial },
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = new[] { net },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = new[] { trace },
        };
        var result = TraceCapacitanceExtractor.Extract(board, net);

        Assert.NotNull(result.FailureReason);
        Assert.Contains("no copper layer has copper under the traces on L1", result.FailureReason);
        Assert.Contains("L2: 2", result.FailureReason);            // about a quarter covered
        Assert.Contains("reference plane", result.FailureReason);
    }

    [Fact]
    public void CoincidentDuplicateDraw_CountsOnce()
    {
        var one = new TraceCenterline(1, new Point2(0, 0), new Point2(40e-3, 0), W);
        var (boardOnce, netOnce) = OneNetBoard(new[] { one });
        var (boardTwice, netTwice) = OneNetBoard(new[] { one, one });

        var once = TraceCapacitanceExtractor.Extract(boardOnce, netOnce);
        var twice = TraceCapacitanceExtractor.Extract(boardTwice, netTwice);

        Assert.Null(once.FailureReason);
        Assert.Null(twice.FailureReason);
        Assert.Equal(once.TotalFarads, twice.TotalFarads);   // bitwise: same group solve
        Assert.Contains(twice.Assumptions, a => a.Contains("duplicate"));
    }

    // ------------------------------------------------------------------
    // Physics: the wide-strip parallel-plate limit (one-sided, fringing is positive).
    // ------------------------------------------------------------------

    [Fact]
    public void WideStrip_ApproachesParallelPlate_FromAbove()
    {
        // C′ ≥ ε₀εr·w/h always (fringing + the air side only ADD), and the ratio falls
        // monotonically toward 1 as w/h grows — the trend is the assertion, no golden value.
        double[] widthOverH = { 5, 20, 80 };
        double previous = double.MaxValue;
        foreach (double m in widthOverH)
        {
            double w = m * H;
            double ratio = WizardCPerMeter(w) / (Epsilon0 * EpsR * w / H);
            Assert.True(ratio > 1, $"w/h={m}: C' ratio {ratio:g5} must exceed the plate bound");
            Assert.True(ratio < previous, $"w/h={m}: ratio {ratio:g5} must fall toward 1");
            previous = ratio;
        }
        Assert.True(previous < 1.15, $"w/h=80 ratio {previous:g5} should be near the plate limit");
    }

    // ------------------------------------------------------------------
    // Pads: the plate term is the formula, exactly, and says so.
    // ------------------------------------------------------------------

    [Fact]
    public void PadPlateTerm_IsExact_AndNamed()
    {
        var pad = new CopperPad(1, new Point2(45e-3, 0), Rect(44e-3, -1e-3, 46e-3, 1e-3), 2e-3);
        var (board, net) = OneNetBoard(new[]
        {
            new TraceCenterline(1, new Point2(0, 0), new Point2(40e-3, 0), W),
        }, pads: new[] { pad });
        var result = TraceCapacitanceExtractor.Extract(board, net);

        Assert.Null(result.FailureReason);
        Assert.Equal(1, result.PadCount);
        double wantPad = Epsilon0 * EpsR * (2e-3 * 2e-3) / H;
        Assert.True(Math.Abs(result.PadFarads - wantPad) <= 1e-12 * wantPad,
            $"pad plate {result.PadFarads:g9} vs ε₀εr·A/h {wantPad:g9}");
        Assert.Equal(result.TraceFarads + result.PadFarads, result.TotalFarads);
        Assert.Contains(result.Assumptions, a => a.Contains("parallel-plate"));
    }

    // ------------------------------------------------------------------
    // Typed failures — never a garbage number.
    // ------------------------------------------------------------------

    [Fact]
    public void PourNet_NoCenterlines_IsTypedFailure()
    {
        var island = new CopperIsland(0, 1, "L1", Rect(0, 0, 40e-3, 20e-3));
        var net = new CopperNet(1, new[] { island }) { Name = "GND" };
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = new[] { island },
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = new[] { net },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = Array.Empty<TraceCenterline>(),
        };
        var result = TraceCapacitanceExtractor.Extract(board, net);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("area model", result.FailureReason);
    }

    [Fact]
    public void NoAdjacentDielectricGap_IsTypedFailure()
    {
        // A one-copper-layer board with no stackup: no gap exists on either side of L1.
        var island = new CopperIsland(0, 1, "L1", Rect(-100e-3, -100e-3, 100e-3, 100e-3));
        var net = new CopperNet(1, new[] { island }) { Name = "NET1" };
        var board = new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = new[] { island },
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = new[] { net },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
            TraceCenterlines = new[]
            {
                new TraceCenterline(1, new Point2(0, 0), new Point2(40e-3, 0), W),
            },
        };
        var result = TraceCapacitanceExtractor.Extract(board, net);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("reference plane", result.FailureReason);
    }
}
