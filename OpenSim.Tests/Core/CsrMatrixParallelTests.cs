using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Core;

public class CsrMatrixParallelTests
{
    /// <summary>
    /// The array overload of Multiply parallelizes over rows above a size threshold. The
    /// claim is BITWISE equality with the sequential span path — each row is an
    /// independently accumulated dot product with unchanged in-row order and a disjoint
    /// write — and a claim like that gets a gate, on a system safely above the threshold.
    /// </summary>
    [Fact]
    public void ParallelMultiply_IsBitwiseIdenticalToSequential_AboveTheThreshold()
    {
        const int n = 30_000;   // above the 20k parallel threshold
        var builder = new SparseMatrixBuilder(n, n);
        // A deterministic pseudo-random 7-ish-diagonal pattern with awkward values.
        uint state = 12345;
        double Next()
        {
            state = state * 1664525u + 1013904223u;
            return (state / (double)uint.MaxValue - 0.5) * Math.PI;
        }
        for (int row = 0; row < n; row++)
        {
            builder.Add(row, row, 7.0 + Math.Abs(Next()));
            foreach (int off in new[] { -173, -1, 1, 173, 4111 })
            {
                int col = row + off;
                if (col >= 0 && col < n) builder.Add(row, col, Next());
            }
        }
        var a = builder.Build();

        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = Next();

        var ySequential = new double[n];
        a.Multiply(x.AsSpan(), ySequential.AsSpan());   // span form: always sequential

        // Repeat the parallel form several times: a racy implementation is flaky, and a
        // flaky gate that runs once proves little.
        for (int repeat = 0; repeat < 5; repeat++)
        {
            var yParallel = new double[n];
            a.Multiply(x, yParallel);                   // array form: parallel at this size
            Assert.Equal(ySequential, yParallel);       // bitwise, not approximately
        }
    }
}
