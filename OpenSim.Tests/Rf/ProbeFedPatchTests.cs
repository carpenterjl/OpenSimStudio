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
/// Since the junction vertex term (Feature 12, 2026-10-05): R peaks 148 Ω at 9.4 GHz for
/// y = −L/4 and 261 Ω at 9.2 GHz for y = −3L/8 (cos² law within 3 %); the quasi-static C
/// reads 2.09 pF; the figures above are the earlier ones.
///
/// The power ledger (sheet, junction and tube, space wave and surface wave, all coherent)
/// closes to 0.1 % on a lossless slab since FU-1; see PowerLedger_OnSubstrate_Closes.
/// </summary>
public class ProbeFedPatchTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public ProbeFedPatchTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

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
    public void ProbeFedResistance_ReproducesTheModalResistanceAtTheProbePoint()
    {
        // The cross-formulation gate (FU-2). A feed that excites one cavity mode sees, at the
        // mode's resistance peak, R_in = |V(y₀)|²/(2P): the modal voltage at the feed point per
        // unit of the power that mode radiates. The reference is a DIFFERENT feed — a 0.5 mm
        // series gap across the patch's centre line, where the mode's current peaks and its
        // voltage is zero, so no gap sits near either radiating edge — with V(y₀) read at the
        // probe's own position. Its voltage probe reads ±0.50 V on the two halves of that patch
        // in the quasi-static limit (the absolute check), both edges agree to the digit, and
        // the reading is stable under refinement.
        //
        // The identity assumes an electrically negligible feed, so the gate is on a thin
        // substrate (0.508 mm, tube current top/base 1.14–1.18). Measured 1.046 at y = −L/4 and
        // 1.052 at y = −3L/8. On the Balanis slab (1.588 mm) the tube is not negligible — its top
        // current is 1.7–2.2× its base current at these high-impedance insets, a distributed
        // L–C network between the port and the patch — and the same comparison reads 1.70 and
        // 1.50, inset-dependent: the feed's own physics, not a modal error. The previous gate
        // (1.258, banded 1.11–1.41) took its reference on the strip between the rim and a
        // series gap 1.3 mm in from it, where |V|²/2P read anything from 141 to 236 Ω with the
        // sampling point. The band keeps its ±0.15 width around the new reading.
        const double h = 0.508e-3;
        var substrate = new SubstrateStackup(2.2, 0.0, h);
        double y = -0.375 * PatchL;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: h, portFraction: 0, snapVertex: (0.0, y));
        var probe = new ProbeFeed(0.0, y, 0.06e-3, Segments);
        var f = new List<double>();
        var r = new List<double>();
        for (double frequency = 10.35e9; frequency <= 10.66e9; frequency += 0.05e9)
        {
            var table = new LayeredKernelTable(substrate, frequency, 0.025);
            f.Add(frequency);
            r.Add(new SurfaceMomSolver().SolveProbeFed(grid.Structure!, table, probe).Surface.InputImpedance.Real);
        }
        int k = r.IndexOf(r.Max());
        Assert.True(k > 0 && k < r.Count - 1, "the resistance peak must lie inside the scan");
        double shift = 0.5 * (r[k - 1] - r[k + 1]) / (r[k - 1] - 2 * r[k] + r[k + 1]);
        double peak = r[k] - 0.25 * (r[k - 1] - r[k + 1]) * shift;

        var at = new LayeredKernelTable(substrate, f[k], 0.025);
        var centre = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: h, portOffset: PatchL / 2, portGapWidth: 0.5e-3);
        var reference = new SurfaceMomSolver().Solve(centre.Structure!, at, centre.Port!);
        double pIn = 0.5 * (1.0 / reference.InputImpedance).Real;
        var v = LayeredPotentialProbe.EdgeVoltage(centre.Structure!, at, reference, new Vector3D(0, y, h));
        double modal = v.Magnitude * v.Magnitude / (2 * pIn);
        Assert.InRange(peak / modal, 0.90, 1.20);
    }

    [Fact]
    public void TheModalReference_ReadsHalfTheGapVoltageOnEachHalf_AtLowFrequency()
    {
        // The absolute check on the reference's voltage probe: at 0.3 GHz the centre-gap patch
        // is two capacitor halves in series across the 1 V gap, so by symmetry each half sits at
        // ±0.5 V everywhere. Measured 0.498–0.527 on a 1.4 mm mesh, 0.499–0.513 on 0.7 mm.
        var table = new LayeredKernelTable(Substrate, 0.3e9, 0.025);
        var centre = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: Thickness, portOffset: PatchL / 2, portGapWidth: 0.5e-3);
        var solution = new SurfaceMomSolver().Solve(centre.Structure!, table, centre.Port!);
        foreach (double y in new[] { -4.0e-3, -2.265e-3, -1.0e-3 })
        {
            var below = LayeredPotentialProbe.EdgeVoltage(centre.Structure!, table, solution, new Vector3D(0, y, Thickness));
            var above = LayeredPotentialProbe.EdgeVoltage(centre.Structure!, table, solution, new Vector3D(0, -y, Thickness));
            Assert.InRange(-below.Real, 0.47, 0.54);
            Assert.InRange(above.Real, 0.47, 0.54);
            Assert.Equal(-below.Real, above.Real, 5);
        }
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
        // Measured 148.3 since the junction vertex term (107.2 without it); the band keeps its
        // ±25 Ω width around the new value.
        Assert.InRange(at94.Surface.InputImpedance.Real, 125.0, 175.0);

        // Inset trend: R follows cos²(π·x/L) between the outer insets (the dominant-
        // mode law; it legitimately degrades toward the patch-center null). Each inset is read
        // at its own resistance peak on a 0.2 GHz grid — 9.4 GHz for y = −L/4, 9.2 GHz for
        // y = −3L/8 — because the two resonate at different frequencies and a ratio at one
        // fixed frequency mixes the trend with the detuning. Measured 260.6/148.3 = 1.757
        // against cos² 1.708 (1.029); without the vertex term the same reading was
        // 210.7/107.2 (1.151), and at a fixed 9.4 GHz 0.94 then and 0.75 now.
        var (outer, _, _) = Solve(9.2e9, -0.375 * PatchL);
        double measuredRatio = outer.Surface.InputImpedance.Real
            / at94.Surface.InputImpedance.Real;
        double cosRatio = Math.Pow(Math.Cos(Math.PI * 0.125), 2) / 0.5; // 1.708
        Assert.InRange(measuredRatio / cosRatio, 0.90, 1.10);
    }

    [Theory]
    [InlineData(2.2, 9.4e9)]     // at resonance (y = −3L/8)
    [InlineData(2.2, 8.0e9)]     // well below it
    [InlineData(2.2, 10.5e9)]    // above it
    [InlineData(4.4, 7.0e9)]
    public void PowerLedger_OnSubstrate_Closes(double epsR, double frequencyHz)
    {
        // The COMPLETE mixed-current ledger: the space wave (raw RWG + junction disc/half-RWGs
        // + the vertical E_θ leg) plus the surface-wave power of the horizontal AND vertical
        // currents launched COHERENTLY (both launch the same TM mode, so the power goes as
        // |a_h + a_v|² and the cross term is real physics).
        //
        // RF-6 (FU-1). This read 1.0415 at resonance, then 0.9643 once the junction vertex term
        // was in the matrix, and 0.879 at 8 GHz. Checked block by block against ½Re(xᴴZx) — the
        // power any current vector x delivers, which the far field and surface wave must
        // account for — the patch currents alone and the tube alone each closed to 1e-4; only
        // the horizontal-vertical cross terms did not, and only on a dielectric. The cause was
        // in the surface-wave formula: its cross terms paired the patch transform, which
        // carries every point's lateral phase e^{jk·ρ′}, with the tube as if it stood at the
        // origin. With the tube's phase the ledger is 0.9999 / 0.9992 / 1.0000 at
        // (2.2, 9.4 GHz) / (2.2, 8 GHz) / (4.4, 7 GHz). The εr = 1 gate below is untouched:
        // air has no surface wave.
        var substrate = new SubstrateStackup(epsR, 0.0, Thickness);
        double y = -0.375 * PatchL;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: Thickness, portFraction: 0, snapVertex: (0.0, y));
        var table = new LayeredKernelTable(substrate, frequencyHz, 0.025);
        var probe = new ProbeFeed(0.0, y, ProbeRadius, Segments);
        var solution = new SurfaceMomSolver().SolveProbeFed(grid.Structure!, table, probe);
        double pIn = 0.5 * Complex.Conjugate(1.0 / solution.Surface.InputImpedance).Real;
        var far = LayeredFarField.Compute(grid.Structure!, table, solution, probe);
        double pSw = LayeredFarField.SurfaceWavePowerWatts(grid.Structure!, table, solution, probe);
        Assert.InRange((far.TotalRadiatedPowerWatts + pSw) / pIn, 0.99, 1.01);
    }

    [Fact]
    public void WithLossyMetal_TheLedgerClosesOnTheHeat()
    {
        // FU-37: the patch sheet and the probe tube carry their surface impedance. With a poor
        // conductor (a hundredth of copper) the heat is a visible share of the input, and the
        // ledger closes only when it is counted: radiated + surface wave + heat = input.
        const double sigma = 5.8e5, frequency = 9.4e9;
        double y = -0.375 * PatchL;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: Thickness, portFraction: 0, snapVertex: (0.0, y));
        var table = new LayeredKernelTable(Substrate, frequency, 0.025);
        var probe = new ProbeFeed(0.0, y, ProbeRadius, Segments);
        var solver = new SurfaceMomSolver
        {
            SheetImpedance = f => SheetLoss.CopperSheet(f, sigma, 35e-6, bothFaces: false),
            WireSurfaceImpedance = f => SheetLoss.RoundWire(f, sigma)
        };
        var lossy = solver.SolveProbeFed(grid.Structure!, table, probe);
        var lossless = new SurfaceMomSolver().SolveProbeFed(grid.Structure!, table, probe);

        double pIn = 0.5 * Complex.Conjugate(1.0 / lossy.Surface.InputImpedance).Real;
        var far = LayeredFarField.Compute(grid.Structure!, table, lossy, probe);
        double pSw = LayeredFarField.SurfaceWavePowerWatts(grid.Structure!, table, lossy, probe);
        double heat = lossy.OhmicLossWatts;
        _output.WriteLine($"Zin {lossless.Surface.InputImpedance:f2} → {lossy.Surface.InputImpedance:f2} Ω; heat {heat / pIn:P1} of the input; " +
                          $"ledger with heat {(far.TotalRadiatedPowerWatts + pSw + heat) / pIn:f4}, without {(far.TotalRadiatedPowerWatts + pSw) / pIn:f4}");
        Assert.True(heat > 0.02 * pIn, "the heat should be visible at a hundredth of copper");
        Assert.InRange((far.TotalRadiatedPowerWatts + pSw + heat) / pIn, 0.99, 1.01);
        Assert.Equal(0.0, lossless.OhmicLossWatts);
    }

    [Fact]
    public void EveryBlockOfTheCurrent_IsAccountedFor()
    {
        // The diagnostic that located RF-6, kept as a gate. For ANY current vector x on a
        // lossless structure, ½Re(xᴴZx) is the power it delivers, and the far field plus the
        // surface wave must account for it — so the patch currents alone, the tube alone and the
        // two together are each a ledger of their own, and a cross term that is wrong shows up
        // in the last one only. Before FU-1: 1.0000, 1.0002 and 0.952 at 8 GHz.
        double y = -0.375 * PatchL;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, MeshEdge, z: Thickness, portFraction: 0, snapVertex: (0.0, y));
        var surface = grid.Structure!;
        var table = new LayeredKernelTable(Substrate, 8.0e9, 0.025);
        var probe = new ProbeFeed(0.0, y, ProbeRadius, Segments);
        var solver = new SurfaceMomSolver();
        var solution = solver.SolveProbeFed(surface, table, probe);
        var z = solver.ProbeFedMatrix(surface, table, probe);
        int edges = surface.BasisCount, total = edges + Segments + 1;

        double Ledger(bool patch, bool tube)
        {
            var x = new Complex[total];
            if (patch) for (int e = 0; e < edges; e++) x[e] = solution.RawEdgeCurrents[e];
            if (tube) for (int n = 0; n < Segments; n++) x[edges + n] = solution.TubeCurrents[n];
            Complex delivered = Complex.Zero;
            for (int i = 0; i < total; i++)
                for (int j = 0; j < total; j++)
                    delivered += Complex.Conjugate(x[i]) * z[i, j] * x[j];
            var tubeCurrents = new Complex[Segments + 1];
            for (int n = 0; n < Segments; n++) tubeCurrents[n] = x[edges + n];
            var raw = x[..edges];
            var part = new ProbeFedSolution(solution.Surface with { EdgeCurrents = raw },
                tubeCurrents, raw, solution.TubeNodes);
            return (LayeredFarField.Compute(surface, table, part, probe).TotalRadiatedPowerWatts
                + LayeredFarField.SurfaceWavePowerWatts(surface, table, part, probe))
                / (0.5 * delivered.Real);
        }
        Assert.InRange(Ledger(patch: true, tube: false), 0.999, 1.001);
        Assert.InRange(Ledger(patch: false, tube: true), 0.999, 1.001);
        Assert.InRange(Ledger(patch: true, tube: true), 0.999, 1.001);
    }

    [Fact]
    public void PowerLedger_DoesNotDependOnWhereThePatchSits()
    {
        // The defect's signature: a lateral shift of the whole structure changes no physics,
        // but it changed the ledger, because only the tube was left at the origin.
        double ledger(double shift)
        {
            double y = -0.375 * PatchL;
            var grid = SurfaceMeshBuilder.BuildRectangularPlate(
                PatchW, PatchL, MeshEdge, z: Thickness, portFraction: 0, snapVertex: (0.0, y));
            var vertices = grid.Structure!.Vertices
                .Select(v => new Vector3D(v.X + shift, v.Y + shift, v.Z)).ToList();
            var moved = new SurfaceStructure(vertices, grid.Structure.Triangles, null);
            var table = new LayeredKernelTable(Substrate, 8.0e9, 0.025);
            var probe = new ProbeFeed(shift, y + shift, ProbeRadius, Segments);
            var solution = new SurfaceMomSolver().SolveProbeFed(moved, table, probe);
            double pIn = 0.5 * Complex.Conjugate(1.0 / solution.Surface.InputImpedance).Real;
            return (LayeredFarField.Compute(moved, table, solution, probe).TotalRadiatedPowerWatts
                + LayeredFarField.SurfaceWavePowerWatts(moved, table, solution, probe)) / pIn;
        }
        double here = ledger(0), there = ledger(5e-3);
        Assert.True(Math.Abs(here - there) < 1e-3, $"ledger {here:f4} here, {there:f4} 5 mm away");
    }

    // ---- A5: the coherent mixed-current surface-wave power ----

    [Fact]
    public void MixedSurfaceWavePower_ReducesToTheVerticalOracle_WithNoPatchCurrent()
    {
        // THE sign-decisive gate for the vertical block. A power ledger is quadratic in the
        // coupling, so it cannot see the cross term's sign; this gate can, because it isolates
        // the vertical block and compares it against VerticalSurfaceWavePowerWatts - the formula
        // already gated to 5e-4 against a probe-only oracle.
        //
        // The isolation has to be done honestly. Zeroing only the horizontal current is NOT
        // enough: the junction charge partition means a non-zero tube TOP current still deposits
        // a point charge at the junction (the disc's delta), which the vertical-only formula has
        // no term for - it was validated on a probe-only structure whose tube top is free and
        // therefore carries no current. Comparing the two in that state compares two different
        // physical situations. Driving the tube top to zero removes the junction charge from
        // both sides and leaves exactly the vertical blocks.
        var (solution, surface, table) = Solve(9.4e9, -0.375 * PatchL);
        var probe = new ProbeFeed(0.0, -0.375 * PatchL, ProbeRadius, Segments);

        var freeEndTube = (Complex[])solution.TubeCurrents.Clone();
        freeEndTube[^1] = Complex.Zero;                 // a free tube top: no junction charge

        var quiet = new ProbeFedSolution(
            solution.Surface with { EdgeCurrents = new Complex[solution.Surface.EdgeCurrents.Count()] },
            freeEndTube,
            new Complex[solution.RawEdgeCurrents.Length],
            solution.TubeNodes);

        double mixed = LayeredFarField.SurfaceWavePowerWatts(surface, table, quiet, probe);
        double verticalOnly = LayeredFarField.VerticalSurfaceWavePowerWatts(
            table.Substrate, table.FrequencyHz,
            OpenSim.Rf.Surface.ProbeAssembly.TubeNodes(table.Substrate, probe),
            freeEndTube);

        Assert.True(Math.Abs(mixed - verticalOnly) < 1e-9 * Math.Abs(verticalOnly),
            $"vertical-only limit {mixed:e6} vs the oracle-gated formula {verticalOnly:e6}");
    }

    [Fact]
    public void MixedSurfaceWavePower_ReducesToTheHorizontalFormula_WithNoTubeCurrent()
    {
        // The complement: with the tube carrying nothing, the mixed form must collapse onto the
        // shipped horizontal-only expression. Together with the gate above this pins both
        // diagonal blocks, so any discrepancy in the full ledger is attributable to the CROSS
        // term alone rather than to a rewritten formula.
        var (solution, surface, table) = Solve(9.4e9, -0.375 * PatchL);
        var probe = new ProbeFeed(0.0, -0.375 * PatchL, ProbeRadius, Segments);

        var noTube = new ProbeFedSolution(
            solution.Surface, new Complex[solution.TubeCurrents.Length],
            solution.RawEdgeCurrents, solution.TubeNodes);

        double mixed = LayeredFarField.SurfaceWavePowerWatts(surface, table, noTube, probe);
        // The horizontal formula, reached through the plain surface overload with the same
        // (raw + junction) current the probe path uses. The junction coefficient is the tube's
        // top current, which is zero here, so the raw RWG current is the whole story.
        double horizontal = LayeredFarField.SurfaceWavePowerWatts(
            surface, table, solution.Surface with { EdgeCurrents = noTube.RawEdgeCurrents });

        Assert.True(Math.Abs(mixed - horizontal) < 1e-9 * Math.Abs(horizontal),
            $"horizontal-only limit {mixed:e6} vs the shipped formula {horizontal:e6}");
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
