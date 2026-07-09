using System.Numerics;
using OpenSim.Core.Numerics;
using Xunit;

namespace OpenSim.Tests.Core;

public class ComplexLuTests
{
    private static ComplexDenseMatrix Random(int n, int seed, bool symmetric)
    {
        var random = new Random(seed);
        var a = new ComplexDenseMatrix(n, n);
        for (int i = 0; i < n; i++)
            for (int j = symmetric ? i : 0; j < n; j++)
            {
                var value = new Complex(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
                a[i, j] = value;
                if (symmetric) a[j, i] = value;
            }
        // Diagonal dominance is NOT added: partial pivoting must cope on its own.
        return a;
    }

    private static double RelativeResidual(ComplexDenseMatrix a, Complex[] x, Complex[] b)
    {
        var ax = a.Multiply(x);
        double num = 0, den = 0;
        for (int i = 0; i < b.Length; i++)
        {
            num += (ax[i] - b[i]).Magnitude * (ax[i] - b[i]).Magnitude;
            den += b[i].Magnitude * b[i].Magnitude;
        }
        return Math.Sqrt(num / den);
    }

    [Fact]
    public void KnownSystem_SolvesExactly()
    {
        // [ 2   i ] [x0]   [ 3+i ]        x = (1+i, 1-i):  2(1+i) + i(1-i) = 2+2i+i+1 = 3+3i… pin
        // [ -i  1 ] [x1] = [ 2-2i]        by construction instead: choose x, compute b = A·x.
        var a = new ComplexDenseMatrix(2, 2);
        a[0, 0] = new Complex(2, 0); a[0, 1] = new Complex(0, 1);
        a[1, 0] = new Complex(0, -1); a[1, 1] = new Complex(1, 0);
        var expected = new[] { new Complex(1, 1), new Complex(1, -1) };
        var b = a.Multiply(expected);

        var x = ComplexLu.Factor(a).Solve(b);

        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(expected[i].Real, x[i].Real, 1e-14);
            Assert.Equal(expected[i].Imaginary, x[i].Imaginary, 1e-14);
        }
    }

    [Fact]
    public void ZeroLeadingPivot_IsHandledByRowSwap()
    {
        var a = new ComplexDenseMatrix(2, 2);
        a[0, 0] = Complex.Zero; a[0, 1] = Complex.One;
        a[1, 0] = Complex.One; a[1, 1] = Complex.One;
        var x = ComplexLu.Factor(a).Solve(new[] { new Complex(2, 0), new Complex(3, 0) });
        Assert.Equal(1.0, x[0].Real, 1e-14);
        Assert.Equal(2.0, x[1].Real, 1e-14);
    }

    [Fact]
    public void ComplexSymmetricSystem_SolvesToTightResidual()
    {
        // The moment-method shape: A = Aᵀ ≠ Aᴴ, dense, no structure to exploit.
        var a = Random(40, seed: 7, symmetric: true);
        var random = new Random(11);
        var b = new Complex[40];
        for (int i = 0; i < b.Length; i++)
            b[i] = new Complex(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);

        var x = ComplexLu.Factor(a).Solve(b);
        Assert.True(RelativeResidual(a, x, b) < 1e-12);
    }

    [Fact]
    public void GeneralSystem_SolvesToTightResidual()
    {
        var a = Random(60, seed: 3, symmetric: false);
        var random = new Random(5);
        var b = new Complex[60];
        for (int i = 0; i < b.Length; i++)
            b[i] = new Complex(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);

        var x = ComplexLu.Factor(a).Solve(b);
        Assert.True(RelativeResidual(a, x, b) < 1e-12);
    }

    [Fact]
    public void SingularMatrix_ThrowsActionably()
    {
        var a = new ComplexDenseMatrix(3, 3);
        for (int j = 0; j < 3; j++)
        {
            a[0, j] = new Complex(1, j);
            a[1, j] = new Complex(2, 2 * j);   // row 1 = 2 × row 0
            a[2, j] = new Complex(j, 1);
        }
        var ex = Assert.Throws<InvalidOperationException>(() => ComplexLu.Factor(a));
        Assert.Contains("singular", ex.Message);
    }

    [Fact]
    public void FactorizationIsReusable_AndDoesNotMutateTheInput()
    {
        var a = Random(20, seed: 9, symmetric: true);
        var before = a.Clone();
        var lu = ComplexLu.Factor(a);
        for (int i = 0; i < 20; i++)
            for (int j = 0; j < 20; j++)
                Assert.Equal(before[i, j], a[i, j]);

        var b1 = new Complex[20];
        var b2 = new Complex[20];
        b1[0] = Complex.One;
        b2[19] = new Complex(0, 1);
        Assert.True(RelativeResidual(a, lu.Solve(b1), b1) < 1e-12);
        Assert.True(RelativeResidual(a, lu.Solve(b2), b2) < 1e-12);
    }

    [Fact]
    public void Solve_IsBitwiseDeterministic()
    {
        var a = Random(30, seed: 13, symmetric: true);
        var b = new Complex[30];
        var random = new Random(17);
        for (int i = 0; i < b.Length; i++)
            b[i] = new Complex(random.NextDouble(), random.NextDouble());

        var x1 = ComplexLu.Factor(a).Solve(b);
        var x2 = ComplexLu.Factor(a).Solve(b);
        for (int i = 0; i < 30; i++)
            Assert.Equal(x1[i], x2[i]);
    }
}
