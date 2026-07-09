namespace OpenSim.Core.Numerics;

/// <summary>
/// Static axis-aligned bounding-box tree over a triangle soup, used to prune
/// ray/triangle candidate sets (point-in-solid crossing counts).
///
/// Contract: this structure is PURE PRUNING — a ray query yields a superset filter,
/// and every triangle whose box the ray touches is reported exactly once (ranges
/// partition the triangle set), so counting intersections over the reported
/// candidates gives byte-identical results to a brute-force scan. It must never
/// change a crossing count. Construction is deterministic: median split on the
/// longest centroid axis with (centroid, original index) as a total order.
/// </summary>
public sealed class TriangleAabbTree
{
    private const int LeafSize = 8;

    private readonly struct Node
    {
        public readonly double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public readonly int Left;    // internal: child node indices; leaf: -1
        public readonly int Right;
        public readonly int Start;   // leaf: range into _order
        public readonly int Count;   // leaf: > 0; internal: 0

        public Node(double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
                    int left, int right, int start, int count)
        {
            MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
            Left = left; Right = right; Start = start; Count = count;
        }
    }

    private readonly Node[] _nodes;
    private readonly int[] _order;          // permutation of triangle indices; leaves own disjoint ranges
    private readonly int[] _stack;          // reusable traversal stack — queries are single-threaded

    /// <summary>Total ray/box candidate triangles visited across all queries — test instrumentation.</summary>
    public long CandidatesVisited { get; private set; }

    public TriangleAabbTree(IReadOnlyList<Vector3D> vertices, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        if (triangles.Count == 0)
            throw new ArgumentException("Cannot build a triangle tree over an empty triangle list.", nameof(triangles));

        int n = triangles.Count;
        var minX = new double[n]; var minY = new double[n]; var minZ = new double[n];
        var maxX = new double[n]; var maxY = new double[n]; var maxZ = new double[n];
        var cx = new double[n]; var cy = new double[n]; var cz = new double[n];
        for (int i = 0; i < n; i++)
        {
            var (ia, ib, ic) = triangles[i];
            Vector3D a = vertices[ia], b = vertices[ib], c = vertices[ic];
            minX[i] = Math.Min(a.X, Math.Min(b.X, c.X)); maxX[i] = Math.Max(a.X, Math.Max(b.X, c.X));
            minY[i] = Math.Min(a.Y, Math.Min(b.Y, c.Y)); maxY[i] = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            minZ[i] = Math.Min(a.Z, Math.Min(b.Z, c.Z)); maxZ[i] = Math.Max(a.Z, Math.Max(b.Z, c.Z));
            cx[i] = (minX[i] + maxX[i]) * 0.5;
            cy[i] = (minY[i] + maxY[i]) * 0.5;
            cz[i] = (minZ[i] + maxZ[i]) * 0.5;
        }

        // Pad every node box by a hair relative to the global scale: a grazing ray that
        // misses a box by an ulp would silently drop a crossing. Padding only ever adds
        // candidate visits — it cannot change which triangles pass the exact ray test.
        double dx = maxX.Max() - minX.Min(), dy = maxY.Max() - minY.Min(), dz = maxZ.Max() - minZ.Min();
        double pad = 1e-12 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (pad == 0) pad = 1e-300; // degenerate single-point mesh: keep boxes non-inverted

        _order = new int[n];
        for (int i = 0; i < n; i++) _order[i] = i;

        var nodes = new List<Node>(Math.Max(4, 2 * n / LeafSize));
        // Build iteratively; each pending entry reserves its node slot up front so
        // parents can record child indices before the children are materialized.
        var pending = new Stack<(int NodeIndex, int Start, int Count)>();
        nodes.Add(default);
        pending.Push((0, 0, n));

        while (pending.Count > 0)
        {
            var (nodeIndex, start, count) = pending.Pop();

            double bMinX = double.PositiveInfinity, bMinY = double.PositiveInfinity, bMinZ = double.PositiveInfinity;
            double bMaxX = double.NegativeInfinity, bMaxY = double.NegativeInfinity, bMaxZ = double.NegativeInfinity;
            double cMinX = double.PositiveInfinity, cMinY = double.PositiveInfinity, cMinZ = double.PositiveInfinity;
            double cMaxX = double.NegativeInfinity, cMaxY = double.NegativeInfinity, cMaxZ = double.NegativeInfinity;
            for (int i = start; i < start + count; i++)
            {
                int t = _order[i];
                bMinX = Math.Min(bMinX, minX[t]); bMaxX = Math.Max(bMaxX, maxX[t]);
                bMinY = Math.Min(bMinY, minY[t]); bMaxY = Math.Max(bMaxY, maxY[t]);
                bMinZ = Math.Min(bMinZ, minZ[t]); bMaxZ = Math.Max(bMaxZ, maxZ[t]);
                cMinX = Math.Min(cMinX, cx[t]); cMaxX = Math.Max(cMaxX, cx[t]);
                cMinY = Math.Min(cMinY, cy[t]); cMaxY = Math.Max(cMaxY, cy[t]);
                cMinZ = Math.Min(cMinZ, cz[t]); cMaxZ = Math.Max(cMaxZ, cz[t]);
            }
            bMinX -= pad; bMinY -= pad; bMinZ -= pad;
            bMaxX += pad; bMaxY += pad; bMaxZ += pad;

            if (count <= LeafSize)
            {
                nodes[nodeIndex] = new Node(bMinX, bMinY, bMinZ, bMaxX, bMaxY, bMaxZ, -1, -1, start, count);
                continue;
            }

            // Median split on the longest axis of the CENTROID bounds (box bounds can be
            // dominated by one sliver); (centroid, original index) keys make ties — and
            // therefore the whole tree — deterministic across runs.
            double ex = cMaxX - cMinX, ey = cMaxY - cMinY, ez = cMaxZ - cMinZ;
            double[] key = ex >= ey && ex >= ez ? cx : ey >= ez ? cy : cz;
            Array.Sort(_order, start, count, Comparer<int>.Create(
                (p, q) => key[p] != key[q] ? key[p].CompareTo(key[q]) : p.CompareTo(q)));
            int mid = count / 2;

            int left = nodes.Count;
            nodes.Add(default);
            int right = nodes.Count;
            nodes.Add(default);
            nodes[nodeIndex] = new Node(bMinX, bMinY, bMinZ, bMaxX, bMaxY, bMaxZ, left, right, 0, 0);
            pending.Push((left, start, mid));
            pending.Push((right, start + mid, count - mid));
        }

        _nodes = nodes.ToArray();
        // Median split halves ranges exactly, so depth ≤ log2(n) + 1; margin for safety.
        _stack = new int[2 * (int)Math.Ceiling(Math.Log2(Math.Max(2, n))) + 8];
    }

    /// <summary>
    /// Appends to <paramref name="candidates"/> the indices of every triangle whose
    /// (padded) box the ray from <paramref name="origin"/> along <paramref name="direction"/>
    /// touches for t ≥ 0. Each triangle is reported at most once. The caller owns and
    /// clears the buffer; reusing one list keeps queries allocation-free. Not thread-safe
    /// (shared traversal stack) — the meshing pipeline queries sequentially.
    /// </summary>
    public void CollectRayCandidates(Vector3D origin, Vector3D direction, List<int> candidates)
    {
        var stack = _stack;
        int top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref readonly var node = ref _nodes[stack[--top]];
            if (!RayTouchesBox(origin, direction, in node))
                continue;

            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                    candidates.Add(_order[i]);
                CandidatesVisited += node.Count;
            }
            else
            {
                if (top + 2 > stack.Length)
                    throw new InvalidOperationException(
                        $"Triangle tree traversal exceeded its depth bound ({stack.Length}) — the tree is malformed.");
                stack[top++] = node.Left;
                stack[top++] = node.Right;
            }
        }
    }

    /// <summary>
    /// Slab test for t ∈ [0, ∞), conservative and NaN-free: a zero direction component
    /// is handled as an explicit interval-membership test instead of producing 0·∞.
    /// Touching a face counts as a hit (inclusive bounds) — pruning must never be
    /// tighter than the exact triangle test it feeds.
    /// </summary>
    private static bool RayTouchesBox(Vector3D o, Vector3D d, in Node n)
    {
        double tMin = 0.0, tMax = double.PositiveInfinity;

        if (d.X == 0.0) { if (o.X < n.MinX || o.X > n.MaxX) return false; }
        else
        {
            double inv = 1.0 / d.X;
            double t0 = (n.MinX - o.X) * inv, t1 = (n.MaxX - o.X) * inv;
            if (t0 > t1) (t0, t1) = (t1, t0);
            if (t0 > tMin) tMin = t0;
            if (t1 < tMax) tMax = t1;
            if (tMin > tMax) return false;
        }

        if (d.Y == 0.0) { if (o.Y < n.MinY || o.Y > n.MaxY) return false; }
        else
        {
            double inv = 1.0 / d.Y;
            double t0 = (n.MinY - o.Y) * inv, t1 = (n.MaxY - o.Y) * inv;
            if (t0 > t1) (t0, t1) = (t1, t0);
            if (t0 > tMin) tMin = t0;
            if (t1 < tMax) tMax = t1;
            if (tMin > tMax) return false;
        }

        if (d.Z == 0.0) { if (o.Z < n.MinZ || o.Z > n.MaxZ) return false; }
        else
        {
            double inv = 1.0 / d.Z;
            double t0 = (n.MinZ - o.Z) * inv, t1 = (n.MaxZ - o.Z) * inv;
            if (t0 > t1) (t0, t1) = (t1, t0);
            if (t0 > tMin) tMin = t0;
            if (t1 < tMax) tMax = t1;
            if (tMin > tMax) return false;
        }

        return true;
    }
}
