using System.Numerics;
using OpenSim.Core.Numerics;
using Xunit;

namespace OpenSim.Tests.Core;

public class ComplexCsrMatrixTests
{
    private static CsrMatrix Tridiagonal(int n, double diag, double off)
    {
        var b = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            b.Add(i, i, diag);
            if (i > 0) b.Add(i, i - 1, off);
            if (i < n - 1) b.Add(i, i + 1, off);
        }
        return b.Build();
    }

    [Fact]
    public void Combine_MultipliesLikeItsParts()
    {
        const int n = 8;
        var re = Tridiagonal(n, 4, -1);
        var im = Tridiagonal(n, 2, -0.5);
        const double omega = 3.0;
        var a = ComplexCsrMatrix.Combine(re, im, omega);

        var x = new Complex[n];
        for (int i = 0; i < n; i++)
            x[i] = new Complex(i + 1, -0.5 * i);
        var y = new Complex[n];
        a.Multiply(x, y);

        // Reference: (re + jω·im)·x computed via the real matrices.
        var xr = new double[n];
        var xi = new double[n];
        for (int i = 0; i < n; i++)
        {
            xr[i] = x[i].Real;
            xi[i] = x[i].Imaginary;
        }
        var t1 = new double[n];
        var t2 = new double[n];
        var t3 = new double[n];
        var t4 = new double[n];
        re.Multiply(xr, t1);
        im.Multiply(xi, t2);
        re.Multiply(xi, t3);
        im.Multiply(xr, t4);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(t1[i] - omega * t2[i], y[i].Real, 1e-12);
            Assert.Equal(t3[i] + omega * t4[i], y[i].Imaginary, 1e-12);
        }
    }

    [Fact]
    public void Combine_MismatchedSparsity_Throws()
    {
        var re = Tridiagonal(6, 4, -1);
        var b = new SparseMatrixBuilder(6, 6);
        for (int i = 0; i < 6; i++)
            b.Add(i, i, 1);              // diagonal only — different pattern
        var im = b.Build();

        var ex = Assert.Throws<ArgumentException>(() => ComplexCsrMatrix.Combine(re, im, 1.0));
        Assert.Contains("sparsity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class CocgSolverTests
{
    private static CsrMatrix Tridiagonal(int n, double diag, double off)
    {
        var b = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            b.Add(i, i, diag);
            if (i > 0) b.Add(i, i - 1, off);
            if (i < n - 1) b.Add(i, i + 1, off);
        }
        return b.Build();
    }

    [Fact]
    public void ComplexSymmetricSystem_KnownSolutionRecovered()
    {
        const int n = 40;
        // A = K_re + j·K_im, both real symmetric ⇒ A complex-symmetric (A = Aᵀ).
        var a = ComplexCsrMatrix.Combine(Tridiagonal(n, 4, -1), Tridiagonal(n, 1.5, -0.25), 1.0);

        var expected = new Complex[n];
        for (int i = 0; i < n; i++)
            expected[i] = new Complex(Math.Sin(i * 0.7), Math.Cos(i * 0.3));
        var b = new Complex[n];
        a.Multiply(expected, b);

        var x = new Complex[n];
        var result = new CocgSolver().Solve(a, b, x);

        Assert.True(result.Converged, $"COCG did not converge ({result.Iterations} iterations).");
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(expected[i].Real, x[i].Real, 1e-9);
            Assert.Equal(expected[i].Imaginary, x[i].Imaginary, 1e-9);
        }
    }

    [Fact]
    public void RealOnlyMatrix_MatchesConjugateGradient()
    {
        const int n = 30;
        var real = Tridiagonal(n, 4, -1);   // SPD
        var zero = Tridiagonal(n, 0, 0);    // same pattern, all zeros
        var complexA = ComplexCsrMatrix.Combine(real, zero, 1.0);

        var rhs = new double[n];
        for (int i = 0; i < n; i++)
            rhs[i] = 1 + 0.1 * i;

        var xReal = new double[n];
        var cg = new ConjugateGradientSolver { Tolerance = 1e-12 };
        Assert.True(cg.Solve(real, rhs, xReal).Converged);

        var b = new Complex[n];
        for (int i = 0; i < n; i++)
            b[i] = rhs[i];
        var xComplex = new Complex[n];
        Assert.True(new CocgSolver { Tolerance = 1e-12 }.Solve(complexA, b, xComplex).Converged);

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(xReal[i], xComplex[i].Real, 1e-9);
            Assert.Equal(0.0, xComplex[i].Imaginary, 1e-9);
        }
    }

    [Fact]
    public void SingularSystem_ThrowsActionableBreakdown()
    {
        // Zero diagonal is caught immediately with a pointed message.
        var b = new SparseMatrixBuilder(3, 3);
        b.Add(0, 1, 1);
        b.Add(1, 0, 1);
        b.Add(2, 2, 1);
        b.Add(0, 0, 0);
        b.Add(1, 1, 0);
        var a = ComplexCsrMatrix.Combine(b.Build(), b.Build(), 0.0);

        var rhs = new Complex[] { 1, 1, 1 };
        var x = new Complex[3];
        var ex = Assert.Throws<InvalidOperationException>(() => new CocgSolver().Solve(a, rhs, x));
        Assert.Contains("singular", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
