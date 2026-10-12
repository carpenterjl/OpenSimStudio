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
    /// ~17 s, so it is computed once. On the fixture's standard mesh: at <see cref="CoarseEdge"/>
    /// the snapped probe vertex leaves a skewed attachment fan (FU-4).</summary>
    private static readonly Lazy<Complex> Coplanar = new(() =>
        new SurfaceMomSolver().SolveProbeFed(Plate(MeshEdge),
            new MultiLayerKernelTable(OneLayer, Frequency, 0.025), Probe())
        .Surface.InputImpedance);

    private static Complex Covered(double coverEpsR, double coverThickness) =>
        new SurfaceMomSolver().SolveProbeFed(Plate(MeshEdge),
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
        // assembly. Measured 5.8e-9 at 1.6 mm, 1.05e-8 at 1.4 mm.
        var covered = Covered(1.0, 3.0e-3);
        var coplanar = Coplanar.Value;
        double rel = (coplanar - covered).Magnitude / coplanar.Magnitude;
        Assert.True(rel <= 1e-6, $"coplanar {coplanar} vs air cover {covered} (rel {rel:e3})");
    }

    [Fact]
    public void ADielectricCover_LoadsTheProbeFedPatch()
    {
        // The physics the air-cover identity cannot show: a REAL cover pulls the resonance down,
        // and at a fixed drive frequency above the covered resonance the patch falls off its
        // resistance peak and its reactance with it. Measured at 9.4 GHz on the 1.4 mm mesh —
        // uncovered 149.6 + j47.9, with a 0.8 mm cover 59.8 + j8.6. How FAR it shifts is the
        // shipped covered-patch trend gate's business; what is asserted here is that a PROBE feed
        // sees it, through the buried-source kernels.
        //
        // (FU-4: this asserted that X turns negative, from 124.7 + j52.5 to 57.8 − j1.4 on the
        // 1.6 mm mesh. That crossing came from the old junction basis on the skewed fan there,
        // about 15 Ω low; on a sound mesh the probe's own inductance keeps X positive. The drop
        // is what the crossing stood for. Changed with the owner's agreement.)
        var covered = Covered(2.2, 0.8e-3);
        var uncovered = Coplanar.Value;
        Assert.True(covered.Imaginary < 0.5 * uncovered.Imaginary,
            $"the cover must pull X down by more than half: {uncovered.Imaginary:F1} → {covered.Imaginary:F1}");
        Assert.True(covered.Real < 0.5 * uncovered.Real,
            $"R should fall off the peak: {uncovered.Real:F1} → {covered.Real:F1}");
        Assert.True(covered.Real > 0, "a passive patch cannot present negative resistance");
    }
}
