using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Fix 12 — a wire antenna taken from a board net keeps the WHOLE net.
///
/// <para>The net mode built its antenna from the path between the two farthest pads and pruned
/// every side branch. That is exact for a DC current (a dead-end branch carries none) and wrong
/// at RF, where an open stub carries a standing wave: it loads the structure and moves its
/// resonance. These tests go through the same calls the app makes — trace graph, wires, grid,
/// solve — and through the checks that now accompany a thin-wire result.</para>
/// </summary>
public class BranchedNetAntennaTests
{
    private readonly ITestOutputHelper _output;
    public BranchedNetAntennaTests(ITestOutputHelper output) => _output = output;

    private const double C0 = 299792458.0;
    private static readonly NetMeshOptions Options = new()
    {
        CopperThickness = 35e-6, DefaultDielectricThickness = 1.6e-3, ViaPlatingThickness = 25e-6
    };

    /// <summary>A 100 mm trace dipole along x with an open stub of the given length standing on
    /// the middle of its +x arm.</summary>
    private static List<TraceCenterline> StubbedDipole(double stubLength)
    {
        const double width = 0.4e-3;
        var traces = new List<TraceCenterline>
        {
            new(1, new Point2(-50e-3, 0), new Point2(0, 0), width),
            new(1, new Point2(0, 0), new Point2(50e-3, 0), width)
        };
        if (stubLength > 0)
            traces.Add(new TraceCenterline(1, new Point2(25e-3, 0), new Point2(25e-3, stubLength), width));
        return traces;
    }

    private static (WireStructure Wire, int Feed, TraceGraphAntenna Antenna) FromNet(
        IReadOnlyList<TraceCenterline> traces)
    {
        var graph = TraceChainBuilder.BuildGraph(traces, Array.Empty<ViaBridge>(), Options);
        Assert.True(graph.Segments is not null, graph.FailureReason);
        var antenna = TraceChainAntenna.FromGraph(graph);
        var grid = WireGridBuilder.Build(antenna.Wires, maxElementLength: 4e-3);
        Assert.True(grid.Structure is not null, grid.FailureReason);
        double z = antenna.Wires[0].A.Z;
        return (grid.Structure!, grid.Structure!.NearestBasis(new Vector3D(0, 0, z)), antenna);
    }

    /// <summary>The first series resonance (X = 0, rising) between 0.9 and 1.7 GHz.</summary>
    private static double Resonance(WireStructure wire, int feed)
    {
        var solver = new ThinWireMomSolver();
        double X(double f) => solver.Solve(wire, f, feed).InputImpedance.Imaginary;
        double low = 0.9e9, high = 1.7e9;
        Assert.True(X(low) < 0 && X(high) > 0, $"no resonance bracketed: {X(low):g4}, {X(high):g4}");
        for (int i = 0; i < 40; i++)
        {
            double mid = 0.5 * (low + high);
            if (X(mid) < 0) low = mid; else high = mid;
        }
        return 0.5 * (low + high);
    }

    [Fact]
    public void TheNetGraph_KeepsTheStub_ThatThePadToPadPathPrunes()
    {
        var traces = StubbedDipole(20e-3);
        var (wire, _, antenna) = FromNet(traces);

        Assert.Equal(1, antenna.BranchNodes);
        Assert.Equal(3, antenna.OpenEnds);
        Assert.Equal(0, antenna.DroppedPieces);
        Assert.Equal(120e-3, antenna.Wires.Sum(w => w.Length), 9);
        Assert.True(wire.IsBranched);
        Assert.Equal(120e-3, wire.TotalLength(), 9);

        // What the app used to build: the path between the two end pads, stub pruned.
        var pads = (new ChainTerminal(new Point2(-50e-3, 0), 1), new ChainTerminal(new Point2(50e-3, 0), 1));
        var chain = TraceChainBuilder.Build(traces, Array.Empty<ViaBridge>(), Options, null, null, pads);
        Assert.True(chain.Chain is not null, chain.FailureReason);
        Assert.Equal(100e-3, TraceChainAntenna.FromChain(chain.Chain!).Sum(w => w.Length), 9);
        Assert.True(chain.PrunedLengthMeters > 19e-3);
    }

    [Fact]
    public void AStubLoadedDipole_ResonatesLower_TheLongerItsStub()
    {
        // The plan's gate. With the stub pruned, all four of these are the same antenna.
        var resonances = new List<double>();
        foreach (double stub in new[] { 0.0, 10e-3, 20e-3, 30e-3 })
        {
            var (wire, feed, _) = FromNet(StubbedDipole(stub));
            resonances.Add(Resonance(wire, feed));
            _output.WriteLine($"stub {stub * 1e3:g3} mm: resonance {resonances[^1] / 1e9:f4} GHz");
        }
        for (int i = 1; i < resonances.Count; i++)
            Assert.True(resonances[i] < resonances[i - 1] * 0.995,
                "resonance by stub length: " + string.Join(", ", resonances.Select(f => (f / 1e9).ToString("f4"))));
        Assert.True(resonances[^1] < 0.93 * resonances[0],
            $"a 30 mm stub moved the resonance only {1 - resonances[^1] / resonances[0]:P1}");
    }

    [Fact]
    public void ADisconnectedPieceOfTheNet_IsReportedNotSilentlyDropped()
    {
        var traces = StubbedDipole(20e-3);
        traces.Add(new TraceCenterline(1, new Point2(0, 40e-3), new Point2(12e-3, 40e-3), 0.4e-3));
        var graph = TraceChainBuilder.BuildGraph(traces, Array.Empty<ViaBridge>(), Options);
        Assert.True(graph.Segments is not null, graph.FailureReason);

        var longest = TraceChainAntenna.FromGraph(graph);
        Assert.Equal(1, longest.DroppedPieces);
        Assert.Equal(12e-3, longest.DroppedLengthMeters, 9);
        Assert.Equal(120e-3, longest.Wires.Sum(w => w.Length), 9);
        Assert.NotNull(WireGridBuilder.Build(longest.Wires, 4e-3).Structure);

        // Asked to keep the piece nearest a point, it keeps that one instead.
        double z = longest.Wires[0].A.Z;
        var island = TraceChainAntenna.FromGraph(graph, new Vector3D(6e-3, 40e-3, z));
        Assert.Equal(12e-3, island.Wires.Sum(w => w.Length), 9);
        Assert.Equal(120e-3, island.DroppedLengthMeters, 9);
    }

    // ------------------------------------------------------------------
    // A two-wire stub across the feed: the quarter-wave transformation
    // ------------------------------------------------------------------

    /// <summary>A dipole along z with a two-wire line of the given length running out along x
    /// from the two sides of its feed, open or shorted at the far end.</summary>
    private static (WireStructure Wire, int Feed) DipoleWithLineStub(double stubLength, bool shorted)
    {
        const double radius = 5e-4, half = 0.235, gap = 0.01;
        var p = new Vector3D(0, 0, gap);
        var q = new Vector3D(0, 0, -gap);
        var wires = new List<WireSegment>
        {
            new(q, Vector3D.Zero, radius), new(Vector3D.Zero, p, radius),
            new(p, new Vector3D(0, 0, half), radius), new(q, new Vector3D(0, 0, -half), radius)
        };
        if (stubLength > 0)
        {
            wires.Add(new WireSegment(p, new Vector3D(stubLength, 0, gap), radius));
            wires.Add(new WireSegment(q, new Vector3D(stubLength, 0, -gap), radius));
            if (shorted)
                wires.Add(new WireSegment(new Vector3D(stubLength, 0, gap),
                    new Vector3D(stubLength, 0, -gap), radius));
        }
        var grid = WireGridBuilder.Build(wires, maxElementLength: 0.0125);
        Assert.True(grid.Structure is not null, grid.FailureReason);
        return (grid.Structure!, grid.Structure!.NearestBasis(Vector3D.Zero));
    }

    [Fact]
    public void AQuarterWaveLineStub_TransformsItsTermination()
    {
        // A two-wire line (spacing 20 mm, radius 0.5 mm, Z0 = 120·acosh(20) = 443 Ω) across the
        // feed of a dipole. A quarter wavelength of line turns an OPEN far end into a short at
        // the feed and a SHORTED far end into an open: the first collapses the input impedance,
        // the second leaves the dipole's own.
        const double stub = 0.25;
        var solver = new ThinWireMomSolver();
        var (bare, bareFeed) = DipoleWithLineStub(0, false);
        var (open, openFeed) = DipoleWithLineStub(stub, false);
        var (shorted, shortedFeed) = DipoleWithLineStub(stub, true);

        // The open stub's short appears where the line is a quarter wave long (a little below
        // c/4l: the open end's fringing makes the line look longer).
        double bestFrequency = 0, bestMagnitude = double.MaxValue;
        for (double f = 240e6; f <= 330e6; f += 2.5e6)
        {
            double magnitude = solver.Solve(open, f, openFeed).InputImpedance.Magnitude;
            if (magnitude < bestMagnitude) (bestMagnitude, bestFrequency) = (magnitude, f);
        }
        double quarterWave = C0 / (4 * stub);
        var dipole = solver.Solve(bare, bestFrequency, bareFeed).InputImpedance;
        var withShort = solver.Solve(shorted, bestFrequency, shortedFeed).InputImpedance;
        _output.WriteLine($"open stub: min |Z| = {bestMagnitude:g4} Ω at {bestFrequency / 1e6:g4} MHz "
            + $"(c/4l = {quarterWave / 1e6:g4} MHz); dipole alone {dipole}; shorted stub {withShort}");

        Assert.InRange(bestFrequency / quarterWave, 0.90, 1.02);
        Assert.True(bestMagnitude < 0.25 * dipole.Magnitude,
            $"an open quarter-wave stub should short the feed: {bestMagnitude:g4} Ω against {dipole.Magnitude:g4} Ω");
        Assert.True((withShort - dipole).Magnitude < 0.25 * dipole.Magnitude,
            $"a shorted quarter-wave stub should leave the dipole alone: {withShort} against {dipole}");
    }

    // ------------------------------------------------------------------
    // The checks that go with a thin-wire result
    // ------------------------------------------------------------------

    [Fact]
    public void AWideTraceAtHighFrequency_IsFlagged_AndAThinDipoleIsNot()
    {
        // The audit's case: a 3 mm trace at 24 GHz. λ/10 is 1.25 mm, the two-radius floor is
        // 1.5 mm, so 4 mm runs come out as 2 mm elements (λ/6.2), and k·a = 0.38.
        const double frequency = 24e9;
        double lambda = C0 / frequency;
        var wide = new[]
        {
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(4e-3, 0, 0), 0.75e-3),
            new WireSegment(new Vector3D(4e-3, 0, 0), new Vector3D(4e-3, 4e-3, 0), 0.75e-3),
            new WireSegment(new Vector3D(4e-3, 4e-3, 0), new Vector3D(8e-3, 4e-3, 0), 0.75e-3)
        };
        var grid = WireGridBuilder.Build(wide, lambda / 10);
        Assert.True(grid.Structure is not null, grid.FailureReason);
        Assert.Contains(grid.Warnings, w => w.Contains("requested maximum"));
        var checks = WireModelChecks.ThinWire(grid.Structure!, frequency);
        Assert.Contains(checks, w => w.Contains("λ/"));
        Assert.Contains(checks, w => w.Contains("k·a"));

        var thin = WireGridBuilder.Build(CanonicalAntennas.Dipole(0.47 * lambda, lambda / 2000), lambda / 10);
        Assert.True(thin.Structure is not null, thin.FailureReason);
        Assert.Empty(thin.Warnings);
        Assert.Empty(WireModelChecks.ThinWire(thin.Structure!, frequency));
    }

    [Fact]
    public void AFeedOnAJunction_IsNamed()
    {
        var (wire, feed, _) = FromNet(StubbedDipole(20e-3));
        Assert.Null(WireModelChecks.FeedAtJunction(wire, feed));

        double z = wire.Nodes[0].Z;
        int atJunction = wire.NearestBasis(new Vector3D(25e-3, 0, z));
        Assert.Contains("junction of 3 wires", WireModelChecks.FeedAtJunction(wire, atJunction));

        // FU-38: the feed at a junction is a choice — every basis there, each between two wires.
        int node = wire.BasisNode(atJunction);
        var options = WireModelChecks.FeedOptionsAt(wire, node);
        Assert.Equal(2, options.Count);
        Assert.Contains(atJunction, options.Select(o => o.Basis));
        Assert.All(options, o => Assert.StartsWith("between the wire toward", o.Description));
        Assert.Equal(2, options.Select(o => o.Description).Distinct().Count());
        // An ordinary node has the one.
        Assert.Single(WireModelChecks.FeedOptionsAt(wire, wire.BasisNode(feed)));
    }

    [Fact]
    public void TheNearField_CloseToAWire_MatchesABruteForceIntegral()
    {
        // Samples a hundredth of an element length off the axis used to come out between 0.33×
        // and 2.1× of the integral they approximate (four fixed panels). The reference here is
        // a 20 000-point midpoint rule over every element, written out independently.
        const double frequency = 300e6, lambda = C0 / frequency, radius = 2e-5;
        var grid = WireGridBuilder.Build(CanonicalAntennas.Dipole(0.47 * lambda, radius), lambda / 20);
        Assert.True(grid.Structure is not null, grid.FailureReason);
        var wire = grid.Structure!;
        var solution = new ThinWireMomSolver().Solve(wire, frequency, wire.NearestBasis(Vector3D.Zero));

        double element = wire.ElementLength(0);
        var axis = wire.ElementDirection(0);
        var side = Math.Abs(axis.Z) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 0, 1);
        var points = new List<Vector3D>();
        foreach (double along in new[] { 0.31, 2.5, 4.0 })
            foreach (double off in new[] { 0.003, 0.01, 0.03, 0.1 })
                points.Add(wire.ElementStart(0) + axis * (along * element) + side * (off * element));

        var map = FieldProbe.Evaluate(wire, solution, points);
        for (int p = 0; p < points.Count; p++)
        {
            double reference = BruteForceE(wire, solution, points[p]);
            Assert.True(Math.Abs(map.Magnitude[p] / reference - 1) < 0.01,
                $"point {p}: |E| {map.Magnitude[p]:g6} against {reference:g6}");
        }

        var near = FieldProbe.NearWire(wire, new[]
        {
            wire.ElementStart(0) + axis * (0.5 * element),
            wire.ElementStart(0) + axis * (0.5 * element) + side * (2 * radius),
            wire.ElementStart(0) + axis * (0.5 * element) + side * (10 * radius)
        });
        Assert.Equal(new[] { true, true, false }, near);
    }

    private static double BruteForceE(WireStructure wire, MomSolution solution, Vector3D point)
    {
        double omega = 2 * Math.PI * solution.FrequencyHz, k = omega / C0;
        var ends = FarFieldEvaluator.ElementEndCurrents(wire, solution);
        var a = new Complex[3];
        var g = new Complex[3];
        const int n = 20000;
        for (int e = 0; e < wire.ElementCount; e++)
        {
            var start = wire.ElementStart(e);
            var t = wire.ElementDirection(e);
            double length = wire.ElementLength(e), c = wire.ElementRadii[e];
            Complex charge = (ends[e].A - ends[e].B) / (Complex.ImaginaryOne * omega * length);
            for (int i = 0; i < n; i++)
            {
                double u = (i + 0.5) / n;
                var d = point - (start + t * (u * length));
                double r = Math.Sqrt(d.LengthSquared + c * c);
                Complex green = Complex.Exp(new Complex(0, -k * r)) / r;
                Complex current = ends[e].A * (1 - u) + ends[e].B * u;
                Complex dg = green * (new Complex(0, -k * r) - 1) / (r * r);
                double w = length / n;
                a[0] += w * current * green * t.X; a[1] += w * current * green * t.Y; a[2] += w * current * green * t.Z;
                g[0] += w * charge * dg * d.X; g[1] += w * charge * dg * d.Y; g[2] += w * charge * dg * d.Z;
            }
        }
        Complex aFactor = new Complex(0, -1) * omega * RfConstants.Mu0 / (4 * Math.PI);
        double phiFactor = 1 / (4 * Math.PI * RfConstants.Eps0);
        double sum = 0;
        for (int i = 0; i < 3; i++)
        {
            var component = aFactor * a[i] - phiFactor * g[i];
            sum += component.Magnitude * component.Magnitude;
        }
        return Math.Sqrt(sum);
    }

    [Fact]
    public void ThePatternGrid_IsFlagged_WhenTheStructureOutgrowsIt()
    {
        // 32 Gauss nodes in cos θ integrate a band limit of k·D ≤ 64, i.e. D ≤ 10.2 λ.
        const double frequency = 1e9, lambda = C0 / frequency;
        Assert.Null(FarFieldEvaluator.GridWarning(0.5 * lambda, frequency));
        Assert.Null(FarFieldEvaluator.GridWarning(10 * lambda, frequency));
        Assert.Contains("wavelengths", FarFieldEvaluator.GridWarning(11 * lambda, frequency));
        // A finer grid lifts it; a hemisphere's θ nodes count double but φ still limits.
        Assert.Null(FarFieldEvaluator.GridWarning(11 * lambda, frequency, 48, 96));
        Assert.NotNull(FarFieldEvaluator.GridWarning(11 * lambda, frequency, 32, 64, hemisphere: true));

        // A monopole's extent includes its image.
        var monopole = WireGridBuilder.Build(CanonicalAntennas.Monopole(0.25 * lambda, lambda / 2000),
            lambda / 10, ground: new GroundPlane(0));
        Assert.True(monopole.Structure is not null, monopole.FailureReason);
        Assert.Equal(0.5 * lambda, FarFieldEvaluator.Extent(monopole.Structure!), 9);
    }
}
