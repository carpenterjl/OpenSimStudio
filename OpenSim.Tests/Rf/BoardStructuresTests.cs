using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Network;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Tests.Rf.MultiLayerProbeFixtures;

namespace OpenSim.Tests.Rf;

/// <summary>Feature 12: pins (vias, shorting pins), lumped parts at ports, matching, and the
/// finite-ground air model.</summary>
public class BoardStructuresTests
{
    private readonly ITestOutputHelper _out;
    public BoardStructuresTests(ITestOutputHelper output) => _out = output;

    private const double Mu0 = 4e-7 * Math.PI;

    [Fact]
    public void OnePortPin_IsTheProbeFedSolve()
    {
        // The pin array is the single-probe assembly per pin, so one pin must be that solve.
        var surface = Plate(CoarseEdge);
        var probe = Probe();
        var table = new LayeredKernelTable(Substrate, Frequency, 0.025);
        var single = new SurfaceMomSolver().SolveProbeFed(surface, table, probe).Surface.InputImpedance;
        var pins = new SurfaceMomSolver().SolvePins(surface, table, new[] { new VerticalPin("feed", probe) });
        var z = pins.Impedance()[0, 0];
        double rel = (z - single).Magnitude / single.Magnitude;
        _out.WriteLine($"probe-fed {single}, pin array {z}, rel {rel:e3}");
        Assert.True(rel <= 1e-12, $"rel {rel:e3}");
    }

    /// <summary>A driven pin and a shorting pin 9 mm apart between a 60 mm plate and the ground at
    /// 30 MHz: the feed sees the loop inductance of two posts in a parallel-plate region,
    /// (µ0·h/π)·ln(s/a) = 2.436 nH, which cannot depend on the dielectric. Measured 2.3712 nH in air
    /// and 2.3758 nH at εr = 4.4 (1.668 nH at εr = 4.4 without the junction vertex term).</summary>
    [Fact]
    public void FeedAndShortingPin_AtLowFrequency_IsTheTwoPostLoopInductance_InAnyDielectric()
    {
        const double h = 1.6e-3, a = 0.2e-3, f = 30e6, s = 9e-3;
        double expected = Mu0 * h / Math.PI * Math.Log(s / a);
        var surface = SurfaceMeshBuilder.BuildRectangularPlate(0.06, 0.06, 3e-3, z: h).Structure!;
        double Inductance(double epsR)
        {
            var table = new LayeredKernelTable(new SubstrateStackup(epsR, 0, h), f, 0.1);
            var solution = new SurfaceMomSolver().SolvePins(surface, table, new[]
            {
                new VerticalPin("feed", new ProbeFeed(-3e-3, 0, a, 3)),
                new VerticalPin("short", new ProbeFeed(6e-3, 0, a, 3), IsPort: false)
            });
            return solution.Impedance()[0, 0].Imaginary / (2 * Math.PI * f);
        }
        double air = Inductance(1.0), fr4 = Inductance(4.4);
        _out.WriteLine($"air {air * 1e9:F4} nH, εr 4.4 {fr4 * 1e9:F4} nH, two-post {expected * 1e9:F4} nH");
        Assert.True(Math.Abs(fr4 - air) / air <= 0.005, $"εr changes L: {air * 1e9:F4} vs {fr4 * 1e9:F4} nH");
        Assert.True(Math.Abs(air - expected) / expected <= 0.04, $"air {air * 1e9:F4} nH vs {expected * 1e9:F4} nH");
        Assert.True(Math.Abs(fr4 - expected) / expected <= 0.04, $"εr 4.4 {fr4 * 1e9:F4} nH vs {expected * 1e9:F4} nH");
    }

    [Fact]
    public void TerminatingAPort_IsTheSolveWithThatPinShorted_AndTheNetworkIsReciprocal()
    {
        // Measured: 7e-17 and 4e-13.
        const double f = 4e9;
        var surface = SurfaceMeshBuilder.BuildRectangularPlate(0.012, 0.009, 1.5e-3, z: Thickness).Structure!;
        var table = new LayeredKernelTable(Substrate, f, 0.025);
        var a = new VerticalPin("a", new ProbeFeed(-3e-3, 0, ProbeRadius, 3));
        var b = new VerticalPin("b", new ProbeFeed(3e-3, 0, ProbeRadius, 3));
        var solver = new SurfaceMomSolver();
        var z2 = solver.SolvePins(surface, table, new[] { a, b }).Impedance();
        var shorted = solver.SolvePins(surface, table, new[] { a, b with { IsPort = false } }).Impedance()[0, 0];
        var reduced = PortTermination.Terminate(z2, new Dictionary<int, Complex> { [1] = 0 })[0, 0];
        double rel = (reduced - shorted).Magnitude / shorted.Magnitude;
        double recip = (z2[0, 1] - z2[1, 0]).Magnitude / z2[0, 1].Magnitude;
        _out.WriteLine($"shorted {shorted}, reduced {reduced}, rel {rel:e3}, reciprocity {recip:e3}");
        Assert.True(rel <= 1e-9, $"rel {rel:e3}");
        Assert.True(recip <= 1e-9, $"reciprocity {recip:e3}");
    }

    [Fact]
    public void MultiLayerPins_AtOneLayer_AreTheSingleSlabPins()
    {
        const double f = 4e9;
        var surface = SurfaceMeshBuilder.BuildRectangularPlate(0.012, 0.009, 1.5e-3, z: Thickness).Structure!;
        var pins = new[]
        {
            new VerticalPin("a", new ProbeFeed(-3e-3, 0, ProbeRadius, 3)),
            new VerticalPin("b", new ProbeFeed(3e-3, 0, ProbeRadius, 3), IsPort: false)
        };
        var solver = new SurfaceMomSolver();
        var slab = solver.SolvePins(surface, new LayeredKernelTable(Substrate, f, 0.025), pins).Impedance()[0, 0];
        var multi = solver.SolvePins(surface, new MultiLayerKernelTable(OneLayer, f, 0.025), pins).Impedance()[0, 0];
        double rel = (slab - multi).Magnitude / slab.Magnitude;
        _out.WriteLine($"slab {slab}, multi-layer {multi}, rel {rel:e3}");
        Assert.True(rel <= 5e-6, $"rel {rel:e3}");
    }

    [Fact]
    public void LSection_TransformsEveryLoadToTheReference()
    {
        var loads = new[] { new Complex(200, -100), new Complex(10, 25), new Complex(80, 300), new Complex(30, -5), new Complex(50, 40) };
        foreach (var load in loads)
        {
            var designs = MatchingNetwork.DesignLSection(load, 50, 1e9);
            Assert.NotEmpty(designs);
            foreach (var d in designs)
            {
                var zin = d.InputImpedance(load, 1e9);
                Assert.True((zin - 50).Magnitude < 1e-9, $"{load}: {d.Describe()} gives {zin}");
            }
        }
        // Q-limited parts: no longer exact, still a match, and lossy.
        var lossy = MatchingNetwork.DesignLSection(new Complex(10, 25), 50, 1e9, inductorQ: 40, capacitorQ: 200);
        foreach (var d in lossy)
        {
            var zin = d.InputImpedance(new Complex(10, 25), 1e9);
            Assert.InRange(PortTermination.Reflection(zin, 50).Magnitude, 1e-6, 0.2);
        }
    }

    [Fact]
    public void LSection_OnASeriesResonator_GivesTheBandAroundTheDesignFrequency()
    {
        // A series RLC "antenna", 20 Ω at 1 GHz, matched there; the −10 dB band brackets 1 GHz.
        double r = 20, l = 20e-9, c = 1 / (Math.Pow(2 * Math.PI * 1e9, 2) * l);
        Complex Z(double f) => new Complex(r, 2 * Math.PI * f * l - 1 / (2 * Math.PI * f * c));
        var sweep = Enumerable.Range(0, 201).Select(i => 0.9e9 + i * 1e6).Select(f => (f, Z(f))).ToList();
        var design = MatchingNetwork.DesignLSection(Z(1e9), 50, 1e9)[0];
        var matched = MatchingNetwork.Apply(design, sweep);
        var band = MatchingNetwork.Band(matched, 50);
        Assert.NotNull(band);
        _out.WriteLine($"{design.Describe()}: band {band!.Value.Low / 1e9:F4}–{band.Value.High / 1e9:F4} GHz");
        Assert.True(band.Value.Low < 1e9 && band.Value.High > 1e9);
        Assert.Null(MatchingNetwork.Band(sweep.Select(p => (p.f, p.Item2)).ToList(), 50, 20));
    }

    /// <summary>The shorted feed of <see cref="FeedAndShortingPin_AtLowFrequency_IsTheTwoPostLoopInductance_InAnyDielectric"/>
    /// in the AIR model: strips 4a wide (a flat strip's equivalent radius) standing on an image
    /// ground under a 20 mm plate, against the layered pins of radius a in air. Measured +6.3 %
    /// on a uniform 2 mm grid, +4.8 % graded from 0.5 mm, +3.4 % from 0.25 mm, +2.2 % from
    /// 0.125 mm — converging from above onto the layered value.</summary>
    [Fact]
    public void AirModel_ConvergesOnTheLayeredPinsInAir()
    {
        const double h = 4e-3, a = 0.25e-3, f = 30e6, x = 4e-3;
        var plate = SurfaceMeshBuilder.BuildRectangularPlate(0.02, 0.02, 2e-3, z: h).Structure!;
        var table = new LayeredKernelTable(new SubstrateStackup(1.0, 0, h), f, 0.04);
        double layered = new SurfaceMomSolver().SolvePins(plate, table, new[]
        {
            new VerticalPin("feed", new ProbeFeed(-x, 0, a, 3)),
            new VerticalPin("short", new ProbeFeed(x, 0, a, 3), IsPort: false)
        }).Impedance()[0, 0].Imaginary / (2 * Math.PI * f);

        double coarse = AirInductance(h, x, 4 * a, f, 0.5e-3, groundHalfWidth: null);
        double fine = AirInductance(h, x, 4 * a, f, 0.25e-3, groundHalfWidth: null);
        _out.WriteLine($"layered {layered * 1e9:F4} nH, air graded 0.5 mm {coarse * 1e9:F4}, 0.25 mm {fine * 1e9:F4}");
        Assert.True(coarse > fine && fine > layered, "the air model should converge from above");
        Assert.True((fine - layered) / layered <= 0.04, $"rel {(fine - layered) / layered:e3}");
    }

    /// <summary>The same air model with a meshed 24 mm ground plate in place of the image ground:
    /// the strips now land on the plate through junction edges. Measured +0.74 % against the image
    /// ground at the same grading (the finite ground lets flux round its edges).</summary>
    [Fact]
    public void AirModel_FiniteGroundPlate_IsCloseToTheImageGround()
    {
        const double h = 4e-3, a = 0.25e-3, f = 30e6, x = 4e-3;
        double image = AirInductance(h, x, 4 * a, f, 0.5e-3, groundHalfWidth: null);
        double plate = AirInductance(h, x, 4 * a, f, 0.5e-3, groundHalfWidth: 0.012);
        _out.WriteLine($"image ground {image * 1e9:F4} nH, 24 mm plate {plate * 1e9:F4} nH");
        Assert.InRange((plate - image) / image, 0.0, 0.02);
    }

    private double AirInductance(double h, double x, double w, double f, double minEdge, double? groundHalfWidth)
    {
        var parts = new List<SheetRectangle>
        {
            new("top", new Vector3D(-0.01, -0.01, h), new Vector3D(0.01, 0.01, h)),
            new("feed", new Vector3D(-x - w / 2, 0, 0), new Vector3D(-x + w / 2, 0, h)),
            new("short", new Vector3D(x - w / 2, 0, 0), new Vector3D(x + w / 2, 0, h)),
        };
        if (groundHalfWidth is { } g) parts.Add(new("ground", new Vector3D(-g, -g, 0), new Vector3D(g, g, 0)));
        var ports = new[] { new SheetPortLine("feed", new Vector3D(-x - w / 2, 0, h / 2), new Vector3D(-x + w / 2, 0, h / 2), new Vector3D(0, 0, 1)) };
        var model = SheetMetalAssembly.Build(parts, ports, 2e-3, maxUnknowns: 4000, minEdgeLength: minEdge,
            imageGround: groundHalfWidth is null ? new GroundPlane(0) : null);
        _out.WriteLine(string.Join("; ", model.Notes));
        var z = 1 / new SurfaceMomSolver().SolveMultiPort(model.Structure, f, model.Ports.Select(p => p.Port).ToList()).Admittance[0, 0];
        return z.Imaginary / (2 * Math.PI * f);
    }

    [Fact]
    public void SheetAssembly_JunctionEdges_CarryOneBasisPerExtraPart()
    {
        // A strip standing in the middle of a plate: its foot is an edge shared by three
        // triangles (two of the plate, one of the strip), so it carries two bases.
        var parts = new[]
        {
            new SheetRectangle("plate", new Vector3D(-0.01, -0.01, 0), new Vector3D(0.01, 0.01, 0)),
            new SheetRectangle("strip", new Vector3D(-0.001, 0, 0), new Vector3D(0.001, 0, 0.005)),
        };
        var model = SheetMetalAssembly.Build(parts, Array.Empty<SheetPortLine>(), 2e-3);
        var foot = model.Structure.Edges.Where(e => model.Structure.Vertices[e.V1].Z == 0 && model.Structure.Vertices[e.V2].Z == 0
            && model.Structure.Vertices[e.V1].Y == 0 && model.Structure.Vertices[e.V2].Y == 0
            && Math.Abs(model.Structure.Vertices[e.V1].X) <= 0.001 && Math.Abs(model.Structure.Vertices[e.V2].X) <= 0.001).ToList();
        Assert.Equal(model.Structure.JunctionEdgeCount * 2, foot.Count);
        Assert.True(model.Structure.JunctionEdgeCount >= 1);
        Assert.Throws<ArgumentException>(() => SheetMetalAssembly.Build(new[]
        {
            parts[0], new SheetRectangle("overlap", new Vector3D(0, 0, 0), new Vector3D(0.005, 0.005, 0))
        }, Array.Empty<SheetPortLine>(), 2e-3));
    }

    [Fact]
    public void PrintedDipoleThroughABridge_IsTheDipoleMeshedWhole()
    {
        // Two arms as "antenna" and "ground" joined by the feed bridge make the same strip as the
        // dipole outlined in one piece. Measured 8.8e-6 apart.
        const double len = 0.06, w = 2e-3, gap = 2e-3, f = 2.3e9;
        Polygon2 Rect(double x0, double x1) => new(new[] { new Point2(x0, -w / 2), new Point2(x1, -w / 2), new Point2(x1, w / 2), new Point2(x0, w / 2) });
        var model = PrintedAntennaBuilder.Build(Rect(gap / 2, len / 2), new[] { Rect(-len / 2, -gap / 2) }, new Point2(0, 0), w, 2e-3);
        foreach (var n in model.Notes) _out.WriteLine(n);
        var bridged = new SurfaceMomSolver().Solve(model.Structure, f, model.Port).InputImpedance;
        var whole = SurfaceMeshBuilder.BuildFromPolygon(Rect(-len / 2, len / 2), 2e-3, 0, new Point2(0, 0));
        var direct = new SurfaceMomSolver().Solve(whole.Structure!, f, whole.Port!).InputImpedance;
        double rel = (bridged - direct).Magnitude / direct.Magnitude;
        _out.WriteLine($"bridged {bridged}, whole {direct}, rel {rel:e3}");
        Assert.True(rel <= 1e-4, $"rel {rel:e3}");
    }

    [Fact]
    public void PrintedMonopoleBesideAGroundPour_HasItsPortAcrossTheBridge()
    {
        // A 30 mm printed monopole 1 mm from the edge of a 40 × 30 mm pour.
        var trace = new Polygon2(new[] { new Point2(-0.0005, 0.001), new Point2(0.0005, 0.001), new Point2(0.0005, 0.031), new Point2(-0.0005, 0.031) });
        var pour = new Polygon2(new[] { new Point2(-0.02, -0.03), new Point2(0.02, -0.03), new Point2(0.02, 0), new Point2(-0.02, 0) });
        var model = PrintedAntennaBuilder.Build(trace, new[] { pour }, new Point2(0, 0.0005), 1e-3, 3e-3);
        Assert.Equal(0.001, model.AntennaFeedPoint.Y, 9);
        Assert.Equal(0.0, model.GroundFeedPoint.Y, 9);
        var z = new SurfaceMomSolver().Solve(model.Structure, 2.2e9, model.Port).InputImpedance;
        _out.WriteLine($"Zin at 2.2 GHz {z}; {string.Join("; ", model.Notes)}");
        Assert.True(z.Real > 0);
    }
}
