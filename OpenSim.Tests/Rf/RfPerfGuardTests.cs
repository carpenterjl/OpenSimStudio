using System.Diagnostics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Wall-clock guards for the Stage G parallel paths. RELATIVE guards (parallel vs
/// DOP-1 on the same fixture in the same process) rather than absolute lines: MoM
/// numerics scale with core count and Debug/Release codegen far more than the import
/// pipeline does, and an absolute line loose enough for a 4-core Debug run could not
/// catch a serialization regression at all. Release-only (Debug soft-skips: the ~5×
/// codegen slowdown makes 20+-second serial baselines a test-suite tax with no extra
/// information) and multicore-only (a relative guard is vacuous on 1–2 cores).
/// Reference measurements, Release, 16 cores: 833-unknown solve 20.2 s DOP-1 → 1.7 s
/// unbounded (11.8×); Balanis-scale kernel table 170 ms → 10 ms (16–19×).
/// </summary>
public class RfPerfGuardTests
{
    private static bool ShouldSkip
    {
        get
        {
#if DEBUG
            return true;
#else
            return Environment.ProcessorCount < 4;
#endif
        }
    }

    [Fact]
    public void SurfaceSolve_ParallelFill_BeatsSerial()
    {
        if (ShouldSkip) return;
        // ~330 unknowns keeps the DOP-1 baseline a few seconds; the O(N²) fill (and
        // O(N³) LU) dominate well before that, so a serialization regression of any
        // stage still shows as parallel ≈ serial. 0.6 is far above the measured
        // ratio (~0.09 on 16 cores, ≲0.35 expected on 4) and far below 1.
        double f = 3e9, lambda = 3e8 / f;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            0.5 * lambda, 0.5 * lambda, lambda / 20.0);
        var s = grid.Structure!;

        _ = new SurfaceMomSolver().Solve(s, f, grid.Port!); // JIT warm-up
        var sw = Stopwatch.StartNew();
        var serial = new SurfaceMomSolver { MaxDegreeOfParallelism = 1 }.Solve(s, f, grid.Port!);
        double tSerial = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var parallel = new SurfaceMomSolver().Solve(s, f, grid.Port!);
        double tParallel = sw.Elapsed.TotalSeconds;

        Assert.Equal(serial.InputImpedance, parallel.InputImpedance);
        Assert.True(tParallel < 0.6 * tSerial,
            $"parallel solve {tParallel:F2} s vs DOP-1 {tSerial:F2} s on " +
            $"{Environment.ProcessorCount} cores — the pair-slot fill or the LU " +
            "trailing update has been serialized");
    }

    [Fact]
    public void KernelTableBuild_Parallel_BeatsSerial()
    {
        if (ShouldSkip) return;
        var substrate = new SubstrateStackup(2.2, 0.02, 1.588e-3);
        _ = new LayeredKernelTable(substrate, 10e9, 0.025); // JIT warm-up

        var sw = Stopwatch.StartNew();
        _ = new LayeredKernelTable(substrate, 10e9, 0.025, maxDegreeOfParallelism: 1);
        double tSerial = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        _ = new LayeredKernelTable(substrate, 10e9, 0.025);
        double tParallel = sw.Elapsed.TotalMilliseconds;

        Assert.True(tParallel < 0.6 * tSerial,
            $"parallel table build {tParallel:F0} ms vs DOP-1 {tSerial:F0} ms on " +
            $"{Environment.ProcessorCount} cores — the knot-slot Parallel.For has been serialized");
    }
}
