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
    public void PowerLedger_HoldsNearResonance_SheetCurrentsOnly()
    {
        // P_rad + P_sw vs ½Re(V·I*) with the probe's own radiation still excluded —
        // measured 1.03 at the near-edge inset, 1.08 at L/4; the 3% plan gate awaits
        // the vertical far-field leg (named work, not a hidden approximation).
        var (solution, surface, table) = Solve(9.4e9, -0.375 * PatchL);
        double pIn = 0.5 * Complex.Conjugate(1.0 / solution.Surface.InputImpedance).Real;
        var far = LayeredFarField.Compute(surface, table, solution.Surface);
        double pSw = LayeredFarField.SurfaceWavePowerWatts(surface, table, solution.Surface);
        Assert.InRange((far.TotalRadiatedPowerWatts + pSw) / pIn, 0.90, 1.12);
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
