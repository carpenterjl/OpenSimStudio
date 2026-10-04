using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;

namespace OpenSim.Tests.Si;

/// <summary>
/// One stackup, read the same way by every consumer. The stackup panel hands the board
/// workflow a single <see cref="NetMeshOptions"/>; its <see cref="NetMeshOptions.Stackup"/>
/// is what the z model, the trace-chain builder, the DC network, the coupled-line and
/// capacitance extractors and the board antenna read. Each test edits ONE number in that
/// object and requires the consumer's result to move by the physical amount — so a
/// consumer that kept a private default (35 µm copper, 1.6 mm / (N−1) gaps, εr 4.4)
/// fails here.
/// </summary>
public class BoardStackupTests
{
    private const double Sigma = 5.8e7;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    /// <summary>The object the application builds from the stackup panel: one copper row
    /// per layer, one gap row per adjacent pair.</summary>
    private static NetMeshOptions Panel(double[] copperMicrons,
        (double Mm, double EpsR, double TanD)[] gaps) => new()
    {
        CopperThickness = 35e-6,
        DefaultDielectricThickness = 1.6e-3,
        LayerThickness = copperMicrons.Select((t, i) => (i + 1, t * 1e-6)).ToDictionary(x => x.Item1, x => x.Item2),
        DielectricGapThickness = gaps.Select((g, i) => (i + 1, g.Mm * 1e-3)).ToDictionary(x => x.Item1, x => x.Item2),
        DielectricGapPermittivity = gaps.Select((g, i) => (i + 1, g.EpsR)).ToDictionary(x => x.Item1, x => x.Item2),
        DielectricGapLossTangent = gaps.Select((g, i) => (i + 1, g.TanD)).ToDictionary(x => x.Item1, x => x.Item2),
    };

    /// <summary>A Gerber-like board (no file stackup): nets of straight traces on the given
    /// layers, each layer one large island, plus bare islands up to <paramref name="copperLayers"/>.</summary>
    private static (PcbBoard Board, List<CopperNet> Nets) Board(int copperLayers,
        params TraceCenterline[] traces)
    {
        var islands = new List<CopperIsland>();
        var nets = new List<CopperNet>();
        foreach (var t in traces)
        {
            double pad = t.Width / 2;
            var island = new CopperIsland(islands.Count, t.LayerOrder, $"L{t.LayerOrder}",
                Rect(Math.Min(t.Start.X, t.End.X) - pad, Math.Min(t.Start.Y, t.End.Y) - pad,
                     Math.Max(t.Start.X, t.End.X) + pad, Math.Max(t.Start.Y, t.End.Y) + pad));
            islands.Add(island);
            nets.Add(new CopperNet(nets.Count + 1, new[] { island }) { Name = $"NET{nets.Count + 1}" });
        }
        for (int layer = 1; layer <= copperLayers; layer++)
        {
            if (traces.Any(t => t.LayerOrder == layer)) continue;
            var plane = new CopperIsland(islands.Count, layer, $"L{layer}", Rect(-50e-3, -50e-3, 50e-3, 50e-3));
            islands.Add(plane);
            nets.Add(new CopperNet(nets.Count + 1, new[] { plane }) { Name = $"PLANE_L{layer}" });
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
            TraceCenterlines = traces,
        };
        return (board, nets);
    }

    // ------------------------------------------------------------------
    // The object itself.
    // ------------------------------------------------------------------

    [Fact]
    public void Stackup_ReadsTheRows_AndFallsBackOnlyWhereThereIsNoRow()
    {
        var stackup = Panel(new[] { 70.0, 18.0 }, new[] { (0.2, 3.5, 0.004) }).Stackup;

        Assert.Equal(70e-6, stackup.CopperThicknessOf(1), 12);
        Assert.Equal(18e-6, stackup.CopperThicknessOf(2), 12);
        Assert.Equal(35e-6, stackup.CopperThicknessOf(3), 12);    // no row
        Assert.Equal(0.2e-3, stackup.GapThicknessOf(1), 12);
        Assert.Equal(3.5, stackup.PermittivityOf(1), 12);
        Assert.Equal(0.004, stackup.LossTangentOf(1), 12);
        Assert.Equal(1.6e-3, stackup.GapThicknessOf(2), 12);      // no row
        Assert.Equal(4.4, stackup.PermittivityOf(2), 12);
        Assert.Equal(0.02, stackup.LossTangentOf(2), 12);
    }

    [Fact]
    public void FromBoard_IsTheFilesStackup_CompletedByAnEvenSplit()
    {
        var (bare, _) = Board(4, new TraceCenterline(1, new Point2(0, 0), new Point2(10e-3, 0), 0.2e-3));
        var defaults = BoardStackup.FromBoard(bare);
        Assert.Equal(1.6e-3 / 3, defaults.GapThicknessOf(1), 12);
        Assert.Equal(1.6e-3 / 3, defaults.GapThicknessOf(3), 12);
        Assert.Equal(4.4, defaults.PermittivityOf(2), 12);
        Assert.Equal("default", defaults.Source);

        var withFile = new PcbBoard
        {
            Outline = bare.Outline, Islands = bare.Islands, Pads = bare.Pads, Vias = bare.Vias,
            Nets = bare.Nets, Layers = bare.Layers, Warnings = bare.Warnings,
            Stackup = new PcbStackupSettings
            {
                CopperLayerThicknesses = new[] { 70e-6, 35e-6, 35e-6, 70e-6 },
                DielectricGapThicknesses = new[] { 0.1e-3, 1.2e-3, 0.1e-3 },
                DielectricGapPermittivities = new[] { 3.8, 4.5, 3.8 },
                DielectricGapLossTangents = new[] { 0.01, 0.02, 0.01 },
            },
        };
        var file = BoardStackup.FromBoard(withFile);
        Assert.Equal(70e-6, file.CopperThicknessOf(4), 12);
        Assert.Equal(1.2e-3, file.GapThicknessOf(2), 12);
        Assert.Equal(3.8, file.PermittivityOf(3), 12);
        Assert.Equal(0.01, file.LossTangentOf(1), 12);
        Assert.Equal("from the board stackup", file.Source);
    }

    // ------------------------------------------------------------------
    // Copper thickness: z model, DC network, coupled-line R.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(35.0, 9.852e-3)]
    [InlineData(70.0, 4.926e-3)]
    public void StraightNet_ResistanceFollowsTheLayerRow(double copperMicrons, double expectedOhms)
    {
        // 20 × 1 mm, σ 5.8e7: R = ℓ/(σ·w·t) — 9.85 mΩ at 1 oz, 4.93 mΩ at 2 oz.
        var panel = Panel(new[] { copperMicrons, 35.0 }, new[] { (1.6, 4.4, 0.02) });
        var trace = new TraceCenterline(1, new Point2(0, 0), new Point2(20e-3, 0), 1e-3);

        var (layerZ, _) = NetMesher.BuildStackupZ(1, 2, panel);
        Assert.Equal(copperMicrons * 1e-6, layerZ[1].zHi - layerZ[1].zLo, 12);

        var graph = TraceChainBuilder.BuildGraph(new[] { trace }, Array.Empty<ViaBridge>(), panel);
        var result = TraceResistanceNetwork.Solve(graph,
            new[] { new ChainTerminal(trace.Start, 1), new ChainTerminal(trace.End, 1) }, Sigma);
        Assert.Null(result.FailureReason);
        double r = Assert.Single(result.Pairs).ResistanceOhms!.Value;
        Assert.Equal(20e-3 / (Sigma * 1e-3 * copperMicrons * 1e-6), r, 1e-12);
        Assert.Equal(expectedOhms, r, 1e-6);
    }

    [Fact]
    public void CoupledExtraction_TakesTheTraceLayersCopper_NotAGlobalValue()
    {
        var a = new TraceCenterline(1, new Point2(0, 0), new Point2(30e-3, 0), 0.2e-3);
        var b = new TraceCenterline(1, new Point2(0, 0.5e-3), new Point2(30e-3, 0.5e-3), 0.2e-3);
        var (board, nets) = Board(2, a, b);
        var pair = nets.Take(2).ToList();

        double RdcPerMeter(double l1Microns)
        {
            var result = BoardCoupledExtractor.Extract(board, pair, new BoardCoupledOptions
            {
                Stackup = Panel(new[] { l1Microns, 35.0 }, new[] { (0.2, 4.4, 0.02) }).Stackup,
                CopperThicknessMeters = 35e-6,                    // the old global: must not win
            });
            Assert.Null(result.FailureReason);
            Assert.Equal(l1Microns * 1e-6, result.CrossSection!.Traces[0].ThicknessMeters, 12);
            return result.Rlgc!.ResistancePerMeter(0, 0);
        }

        double r35 = RdcPerMeter(35), r70 = RdcPerMeter(70);
        Assert.Equal(1 / (Sigma * 0.2e-3 * 35e-6), r35, r35 * 1e-9);
        Assert.Equal(r35 / 2, r70, r35 * 1e-9);
    }

    // ------------------------------------------------------------------
    // Dielectric: capacitance and the coupled-line substrate.
    // ------------------------------------------------------------------

    /// <summary>Hammerstad–Jensen (1980) zero-thickness microstrip C′ [F/m].</summary>
    private static double HammerstadJensenC(double w, double h, double epsR)
    {
        const double c0 = 299792458.0, eta0 = 376.730313668;
        double u = w / h;
        double f = 6 + (2 * Math.PI - 6) * Math.Exp(-Math.Pow(30.666 / u, 0.7528));
        double z01 = eta0 / (2 * Math.PI) * Math.Log(f / u + Math.Sqrt(1 + 4 / (u * u)));
        double a = 1 + Math.Log((Math.Pow(u, 4) + Math.Pow(u / 52, 2)) / (Math.Pow(u, 4) + 0.432)) / 49
                     + Math.Log(1 + Math.Pow(u / 18.1, 3)) / 18.7;
        double b = 0.564 * Math.Pow((epsR - 0.9) / (epsR + 3), 0.053);
        double epsEff = (epsR + 1) / 2 + (epsR - 1) / 2 * Math.Pow(1 + 10 / u, -a * b);
        return epsEff / (c0 * z01);
    }

    [Fact]
    public void TraceCapacitance_FollowsThePanelGap_ByTheHammerstadJensenRatio()
    {
        // 4-layer Gerber board (no file stackup), 50 mm × 0.2 mm trace on L1. The old
        // code priced it over 1.6 mm / 3 = 0.533 mm whatever the panel said.
        const double w = 0.2e-3, length = 50e-3;
        var (board, nets) = Board(4, new TraceCenterline(1, new Point2(0, 0), new Point2(length, 0), w));

        double Capacitance(double gapMm, double epsR)
        {
            var result = TraceCapacitanceExtractor.Extract(board, nets[0], new BoardCoupledOptions
            {
                Stackup = Panel(new[] { 35.0, 35, 35, 35 },
                    new[] { (gapMm, epsR, 0.02), (0.9, 4.4, 0.02), (0.2, 4.4, 0.02) }).Stackup,
            });
            Assert.Null(result.FailureReason);
            Assert.Contains(result.Assumptions, s => s.Contains($"h {gapMm:g3} mm") && s.Contains($"εr {epsR:g3}"));
            return result.TotalFarads;
        }

        double thin = Capacitance(0.2, 4.2), evenSplit = Capacitance(1.6 / 3, 4.4);

        // Each against the closed form (BEM vs H–J: a few percent), and the ratio tighter.
        Assert.Equal(HammerstadJensenC(w, 0.2e-3, 4.2) * length, thin, 0.05 * thin);
        Assert.Equal(HammerstadJensenC(w, 1.6e-3 / 3, 4.4) * length, evenSplit, 0.05 * evenSplit);
        double expectedRatio = HammerstadJensenC(w, 0.2e-3, 4.2) / HammerstadJensenC(w, 1.6e-3 / 3, 4.4);
        Assert.Equal(expectedRatio, thin / evenSplit, 0.05 * expectedRatio);
        Assert.True(thin / evenSplit > 1.4, $"C must rise by >40 % for the thin gap; ratio {thin / evenSplit:g4}");

        // With no stackup handed in, the board's own (here: none ⇒ even split, εr 4.4).
        var fallback = TraceCapacitanceExtractor.Extract(board, nets[0]);
        Assert.Equal(evenSplit, fallback.TotalFarads, evenSplit * 1e-12);
    }

    [Fact]
    public void CoupledExtraction_SubstrateIsThePanelGap()
    {
        var a = new TraceCenterline(1, new Point2(0, 0), new Point2(30e-3, 0), 0.2e-3);
        var b = new TraceCenterline(1, new Point2(0, 0.5e-3), new Point2(30e-3, 0.5e-3), 0.2e-3);
        var (board, nets) = Board(4, a, b);

        var result = BoardCoupledExtractor.Extract(board, nets.Take(2).ToList(), new BoardCoupledOptions
        {
            Stackup = Panel(new[] { 35.0, 35, 35, 35 },
                new[] { (0.11, 3.66, 0.0037), (1.2, 4.4, 0.02), (0.11, 3.66, 0.0037) }).Stackup,
        });

        Assert.Null(result.FailureReason);
        var slab = Assert.Single(result.CrossSection!.Stackup.Layers);
        Assert.Equal(0.11e-3, slab.ThicknessMeters, 12);
        Assert.Equal(3.66, slab.RelativePermittivity, 12);
        Assert.Equal(0.0037, slab.LossTangent, 12);
    }

    // ------------------------------------------------------------------
    // Board antenna: which layer is the ground.
    // ------------------------------------------------------------------

    private static PcbBoard AntennaBoard(Polygon2 patch, params (int Layer, Polygon2 Shape)[] others)
    {
        var islands = new List<CopperIsland> { new(0, 1, "L1", patch) };
        foreach (var (layer, shape) in others)
            islands.Add(new CopperIsland(islands.Count, layer, $"L{layer}", shape));
        return new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = islands,
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = islands.Select((i, k) => new CopperNet(k + 1, new[] { i })).ToList(),
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>(),
        };
    }

    private static readonly BoardStackup FourLayer = Panel(new[] { 35.0, 35, 35, 35 },
        new[] { (0.2, 4.2, 0.015), (1.0, 4.6, 0.02), (0.3, 3.9, 0.01) }).Stackup;

    [Fact]
    public void Antenna_TopNetOverAnL2Pour_IsGroundedOnL2_NotTheBottomLayer()
    {
        var patch = Rect(0, 0, 10e-3, 8e-3);
        var board = AntennaBoard(patch,
            (2, Rect(-20e-3, -20e-3, 30e-3, 30e-3)),              // full pour under the patch
            (3, Rect(0, 0, 1e-3, 1e-3)),
            (4, Rect(-20e-3, -20e-3, 30e-3, 30e-3)));

        var r = BoardAntennaStackup.Resolve(board, 1, FourLayer, patch);

        Assert.True(r.IsSingleSlabTop);
        var slab = Assert.Single(r.Stackup.Layers);
        Assert.Equal(0.2e-3, slab.ThicknessMeters, 12);           // the L1–L2 gap, not 1.5 mm
        Assert.Equal(4.2, slab.RelativePermittivity, 12);
        Assert.Equal(0.015, slab.LossTangent, 12);
        Assert.Contains(r.Assumptions, a => a.Contains("L2 is the nearest layer below the net"));
    }

    [Fact]
    public void Antenna_SkipsALayerThatDoesNotCoverTheNet()
    {
        var patch = Rect(0, 0, 10e-3, 8e-3);
        var board = AntennaBoard(patch,
            (2, Rect(0, 0, 5e-3, 8e-3)),                          // half the footprint: routing, not a plane
            (3, Rect(-20e-3, -20e-3, 30e-3, 30e-3)),
            (4, Rect(-20e-3, -20e-3, 30e-3, 30e-3)));

        var r = BoardAntennaStackup.Resolve(board, 1, FourLayer, patch);

        // Ground-up: gap 2 (1.0 mm, εr 4.6) then gap 1 (0.2 mm, εr 4.2); metal on top.
        Assert.Equal(2, r.Stackup.Layers.Count);
        Assert.Equal(1.0e-3, r.Stackup.Layers[0].ThicknessMeters, 12);
        Assert.Equal(0.2e-3, r.Stackup.Layers[1].ThicknessMeters, 12);
        Assert.True(r.IsCoplanarTop);
        Assert.Contains(r.Assumptions, a => a.Contains("L3 is the nearest layer below the net"));
    }

    [Fact]
    public void Antenna_NoCoveringLayer_FallsBackToTheBottomLayer_AndSaysSo()
    {
        var patch = Rect(0, 0, 10e-3, 8e-3);
        var board = AntennaBoard(patch,
            (2, Rect(20e-3, 20e-3, 21e-3, 21e-3)),
            (3, Rect(20e-3, 20e-3, 21e-3, 21e-3)),
            (4, Rect(20e-3, 20e-3, 21e-3, 21e-3)));

        var r = BoardAntennaStackup.Resolve(board, 1, FourLayer, patch);

        Assert.Equal(3, r.Stackup.Layers.Count);
        Assert.Contains(r.Assumptions, a => a.Contains("no copper layer below L1 lies under the net's footprint")
                                            && a.Contains("BOTTOM layer (L4)"));
    }

    [Fact]
    public void Antenna_WithoutAFootprint_KeepsTheBottomLayerGround()
    {
        var patch = Rect(0, 0, 10e-3, 8e-3);
        var board = AntennaBoard(patch, (2, Rect(-20e-3, -20e-3, 30e-3, 30e-3)), (3, patch), (4, patch));

        var r = BoardAntennaStackup.Resolve(board, 1, FourLayer);

        Assert.Equal(3, r.Stackup.Layers.Count);
        Assert.Contains(r.Assumptions, a => a.Contains("the BOTTOM layer (L4) is modelled as the infinite PEC ground plane"));
    }
}
