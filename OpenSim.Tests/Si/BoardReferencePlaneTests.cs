using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Si;

namespace OpenSim.Tests.Si;

/// <summary>
/// The board coupled-line extraction over the reference planes the board really has.
/// An inner-layer pair between two planes must come out as a stripline — the impedance a
/// designer would compute by hand for it — and not as the surface microstrip (air above)
/// every layer used to be solved as, which read about 75 % high.
/// </summary>
public class BoardReferencePlaneTests
{
    private const double W = 0.15e-3;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    private static readonly Polygon2 Plane = Rect(-20e-3, -20e-3, 60e-3, 20e-3);

    /// <summary>Two parallel 40 mm traces on <paramref name="traceLayer"/>, 0.15 mm apart
    /// edge to edge, with the given other-net copper on other layers.</summary>
    private static (PcbBoard Board, List<CopperNet> Nets) PairBoard(int traceLayer,
        params (int Layer, Polygon2 Shape)[] otherCopper)
    {
        var islands = new List<CopperIsland>();
        var nets = new List<CopperNet>();
        var centerlines = new List<TraceCenterline>();
        foreach (double y in new[] { -0.15e-3, 0.15e-3 })
        {
            var island = new CopperIsland(islands.Count, traceLayer, $"L{traceLayer}",
                Rect(0, y - W / 2, 40e-3, y + W / 2));
            islands.Add(island);
            nets.Add(new CopperNet(nets.Count + 1, new[] { island }) { Name = $"SIG{nets.Count + 1}" });
            centerlines.Add(new TraceCenterline(traceLayer, new Point2(0, y), new Point2(40e-3, y), W));
        }
        foreach (var (layer, shape) in otherCopper)
            islands.Add(new CopperIsland(islands.Count, layer, $"L{layer}", shape));
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
        };
        return (board, nets);
    }

    /// <summary>εr 4.4 everywhere, 0.2 mm gaps: L2 sits centred between L1 and L3.</summary>
    private static BoardCoupledOptions Options(int gaps) => new()
    {
        Stackup = new BoardStackup
        {
            GapThickness = Enumerable.Range(1, gaps).ToDictionary(g => g, _ => 0.2e-3),
            GapPermittivity = Enumerable.Range(1, gaps).ToDictionary(g => g, _ => 4.4),
            GapLossTangent = Enumerable.Range(1, gaps).ToDictionary(g => g, _ => 0.0),
            Source = "from the stackup settings",
        },
    };

    private static double EllipticKOfComplement(double complementaryModulus)
    {
        double a = 1, b = complementaryModulus;
        for (int i = 0; i < 60 && Math.Abs(a - b) > 1e-16 * a; i++)
            (a, b) = (0.5 * (a + b), Math.Sqrt(a * b));
        return Math.PI / (2 * a);
    }

    /// <summary>Cohn's exact zero-thickness symmetric stripline impedance.</summary>
    private static double CohnZ0(double w, double b, double epsR)
    {
        double x = Math.PI * w / (2 * b);
        return 30 * Math.PI / Math.Sqrt(epsR)
            * EllipticKOfComplement(Math.Tanh(x)) / EllipticKOfComplement(1 / Math.Cosh(x));
    }

    [Fact]
    public void InnerLayerPair_BetweenTwoPlanes_IsExtractedAsStripline()
    {
        var (board, nets) = PairBoard(2, (1, Plane), (3, Plane));
        var result = BoardCoupledExtractor.Extract(board, nets, Options(2));

        Assert.Null(result.FailureReason);
        var section = result.CrossSection!;
        Assert.True(section.TopGround);
        Assert.Equal(2, section.Stackup.Layers.Count);
        Assert.Equal(0, section.MetalInterface);
        Assert.Contains(result.Assumptions, a => a.StartsWith("Stripline: trace layer L2"));

        // Homogeneous dielectric: no far-end crosstalk (K_L = K_C), ε_eff = εr.
        var c = result.Rlgc!.CapacitanceFaradsPerMeter;
        var l = result.Rlgc.InductanceHenriesPerMeter;
        double kC = -c[0, 1] / c[0, 0], kL = l[0, 1] / l[0, 0];
        Assert.True(Math.Abs(kL - kC) < 1e-3 * kC, $"K_L {kL:g6} vs K_C {kC:g6}");
        Assert.Equal(4.4, c[0, 0] / result.Rlgc.AirCapacitanceFaradsPerMeter[0, 0], 0.005 * 4.4);
    }

    [Fact]
    public void InnerLayerTrace_ImpedanceIsCohns_NotTheOldMicrostripNumber()
    {
        // One isolated 0.15 mm trace centred in b = 0.4 mm, εr 4.4. Zero-thickness
        // stripline (the model's stated regime): Cohn gives 55.6 Ω. The old air-above
        // microstrip on the 0.2 mm gap read about 80 Ω.
        var (board, nets) = PairBoard(2, (1, Plane), (3, Plane));
        var capacitance = TraceCapacitanceExtractor.Extract(board, nets[0], Options(2));
        Assert.Null(capacitance.FailureReason);
        var group = Assert.Single(capacitance.Groups);

        const double c0 = 299792458.0;
        double z0 = 1 / (c0 * Math.Sqrt(group.CapacitanceFaradsPerMeter * group.AirCapacitanceFaradsPerMeter));
        double cohn = CohnZ0(W, 0.4e-3, 4.4);
        Assert.InRange(cohn, 55, 56);
        Assert.Equal(cohn, z0, 0.02 * cohn);

        // What this layer used to be solved as: the gap below as a slab, air above.
        var old = RlgcExtractor.Extract(new CoupledLineCrossSection(
            new OpenSim.Rf.Layered.LayeredStackup(new[]
                { new OpenSim.Rf.Layered.LayeredStackup.Layer(4.4, 0, 0.2e-3) }), 0,
            new[] { TraceCrossSection.Copper(0, W) }));
        double oldZ0 = Math.Sqrt(old.InductanceHenriesPerMeter[0, 0] / old.CapacitanceFaradsPerMeter[0, 0]);
        Assert.True(oldZ0 > 1.35 * z0, $"old microstrip Z0 {oldZ0:g4} Ω vs stripline {z0:g4} Ω");
        Assert.Equal(4.4, group.EffectivePermittivity, 0.005 * 4.4);
    }

    [Fact]
    public void OuterLayerPair_OverAPlane_IsStillTheSurfaceMicrostrip()
    {
        // Top-layer traces over an L2 plane: the open-top single slab, bit for bit what
        // the wizard solves for the same geometry.
        var (board, nets) = PairBoard(1, (2, Plane));
        var result = BoardCoupledExtractor.Extract(board, nets, Options(1));

        Assert.Null(result.FailureReason);
        var section = result.CrossSection!;
        Assert.False(section.TopGround);
        Assert.Single(section.Stackup.Layers);
        var wizard = RlgcExtractor.Extract(new CoupledLineCrossSection(section.Stackup, 0, section.Traces));
        Assert.Equal(wizard.CapacitanceFaradsPerMeter[0, 1], result.Rlgc!.CapacitanceFaradsPerMeter[0, 1]);
        Assert.Equal(wizard.InductanceHenriesPerMeter[0, 0], result.Rlgc.InductanceHenriesPerMeter[0, 0]);
        Assert.Contains(result.Assumptions, a => a.Contains("the reference plane is L2"));
    }

    [Fact]
    public void RoutingLayerBetweenTraceAndPlane_IsPassedThroughAsDielectric()
    {
        // L1 traces; L2 has only a small piece of copper elsewhere; L3 is the plane.
        var (board, nets) = PairBoard(1, (2, Rect(50e-3, 10e-3, 55e-3, 15e-3)), (3, Plane));
        var result = BoardCoupledExtractor.Extract(board, nets, Options(2));

        Assert.Null(result.FailureReason);
        var section = result.CrossSection!;
        Assert.False(section.TopGround);
        Assert.Equal(2, section.Stackup.Layers.Count);            // both gaps, metal on top
        Assert.Equal(1, section.MetalInterface);
        Assert.Contains(result.Assumptions, a => a.Contains("Reference plane L3 below trace layer L1"));
    }

    [Fact]
    public void NoPlaneUnderTheRuns_IsATypedFailure()
    {
        // The only other copper covers a quarter of the run.
        var (board, nets) = PairBoard(1, (2, Rect(-5e-3, -5e-3, 10e-3, 5e-3)));
        var result = BoardCoupledExtractor.Extract(board, nets, Options(1));

        Assert.NotNull(result.FailureReason);
        Assert.Contains("no copper layer has copper under the traces on L1", result.FailureReason);
        Assert.Contains("reference plane", result.FailureReason);
    }

    [Fact]
    public void TheNetsOwnCopper_IsNotItsReference()
    {
        // The selected nets also own large copper on L2 (a fixture a real board can
        // match: a net with a pour on the next layer). That copper is not a plane for
        // its own traces; the real plane on L3 is.
        var (board, nets) = PairBoard(1, (3, Plane));
        var ownPour = new CopperIsland(board.Islands.Count, 2, "L2", Plane);
        var withPour = new PcbBoard
        {
            Outline = board.Outline,
            Islands = board.Islands.Append(ownPour).ToList(),
            Pads = board.Pads, Vias = board.Vias, Layers = board.Layers, Warnings = board.Warnings,
            TraceCenterlines = board.TraceCenterlines,
            Nets = new[]
            {
                new CopperNet(1, new[] { nets[0].Islands[0], ownPour }) { Name = "SIG1" },
                nets[1],
            },
        };
        var result = BoardCoupledExtractor.Extract(withPour, withPour.Nets.ToList(), Options(2));

        Assert.Null(result.FailureReason);
        Assert.Contains(result.Assumptions, a => a.Contains("Reference plane L3 below trace layer L1"));
    }
}
