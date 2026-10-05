using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Extrude;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Power;
using OpenSim.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Board electro-thermal analysis (Feature 2). The coupling is gated against the
/// self-heating bar, whose closed form with ρ(T) = ρ₀(1 + αθ) separates one-way from
/// two-way coupling in BOTH directions: a current-driven bar runs hotter than the one-way
/// parabola, a voltage-driven bar cooler. The board mesh is gated on what must hold
/// whatever the numbers are: volumes, a conformal copper/laminate interface, the energy
/// balance, and the loss ratio 1 + α·(mean copper rise).
/// </summary>
public class ElectroThermalTests
{
    private readonly ITestOutputHelper _output;
    public ElectroThermalTests(ITestOutputHelper output) => _output = output;

    private const double Sigma0 = 5.8e7, Alpha = 0.004, K = 400, T0 = 300;

    private static readonly Material BarCopper = new()
    {
        Name = "bar copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
        ThermalConductivity = K, SpecificHeat = 385, ElectricalConductivity = Sigma0,
        ResistivityTemperatureCoefficient = Alpha, ResistivityReferenceTemperature = T0
    };

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    [Fact]
    public void Conductivity_FollowsTheLinearResistivityLaw()
    {
        Assert.Equal(Sigma0, BarCopper.ElectricalConductivityAt(T0), 6);
        Assert.Equal(Sigma0 / 1.2, BarCopper.ElectricalConductivityAt(T0 + 50), 3);
        // No coefficient: constant.
        var constant = BarCopper with { ResistivityTemperatureCoefficient = null };
        Assert.Equal(Sigma0, constant.ElectricalConductivityAt(900), 6);
    }

    // ---------------- The self-heating bar ----------------

    private const double Length = 40e-3, Width = 2e-3, Thickness = 0.5e-3;

    /// <summary>A 40 × 2 × 0.5 mm bar along x with its two end faces tagged. The ends are
    /// re-tagged by position: the generator groups side walls by normal direction in 22.5°
    /// bins, the −x direction sits exactly on a bin boundary, and the mesher's nanometre
    /// jitter puts the triangles of that one flat end into two different face ids.</summary>
    private static (FeMesh Mesh, int Left, int Right) Bar()
    {
        var planar = new PlanarMesher().Mesh(
            new[] { new PlanarRegion(PcbStackup.CopperRegion, new[] { Rect(0, 0, Length, Width) }) }, 0.5e-3);
        var extruded = new PcbMeshGenerator().GenerateCopperOnly(planar, Thickness);
        const int left = 7001, right = 7002;
        bool At(BoundaryTriangle t, double x) =>
            Math.Abs(extruded.Nodes[t.A].X - x) < 1e-6 && Math.Abs(extruded.Nodes[t.B].X - x) < 1e-6 &&
            Math.Abs(extruded.Nodes[t.C].X - x) < 1e-6;
        var skin = extruded.BoundaryTriangles
            .Select(t => At(t, 0) ? t with { FaceId = left } : At(t, Length) ? t with { FaceId = right } : t)
            .ToList();
        return (new FeMesh(extruded.Nodes, extruded.Elements, skin, extruded.ElementRegionIds), left, right);
    }

    private static ElectroThermalInput BarInput(FeMesh mesh, int left, int right,
        IReadOnlyList<ConductionTerminal> terminals) => new()
    {
        Mesh = mesh,
        Material = BarCopper,
        Terminals = terminals,
        ThermalConditions = new BoundaryCondition[]
        {
            new FixedTemperature { Name = "ends", FaceIds = new[] { left, right }, Kelvin = T0 }
        },
        StartTemperature = T0,
        Tolerance = 1e-4
    };

    [Fact]
    public void CurrentDrivenBar_RunsHotterThanOneWay_AsTheClosedFormSays()
    {
        // k·θ″ + J²ρ₀(1 + αθ) = 0, θ(0) = θ(L) = 0:
        //   θ(x) = (cos(m(x − L/2)) / cos(mL/2) − 1) / α,  m = J·√(ρ₀α/k).
        // One-way (ρ = ρ₀): the parabola, peak J²ρ₀L²/(8k) = (mL)²/(8α).
        const double halfAngle = 0.8;                                  // mL/2
        double m = 2 * halfAngle / Length;
        double density = m / Math.Sqrt(Alpha / (Sigma0 * K));
        double amps = density * Width * Thickness;
        double peak = (1 / Math.Cos(halfAngle) - 1) / Alpha;
        double oneWayPeak = Math.Pow(2 * halfAngle, 2) / (8 * Alpha);
        double volts = density / Sigma0 * (2 / m) * Math.Tan(halfAngle);   // ∫ρJ dx

        var (mesh, left, right) = Bar();
        var result = ElectroThermalStudy.Solve(BarInput(mesh, left, right, new[]
        {
            new ConductionTerminal { Name = "in", FaceIds = new[] { left }, SourceVolts = 1.0 },
            new ConductionTerminal { Name = "out", FaceIds = new[] { right }, LoadCurrent = amps },
        }));

        double measured = result.Temperature.Max() - T0, measuredOneWay = result.OneWayTemperature.Max() - T0;
        double drop = 1.0 - result.Electrical.Terminals[1].Volts;
        _output.WriteLine($"peak rise {measured:f3} K (closed form {peak:f3}), one-way {measuredOneWay:f3} K " +
                          $"(parabola {oneWayPeak:f3}), drop {drop * 1e3:f4} mV (closed form {volts * 1e3:f4}), " +
                          $"{result.Iterations} passes");
        Assert.True(peak > 1.3 * oneWayPeak);                          // the case separates the two
        Assert.Equal(oneWayPeak, measuredOneWay, oneWayPeak * 0.01);
        Assert.Equal(peak, measured, peak * 0.01);
        Assert.Equal(volts, drop, volts * 0.01);
        // Cold drop is the plain ρ₀·L·J.
        Assert.Equal(density / Sigma0 * Length, 1.0 - result.ColdElectrical.Terminals[1].Volts,
            density / Sigma0 * Length * 0.002);
        // What goes in electrically is what the heat solve was given.
        Assert.Equal(drop * amps, result.Electrical.TotalPower, drop * amps * 1e-6);
    }

    [Fact]
    public void VoltageDrivenBar_RunsCoolerThanOneWay_AsTheClosedFormSays()
    {
        // Same equation with J unknown and V = J·ρ₀·∫(1 + αθ)dx = 2·√(ρ₀k/α)·tan(mL/2):
        //   mL/2 = atan(V / (2·√(ρ₀k/α))). Hot copper draws less current, so the two-way
        // peak is BELOW the one-way V²/(8ρ₀k).
        const double volts = 0.08;
        double scale = Math.Sqrt(K / (Sigma0 * Alpha));
        double halfAngle = Math.Atan(volts / (2 * scale));
        double peak = (1 / Math.Cos(halfAngle) - 1) / Alpha;
        double oneWayPeak = volts * volts * Sigma0 / (8 * K);
        double amps = 2 * halfAngle / Length / Math.Sqrt(Alpha / (Sigma0 * K)) * Width * Thickness;

        var (mesh, left, right) = Bar();
        var result = ElectroThermalStudy.Solve(BarInput(mesh, left, right, new[]
        {
            new ConductionTerminal { Name = "high", FaceIds = new[] { left }, SourceVolts = volts },
            new ConductionTerminal { Name = "low", FaceIds = new[] { right }, SourceVolts = 0.0 },
        }));

        double measured = result.Temperature.Max() - T0, measuredOneWay = result.OneWayTemperature.Max() - T0;
        double current = result.Electrical.Terminals[0].Amps;
        _output.WriteLine($"peak rise {measured:f3} K (closed form {peak:f3}), one-way {measuredOneWay:f3} K " +
                          $"({oneWayPeak:f3}), current {current:f3} A (closed form {amps:f3}), {result.Iterations} passes");
        Assert.True(peak < 0.9 * oneWayPeak);
        Assert.Equal(oneWayPeak, measuredOneWay, oneWayPeak * 0.01);
        Assert.Equal(peak, measured, peak * 0.01);
        Assert.Equal(amps, current, amps * 0.01);
    }

    [Fact]
    public void CurrentBeyondTheStabilityLimit_IsReportedAsRunaway()
    {
        // The closed form has no solution for mL/2 ≥ π/2: heat grows with temperature
        // faster than the ends can remove it.
        double m = 2 * 1.7 / Length;
        double amps = m / Math.Sqrt(Alpha / (Sigma0 * K)) * Width * Thickness;
        var (mesh, left, right) = Bar();
        var ex = Assert.Throws<InvalidOperationException>(() => ElectroThermalStudy.Solve(
            BarInput(mesh, left, right, new[]
            {
                new ConductionTerminal { Name = "in", FaceIds = new[] { left }, SourceVolts = 1.0 },
                new ConductionTerminal { Name = "out", FaceIds = new[] { right }, LoadCurrent = amps },
            })));
        Assert.Contains("runaway", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- A trace on a board ----------------

    private const double Cu = 35e-6, Board = 1.6e-3, BoardX = 40e-3, BoardY = 20e-3;
    private const double TraceX0 = 5e-3, TraceX1 = 35e-3, TraceY0 = 9.5e-3, TraceY1 = 10.5e-3;

    private static readonly Material Copper = new()
    {
        Name = "Copper (annealed)", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
        ThermalConductivity = 401, SpecificHeat = 385, ElectricalConductivity = 5.96e7,
        ResistivityTemperatureCoefficient = 0.00393, Emissivity = 0.15
    };

    private static readonly Material Fr4 = new()
    {
        Name = "FR4 (PCB laminate)", YoungsModulus = 24e9, PoissonRatio = 0.14, Density = 1850,
        ThermalConductivity = 0.29, SpecificHeat = 1100, ElectricalConductivity = 1e-14, Emissivity = 0.90
    };

    private static CopperPad Pad(int layer, double x0, double y0, double x1, double y1, string refDes) =>
        new(layer, new Point2((x0 + x1) / 2, (y0 + y1) / 2), Rect(x0, y0, x1, y1), Math.Max(x1 - x0, y1 - y0))
        { ComponentRef = refDes, Pin = "1" };

    /// <summary>A 30 × 1 mm, 35 µm trace on the top of a 40 × 20 × 1.6 mm two-layer board,
    /// a full-width pad at each end.</summary>
    private static (CopperNet Net, CopperPad[] Pads, NetMeshOptions Options, BoardBodyOptions Board) TraceOnBoard()
    {
        var net = new CopperNet(1, new[] { new CopperIsland(0, 1, "L1", Rect(TraceX0, TraceY0, TraceX1, TraceY1)) });
        var pads = new[]
        {
            Pad(1, TraceX0, TraceY0, TraceX0 + 1e-3, TraceY1, "J1"),
            Pad(1, TraceX1 - 1e-3, TraceY0, TraceX1, TraceY1, "U1"),
        };
        var options = new NetMeshOptions
        {
            TargetEdgeLength = 0.5e-3, CopperThickness = Cu, DefaultDielectricThickness = Board
        };
        var board = new BoardBodyOptions
        {
            Outline = new[] { Rect(0, 0, BoardX, BoardY) }, CopperLayerCount = 2, Margin = 0, GapSubdivisions = 3
        };
        return (net, pads, options, board);
    }

    private static RailSetup TraceRail(NetMesher.Result mesh, double amps) => new()
    {
        Sources = new[] { new RailSource("J1", new[] { mesh.Pads.Single(p => p.ComponentRef == "J1") }, 5.0) },
        Sinks = new[] { new RailSink("U1", new[] { mesh.Pads.Single(p => p.ComponentRef == "U1") }, amps) },
        CopperConductivity = Copper.ElectricalConductivity!.Value,
        CurrentDensityLimit = 0
    };

    private static double Area(FeMesh mesh, BoundaryTriangle t) =>
        0.5 * OpenSim.Core.Numerics.Vector3D.Cross(mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length;

    [Fact]
    public void BoardMesh_IsCopperOnLaminate_Conformal()
    {
        var (net, pads, options, board) = TraceOnBoard();
        var result = new NetMesher().MeshNetOnBoard(net, pads, options, board);
        var mesh = result.Body.Mesh!;

        double copper = 0, laminate = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double v = mesh.ElementVolume(e);
            Assert.True(v > 0);
            if (mesh.RegionOf(e) == PcbStackup.CopperRegion) copper += v; else laminate += v;
        }
        double traceArea = (TraceX1 - TraceX0) * (TraceY1 - TraceY0), boardArea = BoardX * BoardY;
        // (to the arrangement's 50 nm weld tolerance on a 1 mm wide trace)
        Assert.Equal(traceArea * Cu, copper, traceArea * Cu * 1e-4);
        Assert.Equal(boardArea * Board, laminate, boardArea * Board * 1e-4);
        Assert.Equal("FR4 (PCB laminate)", result.Body.RegionMaterialNames![PcbStackup.DielectricRegion]);

        // Conformal interface: the only upward faces are the board's top (laminate, or the
        // copper over it) and the only downward faces its bottom. A copper underside that
        // did not share its triangles with the laminate would show up as extra skin.
        double up = 0, down = 0;
        foreach (var t in mesh.BoundaryTriangles)
        {
            var n = OpenSim.Core.Numerics.Vector3D.Cross(mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]);
            if (n.Z > 0.5 * n.Length) up += Area(mesh, t);
            else if (n.Z < -0.5 * n.Length) down += Area(mesh, t);
        }
        Assert.Equal(boardArea, up, boardArea * 1e-6);
        Assert.Equal(boardArea, down, boardArea * 1e-6);

        // Both pads are electrodes; three element layers through the laminate.
        Assert.Equal(2, result.Pads.Count);
        var levels = mesh.Nodes.Select(p => Math.Round(p.Z * 1e9)).Distinct().Count();
        Assert.Equal(5, levels);                                       // 0, ⅓, ⅔, 1 of the gap, + copper top

        // For the environment the pads are part of the top face, not plates of their own.
        var thermal = RailThermalAnalysis.WithoutPadFaces(mesh);
        Assert.DoesNotContain(thermal.BoundaryTriangles, t => t.FaceId >= PcbMeshGenerator.PadFaceBase);
        Assert.Equal(mesh.BoundaryTriangles.Count, thermal.BoundaryTriangles.Count);
    }

    [Fact]
    public void BoardMesh_GivesTheCopperOnlyRailAnswer_WhenCold()
    {
        // The laminate carries no current: the rail on the board mesh is the rail on the
        // copper alone (two different triangulations of the same trace).
        var (net, pads, options, board) = TraceOnBoard();
        var onBoard = new NetMesher().MeshNetOnBoard(net, pads, options, board);
        var alone = new NetMesher().MeshNet(net, pads, options);
        var a = RailAnalysis.Solve(onBoard, net, TraceRail(onBoard, 2.0));
        var b = RailAnalysis.Solve(alone, net, TraceRail(alone, 2.0));
        _output.WriteLine($"drop on board {a.Sinks[0].DropVolts * 1e3:f4} mV, copper alone {b.Sinks[0].DropVolts * 1e3:f4} mV");
        Assert.Equal(b.Sinks[0].DropVolts, a.Sinks[0].DropVolts, b.Sinks[0].DropVolts * 0.02);
        // And both are the trace's ρℓ/(w·t) between the pad edges, to the pad-edge resolution.
        double expected = 2.0 * 28e-3 / (Copper.ElectricalConductivity!.Value * 1e-3 * Cu);
        Assert.Equal(expected, a.Sinks[0].DropVolts, expected * 0.03);
    }

    [Fact]
    public void TraceOnBoard_HeatBalances_AndTheLossFollowsTheCopperTemperature()
    {
        var (net, pads, options, board) = TraceOnBoard();
        var mesh = new NetMesher().MeshNetOnBoard(net, pads, options, board);
        var fe = mesh.Body.Mesh!;
        const double h = 12.0, ambient = 298.15, amps = 4.0;
        var report = RailThermalAnalysis.Solve(mesh, net, new RailThermalSetup
        {
            Rail = TraceRail(mesh, amps),
            Copper = Copper,
            Laminate = Fr4,
            ThermalConditions = new BoundaryCondition[]
            {
                new Convection { Name = "air", FaceIds = new[] { 0, 1 }, Coefficient = h, AmbientTemperature = ambient }
            },
            Tolerance = 1e-4
        });
        foreach (string line in report.Describe()) _output.WriteLine(line);

        // Energy: everything dissipated leaves through the two convecting faces.
        var thermal = RailThermalAnalysis.WithoutPadFaces(fe);
        var temperature = report.Solution.Temperature;
        double leaving = 0;
        foreach (var t in thermal.BoundaryTriangles)
        {
            if (t.FaceId > 1) continue;
            double mean = (temperature[t.A] + temperature[t.B] + temperature[t.C]) / 3;
            leaving += h * Area(thermal, t) * (mean - ambient);
        }
        Assert.Equal(report.Rail.CopperLossWatts, leaving, report.Rail.CopperLossWatts * 1e-5);

        // The copper is the hot part, on L1, inside the trace.
        Assert.Equal("L1", report.HottestWhere);
        Assert.InRange(report.HottestAt.X, TraceX0, TraceX1);
        Assert.InRange(report.HottestAt.Y, TraceY0 - 1e-9, TraceY1 + 1e-9);
        double rise = report.PeakCopperKelvin - 293.15;
        Assert.True(rise > 5, $"rise {rise:f2} K");

        // With the current imposed, hot copper dissipates more, by exactly the factor its
        // resistivity rose: loss(hot)/loss(cold) = 1 + α·(J²-weighted mean copper rise
        // above the 20 °C reference). The current density is uniform along the trace, so
        // that mean is the volume mean over the copper between the pads.
        double weighted = 0, weight = 0;
        for (int e = 0; e < fe.ElementCount; e++)
        {
            double p = report.Solution.ColdElectrical.ElementPowerDensity[e];
            if (p == 0) continue;
            var el = fe.Elements[e];
            double elementT = 0.25 * (temperature[el.N0] + temperature[el.N1] + temperature[el.N2] + temperature[el.N3]);
            double w = p * fe.ElementVolume(e);
            weighted += w * (elementT - 293.15);
            weight += w;
        }
        double expectedRatio = 1 + 0.00393 * weighted / weight;
        double ratio = report.Rail.CopperLossWatts / report.ColdRail.CopperLossWatts;
        _output.WriteLine($"loss hot/cold {ratio:f5}, 1 + α·mean rise {expectedRatio:f5}");
        Assert.Equal(expectedRatio, ratio, 0.002);
        // The IR drop rose with it, and the feedback made the copper hotter than one-way.
        Assert.True(report.Rail.Sinks[0].DropVolts > report.ColdRail.Sinks[0].DropVolts * 1.01);
        Assert.True(report.PeakCopperKelvin > report.OneWayPeakCopperKelvin);
        Assert.Contains(report.Assumptions, a => a.Contains("only this net's copper"));
    }

    [Fact]
    public void TraceOnBoard_InStillAir_Converges()
    {
        var (net, pads, options, board) = TraceOnBoard();
        var environment = new EnvironmentSettings { AmbientTemperature = 298.15 };
        var onBoard = new NetMesher().MeshNetOnBoard(net, pads, options, board);
        var report = RailThermalAnalysis.Solve(onBoard, net, new RailThermalSetup
        {
            Rail = TraceRail(onBoard, 2.0), Copper = Copper, Laminate = Fr4, Environment = environment
        });
        foreach (string line in report.Describe()) _output.WriteLine(line);

        // For orientation only (not a gate): the IPC-2221 external-trace formula
        // I = 0.048·ΔT^0.44·A^0.725 (A in mil²), solved for ΔT.
        double mils2 = 1e-3 * Cu / Math.Pow(25.4e-6, 2);
        double ipc2221 = Math.Pow(2.0 / (0.048 * Math.Pow(mils2, 0.725)), 1 / 0.44);
        double rise = report.PeakCopperKelvin - 298.15;
        _output.WriteLine($"rise {rise:f2} K at 2 A in 1 mm × 35 µm; IPC-2221 external formula {ipc2221:f2} K");

        Assert.True(rise > 0.5 && rise < 200, $"rise {rise:f2} K");
        Assert.True(report.Iterations >= 1);
        Assert.Equal(298.15, report.StartKelvin);
        Assert.Contains(report.Fields, f => f.Name == "Temperature");
        Assert.Contains(report.Fields, f => f.Name == "Temperature rise");
        Assert.Contains(report.Fields, f => f.Name == "Electric potential");

        // A copper-only mesh is accepted and labelled. (Its rise is printed, not gated: a
        // 1 mm strip alone in air gets a small plate's high film coefficient, and reads
        // within a few kelvin of the trace on bare FR4.)
        var alone = new NetMesher().MeshNet(net, pads, options);
        var bare = RailThermalAnalysis.Solve(alone, net, new RailThermalSetup
        {
            Rail = TraceRail(alone, 2.0), Copper = Copper, Laminate = Fr4, Environment = environment
        });
        _output.WriteLine($"copper alone: rise {bare.PeakCopperKelvin - 298.15:f2} K");
        Assert.Contains(bare.Assumptions, a => a.Contains("copper-only mesh"));
    }

    [Fact]
    public void BoardMesh_WithAVia_KeepsTheBarrelAndExposesTheBottomPad()
    {
        // L1 trace to a via, L2 trace back: source pad on top, load pad on the bottom.
        var via = new Via(new Point2(20e-3, 10e-3), 0.4e-3, Plated: true);
        var net = new CopperNet(1, new[]
        {
            new CopperIsland(0, 1, "L1", Rect(5e-3, 9.5e-3, 21e-3, 10.5e-3)),
            new CopperIsland(1, 2, "L2", Rect(19e-3, 9.5e-3, 35e-3, 10.5e-3)),
        })
        { StitchingVias = new[] { new ViaBridge(via, new[] { 1, 2 }) } };
        var pads = new[]
        {
            Pad(1, 5e-3, 9.5e-3, 6e-3, 10.5e-3, "J1"),
            Pad(2, 34e-3, 9.5e-3, 35e-3, 10.5e-3, "U1"),
        };
        var options = new NetMeshOptions
        {
            TargetEdgeLength = 0.5e-3, CopperThickness = Cu, DefaultDielectricThickness = Board,
            ViaPlatingThickness = 25e-6
        };
        var board = new BoardBodyOptions
        {
            Outline = new[] { Rect(0, 0, BoardX, BoardY) }, CopperLayerCount = 2, Margin = 5e-3, GapSubdivisions = 2
        };
        var onBoard = new NetMesher().MeshNetOnBoard(net, pads, options, board);
        var alone = new NetMesher().MeshNet(net, pads, options);
        Assert.Equal(2, onBoard.Pads.Count);
        Assert.Contains(onBoard.Warnings, w => w.Contains("laminate meshed within 5 mm"));

        RailSetup Setup(NetMesher.Result mesh) => new()
        {
            Sources = new[] { new RailSource("J1", new[] { mesh.Pads.Single(p => p.ComponentRef == "J1") }, 3.3) },
            Sinks = new[] { new RailSink("U1", new[] { mesh.Pads.Single(p => p.ComponentRef == "U1") }, 1.0) },
            CopperConductivity = Copper.ElectricalConductivity!.Value,
            CurrentDensityLimit = 0
        };
        var a = RailAnalysis.Solve(onBoard, net, Setup(onBoard));
        var b = RailAnalysis.Solve(alone, net, Setup(alone));
        _output.WriteLine($"drop on board {a.Sinks[0].DropVolts * 1e3:f4} mV, copper alone {b.Sinks[0].DropVolts * 1e3:f4} mV; " +
                          $"via {a.Vias.Single().Amps:f4} A");
        // All the current crosses the board in the one barrel, on either mesh.
        Assert.Equal(1.0, a.Vias.Single().Amps, 0.005);
        Assert.Equal(1.0, b.Vias.Single().Amps, 0.005);
        Assert.Equal(b.Sinks[0].DropVolts, a.Sinks[0].DropVolts, b.Sinks[0].DropVolts * 0.02);

        // And it solves thermally: the barrel is conformal with the laminate around it
        // (were it not, the heat solve would see a free-floating piece and refuse).
        var report = RailThermalAnalysis.Solve(onBoard, net, new RailThermalSetup
        {
            Rail = Setup(onBoard), Copper = Copper, Laminate = Fr4,
            Environment = new EnvironmentSettings { AmbientTemperature = 293.15 }
        });
        Assert.True(report.PeakCopperKelvin > 293.15);
    }
}
