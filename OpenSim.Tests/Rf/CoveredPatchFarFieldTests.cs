using System.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage F2b — the multi-layer far field and the covered-patch power ledger (the house
/// per-stage identity). <see cref="LayeredFarField.Compute(SurfaceStructure, MultiLayerKernelTable, SurfaceMomSolution, int, int)"/>
/// radiates the horizontal RWG currents through the stack using the transmission-line
/// Green's function's region-0 amplitude (G̃_A = C, W̃ = S/C) instead of the single-slab
/// closed form. Two independent things are gated:
///  • the multi-layer radiation read-out reproduces the pinned single-slab far field — fed
///    the SAME currents, so only the amplitude read-out differs (a sharp identity, since the
///    F1 gates pin the TLGF kernels to the single slab at 1e-12);
///  • the covered-patch power ledger P_rad(hemisphere) + ΣP_sw ≡ ½Re(V·I*) closes to 3% on a
///    lossless covered patch — the honest end-to-end conservation check.
/// </summary>
public class CoveredPatchFarFieldTests
{
    private const double PatchW = 1.186e-2;
    private const double PatchL = 0.906e-2;
    private const double Edge = 1.4e-3;
    private const double SlabH = 1.588e-3;
    private const double RhoMax = 0.03;
    private const double Frequency = 10e9;

    private static (SurfaceStructure Structure, SurfacePort Port) BuildPlate()
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(PatchW, PatchL, Edge, z: SlabH, portFraction: 0);
        Assert.NotNull(grid.Structure);
        Assert.NotNull(grid.Port);
        return (grid.Structure!, grid.Port!);
    }

    /// <summary>Worst relative deviation between two hemisphere intensity patterns.</summary>
    private static double PatternRelDeviation(FarFieldPattern a, FarFieldPattern b)
    {
        int nt = a.ThetaRadians.Count, np = a.PhiRadians.Count;
        double peak = 0;
        for (int i = 0; i < nt; i++)
            for (int j = 0; j < np; j++)
                peak = Math.Max(peak, a.IntensityWattsPerSteradian[i, j]);
        double worst = 0;
        for (int i = 0; i < nt; i++)
            for (int j = 0; j < np; j++)
                if (a.IntensityWattsPerSteradian[i, j] > 1e-6 * peak)
                    worst = Math.Max(worst,
                        Math.Abs(a.IntensityWattsPerSteradian[i, j] - b.IntensityWattsPerSteradian[i, j])
                        / a.IntensityWattsPerSteradian[i, j]);
        return worst;
    }

    [Fact]
    public void N1MultiLayerFarField_MatchesSingleSlab_SameCurrents()
    {
        // Feed the SAME solved currents to both far-field paths: the only difference is the
        // radiation amplitude read-out (SpectralKernels closed form vs the TLGF region-0
        // amplitude). The F1 gates pin the TLGF kernels to the single slab at 1e-12, so C and
        // W̃ = S/C agree to that floor and the pattern must too — a sharp identity on the read-out.
        var (structure, port) = BuildPlate();
        var single = new LayeredKernelTable(new SubstrateStackup(2.2, 0.001, SlabH), Frequency, RhoMax);
        var multi = new MultiLayerKernelTable(
            LayeredStackup.FromSubstrate(new SubstrateStackup(2.2, 0.001, SlabH)), Frequency, RhoMax);
        var sol = new SurfaceMomSolver().Solve(structure, single, port);

        var patSingle = LayeredFarField.Compute(structure, single, sol);
        var patMulti = LayeredFarField.Compute(structure, multi, sol);
        Assert.True(PatternRelDeviation(patSingle, patMulti) < 1e-8,
            $"pattern dev {PatternRelDeviation(patSingle, patMulti):e2}");
        Assert.True(Math.Abs(patMulti.TotalRadiatedPowerWatts - patSingle.TotalRadiatedPowerWatts)
                    < 1e-8 * patSingle.TotalRadiatedPowerWatts, "P_rad mismatch");

        double pswSingle = LayeredFarField.SurfaceWavePowerWatts(structure, single, sol);
        double pswMulti = LayeredFarField.SurfaceWavePowerWatts(structure, multi, sol);
        Assert.True(Math.Abs(pswMulti - pswSingle) < 1e-4 * Math.Abs(pswSingle),
            $"P_sw multi {pswMulti} vs single {pswSingle}");
    }

    [Fact]
    public void SplitSlabFarField_MatchesSingleSlab_SameCurrents()
    {
        // The powerful multi-layer far-field check: a slab split into two identical half
        // layers is genuinely N = 2 (the TLGF radiation amplitude runs the recursion) yet must
        // reproduce the one-slab pattern. Same currents ⇒ isolates the amplitude read-out.
        var (structure, port) = BuildPlate();
        var single = new LayeredKernelTable(new SubstrateStackup(2.2, 0.001, SlabH), Frequency, RhoMax);
        var split = new MultiLayerKernelTable(new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(2.2, 0.001, SlabH / 2),
            new LayeredStackup.Layer(2.2, 0.001, SlabH / 2)
        }), Frequency, RhoMax);
        var sol = new SurfaceMomSolver().Solve(structure, single, port);

        var patSingle = LayeredFarField.Compute(structure, single, sol);
        var patSplit = LayeredFarField.Compute(structure, split, sol);
        Assert.True(PatternRelDeviation(patSingle, patSplit) < 1e-8,
            $"split pattern dev {PatternRelDeviation(patSingle, patSplit):e2}");
    }

    [Fact]
    public void CoveredPatch_PowerLedger_Closes()
    {
        // THE Stage F identity: on a LOSSLESS covered patch all accepted power radiates or
        // launches surface waves, so P_rad(hemisphere) + ΣP_sw ≡ ½Re(V·I*). The solve conserves
        // power by construction (P_in = ½Re(x†Zx)); this checks the far-field + P_sw legs against
        // that internal balance — the multi-layer, buried-source analog of the Stage C ledger.
        var (structure, port) = BuildPlate();
        var table = new MultiLayerKernelTable(
            LayeredStackup.CoveredPatch(2.2, 0.0, SlabH, 0.8e-3), Frequency, RhoMax,
            sourceInterface: LayeredStackup.CoveredPatchMetalInterface);
        var sol = new SurfaceMomSolver().Solve(structure, table, port);

        double pin = 0.5 * (Complex.One / sol.InputImpedance).Real;
        double pRad = LayeredFarField.Compute(structure, table, sol).TotalRadiatedPowerWatts;
        double pSw = LayeredFarField.SurfaceWavePowerWatts(structure, table, sol);
        double ratio = (pRad + pSw) / pin;
        Assert.True(ratio is > 0.97 and < 1.03,
            $"covered-patch ledger P_rad {pRad:g4} + P_sw {pSw:g4} = {(pRad + pSw):g4} vs P_in {pin:g4} (ratio {ratio:F4})");
    }
}
