using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>The layered-media MoM path: the C3 cross-solver gate (εr = 1 through the
/// FULL Sommerfeld pipeline must reproduce the Stage B image-theory patch), assembly
/// contracts (bitwise symmetry, determinism), and the typed v1-scope failures.</summary>
public class LayeredMomTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;

    /// <summary>Identical meshes for both solvers: BuildPatchOverGround IS
    /// BuildRectangularPlate at z = h with a ground attached — the twin drops only
    /// the ground (the layered kernel carries it instead).</summary>
    private static (SurfaceStructure WithGround, SurfaceStructure Bare, SurfacePort Port)
        PatchTwins(double height)
    {
        var grounded = SurfaceMeshBuilder.BuildPatchOverGround(
            0.3 * Lambda, 0.5 * Lambda, height, 0, Lambda / 12);
        var bare = SurfaceMeshBuilder.BuildRectangularPlate(
            0.3 * Lambda, 0.5 * Lambda, Lambda / 12, z: height, portFraction: 0);
        Assert.NotNull(grounded.Structure);
        Assert.NotNull(bare.Structure);
        Assert.Equal(grounded.Port!.EdgeBases, bare.Port!.EdgeBases);
        return (grounded.Structure!, bare.Structure!, grounded.Port!);
    }

    [Fact]
    public void C3_EpsilonROneLayeredSolve_ReproducesTheStageBImagePatch()
    {
        // THE cross-solver gate: an εr = 1 "substrate" routed through the entire
        // Stage C machinery (spectral kernels → Sommerfeld remainder → table →
        // layered assembly with the shifted-triangle image track) against Stage B's
        // independent PEC image pass on the same mesh. Everything differs between
        // the two paths except the physics.
        double height = 0.05 * Lambda;
        var (grounded, bare, port) = PatchTwins(height);

        var stageB = new SurfaceMomSolver().Solve(grounded, Frequency, port);
        var table = new LayeredKernelTable(new SubstrateStackup(1, 0, height),
            Frequency, rhoMax: 0.7 * Lambda);
        var layered = new SurfaceMomSolver().Solve(bare, table, port);

        double rel = (layered.InputImpedance - stageB.InputImpedance).Magnitude
                     / stageB.InputImpedance.Magnitude;
        Assert.True(rel <= 0.005,
            $"Zin layered {layered.InputImpedance} vs Stage B {stageB.InputImpedance} (rel {rel:e2})");
    }

    [Fact]
    public void LayeredImpedanceMatrix_IsComplexSymmetric_Bitwise()
    {
        var bare = SurfaceMeshBuilder.BuildRectangularPlate(
            0.2 * Lambda, 0.3 * Lambda, Lambda / 10, z: 0.03 * Lambda, portFraction: 0.5).Structure!;
        var table = new LayeredKernelTable(new SubstrateStackup(2.2, 0.001, 0.03 * Lambda),
            Frequency, rhoMax: 0.5 * Lambda);
        var z = SurfaceMomSolver.AssembleLayeredImpedanceMatrix(bare, table, 2 * Math.PI * Frequency);
        for (int i = 0; i < bare.BasisCount; i++)
            for (int j = 0; j < bare.BasisCount; j++)
                Assert.Equal(z[i, j], z[j, i]);
    }

    [Fact]
    public void LayeredSolve_IsBitwiseDeterministic()
    {
        SurfaceMomSolution Run()
        {
            var grid = SurfaceMeshBuilder.BuildRectangularPlate(
                0.2 * Lambda, 0.3 * Lambda, Lambda / 10, z: 0.03 * Lambda, portFraction: 0.5);
            var table = new LayeredKernelTable(new SubstrateStackup(2.2, 0, 0.03 * Lambda),
                Frequency, rhoMax: 0.5 * Lambda);
            return new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        }
        var first = Run();
        var second = Run();
        Assert.Equal(first.InputImpedance, second.InputImpedance);
        for (int i = 0; i < first.EdgeCurrents.Length; i++)
            Assert.Equal(first.EdgeCurrents[i], second.EdgeCurrents[i]);
    }

    [Fact]
    public void EpsilonROne_LayeredFarField_ReproducesStageB_Exactly()
    {
        // The far-field machinery's own C3: at εr = 1 the stationary-phase element
        // factors (W̃ = 0, G̃_A = the image pair) must reproduce Stage B's PEC-image
        // pattern on the same currents — measured 1.7e-14 on power, 1.8e-13 pointwise.
        double height = 0.05 * Lambda;
        var (grounded, bare, port) = PatchTwins(height);
        var stageB = new SurfaceMomSolver().Solve(grounded, Frequency, port);
        var table = new LayeredKernelTable(new SubstrateStackup(1, 0, height),
            Frequency, rhoMax: 0.7 * Lambda);
        var layered = new SurfaceMomSolver().Solve(bare, table, port);

        var patternB = SurfaceFarFieldEvaluator.Compute(grounded, stageB);
        var patternC = LayeredFarField.Compute(bare, table, layered);
        // The two SOLVES differ by the C3 tolerance; the pattern machinery itself is
        // compared through the power ratio and normalized intensities.
        Assert.InRange(patternC.TotalRadiatedPowerWatts / patternB.TotalRadiatedPowerWatts,
            0.99, 1.01);
        double isotropic = patternB.TotalRadiatedPowerWatts / (4 * Math.PI);
        for (int ti = 0; ti < patternB.ThetaRadians.Count; ti += 4)
            for (int pi = 0; pi < patternB.PhiRadians.Count; pi += 8)
                Assert.True(Math.Abs(patternC.IntensityWattsPerSteradian[ti, pi]
                                     - patternB.IntensityWattsPerSteradian[ti, pi]) < 0.02 * isotropic);
        // And with no dielectric contrast there is no surface wave to carry power.
        Assert.Equal(0, LayeredFarField.SurfaceWavePowerWatts(bare, table, layered));
    }

    [Fact]
    public void LayeredSolve_RejectsOutOfScopeStructures_Loudly()
    {
        var table = new LayeredKernelTable(new SubstrateStackup(2.2, 0, 0.03 * Lambda),
            Frequency, rhoMax: Lambda);

        // A structure carrying its own ground plane: the substrate model already
        // contains the ground.
        var grounded = SurfaceMeshBuilder.BuildPatchOverGround(
            0.2 * Lambda, 0.3 * Lambda, 0.03 * Lambda, 0, Lambda / 10);
        var ex1 = Assert.Throws<ArgumentException>(() =>
            new SurfaceMomSolver().Solve(grounded.Structure!, table, grounded.Port!));
        Assert.Contains("ground", ex1.Message, StringComparison.OrdinalIgnoreCase);

        // Non-coplanar metal: vertical currents are named future work.
        var bent = new SurfaceStructure(
            new[]
            {
                new Vector3D(0, 0, 0.03 * Lambda), new Vector3D(0.1 * Lambda, 0, 0.03 * Lambda),
                new Vector3D(0, 0.1 * Lambda, 0.03 * Lambda), new Vector3D(0.1 * Lambda, 0.1 * Lambda, 0.08 * Lambda)
            },
            new[] { (0, 1, 2), (1, 3, 2) }, ground: null);
        var port = new SurfacePort(new[] { 0 }, new Vector3D(0, 1, 0));
        var ex2 = Assert.Throws<ArgumentException>(() =>
            new SurfaceMomSolver().Solve(bent, table, port));
        Assert.Contains("one plane", ex2.Message);
    }
}

/// <summary>The Balanis Example 14.1 patch (εr 2.2, h 0.1588 cm, W 1.186 cm,
/// L 0.906 cm, designed for 10 GHz) — the Stage C reference antenna. The resonance is
/// detected FEED-INDEPENDENTLY as the point where the two radiating edges' potential
/// magnitudes balance (the TM01 mode is symmetric; away from resonance the fed edge
/// dominates). The cavity model's absolute edge-R benchmark is NOT gated in v1: the
/// series-gap feed cannot measure patch-to-ground voltage, and formulation C's scalar
/// potential is only the ∇Φ leg of E_z (the −jωA_z leg needs in-slab kernels — named
/// future work); the measured Φ-based V²/2P ≈ 70–85 Ω, h-independent, is reported
/// under a broad plausibility band only.</summary>
public class BalanisPatchTests
{
    private const double PatchW = 1.186e-2;
    private const double PatchL = 0.906e-2;
    private const double SlabH = 1.588e-3;

    private static (SurfaceStructure Structure, SurfacePort Port, LayeredKernelTable Table,
        SurfaceMomSolution Solution) Solve(double epsR, double fHz, double slabH)
    {
        var sub = new SubstrateStackup(epsR, 0, slabH);
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, 1.4e-3, z: slabH, portFraction: 0);
        Assert.NotNull(grid.Structure);
        var table = new LayeredKernelTable(sub, fHz, 0.025);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        return (grid.Structure!, grid.Port!, table, solution);
    }

    /// <summary>|Φ(far edge)| / |Φ(fed edge)| — crosses 1 at the TM01 resonance.</summary>
    private static double EdgeBalance(SurfaceStructure s, LayeredKernelTable t,
        SurfaceMomSolution sol, double slabH)
    {
        var far = LayeredPotentialProbe.ScalarPotential(s, t, sol, new Vector3D(0, PatchL / 2, slabH));
        var near = LayeredPotentialProbe.ScalarPotential(s, t, sol, new Vector3D(0, -PatchL / 2, slabH));
        return far.Magnitude / near.Magnitude;
    }

    [Fact]
    public void Resonance_IsBracketedInTheDesignBand()
    {
        // Design f₀ = 10 GHz; the band is MODEL-vs-model (the transmission-line
        // design formula itself carries ±2–3%). Measured crossing ≈ 9.81 GHz.
        var lo = Solve(2.2, 9.5e9, SlabH);
        var hi = Solve(2.2, 10.3e9, SlabH);
        double balanceLo = EdgeBalance(lo.Structure, lo.Table, lo.Solution, SlabH);
        double balanceHi = EdgeBalance(hi.Structure, hi.Table, hi.Solution, SlabH);
        Assert.True(balanceLo < 1 && balanceHi > 1,
            $"edge balance {balanceLo:F3} at 9.5 GHz, {balanceHi:F3} at 10.3 GHz — no crossing in band");
    }

    [Fact]
    public void ResonanceRatio_ScalesWithPermittivity()
    {
        // f_res(εr)/f_res(1) tracks 1/√εr_eff; εr_eff ∈ (1.9, 2.2) for this geometry
        // (fringing in air) ⇒ the plan band [0.97/√2.2, 1.03/√1.9]. Measured 0.726.
        double CrossingGHz(double epsR, double loGHz, double hiGHz)
        {
            var lo = Solve(epsR, loGHz * 1e9, SlabH);
            var hi = Solve(epsR, hiGHz * 1e9, SlabH);
            double bLo = EdgeBalance(lo.Structure, lo.Table, lo.Solution, SlabH);
            double bHi = EdgeBalance(hi.Structure, hi.Table, hi.Solution, SlabH);
            Assert.True(bLo < 1 && bHi > 1, $"no crossing for εr = {epsR}");
            return loGHz + (hiGHz - loGHz) * (1 - bLo) / (bHi - bLo);
        }
        double ratio = CrossingGHz(2.2, 9.5, 10.3) / CrossingGHz(1.0, 12.8, 14.2);
        Assert.InRange(ratio, 0.97 / Math.Sqrt(2.2), 1.03 / Math.Sqrt(1.9));
    }

    [Fact]
    public void PowerLedger_Closes_AndTheSurfaceWaveTrendHolds()
    {
        // THE Stage C hard gate: P_rad(hemisphere) + P_sw(TM0 residue) ≡ ½Re(V·I*).
        // Measured 1.0000 at every probed frequency and thickness once the
        // Sommerfeld-vs-Fourier factor 2 was pinned; gated at the plan's 3%.
        double LedgerRatio(double slabH, out double swFraction)
        {
            var (s, _, t, sol) = Solve(2.2, 9.8e9, slabH);
            double pin = 0.5 * (Complex.One / sol.InputImpedance).Real;
            double prad = LayeredFarField.Compute(s, t, sol).TotalRadiatedPowerWatts;
            double psw = LayeredFarField.SurfaceWavePowerWatts(s, t, sol);
            Assert.True(psw > 0, "the TM0 mode must carry positive power");
            swFraction = psw / pin;
            return (prad + psw) / pin;
        }
        Assert.InRange(LedgerRatio(SlabH, out double thick), 0.97, 1.03);
        Assert.InRange(LedgerRatio(0.4e-3, out double thin), 0.97, 1.03);
        // Thinner slab ⇒ weaker TM0 launch (measured 0.15 vs 0.03).
        Assert.True(thin < 0.5 * thick,
            $"P_sw/P_in {thin:F3} (0.4 mm) should sit well under {thick:F3} (1.588 mm)");
    }

    [Fact]
    public void ModeStructure_IsTm01_WithBoundedDirectivity()
    {
        var (s, _, t, sol) = Solve(2.2, 9.8e9, SlabH);
        Complex Phi(double x, double y) =>
            LayeredPotentialProbe.ScalarPotential(s, t, sol, new Vector3D(x, y, SlabH));

        // Edge maxima, center null, ~180° flip across the null (measured 8.6×, 187°).
        var farEdge = Phi(0, PatchL / 2);
        var fedEdge = Phi(0, -PatchL / 2);
        var center = Phi(0, 0);
        Assert.True(farEdge.Magnitude > 5 * center.Magnitude);
        Assert.True(fedEdge.Magnitude > 5 * center.Magnitude);
        double flip = Math.Abs(farEdge.Phase - Phi(0, -0.33 * PatchL).Phase) * 180 / Math.PI;
        if (flip > 360) flip -= 360;
        Assert.InRange(flip, 120, 240);
        // Nearly uniform along the radiating edge away from the corners.
        Assert.InRange(Phi(PatchW / 4, PatchL / 2).Magnitude / farEdge.Magnitude, 0.85, 1.2);

        var pattern = LayeredFarField.Compute(s, t, sol);
        Assert.InRange(pattern.MaxDirectivity, 5.0, 7.5); // measured 6.2

        // The reported-not-benchmarked edge quantity (see the class doc): broad band
        // that still catches gross voltage/power regressions.
        double pin = 0.5 * (Complex.One / sol.InputImpedance).Real;
        double rEdgeEquivalent = farEdge.Magnitude * farEdge.Magnitude / (2 * pin);
        Assert.InRange(rEdgeEquivalent, 40, 320);
    }
}
