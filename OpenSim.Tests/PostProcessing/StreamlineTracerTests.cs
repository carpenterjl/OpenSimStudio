using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;

namespace OpenSim.Tests.PostProcessing;

public class StreamlineTracerTests
{
    [Fact]
    public void UniformField_TracesAnExactStraightLine()
    {
        var line = StreamlineTracer.Trace(new Vector3D(0, 0, 0),
            _ => new Vector3D(2, 0, 0), stepLength: 0.1, maxSteps: 50);

        Assert.Equal(51, line.Count);
        for (int i = 0; i < line.Count; i++)
        {
            // Arc-length stepping: exactly 0.1 m apart along +x; RK4 is exact for a
            // constant field, so this is machine-sharp.
            Assert.Equal(0.1 * i, line[i].X, 12);
            Assert.Equal(0.0, line[i].Y, 12);
            Assert.Equal(0.0, line[i].Z, 12);
        }
    }

    [Fact]
    public void SolidBodyRotation_ClosesOnItselfAfterOneRevolution()
    {
        // u = ω × r about z: circles of radius 1. One revolution is 2π of arc; RK4's
        // O(h⁴) local error over ~1257 steps keeps the return-to-start error tiny —
        // the gate is the classic closed-orbit test of an integrator.
        const double radius = 1.0;
        var seed = new Vector3D(radius, 0, 0);
        double step = 0.005;
        int stepsPerRev = (int)Math.Ceiling(2 * Math.PI * radius / step);

        var line = StreamlineTracer.Trace(seed,
            p => new Vector3D(-p.Y, p.X, 0), step, stepsPerRev);

        // Every point stays on the circle...
        foreach (var p in line)
            Assert.Equal(radius, Math.Sqrt(p.X * p.X + p.Y * p.Y), 6);
        // ...and the trace returns to within a fraction of a step of the seed.
        var end = line[^1];
        double gap = (end - seed).Length;
        Assert.True(gap <= 2 * step, $"closure gap {gap:E3} after one revolution");
    }

    [Fact]
    public void LeavingTheDomain_TerminatesTheTrace()
    {
        var line = StreamlineTracer.Trace(new Vector3D(0, 0, 0),
            p => p.X < 1.0 ? new Vector3D(1, 0, 0) : null, stepLength: 0.1, maxSteps: 1000);

        Assert.True(line.Count < 15, $"trace must stop at the domain edge (got {line.Count} points)");
        Assert.True(line[^1].X <= 1.0 + 1e-9);
    }

    [Fact]
    public void StagnationPoint_EndsTheStreamline()
    {
        // Speed decays toward x = 1 and vanishes there: the trace must stop, not spin.
        // Arc-length stepping legitimately overshoots a stagnation point by up to about
        // one step (Δt = step/|u| blows up as |u| → 0), so the gate is "stops within a
        // step of the stagnation region", not exact arrival.
        var line = StreamlineTracer.Trace(new Vector3D(0, 0, 0),
            p => new Vector3D(Math.Max(1.0 - p.X, 0), 0, 0), stepLength: 0.05, maxSteps: 10000);

        Assert.True(line.Count < 10000, "the trace must terminate, not exhaust the cap");
        Assert.True(line[^1].X <= 1.0 + 0.05,
            $"stopped at x = {line[^1].X:F4}, expected within one step of x = 1");
    }

    [Fact]
    public void SeedOutsideTheFlow_ReturnsJustTheSeed()
    {
        var line = StreamlineTracer.Trace(new Vector3D(5, 5, 5),
            _ => null, stepLength: 0.1, maxSteps: 100);
        Assert.Single(line);
    }
}
