using MathNet.Numerics.LinearAlgebra;
using OpenSim.Core.Numerics;
using Xunit;

namespace OpenSim.Tests.Core;

public class CsrMatrixTests
{
    [Fact]
    public void Multiply_MatchesMathNetDenseReference()
    {
        var rng = new Random(42);
        int n = 30;
        var dense = Matrix<double>.Build.Dense(n, n);
        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (rng.NextDouble() < 0.2)
                {
                    double v = rng.NextDouble() * 10 - 5;
                    dense[i, j] += v;
                    builder.Add(i, j, v);
                }
            }
        }
        var csr = builder.Build();

        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = rng.NextDouble();

        var expected = dense * Vector<double>.Build.Dense(x);
        var actual = new double[n];
        csr.Multiply(x, actual);

        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], actual[i], 10);
    }

    [Fact]
    public void Add_AccumulatesDuplicateEntries()
    {
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 0, 1.5);
        builder.Add(0, 0, 2.5);
        var m = builder.Build();
        Assert.Equal(4.0, m.GetValue(0, 0));
        Assert.Equal(1, m.NonZeroCount);
    }
}

public class ConjugateGradientTests
{
    [Fact]
    public void Solve_SmallSpdSystem_ReturnsKnownSolution()
    {
        // A = [[4,1],[1,3]], b = [1,2] → x = [1/11, 7/11]
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 0, 4); builder.Add(0, 1, 1);
        builder.Add(1, 0, 1); builder.Add(1, 1, 3);
        var a = builder.Build();

        var x = new double[2];
        var result = new ConjugateGradientSolver().Solve(a, new double[] { 1, 2 }, x);

        Assert.True(result.Converged);
        Assert.Equal(1.0 / 11.0, x[0], 9);
        Assert.Equal(7.0 / 11.0, x[1], 9);
    }

    [Fact]
    public void Solve_RandomSpdSystem_MatchesMathNetDirectSolve()
    {
        var rng = new Random(7);
        int n = 50;
        // Build SPD matrix A = Bᵀ·B + n·I
        var b = Matrix<double>.Build.Dense(n, n, (_, _) => rng.NextDouble() - 0.5);
        var a = b.TransposeThisAndMultiply(b) + Matrix<double>.Build.DenseIdentity(n) * n;

        var builder = new SparseMatrixBuilder(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                builder.Add(i, j, a[i, j]);

        var rhs = new double[n];
        for (int i = 0; i < n; i++) rhs[i] = rng.NextDouble();

        var expected = a.Solve(Vector<double>.Build.Dense(rhs));

        var x = new double[n];
        var result = new ConjugateGradientSolver { Tolerance = 1e-12 }.Solve(builder.Build(), rhs, x);

        Assert.True(result.Converged);
        for (int i = 0; i < n; i++)
            Assert.Equal(expected[i], x[i], 7);
    }

    [Fact]
    public void Solve_UnconstrainedSystem_ThrowsActionableError()
    {
        // Singular: zero diagonal row.
        var builder = new SparseMatrixBuilder(2, 2);
        builder.Add(0, 0, 1);
        var a = builder.Build();
        var solver = new ConjugateGradientSolver();
        Assert.Throws<InvalidOperationException>(() => solver.Solve(a, new double[] { 1, 1 }, new double[2]));
    }
}

public class KdTreeTests
{
    [Fact]
    public void NearestNeighbor_MatchesBruteForce()
    {
        var rng = new Random(3);
        var points = Enumerable.Range(0, 500)
            .Select(_ => new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()))
            .ToList();
        var tree = new KdTree(points);

        for (int q = 0; q < 50; q++)
        {
            var query = new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble());
            int expected = 0;
            double bestDist = double.PositiveInfinity;
            for (int i = 0; i < points.Count; i++)
            {
                double d = Vector3D.DistanceSquared(query, points[i]);
                if (d < bestDist) { bestDist = d; expected = i; }
            }
            Assert.Equal(expected, tree.NearestNeighbor(query));
        }
    }

    [Fact]
    public void RadiusSearch_MatchesBruteForce()
    {
        var rng = new Random(5);
        var points = Enumerable.Range(0, 300)
            .Select(_ => new Vector3D(rng.NextDouble(), rng.NextDouble(), rng.NextDouble()))
            .ToList();
        var tree = new KdTree(points);
        var query = new Vector3D(0.5, 0.5, 0.5);
        const double radius = 0.25;

        var expected = Enumerable.Range(0, points.Count)
            .Where(i => Vector3D.Distance(points[i], query) <= radius)
            .OrderBy(i => i);
        var actual = tree.RadiusSearch(query, radius).OrderBy(i => i);

        Assert.Equal(expected, actual);
    }
}
