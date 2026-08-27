using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using static OpenSim.Tests.Rf.MultiLayerProbeFixtures;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage C2 — the quasi-static razor for the multi-layer probe junction, one dimension richer
/// than the single slab's (<c>ProbeFedPatchTests.QuasiStaticLimit_SeesTheParallelPlateCapacitor</c>).
/// </summary>
public class MultiLayerProbeCapacitanceTests
{
    [Fact]
    public void ASeriesDielectricStack_ReadsItsQuasiStaticCapacitance()
    {
        // At 0.5 GHz the patch is a capacitor, and a two-layer stack's plate value follows the
        // SERIES law (ε_series = the harmonic mean at equal thicknesses). This is THE junction
        // arbiter because a coupling sign or magnitude error is invisible in a resonant Zin sweep
        // — it enters quadratically — but hits C_eff directly: the affine-fan and mis-oriented-half
        // constructions this junction replaced read 0.2 pF and 50 pF against a ~2 pF answer.
        //
        // What this gate can and cannot arbitrate, stated because the difference matters. The
        // ratio C_eff/C_plate is a GEOMETRY factor (fringing + probe locality) and it is NOT
        // ε-independent, nor even monotone in ε: measured 1.56 (2.2/2.2), 1.75 (2.2/9.8), 1.40
        // (9.8/9.8), because the fringe field around the patch rim samples the two layers quite
        // differently from the way the plate region does. So the band is the window those three
        // measurements span, widened — an honest window, not a law. The SHARP content is that
        // C_eff tracks ε_series monotonically across a 4.5:1 range, which no sign error survives,
        // and that the uniform two-layer case reads 2.054 pF against the shipped single-slab
        // fixture's 2.08 pF (a different mesh and segment count, hence not an identity).
        double f = 0.5e9, eps0 = 8.8541878128e-12;
        var surface = Plate(CoarseEdge);
        var probe = Probe(ThinRadius, 4);
        var solver = new SurfaceMomSolver();

        double previous = 0;
        foreach (var (e1, e2) in new[] { (2.2, 2.2), (2.2, 9.8), (9.8, 9.8) })
        {
            var solution = solver.SolveProbeFed(surface,
                new MultiLayerKernelTable(Two(e1, Thickness / 2, e2, Thickness / 2), f, 0.025),
                probe);
            var zin = solution.Surface.InputImpedance;
            Assert.True(zin.Imaginary < 0, $"({e1}, {e2}): a quasi-static patch must be capacitive");
            double cEff = -1.0 / (2 * Math.PI * f * zin.Imaginary);
            double epsSeries = 2 * e1 * e2 / (e1 + e2);
            double cPlate = eps0 * epsSeries * PatchW * PatchL / Thickness;

            Assert.InRange(cEff / cPlate, 1.2, 2.2);
            Assert.True(cEff > previous,
                $"({e1}, {e2}): C_eff {cEff * 1e12:F3} pF must exceed the lower-ε_series stack's "
                + $"{previous * 1e12:F3} pF");
            previous = cEff;

            // The tube carries a near-uniform current at quasi-statics: no spurious shunt where
            // it crosses the interface.
            Assert.InRange(
                solution.TubeCurrents[^1].Magnitude / solution.TubeCurrents[0].Magnitude, 0.85, 1.05);
        }
    }
}
