namespace OpenSim.Core.Numerics;

/// <summary>
/// The deterministic parallel-fill recipe shared by the solvers: every work item computes its
/// result into its own pre-sized slot on the thread pool, with no shared mutation, and the caller
/// then combines the slots sequentially in index order. Because the combining order is fixed,
/// every floating-point accumulation is the same as in a serial loop, and the result is bitwise
/// identical at any degree of parallelism, including 1.
/// <para>
/// A work item that needs random numbers takes them from
/// <see cref="SplitMix64.ForStream"/> with its own index, never from a shared generator.
/// </para>
/// </summary>
public static class ParallelSlots
{
    /// <summary>Computes one slot per index in parallel. Each call of <paramref name="compute"/>
    /// writes only its own slot. A worker exception surfaces as the first inner exception, never
    /// as an <see cref="AggregateException"/>.</summary>
    /// <param name="maxDegreeOfParallelism">The thread limit; null for no limit.</param>
    public static T[] Compute<T>(int count, int? maxDegreeOfParallelism, Func<int, T> compute,
        CancellationToken cancellationToken = default)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var slots = new T[count];
        try
        {
            Parallel.For(0, count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1,
                    CancellationToken = cancellationToken
                },
                i => slots[i] = compute(i));
        }
        catch (AggregateException e)
        {
            throw e.InnerExceptions[0];
        }
        return slots;
    }
}
