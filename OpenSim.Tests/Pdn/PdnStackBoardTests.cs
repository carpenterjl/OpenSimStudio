using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Pdn;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pdn;

/// <summary>
/// FU-32 on a board: the stack read off a six-layer board — another rail's plane floating
/// between the power plane and the lower ground, the two grounds stitched — must at low
/// frequency be its plate capacitors; the load's own vias must sit in series with the planes; and
/// the fringe the board's edges carry must be the cross-sections through them.
/// </summary>
public class PdnStackBoardTests
{
    private const double Epsilon0 = 8.8541878128e-12;

    private readonly ITestOutputHelper _output;
    public PdnStackBoardTests(ITestOutputHelper output) => _output = output;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) => PlanePairTests.Rect(x0, y0, x1, y1);

    private static readonly CapacitorModel Cap100n = new()
        { PartName = "100n 0402", CapacitanceFarads = 100e-9, EsrOhms = 0.03, EslHenries = 0.4e-9 };

    internal static readonly BoardStackup SixLayers = new()
    {
        GapThickness = new Dictionary<int, double> { [1] = 0.2e-3, [2] = 0.1e-3, [3] = 0.3e-3, [4] = 0.3e-3, [5] = 0.2e-3 },
        GapPermittivity = new Dictionary<int, double> { [1] = 4.2, [2] = 4.0, [3] = 4.3, [4] = 4.3, [5] = 4.2 },
        GapLossTangent = new Dictionary<int, double> { [1] = 0.02, [2] = 0.015, [3] = 0.02, [4] = 0.02, [5] = 0.02 },
        Source = "test"
    };

    /// <summary>
    /// 100 × 80 mm, six layers: parts on L1 (U1, C1, C2) and L6 (C3); ground planes on L2 and L5
    /// over the whole board, stitched by a 20 mm grid of vias; the 3V3 plane on L3 (80 × 60 mm);
    /// a 1V8 plane on L4 (40 × 60 mm, under the right half of 3V3), with no vias — floating here.
    /// Each part's pads have one via, 0.8 mm away; ground vias run through the board.
    /// </summary>
    internal static (PcbBoard Board, CopperNet Power, CopperNet Ground) SixLayerBoard()
    {
        var islands = new List<CopperIsland>();
        var pads = new List<CopperPad>();
        var vias = new List<Via>();
        var powerIslands = new List<CopperIsland>();
        var groundIslands = new List<CopperIsland>();
        var otherIslands = new List<CopperIsland>();
        var powerVias = new List<ViaBridge>();
        var groundVias = new List<ViaBridge>();
        CopperIsland Add(List<CopperIsland> net, int layer, Polygon2 shape)
        {
            var island = new CopperIsland(islands.Count, layer, $"L{layer}", shape);
            islands.Add(island);
            net.Add(island);
            return island;
        }
        IReadOnlyList<Point2> Hole(double x, double y) =>
            Rect(x - 0.4e-3, y - 0.4e-3, x + 0.4e-3, y + 0.4e-3).Outer.Reverse().ToList();
        var groundHoles = new List<IReadOnlyList<Point2>>();       // where 3V3 vias pass the ground planes
        var powerHoles = new List<IReadOnlyList<Point2>>();        // where ground vias pass 3V3
        var otherHoles = new List<IReadOnlyList<Point2>>();        // where ground vias pass 1V8

        void Part(string refdes, double x, double y, int layer)
        {
            foreach (var (isPower, dx, pin) in new[] { (true, -0.5e-3, "1"), (false, 0.5e-3, "2") })
            {
                var shape = Rect(x + dx - 0.3e-3, y - 0.3e-3, x + dx + 0.3e-3, y + 0.3e-3);
                double viaX = x + dx + (isPower ? -0.8e-3 : 0.8e-3);
                var copper = Rect(Math.Min(x + dx, viaX) - 0.3e-3, y - 0.3e-3, Math.Max(x + dx, viaX) + 0.3e-3, y + 0.3e-3);
                Add(isPower ? powerIslands : groundIslands, layer, copper);
                pads.Add(new CopperPad(layer, new Point2(x + dx, y), shape, 0.6e-3) { ComponentRef = refdes, Pin = pin, PartName = "CAP-0402" });
                var via = new Via(new Point2(viaX, y), 0.3e-3, true);
                vias.Add(via);
                if (isPower)
                {
                    powerVias.Add(new ViaBridge(via, new[] { layer, 3 }));
                    if (layer == 1) groundHoles.Add(Hole(viaX, y)); else { groundHoles.Add(Hole(viaX, y)); otherHoles.Add(Hole(viaX, y)); }
                }
                else
                {
                    groundVias.Add(new ViaBridge(via, new[] { 1, 2, 5, 6 }.Where(l => l == layer || l == 2 || l == 5).ToArray()));
                    powerHoles.Add(Hole(viaX, y));
                }
            }
        }
        Part("U1", 0.030, 0.040, 1);
        Part("C1", 0.045, 0.040, 1);
        Part("C2", 0.070, 0.022, 1);
        Part("C3", 0.020, 0.060, 6);

        // Ground stitching: a 20 mm grid, L2 to L5.
        for (double x = 0.010; x < 0.100; x += 0.020)
            for (double y = 0.010; y < 0.080; y += 0.020)
            {
                var via = new Via(new Point2(x, y), 0.3e-3, true);
                vias.Add(via);
                groundVias.Add(new ViaBridge(via, new[] { 2, 5 }));
                if (x > 0.0101 && x < 0.0899 && y > 0.0101 && y < 0.0699) powerHoles.Add(Hole(x, y));
                if (x > 0.0501 && x < 0.0899 && y > 0.0101 && y < 0.0699) otherHoles.Add(Hole(x, y));
            }

        Add(groundIslands, 2, new Polygon2(Rect(0, 0, 0.100, 0.080).Outer, groundHoles));
        Add(powerIslands, 3, new Polygon2(Rect(0.010, 0.010, 0.090, 0.070).Outer, powerHoles));
        Add(otherIslands, 4, new Polygon2(Rect(0.050, 0.010, 0.090, 0.070).Outer, otherHoles));
        Add(groundIslands, 5, new Polygon2(Rect(0, 0, 0.100, 0.080).Outer, groundHoles));

        var power = new CopperNet(1, powerIslands) { Name = "+3V3", StitchingVias = powerVias };
        var ground = new CopperNet(2, groundIslands) { Name = "GND", StitchingVias = groundVias };
        var other = new CopperNet(3, otherIslands) { Name = "+1V8" };
        var board = new PcbBoard
        {
            Outline = new[] { Rect(0, 0, 0.100, 0.080) },
            Islands = islands,
            Pads = pads,
            Vias = vias,
            Nets = new[] { power, ground, other },
            Layers = Array.Empty<BoardLayer>(),
            Warnings = Array.Empty<string>()
        };
        return (board, power, ground);
    }

    [Fact]
    public void TheBoardsStack_AtLowFrequency_IsItsPlateCapacitors()
    {
        // Between 3V3 and ground at 100 kHz: the L2–L3 cavity over the whole 3V3 plane; under the
        // left half the merged L3–L5 cavity (L4's copper thickness filled with resin); under the
        // right half the floating 1V8 plane, its two cavities in series. The stitched grounds
        // are one conductor, and the ground-to-ground cavity outside 3V3 carries nothing.
        var (board, power, ground) = SixLayerBoard();
        var options = new PdnBoardOptions { Stackup = SixLayers, EdgeFringe = false };
        var model = PdnBoard.Extract(board, power, ground, options);
        foreach (string note in model.Notes) _output.WriteLine(note);
        Assert.Equal(new[] { 2, 3, 4, 5 }, model.StackBoardLayers);
        Assert.Equal(new[] { 1, 1, 1, 1 }, model.StackLayers.Select(l => l.Plates.Count));
        Assert.Equal("+1V8", model.StackLayers[2].Plates[0].Net);
        // 20 grid vias, and the ground vias of the four parts (each through L2 and L5).
        Assert.Equal(20 + 4, model.Ties.Count);
        Assert.All(model.Ties, t => Assert.True(t.UpperLayer == 0 && t.LowerLayer == 3));
        Assert.Equal(new[] { "C1", "C2", "C3" }, model.Capacitors.Select(c => c.RefDes));
        Assert.All(model.Capacitors, c => Assert.Null(c.Problem));
        Assert.Equal(BoardSide.Bottom, model.Capacitors[2].Side);

        var setup = PdnBoard.Setup(board, power, ground, model, "U1", _ => Cap100n, options: options)
            with { MinFrequencyHz = 1e5, MaxFrequencyHz = 2e5, PointsPerDecade = 2, ModeCount = 0 };
        // One permittivity at every frequency (the wideband dielectric's ε′ at 100 kHz is not the
        // stackup's), and a coarse mesh: plate capacitances do not depend on it.
        setup = setup with { Stack = setup.Stack! with { WidebandDielectric = false, MeshEdgeMeters = 8e-3 } };
        // U1, C1, C2 and C3: one 3V3 via each goes on into the stack.
        Assert.Equal(4, setup.Stack!.Ports.Count);
        var result = PdnAnalysis.Solve(setup);
        foreach (string line in result.Describe()) _output.WriteLine(line);
        double f = result.FrequenciesHz[0];
        // The capacitance is the admittance's imaginary part (with a lossy dielectric −1/(ω·Im Z)
        // reads high by 1 + tan²δ).
        double measured = (1 / result.BareImpedance[0]).Imaginary / (2 * Math.PI * f);

        double left = 0.040 * 0.060, right = 0.040 * 0.060;
        double upper = Epsilon0 * 4.0 * (left + right) / 0.1e-3;
        double merged = Epsilon0 / (0.3e-3 / 4.3 + 35e-6 / 4.3 + 0.3e-3 / 4.3) * left;
        double c34 = Epsilon0 * 4.3 * right / 0.3e-3, c45 = Epsilon0 * 4.3 * right / 0.3e-3;
        double expected = upper + merged + c34 * c45 / (c34 + c45);
        _output.WriteLine($"at {f / 1e3:g3} kHz: {measured * 1e9:f6} nF, plate capacitors {expected * 1e9:f6} nF ({measured / expected - 1:e2})");
        Assert.Equal(expected, measured, 1e-5 * expected);
    }

    [Fact]
    public void TheLoadsOwnVias_SitInSeriesWithThePlanes()
    {
        // Without the parts the load sees the stack at its 3V3 via's port and, in series, the
        // loop through its two pads, escape traces and vias down to the ground plane: exactly
        // the decap mounting of its geometry (one via per pad, nothing else carrying current).
        var (board, power, ground) = SixLayerBoard();
        var options = new PdnBoardOptions { Stackup = SixLayers, EdgeFringe = false, LoadPackageHenries = 0.2e-9, LoadPackageOhms = 1e-3 };
        var model = PdnBoard.Extract(board, power, ground, options);
        var setup = PdnBoard.Setup(board, power, ground, model, "U1", _ => Cap100n, options: options)
            with { MinFrequencyHz = 1e6, MaxFrequencyHz = 1e8, PointsPerDecade = 1, ModeCount = 0 };
        // A coarse mesh: the identity holds on any.
        setup = setup with { Stack = setup.Stack! with { MeshEdgeMeters = 8e-3 } };
        var stack = new PlaneStack(setup.Stack! with { MaxFrequencyHz = setup.MaxFrequencyHz });
        int port = Assert.Single(setup.Mounting!.Load.Ports);
        double mounting = DecapMounting.LoopInductance(new MountingGeometry(
            new Point2(0.0295, 0.040), new Point2(0.0287, 0.040), 0.3e-3,
            new Point2(0.0305, 0.040), new Point2(0.0313, 0.040), 0.3e-3,
            0.2e-3 + 17.5e-6, 0.6e-3, 35e-6));
        var frequencies = new[] { 1e6, 1e7, 1e8 };
        var planes = stack.Sweep(frequencies);
        var result = PdnAnalysis.Evaluate(stack, frequencies, planes, setup);
        for (int k = 0; k < frequencies.Length; k++)
        {
            Complex expected = planes[k][port, port] + new Complex(1e-3, 2 * Math.PI * frequencies[k] * (mounting + 0.2e-9));
            _output.WriteLine($"{frequencies[k] / 1e6:g3} MHz: {result.BareImpedance[k]} Ω, planes + mounting + package {expected} Ω");
            Assert.True((result.BareImpedance[k] - expected).Magnitude < 1e-9 * expected.Magnitude);
        }
    }

    [Fact]
    public void TheEdgesFringe_IsTheCrossSectionThroughThem()
    {
        // The four-layer board of PdnAnalysisTests: 3V3 (L3, 50 × 40 mm) over ground (L2, 60 × 40,
        // the board's outline). At x = 5 and 55 mm the ground plane and the board run on 5 mm past
        // 3V3's edge; at y = 0 and 40 mm both planes stop at the board's edge. The probe rounds
        // lengths to a quarter of the board's 1.44 mm (5 mm → 5.04 mm) and the half-width to a
        // power of √2 times it (25 and 20 mm → 23.04 mm). At 100 kHz the bare planes hold their
        // plate capacitance plus the fringe of every edge.
        var (board, power, ground) = PdnAnalysisTestsBoard();
        var stackup = PdnAnalysisTestsStackup();
        var options = new PdnBoardOptions { Stackup = stackup };
        var model = PdnBoard.Extract(board, power, ground, options);
        var setup = PdnBoard.Setup(board, power, ground, model, "U1", _ => null, options: options)
            with { MinFrequencyHz = 1e5, MaxFrequencyHz = 2e5, PointsPerDecade = 2, ModeCount = 0 };
        // One permittivity at every frequency: the wideband dielectric's ε′ at 100 kHz is not the stackup's.
        setup = setup with { Stack = setup.Stack! with { WidebandDielectric = false } };
        var result = PdnAnalysis.Solve(setup);
        foreach (string line in result.Describe()) _output.WriteLine(line);
        var stack = new PlaneStack(setup.Stack! with { MaxFrequencyHz = setup.MaxFrequencyHz });
        foreach (var (fringe, length) in stack.FringedEdges)
            _output.WriteLine($"  {length * 1e3:f2} mm: " + string.Join(", ", fringe.Pairs.Select(p =>
                $"L{p.UpperLayer}–L{p.LowerLayer} {p.FaradsPerMeter * 1e12:f3} pF/m (air {p.AirFaradsPerMeter * 1e12:f3})")));
        double f = result.FrequenciesHz[0];
        // The capacitance is the admittance's imaginary part (with a lossy dielectric −1/(ω·Im Z)
        // reads high by 1 + tan²δ).
        double measured = (1 / result.BareImpedance[0]).Imaginary / (2 * Math.PI * f);

        var section = Enumerable.Range(1, 4).Select(l => new SectionLayer(35e-6, l < 4 ? stackup.GapThicknessOf(l) : 0,
            l < 4 ? stackup.PermittivityOf(l) : 1)).ToArray();
        double thickness = 4 * 35e-6 + 0.2e-3 + 0.1e-3 + 1.0e-3, halfWidth = 16 * thickness, reach = 14 * thickness / 4;
        var overGround = EdgeCrossSection.FringeOf(section,
            new[] { new EdgeCrossSection.EdgePlate(1, 0, reach), new EdgeCrossSection.EdgePlate(2, 1, 0) },
            Array.Empty<EdgeCrossSection.EdgeGround>(), reach, halfWidth).Pairs.Single().FaradsPerMeter;
        var flush = EdgeCrossSection.FringeOf(section,
            new[] { new EdgeCrossSection.EdgePlate(1, 0, 0), new EdgeCrossSection.EdgePlate(2, 1, 0) },
            Array.Empty<EdgeCrossSection.EdgeGround>(), 0, halfWidth).Pairs.Single().FaradsPerMeter;
        double plates = Epsilon0 * 4.0 * 0.050 * 0.040 / 0.1e-3;
        double expected = plates + 2 * 0.040 * overGround + 2 * 0.050 * flush;
        _output.WriteLine($"fringe: {overGround * 1e12:f3} pF/m where ground runs on, {flush * 1e12:f3} pF/m where both stop; " +
                          $"plates {plates * 1e12:f3} pF, with the fringe {expected * 1e12:f4} pF, measured {measured * 1e12:f4} pF");
        Assert.Equal(expected, measured, 1e-6 * expected);
    }

    // The board and stackup of PdnAnalysisTests, reached through its private helpers' public twins.
    private static (PcbBoard, CopperNet, CopperNet) PdnAnalysisTestsBoard() => PdnAnalysisTests.FourLayerBoard();
    private static BoardStackup PdnAnalysisTestsStackup() => PdnAnalysisTests.FourLayerStackup;
}
