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
