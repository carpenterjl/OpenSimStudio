using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Extrude;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Power;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Rail-level DC power integrity (Feature 1). Every oracle is a closed form or a hand
/// resistor network, none is another solver of this program:
/// a ladder of trace segments, the two-contact sheet resistance (R_s/π)·acosh(d/2a) with
/// its image correction for a finite disc, the barrel resistance ρ·h/A of a plated via,
/// and the current split of two unequal vias in parallel.
/// </summary>
public class RailAnalysisTests
{
    private readonly ITestOutputHelper _output;
    public RailAnalysisTests(ITestOutputHelper output) => _output = output;

    private const double Sigma = 5.96e7, Cu = 35e-6;

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    private static CopperPad Pad(int layer, double x0, double y0, double x1, double y1,
        string? refDes = null, string? pin = null) =>
        new(layer, new Point2((x0 + x1) / 2, (y0 + y1) / 2), Rect(x0, y0, x1, y1), Math.Max(x1 - x0, y1 - y0))
        { ComponentRef = refDes, Pin = pin };

    // ---------------- Gerber X2 attributes ----------------

    private const string Header = "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1.0*%\n%ADD11C,0.5*%\n";

    [Fact]
    public void Parser_KeepsNetAndPinAttributes_UntilDeleted()
    {
        var doc = new GerberParser().Parse(Header +
            "D10*\n%TO.P,U1,3,VOUT*%\n%TO.N,+3V3*%\nX1000000Y1000000D03*\n" +
            "%TD*%\nX5000000Y1000000D03*\n" +
            "D11*\n%TO.N,Net-(C1-\\u0050ad1)*%\nX1000000Y1000000D02*\nX5000000Y1000000D01*\n" +
            "%TO.N,*%\nX5000000Y3000000D01*\n" +
            "%TO.C,R7*%\n%TO.N,N/C*%\nD10*\nX9000000Y1000000D03*\nM02*");

        var flashes = doc.Ops.OfType<FlashOp>().ToList();
        Assert.Equal(("+3V3", "U1", "3"), (flashes[0].Net, flashes[0].ComponentRef, flashes[0].Pin));
        Assert.Equal((null, null, null), (flashes[1].Net, flashes[1].ComponentRef, flashes[1].Pin));
        // .C names the component only; N/C and an empty name are "no net".
        Assert.Equal((null, "R7", null), (flashes[2].Net, flashes[2].ComponentRef, flashes[2].Pin));

        // A net change closes the polyline: one draw per net, the escape decoded.
        var draws = doc.Ops.OfType<DrawOp>().ToList();
        Assert.Equal(2, draws.Count);
        Assert.Equal("Net-(C1-Pad1)", draws[0].Net);
        Assert.Null(draws[1].Net);
    }

    private static PcbBoard ReadBoard(string top)
    {
        string dir = Path.Combine(Path.GetTempPath(), "opensim-rail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "top.gbr"), "%TF.FileFunction,Copper,L1,Top,Signal*%\n" + top);
            return new PcbBoardReader().Read(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reader_NamesNetsAndPins_FromTheAttributes()
    {
        // Two separate traces with a pad at each end: +3V3 along y = 1, GND along y = 5.
        var board = ReadBoard(Header +
            "%TO.N,+3V3*%\nD10*\n%TO.P,U1,3*%\nX1000000Y1000000D03*\n%TO.P,C4,1*%\nX9000000Y1000000D03*\n%TD.P*%\n" +
            "D11*\nX1000000Y1000000D02*\nX9000000Y1000000D01*\n" +
            "%TO.N,GND*%\nD10*\n%TO.P,U1,2*%\nX1000000Y5000000D03*\n%TO.P,C4,2*%\nX9000000Y5000000D03*\n%TD.P*%\n" +
            "D11*\nX1000000Y5000000D02*\nX9000000Y5000000D01*\n%TD*%\nM02*");

        Assert.Equal(new[] { "+3V3", "GND" }, board.Nets.Select(n => n.Name).OrderBy(n => n));
        Assert.Equal(new[] { "C4.1", "C4.2", "U1.2", "U1.3" },
            board.Pads.Select(p => $"{p.ComponentRef}.{p.Pin}").OrderBy(s => s));
        Assert.All(board.Pads, p => Assert.NotNull(p.NetName));
        Assert.Contains(board.Warnings, w => w.Contains("2 of 2 nets named"));

        // The electrode a user picks carries the pin's name.
        var rail = board.Nets.Single(n => n.Name == "+3V3");
        var mesh = new NetMesher().MeshNet(rail, board.Pads);
        Assert.Contains(mesh.Pads, p => p.Label.StartsWith("U1.3"));
    }

    [Fact]
    public void Reader_ReportsCopperThatCarriesTwoNetNames()
    {
        // One trace, its two pads attributed to different nets: the copper decides (one
        // net), the majority names it, and the disagreement is said.
        var board = ReadBoard(Header +
            "D10*\n%TO.N,VIN*%\nX1000000Y1000000D03*\n%TO.N,VOUT*%\nX9000000Y1000000D03*\n" +
            "D11*\n%TO.N,VIN*%\nX1000000Y1000000D02*\nX9000000Y1000000D01*\nM02*");

        var net = Assert.Single(board.Nets);
        Assert.Equal("VIN", net.Name);
        Assert.Contains(board.Warnings, w => w.Contains("'VIN' (2)") && w.Contains("'VOUT' (1)"));
    }

    // ---------------- Ladder: one source, three loads along a trace ----------------

    /// <summary>A 60 × 1 mm trace with four full-width 1 mm pads. Between pad edges the
    /// current is uniform, so each span is exactly ρ·ℓ/(w·t) and the rail is a ladder.</summary>
    private static (NetMesher.Result Mesh, CopperNet Net) Ladder()
    {
        var net = new CopperNet(1, new[] { new CopperIsland(0, 1, "L1", Rect(0, 0, 60e-3, 1e-3)) });
        var pads = new[]
        {
            Pad(1, 0, 0, 1e-3, 1e-3, "U1", "3"),
            Pad(1, 20e-3, 0, 21e-3, 1e-3, "U2", "1"),
            Pad(1, 40e-3, 0, 41e-3, 1e-3, "U3", "1"),
            Pad(1, 59e-3, 0, 60e-3, 1e-3, "U4", "1"),
        };
        var mesh = new NetMesher().MeshNet(net, pads,
            new NetMeshOptions { TargetEdgeLength = 0.125e-3, CopperThickness = Cu });
        return (mesh, net);
    }

    private static NetMesher.PadElectrode PadOf(NetMesher.Result mesh, string refDes) =>
        mesh.Pads.Single(p => p.ComponentRef == refDes);

    [Fact]
    public void Ladder_LoadVoltages_MatchTheResistorNetwork()
    {
        var (mesh, net) = Ladder();
        var report = RailAnalysis.Solve(mesh, net, new RailSetup
        {
            Sources = new[] { new RailSource("U1", new[] { PadOf(mesh, "U1") }, 3.3) { OutputResistance = 0.010 } },
            Sinks = new[]
            {
                new RailSink("U2", new[] { PadOf(mesh, "U2") }, 1.0),
                new RailSink("U3", new[] { PadOf(mesh, "U3") }, 2.0),
                new RailSink("U4", new[] { PadOf(mesh, "U4") }, 0.5),
            },
            CopperConductivity = Sigma,
            DropLimitPercent = 2.5,
            CurrentDensityLimit = 0
        });

        double perMeter = 1.0 / (Sigma * 1e-3 * Cu);
        double vSource = 3.3 - 0.010 * 3.5;
        double v2 = vSource - 3.5 * perMeter * 19e-3;
        double v3 = v2 - 2.5 * perMeter * 19e-3;
        double v4 = v3 - 0.5 * perMeter * 18e-3;

        Assert.Equal(vSource, report.Sources[0].PadVolts, 6);
        Assert.Equal(3.5, report.Sources[0].Amps, 6);
        Assert.Equal(0.010 * 3.5 * 3.5, report.Sources[0].OutputLossWatts, 6);
        // Drops from the source pad, each within 0.5 % of the network's. (A pad electrode is
        // the mesh faces whose centre lies in the pad, so its edge is resolved to a fraction
        // of an element: at a 0.5 mm edge these spans read 1.6 % short, at 0.125 mm 0.1 %.)
        var expected = new[] { v2, v3, v4 };
        for (int i = 0; i < 3; i++)
        {
            double drop = vSource - report.Sinks[i].Volts, want = vSource - expected[i];
            _output.WriteLine($"{report.Sinks[i].Name}: {report.Sinks[i].Volts:f6} V (network {expected[i]:f6} V)");
            Assert.Equal(want, drop, want * 0.005);
        }
        // Copper loss is the ladder's I²R.
        double loss = perMeter * (3.5 * 3.5 * 19e-3 + 2.5 * 2.5 * 19e-3 + 0.5 * 0.5 * 18e-3);
        Assert.Equal(loss, report.CopperLossWatts, loss * 0.005);

        // 2.5 % of 3.3 V is 82.5 mV below nominal: U2 (67 mV, the source's own 35 mV
        // included) passes, U3 (90 mV) and U4 (94 mV) do not.
        Assert.Equal(3.3, report.NominalVolts);
        Assert.Equal(new[] { true, false, false }, report.Sinks.Select(s => s.Pass));
        Assert.False(report.Pass);
        Assert.Contains(report.Describe(), l => l.Contains("load U3") && l.Contains("FAIL"));
    }

    [Fact]
    public void Ladder_TwoSources_ShareTheLoadAsTheNetworkSays()
    {
        // Sources at both ends of the trace, one load between them: the load current
        // divides inversely to the two path resistances (output resistance included).
        var (mesh, net) = Ladder();
        var report = RailAnalysis.Solve(mesh, net, new RailSetup
        {
            Sources = new[]
            {
                new RailSource("U1", new[] { PadOf(mesh, "U1") }, 5.0) { OutputResistance = 0.005 },
                new RailSource("U4", new[] { PadOf(mesh, "U4") }, 5.0),
            },
            Sinks = new[] { new RailSink("U2", new[] { PadOf(mesh, "U2") }, 3.0) },
            CopperConductivity = Sigma,
            CurrentDensityLimit = 0
        });

        double perMeter = 1.0 / (Sigma * 1e-3 * Cu);
        // U3 is not a terminal here, so its pad is ordinary copper: U4's current runs the
        // whole 38 mm from 59 mm back to 21 mm.
        double left = 0.005 + perMeter * 19e-3, right = perMeter * 38e-3;
        double iLeft = 3.0 * right / (left + right), iRight = 3.0 - iLeft;
        Assert.Equal(iLeft, report.Sources[0].Amps, iLeft * 0.005);
        Assert.Equal(iRight, report.Sources[1].Amps, iRight * 0.005);
        Assert.Equal(5.0 - iLeft * left, report.Sinks[0].Volts, 5e-5);

        // The terminal conductance matrix is symmetric and its rows sum to zero.
        var g = report.Solution.Conductance;
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(0.0, g[i * 3] + g[i * 3 + 1] + g[i * 3 + 2], 1e-6 * g[0]);
            for (int j = 0; j < 3; j++) Assert.Equal(g[i * 3 + j], g[j * 3 + i], 12);
        }
    }

    // ---------------- Two contacts on a sheet ----------------

    private static IReadOnlyList<Point2> Ring(Point2 c, double r, int n, bool clockwise)
    {
        var points = new Point2[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n * (clockwise ? -1 : 1);
            points[i] = new Point2(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }
        return points;
    }

    /// <summary>
    /// Two circular equipotential contacts of radius a, centres d apart, on a sheet:
    /// R = (R_s/π)·acosh(d/2a) on an infinite sheet. On a disc of radius R_d with the
    /// contacts at ±d/2 the insulating edge adds the images at R_d²/(d/2), which raise it
    /// by (R_s/π)·ln((R_d² + s²)/(R_d² − s²)), s = d/2.
    /// </summary>
    [Theory]
    [InlineData(0.6e-3)]
    [InlineData(0.4e-3)]
    public void TwoContactSheetResistance_MatchesAcosh(double edge)
    {
        double a = 1.5e-3, s = 6e-3, disc = 30e-3;
        int contactSides = 48;
        var left = new Point2(-s, 0);
        var right = new Point2(s, 0);
        var sheet = new Polygon2(Ring(new Point2(0, 0), disc, 240, clockwise: false), new[]
        {
            Ring(left, a, contactSides, clockwise: true),
            Ring(right, a, contactSides, clockwise: true)
        });
        var planar = new PlanarMesher().Mesh(new[] { new PlanarRegion(PcbStackup.CopperRegion, new[] { sheet }) }, edge);
        var extruded = new PcbMeshGenerator().GenerateCopperOnly(planar, Cu);

        // The contacts are the walls of the two holes: re-tag those boundary triangles.
        const int leftFace = 5001, rightFace = 5002;
        var skin = extruded.BoundaryTriangles.Select(t =>
        {
            var c = (extruded.Nodes[t.A] + extruded.Nodes[t.B] + extruded.Nodes[t.C]) * (1.0 / 3.0);
            var p = new Point2(c.X, c.Y);
            if ((p - left).Length < a * 1.01) return t with { FaceId = leftFace };
            if ((p - right).Length < a * 1.01) return t with { FaceId = rightFace };
            return t;
        }).ToList();
        var mesh = new FeMesh(extruded.Nodes, extruded.Elements, skin, extruded.ElementRegionIds);

        var result = TerminalConductionSolver.Solve(mesh,
            Enumerable.Repeat(Sigma, mesh.ElementCount).ToArray(), new[]
            {
                new ConductionTerminal { Name = "A", FaceIds = new[] { leftFace }, SourceVolts = 1.0 },
                new ConductionTerminal { Name = "B", FaceIds = new[] { rightFace }, LoadCurrent = 1.0 },
            });

        double sheetOhms = 1.0 / (Sigma * Cu);
        // The contacts are inscribed 48-gons: their equivalent radius is the mean of the
        // inscribed and circumscribed circles to first order.
        double aEffective = a * 0.5 * (1 + Math.Cos(Math.PI / contactSides));
        double expected = sheetOhms / Math.PI *
            (Math.Acosh(s / aEffective) + Math.Log((disc * disc + s * s) / (disc * disc - s * s)));
        double measured = 1.0 - result.Terminals[1].Volts;      // 1 A through it
        _output.WriteLine($"edge {edge * 1e3:g2} mm, {mesh.ElementCount} elements: R = {measured * 1e3:f5} mΩ, " +
                          $"closed form {expected * 1e3:f5} mΩ ({100 * (measured / expected - 1):+0.00;-0.00} %), " +
                          $"infinite sheet {sheetOhms / Math.PI * Math.Acosh(s / a) * 1e3:f5} mΩ");
        Assert.Equal(expected, measured, expected * 0.02);
        // The solve's own balance: P = I²R.
        Assert.Equal(measured, result.TotalPower, measured * 1e-6);
    }

    // ---------------- Vias ----------------

    private const double Gap = 3.2e-3, Plating = 10e-6, HeavyCu = 210e-6;

    /// <summary>Two 8 × 4 mm copper areas, L1 over L2, joined by a 0.3 mm and a 0.6 mm
    /// via. Heavy copper and a long gap make the barrels the only resistance that matters,
    /// so the pair is two resistors in parallel.</summary>
    private static (NetMesher.Result Mesh, CopperNet Net) ViaPair()
    {
        var small = new Via(new Point2(3e-3, 2e-3), 0.3e-3, Plated: true);
        var large = new Via(new Point2(5e-3, 2e-3), 0.6e-3, Plated: true);
        var net = new CopperNet(1, new[]
        {
            new CopperIsland(0, 1, "L1", Rect(0, 0, 8e-3, 4e-3)),
            new CopperIsland(1, 2, "L2", Rect(0, 0, 8e-3, 4e-3)),
        })
        {
            StitchingVias = new[] { new ViaBridge(small, new[] { 1, 2 }), new ViaBridge(large, new[] { 1, 2 }) }
        };
        var pads = new[]
        {
            Pad(1, 0.2e-3, 1e-3, 1.2e-3, 3e-3, "J1", "1"),
            Pad(2, 6.8e-3, 1e-3, 7.8e-3, 3e-3, "U1", "1"),
        };
        var mesh = new NetMesher().MeshNet(net, pads, new NetMeshOptions
        {
            TargetEdgeLength = 0.3e-3,
            CopperThickness = HeavyCu,
            DefaultDielectricThickness = Gap,
            ViaPlatingThickness = Plating
        });
        return (mesh, net);
    }

    private static double BarrelOhms(double drill) =>
        Gap / (Sigma * Math.PI * (Math.Pow(drill / 2 + Plating, 2) - Math.Pow(drill / 2, 2)));

    [Fact]
    public void ParallelVias_ShareCurrentAsTheirBarrelResistances()
    {
        var (mesh, net) = ViaPair();
        var report = RailAnalysis.Solve(mesh, net, new RailSetup
        {
            Sources = new[] { new RailSource("J1", new[] { PadOf(mesh, "J1") }, 1.0) },
            Sinks = new[] { new RailSink("U1", new[] { PadOf(mesh, "U1") }, 2.0) },
            CopperConductivity = Sigma,
            ViaCurrentLimit = 1.0,
            CurrentDensityLimit = 0
        });

        Assert.Equal(2, report.Vias.Count);
        var small = report.Vias.Single(v => v.Diameter < 0.5e-3);
        var large = report.Vias.Single(v => v.Diameter > 0.5e-3);
        double rSmall = BarrelOhms(0.3e-3), rLarge = BarrelOhms(0.6e-3);
        _output.WriteLine($"small via {small.Amps:f4} A, large via {large.Amps:f4} A; " +
                          $"network {2 * rLarge / (rSmall + rLarge):f4} / {2 * rSmall / (rSmall + rLarge):f4} A");

        // Everything the load draws crosses the gap in the two barrels.
        Assert.Equal(2.0, small.Amps + large.Amps, 2.0 * 0.005);
        // Current divides inversely to the barrel resistances ρ·h/A.
        Assert.Equal(rSmall / rLarge, large.Amps / small.Amps, rSmall / rLarge * 0.02);
        // The 1 A per-via limit flags the large via only, and fails the rail.
        Assert.True(large.OverLimit);
        Assert.False(small.OverLimit);
        Assert.False(report.Pass);

        // Pad to pad: the two barrels in parallel, plus what the heavy copper adds —
        // more than the gap-only barrels, less than barrels as long as gap + both layers
        // with 5 % for spreading in the copper.
        double parallel = rSmall * rLarge / (rSmall + rLarge);
        double measured = (1.0 - report.Sinks[0].Volts) / 2.0;
        _output.WriteLine($"pad to pad {measured * 1e3:f4} mΩ, barrels in parallel {parallel * 1e3:f4} mΩ");
        Assert.InRange(measured, parallel, parallel * (Gap + 2 * HeavyCu) / Gap * 1.05);
    }

    [Fact]
    public void ViaBarrelResistance_IsRhoHeightOverWallArea()
    {
        // Across the gap the current in a barrel is axial and uniform, so the potential
        // difference between the barrel's two ends over the via's current is ρ·h/A exactly.
        var (mesh, net) = ViaPair();
        var report = RailAnalysis.Solve(mesh, net, new RailSetup
        {
            Sources = new[] { new RailSource("J1", new[] { PadOf(mesh, "J1") }, 1.0) },
            Sinks = new[] { new RailSink("U1", new[] { PadOf(mesh, "U1") }, 2.0) },
            CopperConductivity = Sigma,
            CurrentDensityLimit = 0
        });
        var fe = mesh.Body.Mesh!;
        double zTop = mesh.LayerZ[1].zLo, zBottom = mesh.LayerZ[2].zHi;
        Assert.Equal(Gap, zTop - zBottom, 1e-12);

        foreach (var via in report.Vias)
        {
            double top = 0, bottom = 0;
            int nTop = 0, nBottom = 0;
            for (int n = 0; n < fe.NodeCount; n++)
            {
                var p = fe.Nodes[n];
                double r = Math.Sqrt(Math.Pow(p.X - via.Position.X, 2) + Math.Pow(p.Y - via.Position.Y, 2));
                if (r > via.Diameter / 2 + Plating * 1.2) continue;
                if (Math.Abs(p.Z - zTop) < 1e-9) { top += report.Solution.Potential[n]; nTop++; }
                if (Math.Abs(p.Z - zBottom) < 1e-9) { bottom += report.Solution.Potential[n]; nBottom++; }
            }
            double measured = (top / nTop - bottom / nBottom) / via.Amps;
            double expected = BarrelOhms(via.Diameter);
            _output.WriteLine($"⌀{via.Diameter * 1e3:g2} mm: {measured * 1e3:f4} mΩ, ρh/A {expected * 1e3:f4} mΩ " +
                              $"({100 * (measured / expected - 1):+0.00;-0.00} %)");
            Assert.Equal(expected, measured, expected * 0.02);
        }
    }

    // ---------------- Neck-down ----------------

    [Fact]
    public void NeckDown_IsFoundWhereTheTraceNarrows()
    {
        // 2 mm wide trace with a 0.4 mm neck between x = 9 and 11 mm, 1 A through it:
        // 14.3 A/mm² in the wide part, 71.4 A/mm² in the neck. Limit 50 A/mm².
        var outline = new Polygon2(new[]
        {
            new Point2(0, 0), new Point2(9e-3, 0), new Point2(9e-3, 0.8e-3), new Point2(11e-3, 0.8e-3),
            new Point2(11e-3, 0), new Point2(20e-3, 0), new Point2(20e-3, 2e-3), new Point2(11e-3, 2e-3),
            new Point2(11e-3, 1.2e-3), new Point2(9e-3, 1.2e-3), new Point2(9e-3, 2e-3), new Point2(0, 2e-3),
        });
        var net = new CopperNet(1, new[] { new CopperIsland(0, 1, "L1", outline) });
        var pads = new[] { Pad(1, 0, 0, 1e-3, 2e-3, "J1", "1"), Pad(1, 19e-3, 0, 20e-3, 2e-3, "U1", "1") };
        var mesh = new NetMesher().MeshNet(net, pads,
            new NetMeshOptions { TargetEdgeLength = 0.2e-3, CopperThickness = Cu });

        var report = RailAnalysis.Solve(mesh, net, new RailSetup
        {
            Sources = new[] { new RailSource("J1", new[] { PadOf(mesh, "J1") }, 12.0) },
            Sinks = new[] { new RailSink("U1", new[] { PadOf(mesh, "U1") }, 1.0) },
            CopperConductivity = Sigma,
            CurrentDensityLimit = 50e6
        });

        Assert.Equal(1, report.PatchesOverLimit);
        var spot = Assert.Single(report.HotSpots);
        Assert.True(spot.OverLimit);
        Assert.Equal("L1", spot.Where);
        Assert.InRange(spot.Position.X, 8.8e-3, 11.2e-3);
        // The patch is the neck: about 2 mm long, and its mean is the neck's I/(w·t)
        // (a little above: the corner concentrations are inside the patch).
        double neck = 1.0 / (0.4e-3 * Cu);
        _output.WriteLine($"neck: mean {spot.MeanDensity * 1e-6:f2} A/mm², peak {spot.PeakDensity * 1e-6:f2} A/mm², " +
                          $"I/(w·t) {neck * 1e-6:f2} A/mm², extent {spot.ExtentMeters * 1e3:f2} mm");
        Assert.InRange(spot.MeanDensity, neck * 0.98, neck * 1.10);
        Assert.InRange(spot.ExtentMeters, 1.9e-3, 2.8e-3);
        // The load is within its voltage limit; the copper is what fails the rail.
        Assert.True(report.Sinks[0].Pass);
        Assert.False(report.Pass);

        // With the limit off the report still says how dense the copper runs.
        var open = RailAnalysis.Solve(mesh, net, new RailSetup
        {
            Sources = new[] { new RailSource("J1", new[] { PadOf(mesh, "J1") }, 12.0) },
            Sinks = new[] { new RailSink("U1", new[] { PadOf(mesh, "U1") }, 1.0) },
            CopperConductivity = Sigma,
            CurrentDensityLimit = 0
        });
        Assert.True(open.Pass);
        Assert.False(Assert.Single(open.HotSpots).OverLimit);
    }

    // ---------------- Refusals ----------------

    [Fact]
    public void LoadOnCopperWithNoSource_IsRefusedByName()
    {
        // Two L1 islands in one "net" with nothing joining them: the load sits on the
        // piece the source cannot reach.
        var net = new CopperNet(1, new[]
        {
            new CopperIsland(0, 1, "L1", Rect(0, 0, 5e-3, 1e-3)),
            new CopperIsland(1, 1, "L1", Rect(6e-3, 0, 11e-3, 1e-3)),
        });
        var pads = new[] { Pad(1, 0, 0, 1e-3, 1e-3, "J1", "1"), Pad(1, 10e-3, 0, 11e-3, 1e-3, "U1", "1") };
        var mesh = new NetMesher().MeshNet(net, pads, new NetMeshOptions { TargetEdgeLength = 0.5e-3 });

        RailSetup Setup(double amps) => new()
        {
            Sources = new[] { new RailSource("J1", new[] { PadOf(mesh, "J1") }, 5.0) },
            Sinks = new[] { new RailSink("U1", new[] { PadOf(mesh, "U1") }, amps) }
        };
        var ex = Assert.Throws<InvalidOperationException>(() => RailAnalysis.Solve(mesh, net, Setup(1.0)));
        Assert.Contains("'U1'", ex.Message);
        Assert.Contains("reaches no source", ex.Message);

        // Drawing nothing it is not an error, but it is not a pass either.
        var idle = RailAnalysis.Solve(mesh, net, Setup(0.0));
        Assert.True(double.IsNaN(idle.Sinks[0].Volts));
        Assert.False(idle.Pass);
        Assert.Contains(idle.Describe(), l => l.Contains("NOT CONNECTED"));
    }

    [Fact]
    public void SetupErrors_AreNamed()
    {
        var (mesh, net) = Ladder();
        var u1 = PadOf(mesh, "U1");
        var u2 = PadOf(mesh, "U2");

        // One pad as source and load at once.
        var shared = Assert.Throws<InvalidOperationException>(() => RailAnalysis.Solve(mesh, net, new RailSetup
        {
            Sources = new[] { new RailSource("REG", new[] { u1 }, 3.3) },
            Sinks = new[] { new RailSink("LOAD", new[] { u1 }, 1.0) }
        }));
        Assert.Contains("'REG'", shared.Message);
        Assert.Contains("'LOAD'", shared.Message);

        // A pad that is not on this mesh.
        var stray = new NetMesher.PadElectrode(1234, new Point2(0.3, 0.3), 1);
        var missing = Assert.Throws<InvalidOperationException>(() => RailAnalysis.Solve(mesh, net, new RailSetup
        {
            Sources = new[] { new RailSource("REG", new[] { u1 }, 3.3) },
            Sinks = new[] { new RailSink("LOAD", new[] { stray }, 1.0) }
        }));
        Assert.Contains("not an electrode of this mesh", missing.Message);

        Assert.Throws<InvalidOperationException>(() => RailAnalysis.Solve(mesh, net, new RailSetup
        {
            Sources = Array.Empty<RailSource>(),
            Sinks = new[] { new RailSink("LOAD", new[] { u2 }, 1.0) }
        }));
    }
}
