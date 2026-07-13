using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage E checkpoint E3: the probe-fed Balanis Ex 14.1 patch. The headline gate is
/// the CROSS-FORMULATION modal identity: the probe-fed input resistance divided by
/// the cavity cos²(πx/L) factor must reproduce the Stage D modal edge resistance
/// R_V = |V_edge|²/(2P_in) measured on the EDGE-FED solve — two entirely different
/// feed models agreeing on the mode (measured ~5%, gated 15% per plan).
///
/// Measured (mesh 1.4 mm, a = 0.2 mm, 3 segments): R peaks 107 Ω at ~9.4 GHz for the
/// y = −L/4 probe (X swings +48 → −6 across 9.4–9.8 GHz — the probe-loaded resonance,
/// slightly below the unloaded edge-balance 9.70 GHz, as probe inductance demands);
/// R/cos² = 201/214 at the two outer insets; the quasi-static limit reads 2.08 pF
/// against the 1.32 pF parallel-plate value (+ fringing ≈ 1.7) — the sharpest junction
/// gate, since coupling sign/magnitude errors are INVISIBLE in Zin sweeps (they enter
/// quadratically) but hit C_eff directly.
///
/// The power ledger carries the sheet currents only (the electrically short probe's
/// own far field is not yet included): 1.03–1.08 near resonance, gated [0.90, 1.12];
/// tightening to the plan's 3% needs the vertical far-field leg — a named follow-up,
/// not an approximation hidden in a band.
/// </summary>
public class ProbeFedPatchTests
{
    private const double PatchW = 1.186e-2;
    private const double PatchL = 0.906e-2;
    private const double MeshEdge = 1.4e-3;
    private const double Thickness = 1.588e-3;
    private const double ProbeRadius = 0.2e-3;
    private const int Segments = 3;
    private static readonly SubstrateStackup Substrate = new(2.2, 0.0, Thickness);

    private static (ProbeFedSolution Solution, SurfaceStructure Surface, LayeredKernelTable Table)
        Solve(double frequencyHz, double yProbe)
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: Thickness, portFraction: 0, snapVertex: (0.0, yProbe));
        Assert.NotNull(grid.Structure);
        var table = new LayeredKernelTable(Substrate, frequencyHz, 0.025);
        var probe = new ProbeFeed(0.0, yProbe, ProbeRadius, Segments);
        var solution = new SurfaceMomSolver().SolveProbeFed(grid.Structure!, table, probe);
        return (solution, grid.Structure!, table);
    }

    [Fact]
    public void QuasiStaticLimit_SeesTheParallelPlateCapacitor()
    {
        // THE junction razor: at 0.5 GHz the patch is a capacitor C = ε₀εr·WL/h
        // = 1.32 pF (+ fringing + probe locality ⇒ ~1.6–2.2). The affine ρ_v fan
        // measured 0.20 pF (point-charge choke) and the mis-oriented halves 50 pF
        // (near-free charge path) — both would fail this by an order of magnitude.
        var (solution, _, _) = Solve(0.5e9, -PatchL / 4);
        var zin = solution.Surface.InputImpedance;
        double cEff = -1.0 / (2 * Math.PI * 0.5e9 * zin.Imaginary);
        Assert.InRange(cEff * 1e12, 1.4, 2.6);
        // The tube current must be uniform at quasi-statics (no spurious shunt).
        double baseMag = solution.TubeCurrents[0].Magnitude;
        double topMag = solution.TubeCurrents[^1].Magnitude;
        Assert.InRange(topMag / baseMag, 0.85, 1.05);
    }

    [Fact]
    public void ProbeFedResistance_ReproducesTheStageDModalResistance()
    {
        // R_in(y=−L/4)/cos²(π/4) against the edge-fed |V_edge|²/(2P_in) at the same
        // frequency — the cross-formulation gate (measured ratio ~1.05).
        double f = 9.4e9;
        var (probeSol, _, _) = Solve(f, -PatchL / 4);
        double rProbe = probeSol.Surface.InputImpedance.Real;
        double modalFromProbe = rProbe / 0.5; // cos²(π/4)

        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: Thickness, portFraction: 0);
        var table = new LayeredKernelTable(Substrate, f, 0.025);
        var edgeSol = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        double pIn = 0.5 * (1.0 / edgeSol.InputImpedance).Real;
        Complex vEdge = Complex.Zero;
        for (int i = 0; i < 5; i++)
            vEdge += LayeredPotentialProbe.EdgeVoltage(grid.Structure!, table, edgeSol,
                new Vector3D(-0.4 * PatchW + 0.2 * PatchW * i, -PatchL / 2 + 0.5 * MeshEdge, Thickness));
        vEdge /= 5;
        double modalFromEdge = vEdge.Magnitude * vEdge.Magnitude / (2 * pIn);

        Assert.InRange(modalFromProbe / modalFromEdge, 0.85, 1.15);
    }

    [Fact]
    public void ProbeFedPatch_ResonatesInBand_WithTheCosSquaredInsetTrend()
    {
        // Resonance: X swings through zero between 9.4 and 9.8 GHz (probe-loaded,
        // slightly below the unloaded 9.70) with R in the modal band at the peak.
        var (at94, _, _) = Solve(9.4e9, -PatchL / 4);
        var (at98, _, _) = Solve(9.8e9, -PatchL / 4);
        Assert.True(at94.Surface.InputImpedance.Imaginary > 0
                 && at98.Surface.InputImpedance.Imaginary < 0,
            $"X should cross zero in (9.4, 9.8) GHz: X(9.4) = {at94.Surface.InputImpedance.Imaginary:F1}, "
            + $"X(9.8) = {at98.Surface.InputImpedance.Imaginary:F1}");
        Assert.InRange(at94.Surface.InputImpedance.Real, 85.0, 135.0); // measured 107

        // Inset trend: R follows cos²(π·x/L) between the outer insets (the dominant-
        // mode law; it legitimately degrades toward the patch-center null).
        var (outer, _, _) = Solve(9.4e9, -0.375 * PatchL);
        double measuredRatio = outer.Surface.InputImpedance.Real
            / at94.Surface.InputImpedance.Real;
        double cosRatio = Math.Pow(Math.Cos(Math.PI * 0.125), 2) / 0.5; // 1.708
        Assert.InRange(measuredRatio / cosRatio, 0.80, 1.10); // measured 0.94
    }

    [Fact]
    public void PowerLedger_OnSubstrate_HoldsToTheOpenVerticalSurfaceWaveLeg()
    {
        // The COMPLETE mixed-current far field: raw RWG + junction disc/half-RWGs +
        // the vertical E_θ leg (space wave), and the horizontal-current surface-wave
        // power. Measured (mesh 1.4 mm, a = 0.2 mm) ~1.05 at resonance. The residual is
        // ISOLATED and NAMED: at εr = 1 the same far field conserves power EXACTLY
        // (CompleteProbeFarField_ConservesPowerExactly_WithNoSubstrate = 1.0000), so the
        // space wave is not the gap — the open item is the vertical tube current's own
        // TM0 surface-wave launch (a mixed A/Φ residue leg), which interferes with the
        // horizontal radial current and pulls this the last few %. Gated as a band with
        // the trend, NOT weakened silently: the physics of the solve (Zin, resonance,
        // R/cos²) are correct and separately gated.
        var (solution, surface, table) = Solve(9.4e9, -0.375 * PatchL);
        double pIn = 0.5 * Complex.Conjugate(1.0 / solution.Surface.InputImpedance).Real;
        var far = LayeredFarField.Compute(surface, table, solution, probe: new ProbeFeed(
            0.0, -0.375 * PatchL, ProbeRadius, Segments));
        double pSw = LayeredFarField.SurfaceWavePowerWatts(surface, table, solution,
            new ProbeFeed(0.0, -0.375 * PatchL, ProbeRadius, Segments));
        Assert.InRange((far.TotalRadiatedPowerWatts + pSw) / pIn, 0.97, 1.10);
    }

    [Fact]
    public void ProbeVerticalFarField_ConservesPower_AsAMonopoleOverGround()
    {
        // The absolute-scale gate for the vertical far-field leg: at εr = 1 a probe of
        // length L (open top) IS a monopole over PEC ground, and a lossless monopole
        // radiates ALL its input power into the hemisphere. So the E_θ pattern
        // LayeredFarField assembles from the tube current must satisfy
        // P_rad(hemisphere) = ½Re(V·I₀*) to the quadrature — an INDEPENDENT check of the
        // far-field integrand against the port power (measured 1.0002 across L, f). This
        // is the sharp gate on the vertical leg; on the microstrip PATCH the ledger does
        // NOT close to a few % (the junction-disc's own radiation and the tube's surface
        // wave are not yet in the far field — see PowerLedger_… below), but the leg
        // itself is exact, which THIS proves.
        double mu0 = 4e-7 * Math.PI, eps0 = 8.8541878128e-12;
        double eta = Math.Sqrt(mu0 / eps0);
        foreach (var (f, L, radius, seg) in new[]
        {
            (2.4e9, 0.0312, 0.5e-3, 8),
            (1.0e9, 0.070, 1.0e-3, 10),
            (5.0e9, 0.014, 0.3e-3, 6),
        })
        {
            var air = new SubstrateStackup(1.0, 0.0, L);
            var set = new VerticalKernelSet(air, f);
            var probe = new ProbeFeed(0, 0, radius, seg);
            var (_, cur) = ProbeAssembly.SolveProbeOnly(set, probe);
            double k0 = set.K0, omega = 2 * Math.PI * f;
            var nodes = ProbeAssembly.TubeNodes(air, probe);
            var currents = new Complex[seg + 1];
            for (int i = 0; i < seg; i++) currents[i] = cur[i]; // open top: node seg carries 0
            var (u, uw) = OpenSim.Rf.GaussLegendre.Rule(64, 0, 1);
            double pRad = 0;
            for (int ti = 0; ti < u.Length; ti++)
            {
                double cosT = u[ti], sinT = Math.Sqrt(1 - cosT * cosT), th = Math.Acos(cosT);
                var gHat = LayeredFarField.VerticalAmplitude(air, k0, th, nodes, currents);
                var eTheta = (omega * k0 * cosT / (4 * Math.PI)) * (-sinT) * gHat;
                pRad += 2 * Math.PI * uw[ti] * eTheta.Magnitude * eTheta.Magnitude / (2 * eta);
            }
            double pIn = 0.5 * cur[0].Real; // V = 1 (real), P_in = ½Re(V·I₀*)
            Assert.InRange(pRad / pIn, 0.98, 1.02);
        }
    }

    [Fact]
    public void CompleteProbeFarField_ConservesPowerExactly_WithNoSubstrate()
    {
        // THE gate that the mixed-current SPACE-wave far field is exact and complete:
        // at εr = 1 there are NO surface waves (P_sw = 0), so a lossless probe-fed patch
        // must radiate ALL its input power — P_rad(hemisphere) = ½Re(V·I*) at ANY
        // frequency, resonant or not. Measured 1.0000 across the band: the raw RWG +
        // junction disc/half-RWG + vertical E_θ legs, summed coherently, ARE the exact
        // radiation operator of the extended probe system. (On a real substrate the
        // vertical current's TM0 surface-wave leg is still open — see the substrate
        // ledger below — but this proves the space-wave side is not the gap.)
        var air = new SubstrateStackup(1.0, 0.0, 2.0e-3);
        foreach (double f in new[] { 8.0e9, 10.0e9, 12.0e9, 14.0e9 })
        {
            double y = -PatchL / 4;
            var grid = SurfaceMeshBuilder.BuildRectangularPlate(
                PatchW, PatchL, MeshEdge, z: 2.0e-3, portFraction: 0, snapVertex: (0.0, y));
            var table = new LayeredKernelTable(air, f, 0.025);
            Assert.Equal(0, table.PoleCount); // no surface waves in air
            var probe = new ProbeFeed(0.0, y, ProbeRadius, Segments);
            var sol = new SurfaceMomSolver().SolveProbeFed(grid.Structure!, table, probe);
            double pIn = 0.5 * Complex.Conjugate(1.0 / sol.Surface.InputImpedance).Real;
            var far = LayeredFarField.Compute(grid.Structure!, table, sol, probe);
            Assert.InRange(far.TotalRadiatedPowerWatts / pIn, 0.99, 1.01);
        }
    }

    [Fact]
    public void VerticalSurfaceWavePower_MatchesTheProbeOnlyOracle()
    {
        // The vertical tube current's TM0 surface-wave launch, validated INDEPENDENTLY:
        // for a probe-only-into-grounded-substrate (a pure vertical radiator) the space
        // wave P_rad is exact, so P_sw = P_in − P_rad is an oracle. The modal formula
        // P_sw = (ωk_p/16π)·2π·Re[∫∫J_z*·Res G_zz·J_z − ∫∫q_v*·Res K_Φ·q_v] reproduces it
        // to ~5e-4 across εr / thickness / frequency — no fitted constant (the horizontal
        // formula's ωk_p/16π prefactor carries over; the 2π is the axial-symmetry
        // azimuth). This is the validated foundation; mixing it into the PATCH ledger
        // additionally needs the junction charge-continuity partition (a named open item).
        foreach (var (eps, h, f, seg) in new[]
        {
            (2.2, 1.588e-3, 9.0e9, 3), (2.2, 1.588e-3, 12.0e9, 3), (10.2, 1.27e-3, 8.0e9, 4),
        })
        {
            var sub = new SubstrateStackup(eps, 0.0, h);
            var set = new VerticalKernelSet(sub, f);
            var probe = new ProbeFeed(0, 0, 0.15e-3, seg);
            var (_, cur) = ProbeAssembly.SolveProbeOnly(set, probe);
            double k0 = set.K0, omega = 2 * Math.PI * f;
            double mu0 = 4e-7 * Math.PI, eps0 = 8.8541878128e-12, eta = Math.Sqrt(mu0 / eps0);
            var nodes = ProbeAssembly.TubeNodes(sub, probe);
            var jc = new Complex[seg + 1];
            for (int i = 0; i < seg; i++) jc[i] = cur[i];
            var (uu, uw) = OpenSim.Rf.GaussLegendre.Rule(64, 0, 1);
            double pRad = 0;
            for (int ti = 0; ti < uu.Length; ti++)
            {
                double cosT = uu[ti], sinT = Math.Sqrt(1 - cosT * cosT), th = Math.Acos(cosT);
                var g = LayeredFarField.VerticalAmplitude(sub, k0, th, nodes, jc);
                var e = (omega * k0 * cosT / (4 * Math.PI)) * (-sinT) * g;
                pRad += 2 * Math.PI * uw[ti] * e.Magnitude * e.Magnitude / (2 * eta);
            }
            double target = 0.5 * cur[0].Real - pRad;
            double model = LayeredFarField.VerticalSurfaceWavePowerWatts(sub, f, nodes, jc);
            Assert.InRange(model / target, 0.98, 1.02);
        }
    }

    [Fact]
    public void JunctionContinuity_IsAnExactDiscreteIdentity()
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: Thickness, portFraction: 0,
            snapVertex: (0.0, -PatchL / 4));
        int vertex = -1;
        double best = double.MaxValue;
        for (int v = 0; v < grid.Structure!.Vertices.Count; v++)
        {
            double dx = grid.Structure.Vertices[v].X;
            double dy = grid.Structure.Vertices[v].Y + PatchL / 4;
            double d = dx * dx + dy * dy;
            if (d < best) { best = d; vertex = v; }
        }
        var fan = new AttachmentFan(grid.Structure, vertex, ProbeRadius);
        // Interior vertex: the wedges tile the full disc, and the halves carry the
        // whole junction current — Σθᵢ = 2π and Σγᵢlᵢ = 1 exactly (to trig roundoff).
        Assert.True(Math.Abs(fan.TotalAngle - 2 * Math.PI) <= 1e-9,
            $"Σθ = {fan.TotalAngle} should be 2π at an interior vertex");
        double crossing = fan.Wedges.Sum(w =>
            w.Gamma * grid.Structure.Edges[w.EdgeBasis].Length);
        Assert.True(Math.Abs(crossing - 1.0) <= 1e-9,
            $"Σγl = {crossing} should be exactly 1 (the transported junction current)");
    }
}
