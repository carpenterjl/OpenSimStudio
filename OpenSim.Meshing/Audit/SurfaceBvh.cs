using OpenSim.Core.Numerics;

namespace OpenSim.Meshing.Audit;

/// <summary>
/// A bounding-box tree over a triangle surface for the mesh audit's three questions:
/// where does a ray first strike the surface, what is the closest surface point to a
/// query point, and which triangles lie within a radius of a point or of a ray.
/// <para>
/// Separate from <see cref="TriangleAabbTree"/> on purpose. That tree's contract is that
/// it only ever prunes an exact crossing COUNT, bit for bit; these queries are nearest /
/// first-hit searches with ordering and tolerances of their own, and folding them into it
/// would put that contract at risk for no gain.
/// </para>
/// Queries are not thread-safe (one traversal stack per instance).
/// </summary>
internal sealed class SurfaceBvh
{
    private const int LeafSize = 4;

    private readonly Vector3D[] _a, _b, _c;
    private readonly double[] _minX, _minY, _minZ, _maxX, _maxY, _maxZ;
    private readonly int[] _left, _right, _start, _count;
    private readonly int[] _order;
    private readonly int[] _stack;
    private readonly double[] _stackKey;
    private int _nodeCount;

    /// <summary>Unit normals as wound; the zero vector for a degenerate triangle.</summary>
    public Vector3D[] Normals { get; }

    public int TriangleCount => _a.Length;

    /// <summary>Diagonal of the box around everything — the scale the tolerances hang on.</summary>
    public double Diagonal { get; }

    public readonly record struct RayHit(double T, int Triangle);

    public SurfaceBvh(IReadOnlyList<Vector3D> vertices, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        int n = triangles.Count;
        if (n == 0) throw new ArgumentException("A surface needs at least one triangle.", nameof(triangles));

        _a = new Vector3D[n]; _b = new Vector3D[n]; _c = new Vector3D[n];
        Normals = new Vector3D[n];
        var cx = new double[n]; var cy = new double[n]; var cz = new double[n];
        var tMin = new Vector3D[n]; var tMax = new Vector3D[n];
        var lo = new Vector3D(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var hi = new Vector3D(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        for (int i = 0; i < n; i++)
        {
            var (ia, ib, ic) = triangles[i];
            Vector3D a = vertices[ia], b = vertices[ib], c = vertices[ic];
            _a[i] = a; _b[i] = b; _c[i] = c;
            var cross = Vector3D.Cross(b - a, c - a);
            double length = cross.Length;
            Normals[i] = length > 0 ? cross / length : Vector3D.Zero;
            tMin[i] = new Vector3D(Math.Min(a.X, Math.Min(b.X, c.X)), Math.Min(a.Y, Math.Min(b.Y, c.Y)),
                Math.Min(a.Z, Math.Min(b.Z, c.Z)));
            tMax[i] = new Vector3D(Math.Max(a.X, Math.Max(b.X, c.X)), Math.Max(a.Y, Math.Max(b.Y, c.Y)),
                Math.Max(a.Z, Math.Max(b.Z, c.Z)));
            cx[i] = (a.X + b.X + c.X) / 3; cy[i] = (a.Y + b.Y + c.Y) / 3; cz[i] = (a.Z + b.Z + c.Z) / 3;
            lo = new Vector3D(Math.Min(lo.X, tMin[i].X), Math.Min(lo.Y, tMin[i].Y), Math.Min(lo.Z, tMin[i].Z));
            hi = new Vector3D(Math.Max(hi.X, tMax[i].X), Math.Max(hi.Y, tMax[i].Y), Math.Max(hi.Z, tMax[i].Z));
        }
        Diagonal = (hi - lo).Length;
        double pad = 1e-12 * Diagonal;

        int capacity = Math.Max(1, 2 * n);
        _minX = new double[capacity]; _minY = new double[capacity]; _minZ = new double[capacity];
        _maxX = new double[capacity]; _maxY = new double[capacity]; _maxZ = new double[capacity];
        _left = new int[capacity]; _right = new int[capacity];
        _start = new int[capacity]; _count = new int[capacity];
        _order = new int[n];
        for (int i = 0; i < n; i++) _order[i] = i;

        var pending = new Stack<(int Node, int Start, int Count)>();
        _nodeCount = 1;
        pending.Push((0, 0, n));
        while (pending.Count > 0)
        {
            var (node, start, count) = pending.Pop();
            double bx0 = double.PositiveInfinity, by0 = double.PositiveInfinity, bz0 = double.PositiveInfinity;
            double bx1 = double.NegativeInfinity, by1 = double.NegativeInfinity, bz1 = double.NegativeInfinity;
            double mx0 = double.PositiveInfinity, my0 = double.PositiveInfinity, mz0 = double.PositiveInfinity;
            double mx1 = double.NegativeInfinity, my1 = double.NegativeInfinity, mz1 = double.NegativeInfinity;
            for (int i = start; i < start + count; i++)
            {
                int t = _order[i];
                bx0 = Math.Min(bx0, tMin[t].X); by0 = Math.Min(by0, tMin[t].Y); bz0 = Math.Min(bz0, tMin[t].Z);
                bx1 = Math.Max(bx1, tMax[t].X); by1 = Math.Max(by1, tMax[t].Y); bz1 = Math.Max(bz1, tMax[t].Z);
                mx0 = Math.Min(mx0, cx[t]); my0 = Math.Min(my0, cy[t]); mz0 = Math.Min(mz0, cz[t]);
                mx1 = Math.Max(mx1, cx[t]); my1 = Math.Max(my1, cy[t]); mz1 = Math.Max(mz1, cz[t]);
            }
            _minX[node] = bx0 - pad; _minY[node] = by0 - pad; _minZ[node] = bz0 - pad;
            _maxX[node] = bx1 + pad; _maxY[node] = by1 + pad; _maxZ[node] = bz1 + pad;

            if (count <= LeafSize)
            {
                _left[node] = -1; _right[node] = -1; _start[node] = start; _count[node] = count;
                continue;
            }

            // Median split on the longest axis of the centroid bounds; (key, index) is a
            // total order, so the tree is the same on every run.
            double ex = mx1 - mx0, ey = my1 - my0, ez = mz1 - mz0;
            double[] key = ex >= ey && ex >= ez ? cx : ey >= ez ? cy : cz;
            Array.Sort(_order, start, count, Comparer<int>.Create(
                (p, q) => key[p] != key[q] ? key[p].CompareTo(key[q]) : p.CompareTo(q)));
            int mid = count / 2;
            int left = _nodeCount++, right = _nodeCount++;
            _left[node] = left; _right[node] = right; _start[node] = 0; _count[node] = 0;
            pending.Push((left, start, mid));
            pending.Push((right, start + mid, count - mid));
        }

        int depth = 2 * (int)Math.Ceiling(Math.Log2(Math.Max(2, n))) + 8;
        _stack = new int[depth];
        _stackKey = new double[depth];
    }

    public (Vector3D A, Vector3D B, Vector3D C) Corners(int triangle) =>
        (_a[triangle], _b[triangle], _c[triangle]);

    public Vector3D Centroid(int triangle) => (_a[triangle] + _b[triangle] + _c[triangle]) / 3.0;

    // ------------------------------------------------------------------ rays

    /// <summary>
    /// The nearest intersection of the ray with the surface at parameter t &gt;
    /// <paramref name="tMin"/> (the direction must be a unit vector, so t is a distance).
    /// Triangle edges count as part of the triangle: a ray through a shared edge may report
    /// either neighbour, never neither.
    /// </summary>
    public bool FirstHit(Vector3D origin, Vector3D direction, double tMin, out RayHit hit)
    {
        double best = double.PositiveInfinity;
        int bestTriangle = -1;
        int top = 0;
        _stack[top++] = 0;
        while (top > 0)
        {
            int node = _stack[--top];
            if (!RayBox(origin, direction, node, 0, best)) continue;
            if (_count[node] > 0)
            {
                for (int i = _start[node]; i < _start[node] + _count[node]; i++)
                {
                    int t = _order[i];
                    if (RayTriangle(origin, direction, t, out double distance) && distance > tMin && distance < best)
                    {
                        best = distance;
                        bestTriangle = t;
                    }
                }
            }
            else
            {
                _stack[top++] = _left[node];
                _stack[top++] = _right[node];
            }
        }
        hit = new RayHit(best, bestTriangle);
        return bestTriangle >= 0;
    }

    /// <summary>
    /// Directions for parity counts, with mutually irrational components. A ray along a
    /// direction with small integer ratios - (1, 1, 1), (-1, 2, 3) - from a point with tidy
    /// coordinates runs exactly through the edges and vertices of a structured surface,
    /// where a crossing is counted twice or not at all; these cannot, short of coincidence.
    /// </summary>
    internal static readonly Vector3D[] ParityDirections =
    {
        new Vector3D(Math.PI, Math.E, 0.5772156649015329).Normalized(),
        new Vector3D(-Math.Sqrt(2), 1.618033988749895, Math.Sqrt(7)).Normalized(),
        new Vector3D(Math.Sqrt(5), -Math.PI / Math.E, -Math.Sqrt(3)).Normalized(),
        new Vector3D(-0.5772156649015329, -Math.Sqrt(11), Math.E).Normalized(),
        new Vector3D(-Math.E, Math.Sqrt(13), -1.618033988749895).Normalized()
    };

    /// <summary>
    /// Whether the point lies inside the closed surface, by crossing parity: three rays,
    /// and two more if those three do not agree.
    /// </summary>
    public bool IsInside(Vector3D point)
    {
        int inside = 0;
        for (int i = 0; i < 3; i++)
            if (CrossingParity(point, ParityDirections[i])) inside++;
        if (inside is 0 or 3) return inside == 3;
        for (int i = 3; i < 5; i++)
            if (CrossingParity(point, ParityDirections[i])) inside++;
        return inside >= 3;
    }

    private bool CrossingParity(Vector3D origin, Vector3D direction)
    {
        bool odd = false;
        int top = 0;
        _stack[top++] = 0;
        while (top > 0)
        {
            int node = _stack[--top];
            if (!RayBox(origin, direction, node, 0, double.PositiveInfinity)) continue;
            if (_count[node] > 0)
            {
                for (int i = _start[node]; i < _start[node] + _count[node]; i++)
                    if (RayTriangle(origin, direction, _order[i], out double t, slack: 0) && t > 0) odd = !odd;
            }
            else
            {
                _stack[top++] = _left[node];
                _stack[top++] = _right[node];
            }
        }
        return odd;
    }

    /// <summary>Appends every intersection of the ray with the surface at t &gt; 0, unordered.
    /// For parity counts — callers vote over several directions, because a ray through a
    /// shared edge is reported once per neighbour.</summary>
    public void AllHits(Vector3D origin, Vector3D direction, List<RayHit> hits)
    {
        double tMin = 1e-12 * Diagonal;
        int top = 0;
        _stack[top++] = 0;
        while (top > 0)
        {
            int node = _stack[--top];
            if (!RayBox(origin, direction, node, 0, double.PositiveInfinity)) continue;
            if (_count[node] > 0)
            {
                for (int i = _start[node]; i < _start[node] + _count[node]; i++)
                {
                    int t = _order[i];
                    if (RayTriangle(origin, direction, t, out double distance) && distance > tMin)
                        hits.Add(new RayHit(distance, t));
                }
            }
            else
            {
                _stack[top++] = _left[node];
                _stack[top++] = _right[node];
            }
        }
    }

    /// <summary>
    /// Appends every triangle some point of which lies within <paramref name="radius"/> of
    /// the half-line from <paramref name="origin"/> (unit direction), with that distance.
    /// </summary>
    public void NearRay(Vector3D origin, Vector3D direction, double radius, List<(int Triangle, double Distance)> results)
    {
        double reach = 4 * Diagonal + (origin - _a[0]).Length;
        var end = origin + direction * reach;
        int top = 0;
        _stack[top++] = 0;
        while (top > 0)
        {
            int node = _stack[--top];
            if (!RayBox(origin, direction, node, radius, double.PositiveInfinity)) continue;
            if (_count[node] > 0)
            {
                for (int i = _start[node]; i < _start[node] + _count[node]; i++)
                {
                    int t = _order[i];
                    double d = RayTriangle(origin, direction, t, out _)
                        ? 0
                        : SegmentTriangleBoundaryDistance(origin, end, t);
                    if (d <= radius) results.Add((t, d));
                }
            }
            else
            {
                _stack[top++] = _left[node];
                _stack[top++] = _right[node];
            }
        }
    }

    /// <summary>Slab test against a node's box grown by <paramref name="grow"/>, for
    /// 0 ≤ t &lt; <paramref name="tMax"/>.</summary>
    private bool RayBox(Vector3D o, Vector3D d, int node, double grow, double tMax)
    {
        double t0 = 0, t1 = tMax;
        if (!Slab(o.X, d.X, _minX[node] - grow, _maxX[node] + grow, ref t0, ref t1)) return false;
        if (!Slab(o.Y, d.Y, _minY[node] - grow, _maxY[node] + grow, ref t0, ref t1)) return false;
        return Slab(o.Z, d.Z, _minZ[node] - grow, _maxZ[node] + grow, ref t0, ref t1);
    }

    private static bool Slab(double o, double d, double min, double max, ref double t0, ref double t1)
    {
        if (d == 0.0) return o >= min && o <= max;
        double inv = 1.0 / d;
        double a = (min - o) * inv, b = (max - o) * inv;
        if (a > b) (a, b) = (b, a);
        if (a > t0) t0 = a;
        if (b < t1) t1 = b;
        return t0 <= t1;
    }

    /// <summary>Möller–Trumbore with inclusive edges (a hair of slack on the barycentric
    /// bounds): the audit asks for a nearest hit, never a crossing count, so reporting a
    /// shared edge twice is harmless and missing it is not.</summary>
    private bool RayTriangle(Vector3D origin, Vector3D direction, int triangle, out double t,
        double slack = 1e-9)
    {
        t = 0;
        var a = _a[triangle];
        var e1 = _b[triangle] - a;
        var e2 = _c[triangle] - a;
        var p = Vector3D.Cross(direction, e2);
        double det = Vector3D.Dot(e1, p);
        // Parallel when the ray lies within ~1e-12 rad of the triangle's plane.
        if (Math.Abs(det) <= 1e-12 * e1.Length * e2.Length) return false;
        double inv = 1.0 / det;
        var s = origin - a;
        double u = Vector3D.Dot(s, p) * inv;
        if (u < -slack || u > 1 + slack) return false;
        var q = Vector3D.Cross(s, e1);
        double v = Vector3D.Dot(direction, q) * inv;
        if (v < -slack || u + v > 1 + slack) return false;
        t = Vector3D.Dot(e2, q) * inv;
        return t >= 0;
    }

    // ------------------------------------------------------------ closest point

    /// <summary>
    /// The closest point of the surface to <paramref name="p"/> among the triangles
    /// <paramref name="accept"/> admits (all of them when null), no further than
    /// <paramref name="maxDistance"/>.
    /// </summary>
    public bool Closest(Vector3D p, double maxDistance, Func<int, bool>? accept,
        out int triangle, out double distance, out Vector3D point)
    {
        double best = maxDistance;
        double bestSquared = double.IsPositiveInfinity(best) ? double.PositiveInfinity : best * best;
        triangle = -1;
        point = default;

        int top = 0;
        _stack[top] = 0;
        _stackKey[top++] = BoxDistanceSquared(p, 0);
        while (top > 0)
        {
            int node = _stack[--top];
            if (_stackKey[top] > bestSquared) continue;
            if (_count[node] > 0)
            {
                for (int i = _start[node]; i < _start[node] + _count[node]; i++)
                {
                    int t = _order[i];
                    if (accept is not null && !accept(t)) continue;
                    var q = ClosestPointOnTriangle(p, _a[t], _b[t], _c[t]);
                    double d2 = Vector3D.DistanceSquared(p, q);
                    if (d2 < bestSquared || (d2 == bestSquared && triangle < 0))
                    {
                        bestSquared = d2;
                        triangle = t;
                        point = q;
                    }
                }
            }
            else
            {
                // Nearer child last, so it is popped first.
                double dl = BoxDistanceSquared(p, _left[node]), dr = BoxDistanceSquared(p, _right[node]);
                if (dl <= dr)
                {
                    _stack[top] = _right[node]; _stackKey[top++] = dr;
                    _stack[top] = _left[node]; _stackKey[top++] = dl;
                }
                else
                {
                    _stack[top] = _left[node]; _stackKey[top++] = dl;
                    _stack[top] = _right[node]; _stackKey[top++] = dr;
                }
            }
        }
        distance = triangle >= 0 ? Math.Sqrt(bestSquared) : double.PositiveInfinity;
        return triangle >= 0;
    }

    /// <summary>Distance from <paramref name="p"/> to the surface (∞ beyond
    /// <paramref name="maxDistance"/>).</summary>
    public double Distance(Vector3D p, double maxDistance = double.PositiveInfinity) =>
        Closest(p, maxDistance, null, out _, out double d, out _) ? d : double.PositiveInfinity;

    /// <summary>Appends every triangle within <paramref name="radius"/> of
    /// <paramref name="p"/>, with its distance.</summary>
    public void Within(Vector3D p, double radius, List<(int Triangle, double Distance)> results)
    {
        double r2 = radius * radius;
        int top = 0;
        _stack[top++] = 0;
        while (top > 0)
        {
            int node = _stack[--top];
            if (BoxDistanceSquared(p, node) > r2) continue;
            if (_count[node] > 0)
            {
                for (int i = _start[node]; i < _start[node] + _count[node]; i++)
                {
                    int t = _order[i];
                    double d2 = Vector3D.DistanceSquared(p, ClosestPointOnTriangle(p, _a[t], _b[t], _c[t]));
                    if (d2 <= r2) results.Add((t, Math.Sqrt(d2)));
                }
            }
            else
            {
                _stack[top++] = _left[node];
                _stack[top++] = _right[node];
            }
        }
    }

    private double BoxDistanceSquared(Vector3D p, int node)
    {
        double dx = p.X < _minX[node] ? _minX[node] - p.X : p.X > _maxX[node] ? p.X - _maxX[node] : 0;
        double dy = p.Y < _minY[node] ? _minY[node] - p.Y : p.Y > _maxY[node] ? p.Y - _maxY[node] : 0;
        double dz = p.Z < _minZ[node] ? _minZ[node] - p.Z : p.Z > _maxZ[node] ? p.Z - _maxZ[node] : 0;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Ericson, "Real-Time Collision Detection" §5.1.5.</summary>
    internal static Vector3D ClosestPointOnTriangle(Vector3D p, Vector3D a, Vector3D b, Vector3D c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        double d1 = Vector3D.Dot(ab, ap), d2 = Vector3D.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;

        var bp = p - b;
        double d3 = Vector3D.Dot(ab, bp), d4 = Vector3D.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;

        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));

        var cp = p - c;
        double d5 = Vector3D.Dot(ab, cp), d6 = Vector3D.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;

        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));

        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

        double sum = va + vb + vc;
        // A degenerate (zero-area) triangle that slipped through every edge region.
        if (sum == 0) return a;
        double denom = 1.0 / sum;
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    /// <summary>
    /// Distance from the segment p–q to a triangle it does not pierce: the nearer of the
    /// segment's endpoints to the triangle, and of the segment to the triangle's three edges.
    /// </summary>
    private double SegmentTriangleBoundaryDistance(Vector3D p, Vector3D q, int triangle)
    {
        Vector3D a = _a[triangle], b = _b[triangle], c = _c[triangle];
        double best = Vector3D.Distance(p, ClosestPointOnTriangle(p, a, b, c));
        best = Math.Min(best, Vector3D.Distance(q, ClosestPointOnTriangle(q, a, b, c)));
        best = Math.Min(best, SegmentSegmentDistance(p, q, a, b));
        best = Math.Min(best, SegmentSegmentDistance(p, q, b, c));
        return Math.Min(best, SegmentSegmentDistance(p, q, c, a));
    }

    /// <summary>Ericson §5.1.9, closest points of two segments.</summary>
    internal static double SegmentSegmentDistance(Vector3D p1, Vector3D q1, Vector3D p2, Vector3D q2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        double a = Vector3D.Dot(d1, d1), e = Vector3D.Dot(d2, d2), f = Vector3D.Dot(d2, r);
        double s, t;
        if (a <= 0 && e <= 0) return r.Length;
        if (a <= 0)
        {
            s = 0;
            t = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            double c = Vector3D.Dot(d1, r);
            if (e <= 0)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                double b = Vector3D.Dot(d1, d2);
                double denom = a * e - b * b;
                s = denom > 0 ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Math.Clamp((b - c) / a, 0, 1);
                }
            }
        }
        return Vector3D.Distance(p1 + d1 * s, p2 + d2 * t);
    }
}
