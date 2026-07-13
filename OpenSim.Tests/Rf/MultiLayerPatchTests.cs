using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>Stage F2a — the multi-layer RWG MoM path (metal coplanar at the top of an
/// N-layer grounded stackup). The kernel split now carries G_A's single ε-independent
/// ground image plus K_Φ's quasi-static image SERIES; these gates prove the N-image
/// assembly reproduces the pinned single-slab physics where it must:
///  • N = 1 (one-layer stackup) ≡ the single-slab <see cref="LayeredKernelTable"/> path;
///  • a slab SPLIT into two identical half-thickness layers ≡ the one-slab solve — a
///    genuine 2-layer input whose image series / poles must sum back to the single answer.
/// Both are checked at the assembled-Z level (sharp, no resonance amplification) and at
/// the port impedance (the physics). The single-slab path itself is untouched.</summary>
public class MultiLayerPatchTests
{
    private const double Frequency = 10e9;
    private const double PatchW = 1.186e-2;
    private const double PatchL = 0.906e-2;
    private const double SlabH = 1.588e-3;
    private const double Edge = 1.4e-3;
    private const double RhoMax = 0.025;

    private static (SurfaceStructure Structure, SurfacePort Port) Plate() =>
        BuildPlate();

    private static (SurfaceStructure Structure, SurfacePort Port) BuildPlate()
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            PatchW, PatchL, Edge, z: SlabH, portFraction: 0);
        Assert.NotNull(grid.Structure);
        Assert.NotNull(grid.Port);
        return (grid.Structure!, grid.Port!);
    }

    /// <summary>Max relative entrywise deviation between two assembled impedance matrices,
    /// over entries above a magnitude floor (small entries are dominated by cancellation).</summary>
    private static double MatrixRelDeviation(ComplexDenseMatrix a, ComplexDenseMatrix b)
    {
        double scale = 0;
        for (int i = 0; i < a.Rows; i++)
            for (int j = 0; j < a.Columns; j++)
                scale = Math.Max(scale, a[i, j].Magnitude);
        double worst = 0;
        for (int i = 0; i < a.Rows; i++)
            for (int j = 0; j < a.Columns; j++)
                if (a[i, j].Magnitude > 1e-6 * scale)
                    worst = Math.Max(worst, (a[i, j] - b[i, j]).Magnitude / a[i, j].Magnitude);
        return worst;
    }

    [Fact]
    public void N1_MultiLayerSolve_MatchesTheSingleSlabPath()
    {
        // A one-layer LayeredStackup routed through the whole Stage F pipeline (TLGF images
        // + multi-layer Sommerfeld remainder + N-image MoM split) must reproduce the pinned
        // single-slab table on the SAME mesh. Everything about the two kernel builds differs
        // except the physics; the extraction gates pin the kernels to ~1e-7, and that is what
        // the assembled matrix and the port impedance inherit.
        var (structure, port) = Plate();
        var sub = new SubstrateStackup(2.2, 0.001, SlabH);
        var single = new LayeredKernelTable(sub, Frequency, RhoMax);
        var multi = new MultiLayerKernelTable(LayeredStackup.FromSubstrate(sub), Frequency, RhoMax);

        double omega = 2 * Math.PI * Frequency;
        var zSingle = SurfaceMomSolver.AssembleLayeredImpedanceMatrix(structure, single, omega);
        var zMulti = SurfaceMomSolver.AssembleLayeredImpedanceMatrix(structure, multi, omega);
        // The floor is the kernel-TABLE accuracy (two independently-gridded splines of the
        // same kernel — the extraction suite gates the tabulated N=1 match at 1e-4; the sharp
        // 1e-7 is the direct-integration path, which the MoM does not use). The N-image split
        // logic itself is exact; a mis-scaled/mis-placed image would deviate far past this.
        double zDev = MatrixRelDeviation(zSingle, zMulti);
        Assert.True(zDev < 1e-4, $"assembled-Z deviation {zDev:e2} exceeds 1e-4");

        var solSingle = new SurfaceMomSolver().Solve(structure, single, port);
        var solMulti = new SurfaceMomSolver().Solve(structure, multi, port);
        double zinRel = (solMulti.InputImpedance - solSingle.InputImpedance).Magnitude
                        / solSingle.InputImpedance.Magnitude;
        Assert.True(zinRel < 1e-3,
            $"Zin multi {solMulti.InputImpedance} vs single {solSingle.InputImpedance} (rel {zinRel:e2})");
    }

    [Fact]
    public void SplitSlab_MatchesTheSingleSlabPath()
    {
        // The powerful multi-layer gate: split the substrate into two identical
        // half-thickness layers. The stackup is genuinely N = 2 (the recursion, the image
        // series, and the pole residues all run the multi-layer branch), yet the physics is
        // one uniform slab — so the assembled Z and the port impedance must match the
        // one-layer solve. A wrong generalization that happens to pass N = 1 still fails here.
        var (structure, port) = Plate();
        var single = new LayeredKernelTable(new SubstrateStackup(2.2, 0.001, SlabH), Frequency, RhoMax);
        var split = new MultiLayerKernelTable(new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(2.2, 0.001, SlabH / 2),
            new LayeredStackup.Layer(2.2, 0.001, SlabH / 2)
        }), Frequency, RhoMax);

        double omega = 2 * Math.PI * Frequency;
        var zSingle = SurfaceMomSolver.AssembleLayeredImpedanceMatrix(structure, single, omega);
        var zSplit = SurfaceMomSolver.AssembleLayeredImpedanceMatrix(structure, split, omega);
        double zDev = MatrixRelDeviation(zSingle, zSplit);
        Assert.True(zDev < 1e-4, $"split-slab assembled-Z deviation {zDev:e2} exceeds 1e-4");

        var solSingle = new SurfaceMomSolver().Solve(structure, single, port);
        var solSplit = new SurfaceMomSolver().Solve(structure, split, port);
        double zinRel = (solSplit.InputImpedance - solSingle.InputImpedance).Magnitude
                        / solSingle.InputImpedance.Magnitude;
        Assert.True(zinRel < 1e-3,
            $"split-slab Zin {solSplit.InputImpedance} vs single {solSingle.InputImpedance} (rel {zinRel:e2})");
    }

    [Fact]
    public void MultiLayerSolve_IsBitwiseDeterministic()
    {
        SurfaceMomSolution Run()
        {
            var (structure, port) = BuildPlate();
            var table = new MultiLayerKernelTable(new LayeredStackup(new[]
            {
                new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
                new LayeredStackup.Layer(2.2, 0.001, 0.8e-3)
            }), Frequency, RhoMax);
            return new SurfaceMomSolver().Solve(structure, table, port);
        }
        var first = Run();
        var second = Run();
        Assert.Equal(first.InputImpedance, second.InputImpedance);
        for (int i = 0; i < first.EdgeCurrents.Length; i++)
            Assert.Equal(first.EdgeCurrents[i], second.EdgeCurrents[i]);
    }

    [Fact]
    public void TwoDifferentLayers_ShiftTheImpedanceAwayFromEitherHomogeneousSlab()
    {
        // A sanity check that the N-image path is NOT trivially collapsing to one dielectric:
        // a genuine εr 4.4 / εr 2.2 stack must land away from both homogeneous single slabs of
        // the same total thickness (its effective permittivity sits between them).
        var (structure, port) = Plate();
        Complex Zin(double epsR) => new SurfaceMomSolver()
            .Solve(structure, new LayeredKernelTable(new SubstrateStackup(epsR, 0, SlabH), Frequency, RhoMax),
                port).InputImpedance;
        var stacked = new SurfaceMomSolver().Solve(structure,
            new MultiLayerKernelTable(new LayeredStackup(new[]
            {
                new LayeredStackup.Layer(4.4, 0, SlabH / 2),
                new LayeredStackup.Layer(2.2, 0, SlabH / 2)
            }), Frequency, RhoMax), port).InputImpedance;

        var hi = Zin(4.4);
        var lo = Zin(2.2);
        double toHi = (stacked - hi).Magnitude / hi.Magnitude;
        double toLo = (stacked - lo).Magnitude / lo.Magnitude;
        Assert.True(toHi > 1e-3 && toLo > 1e-3,
            $"stacked Zin {stacked} coincides with a homogeneous slab (εr4.4 {hi}, εr2.2 {lo})");
    }
}
