using System.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using static OpenSim.Tests.Rf.MultiLayerProbeFixtures;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage C2 — the probe-fed far field and power ledger over an N-layer grounded stackup.
///
/// <para>Two genuinely new pieces: the vertical amplitude Ĝ(θ) read at the TOP of the stack
/// through the TLGF (rather than at the slab top through a closed form), and the vertical /
/// cross surface-wave residues from the multi-layer poles. Everything else — the quadrature, the
/// junction transform, the coherent |a_h + a_v|² sum — is the shipped machinery, now written once
/// against a radiation-medium seam.</para>
///
/// <para><b>What is NOT gated here, and why.</b> The absolute P_rad + P_sw ledger on a
/// dielectric stack. The shipped single-slab probe carries a documented open item there (the
/// layered far-field quadrature over-counts by a percent), and MEASURING the shipped path across
/// εr on this mesh shows how much room that leaves: εr = 2.2 reads 1.110 at its resonance,
/// εr = 3.59 reads 1.190, εr = 9.8 reads 1.157 — and off resonance the shipped path returns a
/// small NEGATIVE input resistance, so the ratio is not even defined there. A two-material stack
/// measures 1.270, inside that family. Gating a number in that range would gate the open item's
/// magnitude on a coarse mesh, not this stage's physics. What IS gated instead is sharper: the
/// εr = 1 limit exactly, the N = 1 limit against the single slab, the vertical launch against an
/// independent oracle on genuinely two-material stacks, and both limits of the coherent form.</para>
/// </summary>
public class MultiLayerProbeFarFieldTests
{
    [Theory]
    [InlineData(8.0e9)]
    [InlineData(12.0e9)]
    public void AtEpsilonOne_TheProbeFedPatchRadiatesAllItsInputPower(double frequency)
    {
        // The absolute gate on the whole mixed-current far field, through the multi-layer path:
        // at εr = 1 there are NO surface waves, so a lossless probe-fed patch must radiate ALL of
        // its input power at ANY frequency, resonant or not. The stack is TWO air layers, so the
        // interface-pinned tube and the TLGF read-out are both exercised rather than short-circuited.
        // Measured 0.999979 / 1.000003 / 1.000048 at 8 / 10 / 12 GHz.
        double y = -PatchL / 4;
        var surface = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: 2.0e-3, portFraction: 0, snapVertex: (0.0, y)).Structure!;
        var air = Two(1.0, 1.0e-3, 1.0, 1.0e-3);
        var table = new MultiLayerKernelTable(air, frequency, 0.025);
        Assert.Equal(0, table.PoleCount); // no surface waves in air

        var probe = new ProbeFeed(0.0, y, ProbeRadius, 3);
        var solution = new SurfaceMomSolver().SolveProbeFed(surface, table, probe);
        double pIn = 0.5 * Complex.Conjugate(1.0 / solution.Surface.InputImpedance).Real;
        var far = LayeredFarField.Compute(surface, table, solution, probe);
        Assert.InRange(far.TotalRadiatedPowerWatts / pIn, 0.99, 1.01);
    }

    [Fact]
    public void AtOneLayer_TheFarFieldIsTheSingleSlabFarField()
    {
        // The far field adds NO error of its own beyond the solve's: a one-layer stackup
        // reproduces the single-slab probe's pattern, radiated power and surface-wave power at the
        // same ~7e-7 the two solves already differ by. Measured: P_rad 7.7e-7, P_sw 7.8e-7, worst
        // pattern point 7.7e-7, D_max identical to six figures.
        var surface = Plate(MeshEdge);
        var probe = Probe();
        var solver = new SurfaceMomSolver();

        var slabTable = new LayeredKernelTable(Substrate, Frequency, 0.025);
        var slab = solver.SolveProbeFed(surface, slabTable, probe);
        var slabFar = LayeredFarField.Compute(surface, slabTable, slab, probe);
        double slabSw = LayeredFarField.SurfaceWavePowerWatts(surface, slabTable, slab, probe);

        var mlTable = new MultiLayerKernelTable(OneLayer, Frequency, 0.025);
        var multi = solver.SolveProbeFed(surface, mlTable, probe);
        var mlFar = LayeredFarField.Compute(surface, mlTable, multi, probe);
        double mlSw = LayeredFarField.SurfaceWavePowerWatts(surface, mlTable, multi, probe);

        Assert.True(Math.Abs(slabFar.TotalRadiatedPowerWatts - mlFar.TotalRadiatedPowerWatts)
                    <= 5e-6 * slabFar.TotalRadiatedPowerWatts,
            $"P_rad {slabFar.TotalRadiatedPowerWatts:e6} vs {mlFar.TotalRadiatedPowerWatts:e6}");
        Assert.True(Math.Abs(slabSw - mlSw) <= 5e-6 * Math.Abs(slabSw),
            $"P_sw {slabSw:e6} vs {mlSw:e6}");

        double peak = 0;
        foreach (double u in slabFar.IntensityWattsPerSteradian) peak = Math.Max(peak, u);
        for (int ti = 0; ti < slabFar.ThetaRadians.Count; ti++)
            for (int pi = 0; pi < slabFar.PhiRadians.Count; pi++)
            {
                double delta = Math.Abs(slabFar.IntensityWattsPerSteradian[ti, pi]
                                        - mlFar.IntensityWattsPerSteradian[ti, pi]);
                Assert.True(delta <= 5e-6 * peak, $"pattern point ({ti}, {pi}): {delta / peak:e3}");
            }
    }

    [Theory]
    [InlineData(2.2, 0.8e-3, 9.8, 0.788e-3, 9.0e9, 3)]
    [InlineData(4.4, 0.6e-3, 2.2, 1.0e-3, 12.0e9, 3)]
    [InlineData(9.8, 0.635e-3, 9.8, 0.635e-3, 8.0e9, 4)]
    [InlineData(1.0, 1.0e-3, 2.2, 1.0e-3, 10.0e9, 4)]
    public void TheVerticalLaunchMatchesTheProbeOnlyOracle(
        double e1, double h1, double e2, double h2, double frequency, int segments)
    {
        // The sharp gate on the two pieces the εr = 1 and N = 1 limits cannot reach: the
        // multi-layer vertical far-field amplitude and the multi-layer vertical pole residues, on
        // a GENUINELY two-material stack. For a probe-only structure (no patch) the space wave is
        // exact, so P_sw = P_in − P_rad is an independent oracle for the modal formula — the same
        // construction the single-slab gate uses, which measures 5e-4 there. Measured here:
        // 1.00019, 1.00031, 1.00015, 1.00034 — including the high-ε layer on top, the high-ε layer
        // on the bottom, an AIR layer under a dielectric, and a uniform stack described as two.
        var stack = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(e1, 0.0, h1),
            new LayeredStackup.Layer(e2, 0.0, h2)
        });
        var set = new MultiLayerVerticalKernelSet(stack, frequency);
        var probe = new ProbeFeed(0, 0, 0.12e-3, segments);
        var nodes = ProbeAssembly.TubeNodes(stack, null, probe);
        var (_, currents) = ProbeAssembly.SolveProbeOnly(set, nodes, probe);

        var tube = new Complex[nodes.Length];
        for (int i = 0; i + 1 < nodes.Length; i++) tube[i] = currents[i]; // open top carries nothing

        double mu0 = 4e-7 * Math.PI, eps0 = 8.8541878128e-12;
        double eta = Math.Sqrt(mu0 / eps0);
        double k0 = set.K0, omega = 2 * Math.PI * frequency;
        var (u, uw) = GaussLegendre.Rule(64, 0, 1);
        double pRad = 0;
        for (int ti = 0; ti < u.Length; ti++)
        {
            double cosT = u[ti], sinT = Math.Sqrt(1 - cosT * cosT);
            var g = LayeredFarField.VerticalAmplitude(stack, k0, Math.Acos(cosT), nodes, tube);
            var e = (omega * k0 * cosT / (4 * Math.PI)) * (-sinT) * g;
            pRad += 2 * Math.PI * uw[ti] * e.Magnitude * e.Magnitude / (2 * eta);
        }
        double oracle = 0.5 * currents[0].Real - pRad;   // V = 1, so P_in = ½Re(V·I₀*)
        double model = LayeredFarField.VerticalSurfaceWavePowerWatts(set, nodes, tube);
        Assert.InRange(model / oracle, 0.99, 1.01);
    }

    [Fact]
    public void TheCoherentLedgerReducesToItsTwoLimits_OnATwoMaterialStack()
    {
        // The mixed surface-wave form adds the horizontal and vertical launches COHERENTLY, so
        // the cross term is the one piece no limit gate reaches. Pinning BOTH diagonal blocks on a
        // two-material stack is what makes the cross term attributable: with the tube top driven
        // to zero (a free end, no junction charge on either side) the mixed form must be exactly
        // the oracle-gated vertical formula, and with the tube carrying nothing it must be exactly
        // the shipped horizontal one. Both at 1e-9 — the A5 argument, over a stackup.
        var surface = Plate(CoarseEdge);
        var probe = Probe(ThinRadius, 3);
        var stack = Two(2.2, Thickness / 2, 9.8, Thickness / 2);
        double f = 7.0e9;
        var table = new MultiLayerKernelTable(stack, f, 0.025);
        var solution = new SurfaceMomSolver().SolveProbeFed(surface, table, probe);
        Assert.True(table.PoleCount > 0, "the fixture must actually carry a surface wave");

        var freeEndTube = (Complex[])solution.TubeCurrents.Clone();
        freeEndTube[^1] = Complex.Zero;
        var verticalOnly = new ProbeFedSolution(
            solution.Surface with { EdgeCurrents = new Complex[solution.Surface.EdgeCurrents.Length] },
            freeEndTube, new Complex[solution.RawEdgeCurrents.Length], solution.TubeNodes);
        double mixedVertical = LayeredFarField.SurfaceWavePowerWatts(
            surface, table, verticalOnly, probe);
        double vertical = LayeredFarField.VerticalSurfaceWavePowerWatts(
            new MultiLayerVerticalKernelSet(stack, f), solution.TubeNodes, freeEndTube);
        Assert.True(Math.Abs(mixedVertical - vertical) < 1e-9 * Math.Abs(vertical),
            $"vertical-only limit {mixedVertical:e6} vs the oracle-gated formula {vertical:e6}");

        var horizontalOnly = new ProbeFedSolution(
            solution.Surface, new Complex[solution.TubeCurrents.Length],
            solution.RawEdgeCurrents, solution.TubeNodes);
        double mixedHorizontal = LayeredFarField.SurfaceWavePowerWatts(
            surface, table, horizontalOnly, probe);
        double horizontal = LayeredFarField.SurfaceWavePowerWatts(surface, table,
            solution.Surface with { EdgeCurrents = horizontalOnly.RawEdgeCurrents });
        Assert.True(Math.Abs(mixedHorizontal - horizontal) < 1e-9 * Math.Abs(horizontal),
            $"horizontal-only limit {mixedHorizontal:e6} vs the shipped formula {horizontal:e6}");
    }
}
