using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Network;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>FU-47: the library pieces the antenna panel's pins, ports and printed antennas stand on —
/// driving one port of a multi-port with the others loaded or open, pins found among a board's vias,
/// and a printed antenna built from the nets themselves.</summary>
public class PortDriveTests
{
    private readonly ITestOutputHelper _out;
    public PortDriveTests(ITestOutputHelper output) => _out = output;

    /// <summary>A passive, reciprocal 4-port: Z = R + jX with R symmetric positive definite.</summary>
    private static Complex[,] FourPort() => new Complex[,]
    {
        { new(50, 20), new(8, -5), new(3, 12), new(1, 2) },
        { new(8, -5), new(42, -30), new(6, 4), new(2, -1) },
        { new(3, 12), new(6, 4), new(61, 7), new(5, 3) },
        { new(1, 2), new(2, -1), new(5, 3), new(35, 15) },
    };

    [Fact]
    public void DrivingAPort_MeetsEveryClosure_AndSeesTheTerminatedImpedance()
    {
        var z = FourPort();
        var loads = new Dictionary<int, Complex> { [1] = new(50, 0), [3] = Complex.Zero };   // port 2 open
        var (v, i) = PortTermination.Drive(z, 0, loads);
        // V = Z·I everywhere, the source's volt at the driven port, the loads' laws, no current open.
        for (int p = 0; p < 4; p++)
        {
            Complex zi = Complex.Zero;
            for (int q = 0; q < 4; q++) zi += z[p, q] * i[q];
            Assert.True((zi - v[p]).Magnitude <= 1e-12 * 50, $"port {p}: Z·I {zi} vs V {v[p]}");
        }
        Assert.Equal(Complex.One, v[0]);
        Assert.True((v[1] + loads[1] * i[1]).Magnitude <= 1e-13, $"port 1: V {v[1]}, −Z_L·I {-loads[1] * i[1]}");
        Assert.True(v[3].Magnitude <= 1e-13, $"shorted port 3 has {v[3]} V");
        Assert.Equal(Complex.Zero, i[2]);

        // Seen from the driven port: the open port is simply absent, the loads close theirs.
        var kept = new[] { 0, 1, 3 };
        var sub = new Complex[3, 3];
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++) sub[a, b] = z[kept[a], kept[b]];
        var reduced = PortTermination.Terminate(sub, new Dictionary<int, Complex> { [1] = loads[1], [2] = loads[3] })[0, 0];
        var zin = v[0] / i[0];
        _out.WriteLine($"Zin {zin}, terminated {reduced}");
        Assert.True((zin - reduced).Magnitude <= 1e-12 * reduced.Magnitude, $"{zin} vs {reduced}");

        Assert.Throws<ArgumentException>(() => PortTermination.Drive(z, 1, loads));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortTermination.Drive(z, 4, new Dictionary<int, Complex>()));
    }

    [Fact]
    public void DrivingAPinArray_ThroughItsImpedance_IsItsOwnSuperposition()
    {
        // Two feed pins: drive the first, close the second with 50 Ω. The pin array's currents for
        // those port voltages carry exactly the port currents Drive found.
        const double h = 1.588e-3, f = 4e9;
        var surface = SurfaceMeshBuilder.BuildRectangularPlate(0.012, 0.009, 1.5e-3, z: h).Structure!;
        var table = new LayeredKernelTable(new SubstrateStackup(2.2, 0, h), f, 0.025);
        var pins = new[]
        {
            new VerticalPin("a", new ProbeFeed(-3e-3, 0, 0.2e-3, 3)),
            new VerticalPin("b", new ProbeFeed(3e-3, 0, 0.2e-3, 3))
        };
        var solution = new SurfaceMomSolver().SolvePins(surface, table, pins);
        var (v, i) = PortTermination.Drive(solution.Impedance(), 0, new Dictionary<int, Complex> { [1] = 50 });
        var currents = solution.Currents(v);
        for (int p = 0; p < 2; p++)
            Assert.True((currents.PortCurrents[p] - i[p]).Magnitude <= 1e-9 * i[0].Magnitude,
                $"port {p}: {currents.PortCurrents[p]} vs {i[p]}");
        // Power: what the source delivers is what the array takes in at both ports (the loaded one
        // gives back what the load dissipates) plus the load's share.
        double source = 0.5 * (v[0] * Complex.Conjugate(i[0])).Real;
        double load = 0.5 * 50 * i[1].Magnitude * i[1].Magnitude;
        _out.WriteLine($"source {source:e6} W, load {load:e6} W, array net {currents.InputPowerWatts:e6} W");
        Assert.True(load > 0);
        Assert.True(Math.Abs(currents.InputPowerWatts + load - source) <= 1e-9 * source,
            $"array {currents.InputPowerWatts} + load {load} vs source {source}");
    }

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) => new(new[]
    {
        new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1)
    });

    /// <summary>A patch on L1 over a plane on L2, one net: a shorting via joins them, a feed via rises
    /// through the plane's antipad; a via off the patch and an unplated hole complete the set.</summary>
    private static (PcbBoard Board, CopperNet Net, CopperIsland Patch) PatchOverPlane()
    {
        var patch = new CopperIsland(0, 1, "L1", Rect(-0.01, -0.01, 0.01, 0.01));
        var plane = new CopperIsland(1, 2, "L2", Rect(-0.02, -0.02, 0.02, 0.02));
        var shorting = new Via(new Point2(5e-3, 0), 0.4e-3, Plated: true);
        var feed = new Via(new Point2(-5e-3, 0), 0.6e-3, Plated: true);
        var outside = new Via(new Point2(15e-3, 0), 0.4e-3, Plated: true);
        var hole = new Via(new Point2(0, 5e-3), 1e-3, Plated: false);
        var net = new CopperNet(0, new[] { patch, plane })
        {
            Name = "ANT",
            StitchingVias = new[] { new ViaBridge(shorting, new[] { 1, 2 }), new ViaBridge(outside, new[] { 1, 2 }) }
        };
        var board = new PcbBoard
        {
            Outline = new[] { Rect(-0.02, -0.02, 0.02, 0.02) }, Islands = new[] { patch, plane }, Pads = Array.Empty<CopperPad>(),
            Vias = new[] { shorting, feed, outside, hole }, Nets = new[] { net },
            Layers = Array.Empty<BoardLayer>(), Warnings = Array.Empty<string>()
        };
        return (board, net, patch);
    }

    [Fact]
    public void BoardPins_AreTheViasOnTheIsland_ShortingWhereTheNetJoinsThePlane()
    {
        var (board, net, patch) = PatchOverPlane();
        var resolved = BoardAntennaStackup.Resolve(board, 1, new BoardStackup(), patch.Shape);
        Assert.Equal(2, resolved.GroundLayer);
        var pins = BoardPins.OnIsland(board, net, patch, resolved.GroundLayer);
        foreach (var pin in pins) _out.WriteLine(pin.Description);
        Assert.Equal(2, pins.Count);
        Assert.Equal(new Point2(5e-3, 0), pins[0].Position);
        Assert.True(pins[0].ShortsToGround);
        Assert.Equal(0.2e-3, pins[0].RadiusMeters, 15);
        Assert.Equal(new Point2(-5e-3, 0), pins[1].Position);
        Assert.False(pins[1].ShortsToGround);
        Assert.Throws<ArgumentOutOfRangeException>(() => BoardPins.OnIsland(board, net, patch, 1));
    }

    [Fact]
    public void PrintedAntenna_FromTheNets_IsTheBuildFromTheirCopper()
    {
        // BoardStructuresTests' printed dipole half: a 30 mm trace above a pour, as two nets on L1 —
        // one of them unnamed (a Gerber set without net names), which the name lookup cannot reach.
        var trace = new Polygon2(new[] { new Point2(-0.0005, 0.001), new Point2(0.0005, 0.001), new Point2(0.0005, 0.031), new Point2(-0.0005, 0.031) });
        var pour = Rect(-0.02, -0.03, 0.02, 0);
        var antennaIsland = new CopperIsland(0, 1, "L1", trace);
        var groundIsland = new CopperIsland(1, 1, "L1", pour);
        var antenna = new CopperNet(0, new[] { antennaIsland });
        var ground = new CopperNet(1, new[] { groundIsland }) { Name = "GND" };
        var feedNear = new Point2(0, 0.0005);
        var built = PrintedAntennaBuilder.Build(trace, new[] { pour }, feedNear, 1e-3, 3e-3);
        var fromNets = PrintedAntennaBuilder.FromNets(1, antenna, ground, feedNear, 1e-3, 3e-3);
        Assert.Equal(built.Structure.Vertices, fromNets.Structure.Vertices);
        var solver = new SurfaceMomSolver();
        var z0 = solver.Solve(built.Structure, 2.2e9, built.Port).InputImpedance;
        var z1 = solver.Solve(fromNets.Structure, 2.2e9, fromNets.Port).InputImpedance;
        _out.WriteLine($"Build {z0}, FromNets {z1}");
        Assert.Equal(z0, z1);
        var refusal = Assert.Throws<ArgumentException>(() => PrintedAntennaBuilder.FromNets(1, ground, ground, feedNear, 1e-3, 3e-3));
        Assert.Contains("same net", refusal.Message);
        var noCopper = Assert.Throws<ArgumentException>(() => PrintedAntennaBuilder.FromNets(2, antenna, ground, feedNear, 1e-3, 3e-3));
        Assert.Contains("Net 0", noCopper.Message);
    }
}
