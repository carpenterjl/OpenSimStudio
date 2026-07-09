using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Core;

public class JacobiEigenSolverTests
{
    // Tridiagonal (-1, 2, -1) has the known spectrum λ_j = 4·sin²(jπ/(2(n+1))).
    [Fact]
    public void Tridiagonal_KnownSpectrum_Recovered()
    {
        const int n = 5;
        var a = new double[n][];
        for (int i = 0; i < n; i++)
        {
            a[i] = new double[n];
            a[i][i] = 2;
            if (i > 0) a[i][i - 1] = -1;
            if (i < n - 1) a[i][i + 1] = -1;
        }

        var (values, vectors) = JacobiEigenSolver.Solve(a);

        for (int j = 1; j <= n; j++)
        {
            double expected = 4 * Math.Pow(Math.Sin(j * Math.PI / (2 * (n + 1))), 2);
            Assert.Equal(expected, values[j - 1], 1e-10);
        }

        // Eigenvectors: orthonormal and satisfying A·v = λ·v.
        for (int k = 0; k < n; k++)
        {
            for (int l = 0; l < n; l++)
            {
                double dot = Enumerable.Range(0, n).Sum(i => vectors[k][i] * vectors[l][i]);
                Assert.Equal(k == l ? 1.0 : 0.0, dot, 1e-10);
            }
            for (int i = 0; i < n; i++)
            {
                double av = Enumerable.Range(0, n).Sum(j => a[i][j] * vectors[k][j]);
                Assert.Equal(values[k] * vectors[k][i], av, 1e-9);
            }
        }
    }
}

public class SubspaceEigensolverTests
{
    /// <summary>Fixed-fixed spring-mass chain: K = k·tridiag(−1,2,−1), M = m·I, with
    /// the exact spectrum λ_j = (4k/m)·sin²(jπ/(2(n+1))).</summary>
    private static (CsrMatrix K, CsrMatrix M) SpringMassChain(int n, double k, double m)
    {
        var kb = new SparseMatrixBuilder(n, n);
        var mb = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            kb.Add(i, i, 2 * k);
            if (i > 0) kb.Add(i, i - 1, -k);
            if (i < n - 1) kb.Add(i, i + 1, -k);
            mb.Add(i, i, m);
        }
        return (kb.Build(), mb.Build());
    }

    [Fact]
    public void SpringMassChain_LowestModesMatchAnalyticSpectrum()
    {
        const int n = 50;
        const double k = 1000, m = 2;
        var (kMat, mMat) = SpringMassChain(n, k, m);

        var result = new SubspaceEigensolver { ModeCount = 6 }.Solve(kMat, mMat);

        Assert.True(result.Converged, $"Did not converge in {result.Iterations} iterations.");
        for (int j = 1; j <= 6; j++)
        {
            double expected = 4 * k / m * Math.Pow(Math.Sin(j * Math.PI / (2 * (n + 1))), 2);
            Assert.Equal(expected, result.Eigenvalues[j - 1], expected * 1e-7);
        }

        // M-orthonormal (M = m·I ⇒ φᵢ·φⱼ = δᵢⱼ/m) and small true residuals.
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                double dot = m * Enumerable.Range(0, n).Sum(idx =>
                    result.Eigenvectors[i][idx] * result.Eigenvectors[j][idx]);
                Assert.Equal(i == j ? 1.0 : 0.0, dot, 1e-8);
            }
            Assert.True(result.ResidualNorms[i] < 1e-6,
                $"Mode {i + 1} residual {result.ResidualNorms[i]:g3} exceeds 1e-6.");
        }
    }

    [Fact]
    public void Solve_IsBitwiseDeterministic()
    {
        var (kMat, mMat) = SpringMassChain(40, 500, 1.5);
        var solver = new SubspaceEigensolver { ModeCount = 4 };

        var first = solver.Solve(kMat, mMat);
        var second = solver.Solve(kMat, mMat);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(first.Eigenvalues[i], second.Eigenvalues[i]);   // exact
            for (int j = 0; j < 40; j++)
                Assert.Equal(first.Eigenvectors[i][j], second.Eigenvectors[i][j]);
        }
    }

    // The real FE path: TET4 stiffness + consistent mass of a supported box. Asserts
    // the contract the modal solver relies on — M-orthonormality and true generalized
    // residuals — on genuinely sparse SPD matrices from the actual assemblers.
    [Fact]
    public void FiniteElementPair_ModesAreMassOrthonormalWithSmallResiduals()
    {
        var mesh = new DelaunayMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(0.1, 0.04, 0.04), new MeshSettings { TargetEdgeLength = 0.02 });
        var steel = new Material
        {
            Name = "Steel", YoungsModulus = 200e9, PoissonRatio = 0.3, Density = 7850
        };
        var assembler = new Tet4Assembler(mesh, steel);
        var prescribed = new Dictionary<int, double>();
        foreach (int node in mesh.GetFaceNodes(new[] { 0 }))
        {
            prescribed[node * 3] = 0;
            prescribed[node * 3 + 1] = 0;
            prescribed[node * 3 + 2] = 0;
        }
        var k = ConstrainedSystemSolver.Reduce(assembler.AssembleStiffness(), prescribed);
        var m = ConstrainedSystemSolver.Reduce(assembler.AssembleMass(), prescribed);

        var result = new SubspaceEigensolver { ModeCount = 4 }.Solve(k.Reduced, m.Reduced);

        Assert.True(result.Converged);
        int n = k.FreeCount;
        var mPhi = new double[n];
        for (int i = 0; i < 4; i++)
        {
            Assert.True(result.Eigenvalues[i] > 0, "Supported structure must have positive eigenvalues.");
            Assert.True(result.ResidualNorms[i] < 1e-6,
                $"Mode {i + 1} residual {result.ResidualNorms[i]:g3} exceeds 1e-6.");
            m.Reduced.Multiply(result.Eigenvectors[i], mPhi);
            for (int j = 0; j < 4; j++)
            {
                double dot = Enumerable.Range(0, n).Sum(idx => result.Eigenvectors[j][idx] * mPhi[idx]);
                Assert.Equal(i == j ? 1.0 : 0.0, dot, 1e-8);
            }
        }
    }
}
