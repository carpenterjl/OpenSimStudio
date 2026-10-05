using System.Globalization;
using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Extraction;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using Xunit.Abstractions;

namespace OpenSim.Tests.Extraction;

/// <summary>
/// Board-level extraction on top of the filament solve, the net capacitance matrix, and the
/// SPICE and Touchstone files read back.
/// </summary>
public class ExtractionTests
{
    private const double Sigma = 5.8e7;
    private readonly ITestOutputHelper _output;
    public ExtractionTests(ITestOutputHelper output) => _output = output;

    private static TraceSegment3D Bar(double x0, double y0, double x1, double y1, double z = 0, double w = 0.3e-3) =>
        new(new Vector3D(x0, y0, z), new Vector3D(x1, y1, z), w, 35e-6);

    [Fact]
    public void AChainAtLowFrequency_IsThePartialInductanceComposer()
    {
        // An L-shaped route with a via down: at 10 Hz every section is one filament.
        var chain = new List<TraceSegment3D>
        {
            Bar(0, 0, 8e-3, 0), Bar(8e-3, 0, 8e-3, 5e-3),
            new(new Vector3D(8e-3, 5e-3, 0), new Vector3D(8e-3, 5e-3, -0.4e-3), 0.3e-3, 25e-6, SegmentProfile.RoundWire)
        };
        var result = LoopExtraction.Chain(chain, new[] { 10.0 });
        Assert.Equal(3, result.Filaments);
        double r = 13e-3 / (Sigma * 0.3e-3 * 35e-6) + 0.4e-3 / (Sigma * Math.PI * 0.15e-3 * 0.15e-3);
        Assert.Equal(r, result.Points[0].Resistance(0, 0), 1e-9 * r);
        // The composer takes the wire at its GMD, the filament solve as the square of its
        // area: the same to a part in a thousand of this chain.
        double composed = new LoopComposer().Compose(chain).LoopInductance;
        _output.WriteLine($"L {result.Points[0].Inductance(0, 0) * 1e9:f4} nH, composer {composed * 1e9:f4} nH");
        Assert.Equal(composed, result.Points[0].Inductance(0, 0), 2e-3 * composed);
    }

    [Fact]
    public void OutAndBackOnTwoTraces_LosesInductanceAndGainsResistanceWithFrequency()
    {
        var go = new[] { Bar(0, 0, 20e-3, 0, w: 1e-3) };
        var back = new[] { Bar(20e-3, 1.3e-3, 0, 1.3e-3, w: 1e-3) };
        var result = LoopExtraction.ChainAndReturn(go, back, new[] { 10.0, 1e6, 30e6 });
        foreach (string line in result.Describe()) _output.WriteLine(line);

        double dc = new MutualCouplingAnalyzer().Analyze(go, back).LoopInductanceHenries;
        Assert.Equal(dc, result.Points[0].Inductance(0, 0), 1e-6 * dc);
        Assert.Equal(2 * 20e-3 / (Sigma * 1e-3 * 35e-6), result.Points[0].Resistance(0, 0), 1e-11);
        // The currents draw toward the facing edges: less loop area, more loss.
        Assert.True(result.Points[1].Inductance(0, 0) < result.Points[0].Inductance(0, 0));
        Assert.True(result.Points[2].Inductance(0, 0) < result.Points[1].Inductance(0, 0));
        Assert.True(result.Points[2].Resistance(0, 0) > 1.5 * result.Points[0].Resistance(0, 0));
        // And it does not matter which way round the return was drawn.
        var reversed = LoopExtraction.ChainAndReturn(go, new[] { Bar(0, 1.3e-3, 20e-3, 1.3e-3, w: 1e-3) }, new[] { 1e6 });
        // (to the five digits the bar kernel's quadrature holds between thin strips)
        Assert.Equal(result.Points[1].Inductance(0, 0), reversed.Points[0].Inductance(0, 0), 2e-5 * dc);
    }

    [Fact]
    public void AReturnThroughAPour_GoesRoundASlot()
    {
        // A 6 mm trace 0.3 mm over a pour, and the same with a slot cut across under it.
        double z = 0.3e-3 + 35e-6;
        var chain = new[] { Bar(0, 0, 6e-3, 0, z, 0.4e-3) };
        var solid = new Polygon2(new[] { new Point2(-3e-3, -3e-3), new Point2(9e-3, -3e-3), new Point2(9e-3, 3e-3), new Point2(-3e-3, 3e-3) });
        var slotted = new Polygon2(solid.Outer, new IReadOnlyList<Point2>[]
            { new[] { new Point2(2.7e-3, -2e-3), new Point2(2.7e-3, 2e-3), new Point2(3.3e-3, 2e-3), new Point2(3.3e-3, -2e-3) } });
        var options = new LoopExtractionOptions { PlanePitchMeters = 0.3e-3, PlaneMarginMeters = 3e-3 };
        var f = new[] { 100e6 };
        var a = LoopExtraction.ChainOverPlane(chain, new[] { solid }, (-35e-6, 0), f, options);
        var b = LoopExtraction.ChainOverPlane(chain, new[] { slotted }, (-35e-6, 0), f, options);
        _output.WriteLine($"over the solid pour {a.Points[0].Inductance(0, 0) * 1e9:f3} nH, over the slot {b.Points[0].Inductance(0, 0) * 1e9:f3} nH ({a.Filaments} filaments)");
        Assert.True(b.Points[0].Inductance(0, 0) > 1.3 * a.Points[0].Inductance(0, 0));

        // No copper under an end: refused.
        var ex = Assert.Throws<InvalidOperationException>(() => LoopExtraction.ChainOverPlane(
            new[] { Bar(0, 0, 14e-3, 0, z, 0.4e-3) }, new[] { solid }, (-35e-6, 0), f, options));
        Assert.Contains("no copper under an end", ex.Message);
    }

    // ------------------------------------------------------------------ capacitance

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    [Fact]
    public void TheNetCapacitanceMatrix_IsEachNetsOwnPlusWhatNeighboursShare()
    {
        // Three 30 mm traces on L1 over a plane on L2: A and B 0.2 mm apart, C far away.
        const double w = 0.3e-3, length = 30e-3, gap = 0.2e-3;
        var islands = new List<CopperIsland>();
        var traces = new List<TraceCenterline>();
        var nets = new List<CopperNet>();
        void Net(string name, double y)
        {
            var island = new CopperIsland(islands.Count, 1, "L1", Rect(0, y - w / 2, length, y + w / 2));
            islands.Add(island);
            traces.Add(new TraceCenterline(1, new Point2(0, y), new Point2(length, y), w));
            nets.Add(new CopperNet(nets.Count + 1, new[] { island }) { Name = name });
        }
        Net("A", 0); Net("B", w + gap); Net("C", 10e-3);
        var plane = new CopperIsland(islands.Count, 2, "L2", Rect(-5e-3, -5e-3, 35e-3, 15e-3));
        islands.Add(plane);
        nets.Add(new CopperNet(nets.Count + 1, new[] { plane }) { Name = "GND" });
        var board = new PcbBoard
        {
            Outline = new[] { Rect(-5e-3, -5e-3, 35e-3, 15e-3) }, Islands = islands, Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(), Nets = nets, Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>(),
            TraceCenterlines = traces
        };
        var stackup = new BoardStackup
        {
            GapThickness = new Dictionary<int, double> { [1] = 0.2e-3 },
            GapPermittivity = new Dictionary<int, double> { [1] = 4.2 },
            GapLossTangent = new Dictionary<int, double> { [1] = 0.0 }
        };

        var matrix = NetCapacitance.Extract(board, nets.Take(3).ToList(), stackup);
        for (int i = 0; i < 3; i++)
            _output.WriteLine($"{matrix.Nets[i]}: " + string.Join("  ", Enumerable.Range(0, 3).Select(j => $"{matrix.Farads[i, j] * 1e12,8:f4} pF")));

        // The pair's mutual, from the cross-section solved here.
        var layers = new LayeredStackup(new[] { new LayeredStackup.Layer(4.2, 0, 0.2e-3) });
        var pair = RlgcExtractor.Extract(new CoupledLineCrossSection(layers, 0, new[]
        {
            new TraceCrossSection(-(w + gap) / 2, w, 35e-6, Sigma), new TraceCrossSection((w + gap) / 2, w, 35e-6, Sigma)
        }), new RlgcModel { ThicknessCorrection = true, SurfaceImpedance = false, WidebandDielectric = false });
        double mutual = -pair.CapacitanceFaradsPerMeter[0, 1] * length;
        Assert.Equal(mutual, matrix.Between(0, 1), 1e-6 * mutual);
        Assert.Equal(matrix.Farads[0, 1], matrix.Farads[1, 0], 1e-18);
        Assert.Equal(0, matrix.Between(0, 2), 1e-18);
        Assert.Equal(0, matrix.Between(1, 2), 1e-18);

        // To the planes it is the single-trace extraction; the total adds the mutual.
        double alone = TraceCapacitanceExtractor.Extract(board, nets[2], new BoardCoupledOptions { Stackup = stackup }).TotalFarads;
        Assert.Equal(alone, matrix.Farads[2, 2], 1e-9 * alone);
        Assert.Equal(alone, matrix.ToPlanes(0), 1e-9 * alone);
        Assert.Equal(alone + mutual, matrix.Farads[0, 0], 1e-9 * alone);
        Assert.True(mutual > 0.05 * alone && mutual < alone);
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public void TheSpiceSubcircuit_HoldsTheMatrixItWasWrittenFrom()
    {
        var two = LoopExtraction.TwoChains(new[] { Bar(0, 0, 15e-3, 0) }, "trace A",
            new[] { Bar(0, 0.6e-3, 15e-3, 0.6e-3) }, "trace B", new[] { 20e6 });
        var z = two.Points[0].Impedance;
        double f = 20e6, omega = 2 * Math.PI * f;
        string text = ParasiticExport.SpiceSubcircuit("pair of traces", two.PortNames, z, f,
            new[] { new ExportCapacitor("p1", "p2", 1.2e-12), new ExportCapacitor("p1", "0", 3.4e-12) });
        _output.WriteLine(text);
        Assert.Contains(".subckt pair_of_traces p1 n1 p2 n2", text);
        Assert.Contains(".ends pair_of_traces", text);
        Assert.Contains("C1 p1 p2 1.2E-12", text);

        // Read the elements back and rebuild Z.
        var r = new double[2, 2];
        var l = new double[2];
        double k = 0;
        foreach (string line in text.Split('\n').Select(s => s.Trim()))
        {
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) continue;
            double Value() => double.Parse(t[^1], CultureInfo.InvariantCulture);
            if (t[0] is "R1" or "R2") r[t[0][1] - '1', t[0][1] - '1'] = Value();
            else if (t[0] is "L1" or "L2") l[t[0][1] - '1'] = Value();
            else if (t[0] == "K1_2") k = Value();
            else if (t[0].StartsWith('H')) r[t[0][1] - '1', t[0][3] - '1'] = Value();
        }
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                var rebuilt = new Complex(r[i, j], omega * (i == j ? l[i] : k * Math.Sqrt(l[0] * l[1])));
                Assert.True((rebuilt - z[i, j]).Magnitude < 1e-7 * z[i, j].Magnitude, $"Z{i + 1}{j + 1}: {rebuilt} against {z[i, j]}");
            }
        // Each branch is a series chain from p to n.
        Assert.Contains("R1 p1 a1", text);
        Assert.Contains("V1 a1 b1 0", text);
        Assert.Contains("H1_2 c1_0 n1 V2", text);
    }

    [Fact]
    public void TheTouchstoneExport_ReadsBackAsTheSameImpedance()
    {
        var frequencies = new[] { 1e6, 1e7, 1e8 };
        var two = LoopExtraction.TwoChains(new[] { Bar(0, 0, 15e-3, 0) }, "A", new[] { Bar(0, 0.6e-3, 15e-3, 0.6e-3) }, "B", frequencies);
        string text = ParasiticExport.Touchstone(frequencies, two.Points.Select(p => p.Impedance).ToList(), two.PortNames);
        var read = TouchstoneReader.Read(text, 2);
        for (int p = 0; p < 3; p++)
        {
            var z = NetworkParameters.ScatteringToImpedance(read.Scattering[p], read.ReferenceOhms);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True((z[i, j] - two.Points[p].Impedance[i, j]).Magnitude < 1e-6 * two.Points[p].Impedance[i, i].Magnitude);
        }
    }

    [Fact]
    public void APortWithNoInductance_CannotBeWrittenAsRL()
    {
        var z = new Complex[,] { { new Complex(1, -5) } };
        Assert.Throws<InvalidOperationException>(() => ParasiticExport.SpiceSubcircuit("x", new[] { "p" }, z, 1e6));
    }
}
