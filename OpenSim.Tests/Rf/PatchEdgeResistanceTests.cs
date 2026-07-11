using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage D checkpoint D3 — the cavity-model edge-resistance benchmark that Stage C
/// documented as OPEN, closed by the voltage kernel. Formulation C's Φ alone is
/// gauge-dependent; V = −∫₀^d E_z dz through K_V (BOTH legs: −∂Φ/∂z and the −jωA_z
/// sourced by the horizontal currents through the TM coupling) gives the physical
/// patch-to-ground voltage, and R_edge = |V_edge|²/(2P_in) lands in the cavity band.
///
/// Measured (Balanis Ex 14.1 patch, 1.4 mm mesh, at each thickness's OWN
/// edge-balance resonance): R_edge = 168 Ω (h = 1.588 mm, f_res 9.70 GHz),
/// 200 Ω (0.8 mm, 10.30 GHz), 220 Ω (0.3 mm, 10.80 GHz) — monotonically approaching
/// the two-slot 228 Ω as the slab thins (the two-slot model neglects surface waves
/// and substrate fringing, both of which shrink with h; at the reference thickness
/// P_sw alone is ~16% of the input). The independent physics checks carried by the
/// same numbers: the FED and FAR radiating edges — perturbed very differently by the
/// series-gap feed — agree on R to ~1% exactly AT resonance, and the old Φ-only
/// probe underreads V by ~30% (its documented anomaly, now demonstrably a gauge
/// artifact, not physics).
/// </summary>
public class PatchEdgeResistanceTests
{
    private const double PatchW = 1.186e-2;
    private const double PatchL = 0.906e-2;
    private const double MeshEdge = 1.4e-3;

    private static (double RFar, double RFed, double RPhi, double Balance) Measure(
        double thickness, double frequencyHz)
    {
        var substrate = new SubstrateStackup(2.2, 0.0, thickness);
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: thickness, portFraction: 0);
        Assert.NotNull(grid.Structure);
        var table = new LayeredKernelTable(substrate, frequencyHz, 0.025);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        double pin = 0.5 * (1.0 / solution.InputImpedance).Real; // 1 V gap drive

        Complex EdgeAverage(double y, bool voltage)
        {
            Complex sum = Complex.Zero;
            for (int i = 0; i < 5; i++)
            {
                var point = new Vector3D(-0.4 * PatchW + 0.2 * PatchW * i, y, thickness);
                sum += voltage
                    ? LayeredPotentialProbe.EdgeVoltage(grid.Structure!, table, solution, point)
                    : LayeredPotentialProbe.ScalarPotential(grid.Structure!, table, solution, point);
            }
            return sum / 5;
        }

        double inset = 0.5 * MeshEdge;
        var vFar = EdgeAverage(PatchL / 2 - inset, voltage: true);
        var vFed = EdgeAverage(-PatchL / 2 + inset, voltage: true);
        var phiFar = EdgeAverage(PatchL / 2 - inset, voltage: false);
        return (vFar.Magnitude * vFar.Magnitude / (2 * pin),
                vFed.Magnitude * vFed.Magnitude / (2 * pin),
                phiFar.Magnitude * phiFar.Magnitude / (2 * pin),
                vFar.Magnitude / vFed.Magnitude);
    }

    [Fact]
    public void EdgeResistance_LandsInTheCavityBand_TheOpenBenchmarkCloses()
    {
        // The headline: at the reference thickness's own resonance (9.70 GHz by the
        // edge-balance criterion), |V_edge|²/(2P_in) sits in the cavity band that the
        // Φ-only probe (70–85 Ω at the rim midpoint, ~130 Ω edge-averaged) never
        // reached. Measured 168 Ω.
        var (rFar, rFed, _, balance) = Measure(1.588e-3, 9.70e9);
        Assert.InRange(balance, 0.90, 1.10);            // we ARE at resonance
        Assert.InRange(rFar, 150.0, 320.0);             // the cavity band — CLOSED
        Assert.InRange(rFed / rFar, 0.85, 1.15);        // feed-position consistency
    }

    [Fact]
    public void EdgeResistance_ApproachesTheTwoSlotValue_AsTheSlabThins()
    {
        // The two-slot 228 Ω is a THIN-substrate model (no surface waves, no
        // substrate fringing). The full-wave R_edge must approach it from below as
        // h shrinks — a trend gate, each thickness at its own resonance. Measured
        // 168 → 200 → 220 Ω (3.7% below two-slot at h = 0.3 mm, h/λ_d ≈ 1.5%).
        var (rThick, _, _, bThick) = Measure(1.588e-3, 9.70e9);
        var (rMid, _, _, bMid) = Measure(0.8e-3, 10.30e9);
        var (rThin, rThinFed, _, bThin) = Measure(0.3e-3, 10.80e9);
        Assert.InRange(bThick, 0.90, 1.10);
        Assert.InRange(bMid, 0.90, 1.10);
        Assert.InRange(bThin, 0.90, 1.10);

        Assert.True(rThick < rMid && rMid < rThin,
            $"R_edge should rise toward the two-slot value as h thins: {rThick:F0}, {rMid:F0}, {rThin:F0}");
        Assert.InRange(rThin, 190.0, 260.0);            // 228 Ω two-slot comparator
        Assert.InRange(rThinFed / rThin, 0.85, 1.15);   // consistency holds thin too
    }

    [Fact]
    public void PhiOnlyProbe_UnderreadsTheEdgeVoltage()
    {
        // The documented Stage C anomaly, pinned as a gauge artifact: at the
        // reference thickness the Φ-only R sits ~30% below the two-leg value
        // (measured ratio 1.30). If this gate ever fails toward 1.0, the A_z leg
        // has been lost somewhere.
        var (rFar, _, rPhi, _) = Measure(1.588e-3, 9.70e9);
        Assert.InRange(rFar / rPhi, 1.15, 1.60);
    }

    [Fact]
    public void VoltageKernelTable_MatchesDirectIntegration()
    {
        // The K_V spline against spline-free direct integration at off-grid ρ —
        // the same accuracy contract as the A/Φ table gate.
        var substrate = new SubstrateStackup(2.2, 0.02, 1.588e-3);
        var table = new LayeredKernelTable(substrate, 10e9, 0.025);
        for (int i = 0; i < 24; i++)
        {
            double rho = 3e-5 * Math.Pow(0.024 / 3e-5, (i + 0.37) / 24.0);
            var spline = table.EvaluateVoltageKernel(rho);
            var direct = table.EvaluateVoltageKernelDirect(rho, refinement: 2);
            double rel = (spline - direct).Magnitude / direct.Magnitude;
            Assert.True(rel <= 1e-6, $"K_V spline at ρ={rho:g4}: rel {rel:e2}");
        }
    }

    [Fact]
    public void VoltageKernelTable_CollapsesToKPhi_AtEpsilonROne()
    {
        // εr = 1 kills the A_z leg exactly: the tabulated K_V must equal the
        // tabulated K_Φ through the whole pipeline (remainders, poles, splines).
        var table = new LayeredKernelTable(new SubstrateStackup(1, 0, 1.588e-3), 10e9, 0.025);
        for (int i = 0; i <= 20; i++)
        {
            double rho = 1e-5 * Math.Pow(0.024 / 1e-5, i / 20.0);
            var (_, kPhi) = table.EvaluateKernels(rho);
            var kV = table.EvaluateVoltageKernel(rho);
            double rel = (kV - kPhi).Magnitude / kPhi.Magnitude;
            Assert.True(rel <= 1e-12, $"K_V vs K_Φ at εr=1, ρ={rho:g4}: rel {rel:e2}");
        }
    }
}
