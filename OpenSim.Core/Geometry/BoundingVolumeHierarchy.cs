using OpenSim.Core.Numerics;

namespace OpenSim.Core.Geometry;

/// <summary>
/// What a <see cref="BoundingVolumeHierarchy"/> traversal calls for each primitive whose box
/// the ray enters before the current far limit. The visitor tests the primitive itself and may
/// lower <paramref name="tMax"/>; boxes that start beyond the lowered limit are then skipped.
/// A struct visitor is called without boxing or a delegate.
/// </summary>
public interface IBvhRayVisitor
{
    /// <summary>Tests primitive <paramref name="primitive"/> (its index in the list the tree was
    /// built from). Lower <paramref name="tMax"/> to prune what lies farther away.</summary>
    void Visit(int primitive, ref double tMax);
}

/// <summary>
/// A bounding-volume hierarchy in double precision over axis-aligned boxes, one per primitive,
/// for first-hit ray queries. The primitives themselves (triangles, analytic optical objects)
/// are the caller's: the tree only decides which of them a ray can reach, nearest boxes first,
/// and hands each to an <see cref="IBvhRayVisitor"/>.
/// <para>
/// <b>Construction</b> is the "equal counts" split of Pharr, Jakob and Humphreys, Physically
/// Based Rendering, 4th edition (pbr-book.org), section 7.3.1 (BVH Construction): the
/// primitives of a node are split at the median of their box centroids along the axis where the
/// centroids spread most. Ties are broken by primitive index, so the tree is the same on every
/// run and every thread. The starting point was OpenSim.Meshing's internal triangle tree
/// (<c>OpenSim.Meshing/Audit/SurfaceBvh.cs</c>, constructor and <c>FirstHit</c>), copied and
/// generalised to boxes and visitors; that file is unchanged.
/// </para>
/// <para>
/// <b>Ray–box test.</b> The slab test of PBR section 6.1.2 with the conservative widening of
/// section 6.8.2 (Conservative Ray–Bounds Intersections): each slab's far parameter is multiplied
/// by 1 + 2γ₃, with γₙ = nε/(1 − nε) and ε half the machine epsilon (section 6.8.1), so a box the
/// exact ray meets is never missed because of rounding. A false positive costs only a visit.
/// </para>
/// <para>
/// <b>Traversal</b> is front to back: of a node's two children the one nearer along the ray's
/// split axis is visited first (PBR section 7.3.5), so a visitor that lowers its far limit
/// prunes early. The tree is immutable and every query keeps its own stack, so queries are safe
/// from any number of threads.
/// </para>
/// </summary>
public sealed class BoundingVolumeHierarchy
{
    /// <summary>Primitives per leaf at most.</summary>
    public const int LeafSize = 2;

    private readonly double[] _minX, _minY, _minZ, _maxX, _maxY, _maxZ;
    private readonly int[] _left, _right, _start, _count, _axis;
    private readonly int[] _order;
    private readonly int _depth;

    /// <summary>The number of primitives.</summary>
    public int Count => _order.Length;

    /// <summary>The box around every primitive.</summary>
    public Aabb Bounds { get; }

    /// <summary>The tree over <paramref name="boxes"/>, one per primitive. A box with a NaN
    /// corner or a minimum above its maximum is refused, naming the primitive.</summary>
    public BoundingVolumeHierarchy(IReadOnlyList<Aabb> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        int n = boxes.Count;
        if (n == 0) throw new ArgumentException("A bounding-volume hierarchy needs at least one primitive.", nameof(boxes));
        var cx = new double[n];
        var cy = new double[n];
        var cz = new double[n];
        for (int i = 0; i < n; i++)
        {
            var b = boxes[i];
            if (!(b.Min.X <= b.Max.X && b.Min.Y <= b.Max.Y && b.Min.Z <= b.Max.Z))
                throw new ArgumentException(
                    $"Primitive {i}: its box {b.Min} to {b.Max} is empty or not a number; every primitive needs a finite box with minimum ≤ maximum.", nameof(boxes));
            var c = b.Center;
            cx[i] = c.X; cy[i] = c.Y; cz[i] = c.Z;
        }

        int capacity = 2 * n;
        _minX = new double[capacity]; _minY = new double[capacity]; _minZ = new double[capacity];
        _maxX = new double[capacity]; _maxY = new double[capacity]; _maxZ = new double[capacity];
        _left = new int[capacity]; _right = new int[capacity];
        _start = new int[capacity]; _count = new int[capacity]; _axis = new int[capacity];
        _order = new int[n];
        for (int i = 0; i < n; i++) _order[i] = i;

        int nodeCount = 1, depth = 1;
        var pending = new Stack<(int Node, int Start, int Count, int Depth)>();
        pending.Push((0, 0, n, 1));
        while (pending.Count > 0)
        {
            var (node, start, count, level) = pending.Pop();
            depth = Math.Max(depth, level);
            double bx0 = double.PositiveInfinity, by0 = double.PositiveInfinity, bz0 = double.PositiveInfinity;
            double bx1 = double.NegativeInfinity, by1 = double.NegativeInfinity, bz1 = double.NegativeInfinity;
            double mx0 = double.PositiveInfinity, my0 = double.PositiveInfinity, mz0 = double.PositiveInfinity;
            double mx1 = double.NegativeInfinity, my1 = double.NegativeInfinity, mz1 = double.NegativeInfinity;
            for (int i = start; i < start + count; i++)
            {
                int p = _order[i];
                var b = boxes[p];
                bx0 = Math.Min(bx0, b.Min.X); by0 = Math.Min(by0, b.Min.Y); bz0 = Math.Min(bz0, b.Min.Z);
                bx1 = Math.Max(bx1, b.Max.X); by1 = Math.Max(by1, b.Max.Y); bz1 = Math.Max(bz1, b.Max.Z);
                mx0 = Math.Min(mx0, cx[p]); my0 = Math.Min(my0, cy[p]); mz0 = Math.Min(mz0, cz[p]);
                mx1 = Math.Max(mx1, cx[p]); my1 = Math.Max(my1, cy[p]); mz1 = Math.Max(mz1, cz[p]);
            }
            _minX[node] = bx0; _minY[node] = by0; _minZ[node] = bz0;
            _maxX[node] = bx1; _maxY[node] = by1; _maxZ[node] = bz1;

            if (count <= LeafSize)
            {
                _left[node] = -1; _right[node] = -1; _start[node] = start; _count[node] = count;
                continue;
            }

            double ex = mx1 - mx0, ey = my1 - my0, ez = mz1 - mz0;
            int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
            double[] key = axis == 0 ? cx : axis == 1 ? cy : cz;
            Array.Sort(_order, start, count, Comparer<int>.Create(
                (p, q) => key[p] != key[q] ? key[p].CompareTo(key[q]) : p.CompareTo(q)));
            int mid = count / 2;
            int left = nodeCount++, right = nodeCount++;
            _left[node] = left; _right[node] = right; _start[node] = 0; _count[node] = 0; _axis[node] = axis;
            pending.Push((left, start, mid, level + 1));
            pending.Push((right, start + mid, count - mid, level + 1));
        }
        _depth = depth;
        Bounds = new Aabb(new Vector3D(_minX[0], _minY[0], _minZ[0]), new Vector3D(_maxX[0], _maxY[0], _maxZ[0]));
    }

    /// <summary>γₙ = nε/(1 − nε), ε = 2⁻⁵³ (PBR 4th ed., section 6.8.1, with the unit round-off
    /// of double precision).</summary>
    public static double Gamma(int n)
    {
        return n * UnitRoundoff / (1 - n * UnitRoundoff);
    }

    /// <summary>ε = 2⁻⁵³, half the gap between 1 and the next double.</summary>
    private static readonly double UnitRoundoff = (Math.BitIncrement(1.0) - 1.0) / 2;

    private static readonly double FarWidening = 1 + 2 * Gamma(3);

    /// <summary>
    /// The parameter interval [tEnter, tExit] over which the ray o + t·d (t in [tMin, tMax])
    /// lies in <paramref name="box"/>, by the slab test with the far parameter widened by
    /// 1 + 2γ₃ (PBR 4th ed., sections 6.1.2 and 6.8.2). False when the ray misses the box. A
    /// zero direction component keeps the slab only if the origin lies within it.
    /// </summary>
    public static bool RayBox(Vector3D origin, Vector3D direction, Aabb box, double tMin, double tMax,
        out double tEnter, out double tExit)
    {
        tEnter = tMin; tExit = tMax;
        return Slab(origin.X, direction.X, box.Min.X, box.Max.X, ref tEnter, ref tExit)
            && Slab(origin.Y, direction.Y, box.Min.Y, box.Max.Y, ref tEnter, ref tExit)
            && Slab(origin.Z, direction.Z, box.Min.Z, box.Max.Z, ref tEnter, ref tExit);
    }

    /// <summary>
    /// Visits, nearest boxes first, every primitive whose box the ray
    /// <paramref name="origin"/> + t·<paramref name="direction"/> enters for t in
    /// [<paramref name="tMin"/>, <paramref name="tMax"/>]; the visitor may lower
    /// <paramref name="tMax"/> as it finds hits. Returns the final far limit.
    /// </summary>
    public double Traverse<TVisitor>(Vector3D origin, Vector3D direction, double tMin, double tMax, ref TVisitor visitor)
        where TVisitor : IBvhRayVisitor
    {
        if (!(tMin <= tMax)) return tMax;
        Span<int> stack = _depth + 2 <= 256 ? stackalloc int[_depth + 2] : new int[_depth + 2];
        int top = 0;
        stack[top++] = 0;
        bool negX = direction.X < 0, negY = direction.Y < 0, negZ = direction.Z < 0;
        while (top > 0)
        {
            int node = stack[--top];
            if (!NodeHit(origin, direction, node, tMin, tMax)) continue;
            if (_left[node] < 0)
            {
                for (int i = _start[node]; i < _start[node] + _count[node]; i++)
                    visitor.Visit(_order[i], ref tMax);
                continue;
            }
            bool farFirst = _axis[node] switch { 0 => negX, 1 => negY, _ => negZ };
            // Push the far child first so the near one is popped first.
            if (farFirst) { stack[top++] = _left[node]; stack[top++] = _right[node]; }
            else { stack[top++] = _right[node]; stack[top++] = _left[node]; }
        }
        return tMax;
    }

    private bool NodeHit(Vector3D o, Vector3D d, int node, double tMin, double tMax)
    {
        double t0 = tMin, t1 = tMax;
        return Slab(o.X, d.X, _minX[node], _maxX[node], ref t0, ref t1)
            && Slab(o.Y, d.Y, _minY[node], _maxY[node], ref t0, ref t1)
            && Slab(o.Z, d.Z, _minZ[node], _maxZ[node], ref t0, ref t1);
    }

    private static bool Slab(double o, double d, double min, double max, ref double t0, ref double t1)
    {
        if (d == 0.0) return o >= min && o <= max;
        double inv = 1.0 / d;
        double a = (min - o) * inv, b = (max - o) * inv;
        if (a > b) (a, b) = (b, a);
        b *= FarWidening;   // PBR 6.8.2: tFar *= 1 + 2γ₃
        if (a > t0) t0 = a;
        if (b < t1) t1 = b;
        return t0 <= t1;
    }
}
