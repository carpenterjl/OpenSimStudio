using System.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage F2b step 4 — the covered patch at the MoM level: a radiating patch buried under a
/// dielectric cover of the same εr as its substrate (a homogeneous slab split at the metal,
/// so ∂_z ã_z is single-valued at the sheet — the interior-source read-out F2b-1..3 built and
/// gated at the kernel level). Because the layered kernel is RADIAL (ρ only), the buried
/// patch's absolute z never enters assembly: the interior <see cref="MultiLayerKernelTable"/>
/// (built with <c>sourceInterface</c>) encodes the metal height, and the existing
/// <see cref="SurfaceMomSolver.Solve(SurfaceStructure, MultiLayerKernelTable, SurfacePort, double)"/>
/// consumes it unchanged. These gates prove the resulting physics:
///  • an all-air cover ≡ the coplanar-at-top solve of the sub-slab beneath the metal (Zin);
///  • a real dielectric cover pulls the resonance DOWN, and the shift GROWS with cover
///    thickness (the loading trend — no sharp published Δf exists for the homogeneous
///    covered patch, so this is a collapse + asserted-trend gate);
///  • the covered solve is bitwise deterministic.
/// The far-field / power-ledger gates for the covered patch live in
/// <see cref="CoveredPatchFarFieldTests"/>.
/// </summary>
public class CoveredPatchTests
{
    private const double PatchW = 1.186e-2;
    private const double PatchL = 0.906e-2;
    private const double Edge = 1.4e-3;
    private const double RhoMax = 0.03;

    // A patch on a thin low-εr substrate — low εr maximizes the fringing field the cover
    // captures, so the cover-loading shift is largest and cleanest to resolve.
    private const double EpsR = 2.2;
    private const double TanD = 0.001;
    private const double HSub = 0.5e-3;

    private static (SurfaceStructure Structure, SurfacePort Port) BuildPlate(double z = HSub)
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(PatchW, PatchL, Edge, z, portFraction: 0);
        Assert.NotNull(grid.Structure);
        Assert.NotNull(grid.Port);
        return (grid.Structure!, grid.Port!);
    }

    [Theory]
    [InlineData(2.2, 0.001)]
    [InlineData(4.4, 0.02)]
    [InlineData(6.0, 0.0)]
    public void AirCover_EqualsCoplanarTopSubSlab_AtZinLevel(double epsR, double tanD)
    {
        // A covered patch whose cover is air (εr = 1) is the same boundary-value problem as the
        // bare coplanar-at-top patch of the sub-slab beneath the metal — everything above the
        // sheet is uniform air. Solved on the SAME mesh (the plate z is irrelevant to the radial
        // kernel), the interior-table Zin must equal the F2a coplanar-at-top Zin to the table-
        // spline floor (1e-3, the same tolerance the F2a N=1 gate carries).
        var (structure, port) = BuildPlate();
        const double f = 10e9;
        var subSlab = new MultiLayerKernelTable(
            LayeredStackup.FromSubstrate(new SubstrateStackup(epsR, tanD, HSub)), f, RhoMax);
        var airCover = new MultiLayerKernelTable(
            new LayeredStackup(new[]
            {
                new LayeredStackup.Layer(epsR, tanD, HSub),
                new LayeredStackup.Layer(1, 0, 0.4e-3),
                new LayeredStackup.Layer(1, 0, 0.7e-3),
            }), f, RhoMax, sourceInterface: 0);

        var zTop = new SurfaceMomSolver().Solve(structure, subSlab, port).InputImpedance;
        var zCov = new SurfaceMomSolver().Solve(structure, airCover, port).InputImpedance;
        double rel = (zCov - zTop).Magnitude / zTop.Magnitude;
        Assert.True(rel < 1e-3, $"air-cover Zin {zCov} vs coplanar-top {zTop} (rel {rel:e2})");
    }

    [Fact]
    public void HomogeneousCover_ShiftsResonanceDown_GrowingWithThickness()
    {
        // The covered-patch physics: a dielectric cover of the substrate's εr raises the
        // effective permittivity the fringing field sees, lowering the resonance — and thicker
        // cover captures more fringing, so the downward shift grows with thickness (saturating
        // toward the fully-enclosed εr). Collapse + asserted-trend (no golden Δf for this case).
        var (structure, port) = BuildPlate();
        const double fMin = 8, fMax = 13; // GHz — brackets the ~11 GHz bare resonance with margin
        const int count = 21;

        double bare = ResonanceGHz(structure, port,
            f => new MultiLayerKernelTable(
                LayeredStackup.FromSubstrate(new SubstrateStackup(EpsR, TanD, HSub)), f, RhoMax),
            fMin, fMax, count);

        double[] covers = { 0.6e-3, 1.2e-3, 2.4e-3 };
        var res = covers.Select(hCover => ResonanceGHz(structure, port,
            f => new MultiLayerKernelTable(
                LayeredStackup.CoveredPatch(EpsR, TanD, HSub, hCover), f, RhoMax,
                sourceInterface: LayeredStackup.CoveredPatchMetalInterface),
            fMin, fMax, count)).ToArray();

        Assert.True(res[0] < bare,
            $"thin cover resonance {res[0]:g4} GHz not below bare {bare:g4} GHz");
        Assert.True(res[1] < res[0],
            $"resonance {res[1]:g4} (1.2 mm) not below {res[0]:g4} (0.6 mm)");
        Assert.True(res[2] < res[1],
            $"resonance {res[2]:g4} (2.4 mm) not below {res[1]:g4} (1.2 mm)");
    }

    // ---- A1.3: the SUPERSTRATE (cover εr ≠ substrate εr) at the solve level ----

    [Theory]
    [InlineData(2.2, 0.001, 0.6e-3)]
    [InlineData(4.4, 0.02, 1.2e-3)]
    [InlineData(10.2, 0.0023, 0.4e-3)]
    public void MatchedCoverOverload_IsBitwiseTheShippedCoveredPatch(
        double epsR, double tanD, double hCover)
    {
        // The two-material factory with cover ≡ substrate must be the SHIPPED four-argument
        // covered patch, bit for bit — the pin that says lifting the restriction changed no
        // existing answer. Compared at the solve, not just the Layer records, so any downstream
        // dependence on how the stackup was CONSTRUCTED would show.
        var (structure, port) = BuildPlate();
        Complex Solve(LayeredStackup s) => new SurfaceMomSolver().Solve(
            structure,
            new MultiLayerKernelTable(s, 10e9, RhoMax,
                sourceInterface: LayeredStackup.CoveredPatchMetalInterface),
            port).InputImpedance;

        Assert.Equal(
            Solve(LayeredStackup.CoveredPatch(epsR, tanD, HSub, hCover)),
            Solve(LayeredStackup.CoveredPatch(epsR, tanD, HSub, epsR, tanD, hCover)));
    }

    [Theory]
    [InlineData(2.2, 0.001)]
    [InlineData(4.4, 0.02)]
    [InlineData(6.0, 0.0)]
    public void AirSuperstrate_EqualsCoplanarTopSubSlab(double epsR, double tanD)
    {
        // The εr₂ = 1 limit of the general superstrate: an air cover is no cover, so the buried
        // solve must equal the bare coplanar-at-top solve of the sub-slab. This is the same
        // physics as AirCover_EqualsCoplanarTopSubSlab_AtZinLevel but reached through the
        // two-material factory — it gates the FACTORY's layer ordering (substrate first, cover
        // second), which a symmetric matched-εr stack cannot distinguish.
        var (structure, port) = BuildPlate();
        const double f = 10e9;
        var subSlab = new MultiLayerKernelTable(
            LayeredStackup.FromSubstrate(new SubstrateStackup(epsR, tanD, HSub)), f, RhoMax);
        var airCover = new MultiLayerKernelTable(
            LayeredStackup.CoveredPatch(epsR, tanD, HSub, 1.0, 0.0, 0.9e-3), f, RhoMax,
            sourceInterface: LayeredStackup.CoveredPatchMetalInterface);

        var zTop = new SurfaceMomSolver().Solve(structure, subSlab, port).InputImpedance;
        var zCov = new SurfaceMomSolver().Solve(structure, airCover, port).InputImpedance;
        double rel = (zCov - zTop).Magnitude / zTop.Magnitude;
        Assert.True(rel < 1e-3, $"air-superstrate Zin {zCov} vs coplanar-top {zTop} (rel {rel:e2})");
    }

    [Fact]
    public void Superstrate_ShiftsResonanceDown_MonotonicallyWithCoverEpsR()
    {
        // The superstrate trend: at FIXED cover thickness, a denser cover captures the fringing
        // field in a higher permittivity, so the resonance falls monotonically with cover εr.
        // Asserted trend, like the thickness gate above; no golden Δf exists for this case.
        //
        // Cover PERMITTIVITY moves the resonance far more than cover thickness does — ε_eff
        // roughly averages the two media, so εr 6 over an εr 2.2 substrate lands near 7 GHz
        // against the bare patch's ~11 GHz. That needs a wide band, which is why this uses the
        // coarse-then-fine helper (and why the fixed narrow grid the thickness gate can afford
        // would silently return a band edge here — measured, on the first run of this gate).
        // The εr₂ = 1 ≡ bare identity is gated separately and cheaply by
        // AirSuperstrate_EqualsCoplanarTopSubSlab, so no bare sweep is repeated here.
        var (structure, port) = BuildPlate();
        const double hCover = 1.2e-3;

        double[] coverEps = { 1.0, 3.0, 6.0 };
        var res = coverEps.Select(eps => ResonanceGHzWide(structure, port,
            f => new MultiLayerKernelTable(
                LayeredStackup.CoveredPatch(EpsR, TanD, HSub, eps, 0.0, hCover), f, RhoMax,
                sourceInterface: LayeredStackup.CoveredPatchMetalInterface),
            fMinGHz: 4, fMaxGHz: 13.5)).ToArray();

        for (int i = 1; i < res.Length; i++)
            Assert.True(res[i] < res[i - 1],
                $"resonance {res[i]:g4} GHz (cover εr {coverEps[i]}) not below "
                + $"{res[i - 1]:g4} GHz (cover εr {coverEps[i - 1]})");
    }

    [Fact]
    public void AsymmetricSuperstrate_PowerLedgerCloses()
    {
        // The house identity on a stack the shipped scope could not express: LOSSLESS substrate
        // AND a lossless cover of a DIFFERENT εr, so all accepted power radiates or launches
        // surface waves. If the read-out side had been wrong at the ε jump, the charge term
        // would be scaled and this ledger would not close.
        var (structure, port) = BuildPlate();
        const double f = 10e9;
        var table = new MultiLayerKernelTable(
            LayeredStackup.CoveredPatch(2.2, 0.0, HSub, 6.0, 0.0, 0.8e-3), f, RhoMax,
            sourceInterface: LayeredStackup.CoveredPatchMetalInterface);
        var sol = new SurfaceMomSolver().Solve(structure, table, port);

        double pin = 0.5 * (Complex.One / sol.InputImpedance).Real;
        double pRad = LayeredFarField.Compute(structure, table, sol).TotalRadiatedPowerWatts;
        double pSw = LayeredFarField.SurfaceWavePowerWatts(structure, table, sol);
        double ratio = (pRad + pSw) / pin;
        Assert.True(ratio is > 0.97 and < 1.03,
            $"asymmetric-superstrate ledger P_rad {pRad:g4} + P_sw {pSw:g4} = {(pRad + pSw):g4} "
            + $"vs P_in {pin:g4} (ratio {ratio:F4})");
    }

    [Fact]
    public void CoveredPatchSolve_IsBitwiseDeterministic()
    {
        Complex Run()
        {
            var (structure, port) = BuildPlate();
            var table = new MultiLayerKernelTable(
                LayeredStackup.CoveredPatch(4.4, 0.02, 0.8e-3, 0.6e-3), 10e9, RhoMax,
                sourceInterface: LayeredStackup.CoveredPatchMetalInterface);
            return new SurfaceMomSolver().Solve(structure, table, port).InputImpedance;
        }
        Assert.Equal(Run(), Run());
    }

    /// <summary>Resonance over a WIDE band: a coarse scan brackets the Re(Zin) peak, a fine scan
    /// resolves it, and parabolic interpolation refines it — the same estimator as
    /// <see cref="ResonanceGHz"/>, reached in two stages so a band wide enough for large
    /// permittivity shifts costs about the same number of solves as one narrow fixed grid.
    ///
    /// <para>A peak landing on a band EDGE is a typed failure, not a number: the maximum of a
    /// monotone segment is not a resonance, and returning the edge frequency would fake a
    /// monotone trend out of a window that was simply too narrow.</para></summary>
    private static double ResonanceGHzWide(SurfaceStructure structure, SurfacePort port,
        Func<double, MultiLayerKernelTable> tableAtHz, double fMinGHz, double fMaxGHz,
        int coarse = 15, int fine = 7)
    {
        var solver = new SurfaceMomSolver();
        double Re(double fGHz) =>
            solver.Solve(structure, tableAtHz(fGHz * 1e9), port).InputImpedance.Real;

        var fs = new double[coarse];
        var re = new double[coarse];
        for (int i = 0; i < coarse; i++)
        {
            fs[i] = fMinGHz + (fMaxGHz - fMinGHz) * i / (coarse - 1);
            re[i] = Re(fs[i]);
        }
        int k = 0;
        for (int i = 1; i < coarse; i++)
            if (re[i] > re[k]) k = i;
        Assert.True(k > 0 && k < coarse - 1,
            $"Re(Zin) peaks at the band edge ({fs[k]:g4} GHz of [{fMinGHz:g4}, {fMaxGHz:g4}] GHz) "
            + "— the resonance is outside the swept window, so widen it. A boundary maximum is "
            + "not a resonance.");

        // Fine scan across the bracketing coarse interval [fs[k-1], fs[k+1]].
        double lo = fs[k - 1], hi = fs[k + 1];
        var ff = new double[fine];
        var fr = new double[fine];
        for (int i = 0; i < fine; i++)
        {
            ff[i] = lo + (hi - lo) * i / (fine - 1);
            fr[i] = Re(ff[i]);
        }
        int j = 0;
        for (int i = 1; i < fine; i++)
            if (fr[i] > fr[j]) j = i;
        if (j > 0 && j < fine - 1)
        {
            double y0 = fr[j - 1], y1 = fr[j], y2 = fr[j + 1];
            double denom = y0 - 2 * y1 + y2;
            double delta = denom != 0 ? 0.5 * (y0 - y2) / denom : 0;
            return ff[j] + delta * (hi - lo) / (fine - 1);
        }
        return ff[j];
    }

    /// <summary>Sweep Re(Zin) over a frequency band and return the resonance (GHz) as the
    /// Re(Zin)-peak frequency, refined by 3-point parabolic interpolation so the estimate is
    /// sub-grid-resolution (the small cover-loading shifts must be resolved past the grid step).</summary>
    private static double ResonanceGHz(SurfaceStructure structure, SurfacePort port,
        Func<double, MultiLayerKernelTable> tableAtHz, double fMinGHz, double fMaxGHz, int count)
    {
        var fs = new double[count];
        var re = new double[count];
        var solver = new SurfaceMomSolver();
        for (int i = 0; i < count; i++)
        {
            double fGHz = fMinGHz + (fMaxGHz - fMinGHz) * i / (count - 1);
            fs[i] = fGHz;
            re[i] = solver.Solve(structure, tableAtHz(fGHz * 1e9), port).InputImpedance.Real;
        }
        int k = 0;
        for (int i = 1; i < count; i++)
            if (re[i] > re[k]) k = i;
        if (k > 0 && k < count - 1)
        {
            double y0 = re[k - 1], y1 = re[k], y2 = re[k + 1];
            double denom = y0 - 2 * y1 + y2;
            double delta = denom != 0 ? 0.5 * (y0 - y2) / denom : 0;   // ∈ (−1, 1) near a peak
            return fs[k] + delta * (fMaxGHz - fMinGHz) / (count - 1);
        }
        return fs[k];
    }
}
