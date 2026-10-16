using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using Xunit.Abstractions;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The compressed iterative solve (FU-33) against the dense factorization it replaces at
/// scale: the same models solved both ways must agree to the compression and iteration
/// tolerances, on every port entry, at low and high frequency.
/// </summary>
public class PeecIterativeTests
{
    private const double Sigma = 5.8e7;
    private readonly ITestOutputHelper _output;
    public PeecIterativeTests(ITestOutputHelper output) => _output = output;

    private static PeecModel TraceOverPlane(PeecSolver solver)
    {
        // A trace 0.3 mm over an 8 × 4 mm plane, returned through it: bundles, a grid and a
        // port path through both.
        var model = new PeecModel { Solver = solver };
        var plane = model.AddPlane(new[] { new Polygon2(new[]
            { new Point2(-1e-3, -2e-3), new Point2(7e-3, -2e-3), new Point2(7e-3, 2e-3), new Point2(-1e-3, 2e-3) }) },
            z: -17.5e-6, 35e-6, pitch: 0.4e-3, Sigma);
        double zt = 0.3e-3 + 17.5e-6;
        int start = model.AddNode(new Vector3D(0.2e-3, 0.2e-3, zt)), end = model.AddNode(new Vector3D(6e-3, 0.2e-3, zt));
        model.AddBar(start, end, 0.5e-3, 35e-6, Sigma, acrossWidth: 5, acrossThickness: 3);
        model.AddShort(end, plane.NodeNear(new Point2(6e-3, 0.2e-3)));
        model.AddPort("loop", start, plane.NodeNear(new Point2(0.2e-3, 0.2e-3)));
        return model;
    }

    [Fact]
    public void TheIterativeSolve_AgreesWithTheDenseSolve_OverAPlane()
    {
        var frequencies = new[] { 1e3, 1e6, 100e6 };
        var dense = TraceOverPlane(PeecSolver.Dense).Solve(frequencies);
        var iterative = TraceOverPlane(PeecSolver.Iterative).Solve(frequencies);
        for (int k = 0; k < frequencies.Length; k++)
        {
            var a = dense[k].Impedance[0, 0];
            var b = iterative[k].Impedance[0, 0];
            double relative = (a - b).Magnitude / a.Magnitude;
            _output.WriteLine($"{frequencies[k]:g4} Hz: dense {a}, iterative {b}, relative {relative:e2}");
            Assert.True(relative < 2e-6, $"{frequencies[k]} Hz: {relative}");
        }
    }

    [Fact]
    public void ATubeAtLowFrequency_HasTheShellsResistance_AndTheTubesInductance()
    {
        // A plated barrel as a ring of filaments: at DC the resistance is the shell's exactly,
        // and the ring's inductance is the thin tube's (GMD = the shell radius), where a solid
        // wire of the same diameter would read higher.
        double d = 0.6e-3, wall = 25e-6, l = 1.6e-3;
        var model = new PeecModel();
        int a = model.AddNode(new Vector3D(0, 0, 0)), b = model.AddNode(new Vector3D(0, 0, l));
        model.AddTube(a, b, d, wall, Sigma);
        model.AddPort("barrel", a, b);
        var point = model.Solve(new[] { 1.0 })[0];
        double shell = l / (Sigma * Math.PI * wall * (d - wall));
        Assert.Equal(shell, point.Resistance(0, 0), 1e-9 * shell);
        double tube = PartialInductance.RoundTubeSelfInductance(l, (d - wall) / 2);
        double wire = PartialInductance.RoundWireSelfInductance(l, d / 2);
        _output.WriteLine($"ring of 8: {point.Inductance(0, 0) * 1e12:f2} pH; tube {tube * 1e12:f2} pH, solid wire {wire * 1e12:f2} pH");
        // Eight chords against the smooth shell: measured 2026-10-15 at −0.8 % of the tube's
        // closed form; the band allows 2 %. The solid wire is the wrong answer by far more.
        Assert.Equal(tube, point.Inductance(0, 0), 0.02 * tube);
        Assert.True(Math.Abs(point.Inductance(0, 0) - tube) < 0.3 * Math.Abs(wire - tube));
    }

    [Fact]
    public void AGradedPlane_CarriesALongLoopPastTheOldNodeBudget()
    {
        // A 25 mm trace 0.3 mm over a 32 × 16 mm plane: the graded mesh keeps the trace's
        // height under the chain and grows 1.3 outward, and the compressed solve carries the
        // model where the dense one (and the old 1400-node budget) could not. At 300 MHz the
        // loop must sit on the 2D line, as the small uniform model does.
        double w = 0.5e-3, t = 35e-6, h = 0.3e-3, l = 25e-3;
        var chain = new[] { new TraceSegment3D(new Vector3D(0, 0, h + t / 2 + 17.5e-6),
            new Vector3D(l, 0, h + t / 2 + 17.5e-6), w, t) };
        var plane = new[] { new Polygon2(new[]
        {
            new Point2(-3.5e-3, -8e-3), new Point2(28.5e-3, -8e-3), new Point2(28.5e-3, 8e-3), new Point2(-3.5e-3, 8e-3)
        }) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = OpenSim.Rf.Extraction.LoopExtraction.ChainOverPlane(chain, plane, (-35e-6, 0),
            new[] { 300e6 }, new OpenSim.Rf.Extraction.LoopExtractionOptions { PlaneStripsAcrossThickness = 2 });
        watch.Stop();
        foreach (string line in result.Assumptions) _output.WriteLine(line);
        _output.WriteLine($"{result.Filaments} filaments in {watch.Elapsed.TotalSeconds:f1} s");
        Assert.True(result.Filaments > PeecModel.DenseLimit, "the model should be past the dense limit");
        Assert.Contains(result.Assumptions, a => a.Contains("compressed iterative"));

        var section = new CoupledLineCrossSection(new LayeredStackup(new[] { new LayeredStackup.Layer(1.0, 0, h) }), 0,
            new[] { new TraceCrossSection(0, w, t, Sigma) });
        double perLength = RlgcExtractor.Extract(section).InductanceHenriesPerMeter[0, 0];
        double high = result.Points[0].Inductance(0, 0);
        _output.WriteLine($"loop {high * 1e9:f3} nH, line L'·l {perLength * l * 1e9:f3} nH ({high / (perLength * l) - 1:p1})");
        // The line has no ends; the loop has two connections and the plane's spreading.
        // Measured 2026-10-15: +1.3 %; the band allows 6 %.
        Assert.Equal(perLength * l, high, 0.06 * perLength * l);
    }

    [Fact]
    public void TheIterativeSolve_AgreesWithTheDenseSolve_OnTwoCoupledPorts()
    {
        PeecModel Build(PeecSolver solver)
        {
            var model = new PeecModel { Solver = solver };
            for (int k = 0; k < 2; k++)
            {
                int a = model.AddNode(new Vector3D(0, k * 0.8e-3, 0)), b = model.AddNode(new Vector3D(12e-3, k * 0.8e-3, 0));
                model.AddBar(a, b, 0.4e-3, 35e-6, Sigma, acrossWidth: 5, acrossThickness: 3);
                model.AddPort($"trace {k + 1}", a, b);
            }
            return model;
        }
        var dense = Build(PeecSolver.Dense).Solve(new[] { 30e6 }, keepCurrents: true)[0];
        var iterative = Build(PeecSolver.Iterative).Solve(new[] { 30e6 }, keepCurrents: true)[0];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                double relative = (dense.Impedance[i, j] - iterative.Impedance[i, j]).Magnitude
                    / dense.Impedance[i, i].Magnitude;
                _output.WriteLine($"Z[{i},{j}]: dense {dense.Impedance[i, j]}, iterative {iterative.Impedance[i, j]} ({relative:e2})");
                Assert.True(relative < 2e-6, $"Z[{i},{j}]: {relative}");
            }
        // The filament currents agree too: one ampere through port 1, none net through port 2.
        System.Numerics.Complex first = 0, second = 0;
        for (int i = 0; i < 15; i++)
        {
            first += iterative.FilamentCurrents![0][i];
            second += iterative.FilamentCurrents[0][15 + i];
        }
        Assert.True((first - 1).Magnitude < 1e-8 && second.Magnitude < 1e-8);
    }
}
