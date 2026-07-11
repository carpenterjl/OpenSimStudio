using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage G's contract: the parallel fill / kernel-table / LU paths are BITWISE
/// identical to their serial forms at ANY degree of parallelism (the import pipeline's
/// DOP-pinning idiom). Pair moments and table knots compute into pre-sized slots and
/// every floating-point accumulation happens in the canonical serial order afterwards,
/// so these are exact-equality gates — any reordering of FP arithmetic trips them.
/// </summary>
public class RfDeterminismTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;
    private static readonly double Omega = 2 * Math.PI * Frequency;
    private static readonly double K = Omega / 299_792_458.0;

    [Fact]
    public void FreeSpaceAndImageFill_IsBitwiseIdentical_AtAnyDop()
    {
        // A patch over ground exercises BOTH assembly passes (direct + PEC image).
        var grid = SurfaceMeshBuilder.BuildPatchOverGround(
            0.25 * Lambda, 0.35 * Lambda, 0.05 * Lambda, 0, Lambda / 10);
        var s = grid.Structure!;

        var serial = SurfaceMomSolver.AssembleImpedanceMatrix(s, K, Omega, maxDegreeOfParallelism: 1);
        var unbounded = SurfaceMomSolver.AssembleImpedanceMatrix(s, K, Omega);
        var three = SurfaceMomSolver.AssembleImpedanceMatrix(s, K, Omega, maxDegreeOfParallelism: 3);
        for (int i = 0; i < s.BasisCount; i++)
            for (int j = 0; j < s.BasisCount; j++)
            {
                Assert.Equal(serial[i, j], unbounded[i, j]);
                Assert.Equal(serial[i, j], three[i, j]);
            }
    }

    [Fact]
    public void LayeredFill_IsBitwiseIdentical_AtAnyDop()
    {
        double height = 0.03 * Lambda;
        var bare = SurfaceMeshBuilder.BuildRectangularPlate(
            0.2 * Lambda, 0.3 * Lambda, Lambda / 10, z: height, portFraction: 0.5).Structure!;
        var table = new LayeredKernelTable(new SubstrateStackup(2.2, 0.001, height),
            Frequency, rhoMax: 0.5 * Lambda);

        var serial = SurfaceMomSolver.AssembleLayeredImpedanceMatrix(bare, table, Omega,
            maxDegreeOfParallelism: 1);
        var unbounded = SurfaceMomSolver.AssembleLayeredImpedanceMatrix(bare, table, Omega);
        for (int i = 0; i < bare.BasisCount; i++)
            for (int j = 0; j < bare.BasisCount; j++)
                Assert.Equal(serial[i, j], unbounded[i, j]);
    }

    [Fact]
    public void KernelTable_IsBitwiseIdentical_AtAnyDop()
    {
        // rhoMax > 2/k1 so the FAR region exists and BOTH knot loops run.
        var substrate = new SubstrateStackup(2.2, 0.02, 1.588e-3);
        double rhoMax = 0.025;
        var serial = new LayeredKernelTable(substrate, 10e9, rhoMax, maxDegreeOfParallelism: 1);
        var unbounded = new LayeredKernelTable(substrate, 10e9, rhoMax);

        Assert.Equal(serial.PoleCount, unbounded.PoleCount);
        for (int i = 0; i <= 400; i++)
        {
            double rho = 1e-7 * Math.Pow(rhoMax / 1e-7, i / 400.0);
            Assert.Equal(serial.EvaluateSmooth(rho), unbounded.EvaluateSmooth(rho));
            Assert.Equal(serial.EvaluateKernels(rho), unbounded.EvaluateKernels(rho));
        }
    }

    [Fact]
    public void ComplexLuFactor_IsBitwiseIdentical_AtAnyDop()
    {
        // 150 > the 64-row parallel threshold, so the parallel trailing update runs.
        // Deterministic LCG fill; diagonally dominated so pivoting still permutes some
        // rows (the off-diagonal magnitudes vary) without being singular.
        const int n = 150;
        var m = new ComplexDenseMatrix(n, n);
        uint state = 12345;
        double Next()
        {
            state = state * 1664525u + 1013904223u;
            return state / (double)uint.MaxValue - 0.5;
        }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = new Complex(Next() + (i == j ? 3.0 : 0.0), Next());

        var rhs = new Complex[n];
        for (int i = 0; i < n; i++) rhs[i] = new Complex(Next(), Next());

        var serial = ComplexLu.Factor(m, maxDegreeOfParallelism: 1).Solve(rhs);
        var unbounded = ComplexLu.Factor(m).Solve(rhs);
        for (int i = 0; i < n; i++)
            Assert.Equal(serial[i], unbounded[i]);
    }

    [Fact]
    public void FullLayeredSolve_Zin_IsBitwiseIdentical_AtAnyDop()
    {
        double height = 0.03 * Lambda;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            0.2 * Lambda, 0.3 * Lambda, Lambda / 10, z: height, portFraction: 0.5);
        var table = new LayeredKernelTable(new SubstrateStackup(2.2, 0, height),
            Frequency, rhoMax: 0.5 * Lambda);

        var serial = new SurfaceMomSolver { MaxDegreeOfParallelism = 1 }
            .Solve(grid.Structure!, table, grid.Port!);
        var parallel = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        Assert.Equal(serial.InputImpedance, parallel.InputImpedance);
        for (int i = 0; i < serial.EdgeCurrents.Length; i++)
            Assert.Equal(serial.EdgeCurrents[i], parallel.EdgeCurrents[i]);
    }
}
