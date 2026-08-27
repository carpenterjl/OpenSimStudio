using System.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using static OpenSim.Tests.Rf.MultiLayerProbeFixtures;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage C2 — a probe feeding a patch that is BURIED under a dielectric cover: the metal plane is
/// no longer the top of the stack, so the tube ends on an interior interface and the kernels come
/// from the interior-source path (interior images, interior-plane pole residues, the interior
/// Sommerfeld remainder).
/// </summary>
public class CoveredProbeFedPatchTests
{
    /// <summary>The coplanar-top reference, shared by both gates — a multi-layer probe solve costs
    /// ~17 s, so it is computed once.</summary>
    private static readonly Lazy<Complex> Coplanar = new(() =>
        new SurfaceMomSolver().SolveProbeFed(Plate(CoarseEdge),
            new MultiLayerKernelTable(OneLayer, Frequency, 0.025), Probe())
        .Surface.InputImpedance);

    private static Complex Covered(double coverEpsR, double coverThickness) =>
        new SurfaceMomSolver().SolveProbeFed(Plate(CoarseEdge),
            new MultiLayerKernelTable(
                LayeredStackup.CoveredPatch(2.2, 0.0, Thickness, coverEpsR, 0.0, coverThickness),
                Frequency, 0.025, sourceInterface: LayeredStackup.CoveredPatchMetalInterface),
            Probe()).Surface.InputImpedance;

    [Fact]
    public void AnAirCoveredPatch_IsTheCoplanarTopPatch()
    {
        // The capability this stage adds, gated as an IDENTITY rather than a trend: a patch
        // buried under 3 mm of AIR is physically the coplanar-top patch, so the buried-source
        // path must reproduce the top-source path — which it shares nothing with but the
        // assembly. Measured 5.8e-9.
        var covered = Covered(1.0, 3.0e-3);
        var coplanar = Coplanar.Value;
        double rel = (coplanar - covered).Magnitude / coplanar.Magnitude;
        Assert.True(rel <= 1e-6, $"coplanar {coplanar} vs air cover {covered} (rel {rel:e3})");
    }

    [Fact]
    public void ADielectricCover_LoadsTheProbeFedPatch()
    {
        // The physics the air-cover identity cannot show: a REAL cover pulls the resonance down,
        // and a fixed drive frequency then sits above it instead of below. Measured at 9.4 GHz —
        // uncovered 124.7 + j52.5, with a 0.8 mm cover 57.8 − j1.4: the reactance has crossed
        // zero (the resonance passed under the drive) and R has fallen away from its peak. How
        // FAR it shifts is the shipped covered-patch trend gate's business; what is asserted here
        // is that a PROBE feed sees it, through the buried-source kernels.
        var covered = Covered(2.2, 0.8e-3);
        var uncovered = Coplanar.Value;
        Assert.True(uncovered.Imaginary > 0 && covered.Imaginary < 0,
            $"the cover must carry the resonance past 9.4 GHz: X {uncovered.Imaginary:F1} → "
            + $"{covered.Imaginary:F1}");
        Assert.True(covered.Real < uncovered.Real,
            $"R should fall off the peak: {uncovered.Real:F1} → {covered.Real:F1}");
        Assert.True(covered.Real > 0, "a passive patch cannot present negative resistance");
    }
}
