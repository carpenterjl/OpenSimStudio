namespace OpenSim.Rf.Surface;

/// <summary>
/// The deterministic parallel-fill recipe for the O(N²) triangle-pair assembly — the
/// PCB import pipeline's pattern applied to the MoM: enumerate the canonical p ≤ q
/// pair list up front, compute each pair's kernel moments into its own pre-sized slot
/// on the thread pool (no shared mutation anywhere), then scatter sequentially in
/// canonical pair order. The scatter order — and therefore every floating-point
/// accumulation into Z — is identical to the historical serial double loop, so the
/// assembled matrix is bitwise identical at ANY degree of parallelism, including 1.
/// </summary>
internal static class PairMomentSchedule
{
    /// <summary>Canonical p ≤ q pairs over triangles that carry at least one RWG basis,
    /// in the exact order the serial double loop visited them.</summary>
    public static (int P, int Q)[] Build(SurfaceStructure surface)
    {
        int n = surface.Triangles.Count;
        var pairs = new List<(int, int)>();
        for (int p = 0; p < n; p++)
        {
            if (surface.TriangleSupports[p].Count == 0) continue;
            for (int q = p; q < n; q++)
                if (surface.TriangleSupports[q].Count != 0)
                    pairs.Add((p, q));
        }
        return pairs.ToArray();
    }

    /// <summary>Computes one moment slot per pair in parallel. Each iteration writes
    /// ONLY its own slot; a worker exception surfaces as the first inner exception
    /// (the import pipeline's convention), never a swallowed AggregateException.</summary>
    public static TMoments[] Compute<TMoments>((int P, int Q)[] pairs,
        int? maxDegreeOfParallelism, Func<int, int, TMoments> moments)
    {
        var slots = new TMoments[pairs.Length];
        try
        {
            Parallel.For(0, pairs.Length,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 },
                i => slots[i] = moments(pairs[i].P, pairs[i].Q));
        }
        catch (AggregateException e)
        {
            throw e.InnerExceptions[0];
        }
        return slots;
    }
}
