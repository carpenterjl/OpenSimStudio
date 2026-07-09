
namespace OpenSim.Core.Geometry2D;

/// <summary>
/// Thrown when constraint recovery cannot converge — a degenerate or self-intersecting
/// input, or a vertex ordering the flip/split scheme can't resolve. A distinct type so
/// callers can retry with a perturbed input (e.g. a different edge length) instead of
/// aborting the whole operation.
/// </summary>
public sealed class ConstraintRecoveryException : InvalidOperationException
{
    public ConstraintRecoveryException(string message) : base(message) { }
}

/// <summary>
/// 2D constrained Delaunay triangulation by incremental Bowyer–Watson with a
/// symbolic infinite ("ghost") vertex — the same scheme the 3D mesher uses; a finite
/// super-shape loses hull triangles and feeds huge coordinates to the predicates.
/// Constraint edges are recovered by diagonal flips after insertion and are never
/// crossed by later insertions (making subsequent refinement safe).
/// </summary>
public sealed class Cdt2D
{
    private const int Inf = -1;

    /// <summary>Constraint-recovery split depth beyond which we declare non-convergence.
    /// <see cref="SplitMidpoint"/> caps legitimate midpoint splitting at 64, so any depth
    /// past this is a runaway (a degeneracy re-pushing the same sub-segment).</summary>
    private const int MaxRecoveryDepth = 128;

    private struct Tri
    {
        public int A, B, C;       // vertices, CCW for finite triangles; Inf marks a ghost
        public int NA, NB, NC;    // neighbor across the edge opposite A / B / C
        public bool Dead;

        public readonly int Vertex(int i) => i == 0 ? A : i == 1 ? B : C;
        public readonly int Neighbor(int i) => i == 0 ? NA : i == 1 ? NB : NC;
        public void SetNeighbor(int i, int t) { if (i == 0) NA = t; else if (i == 1) NB = t; else NC = t; }
    }

    private readonly List<Point2> _points = new();
    private readonly List<Tri> _tris = new();
    private readonly HashSet<(int, int)> _constrained = new();
    private double _jitterAmp;
    private int _lastLive;

    // Directed edge (u,v) → the live triangle owning it, and vertex → an incident
    // triangle hint. Recovery interrogates edges once per constraint sub-segment and
    // once per flip; without these maps every lookup is a full-triangle scan, which
    // goes quadratic on pour-sized nets (minutes instead of seconds).
    private readonly Dictionary<(int, int), int> _edgeTri = new();
    private readonly Dictionary<int, int> _vertexTri = new();

    private void Register(int ti)
    {
        var t = _tris[ti];
        for (int e = 0; e < 3; e++)
        {
            _edgeTri[(t.Vertex((e + 1) % 3), t.Vertex((e + 2) % 3))] = ti;
            int v = t.Vertex(e);
            if (v != Inf) _vertexTri[v] = ti;
        }
    }

    private void Unregister(int ti)
    {
        var t = _tris[ti];
        for (int e = 0; e < 3; e++)
        {
            var key = (t.Vertex((e + 1) % 3), t.Vertex((e + 2) % 3));
            if (_edgeTri.TryGetValue(key, out int owner) && owner == ti)
                _edgeTri.Remove(key);
        }
    }

    public IReadOnlyList<Point2> Points => _points;

    /// <summary>Finite triangles (a,b,c), CCW.</summary>
    public IEnumerable<(int A, int B, int C)> Triangles()
    {
        foreach (var t in _tris)
            if (!t.Dead && t.A != Inf && t.B != Inf && t.C != Inf)
                yield return (t.A, t.B, t.C);
    }

    public bool IsConstrained(int u, int v) => _constrained.Contains(Key(u, v));

    /// <summary>Diagnostic: verifies neighbor symmetry and shared-edge consistency. Empty = valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        for (int i = 0; i < _tris.Count; i++)
        {
            var t = _tris[i];
            if (t.Dead) continue;
            for (int e = 0; e < 3; e++)
            {
                int n = t.Neighbor(e);
                if (n < 0) { problems.Add($"T{i} edge {e} has no neighbor."); continue; }
                if (_tris[n].Dead) { problems.Add($"T{i} edge {e} -> dead T{n}."); continue; }
                int u = t.Vertex((e + 1) % 3), v = t.Vertex((e + 2) % 3);
                bool back = false;
                for (int e2 = 0; e2 < 3; e2++)
                    if (_tris[n].Neighbor(e2) == i
                        && _tris[n].Vertex((e2 + 1) % 3) == v && _tris[n].Vertex((e2 + 2) % 3) == u)
                        back = true;
                if (!back) problems.Add($"T{i} edge {e} ({u},{v}) not mirrored by T{n}.");
            }
        }
        return problems;
    }

    /// <summary>
    /// Builds the triangulation of <paramref name="points"/> and recovers every
    /// <paramref name="constraintEdges"/> pair (indices into the point list).
    /// </summary>
    public void Triangulate(IReadOnlyList<Point2> points, IEnumerable<(int U, int V)> constraintEdges)
    {
        if (points.Count < 3)
            throw new InvalidOperationException("Triangulation needs at least 3 points.");

        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        double diag = Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2));
        if (diag <= 0)
            throw new InvalidOperationException("All points coincide.");

        // Deterministic sub-feature jitter (≈1e-6·diag): axis-aligned PCB outlines put
        // many points on exact lines/circles, feeding degeneracies to the orientation
        // and in-circle predicates. The 3D mesher breaks the same degeneracies the same
        // way; the perturbation is far below copper feature size and FEM accuracy.
        // Jitter must stay well below the smallest inter-point spacing or it can shove
        // near-coincident points past each other and fabricate self-intersections. Cap it
        // at a fraction of the closest pair (bounded below so it still breaks degeneracies).
        _jitterAmp = Math.Min(1e-6 * diag, 0.05 * ClosestPairLowerBound(points));
        for (int i = 0; i < points.Count; i++)
            _points.Add(new Point2(points[i].X + Jitter(i, 1) * _jitterAmp, points[i].Y + Jitter(i, 2) * _jitterAmp));

        var (i0, i1, i2) = Bootstrap();
        for (int i = 0; i < points.Count; i++)
        {
            if (i == i0 || i == i1 || i == i2) continue;
            // A point too close to an existing vertex to seed a cavity is merged onto it
            // (harmless for interior points; constraint edges are remapped through it).
            if (InsertVertex(i) < 0)
                _remap[i] = NearestInserted(_points[i], i);
        }

        foreach (var (u, v) in constraintEdges)
            RecoverConstraint(Resolve(u), Resolve(v));
    }

    private readonly Dictionary<int, int> _remap = new();
    private int Resolve(int i)
    {
        while (_remap.TryGetValue(i, out int r)) i = r;
        return i;
    }

    /// <summary>Nearest already-inserted (index &lt; upper) live vertex, following merges.</summary>
    private int NearestInserted(Point2 p, int upper)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < upper; i++)
        {
            int v = Resolve(i);
            double d = (_points[v] - p).Length;
            if (d < bestD) { bestD = d; best = v; }
        }
        return best;
    }

    /// <summary>
    /// Inserts one extra point (used by refinement); must not lie on a constrained
    /// edge. Returns -1 (and leaves the triangulation unchanged) when the point
    /// coincides with an existing vertex.
    /// </summary>
    public int InsertPoint(Point2 p)
    {
        var jittered = new Point2(p.X + Jitter(_points.Count, 1) * _jitterAmp,
                                  p.Y + Jitter(_points.Count, 2) * _jitterAmp);
        _points.Add(jittered);
        int vi = InsertVertex(_points.Count - 1);
        if (vi < 0)
            _points.RemoveAt(_points.Count - 1);
        return vi;
    }

    /// <summary>
    /// Closest inter-point distance via a uniform spatial hash (expected O(n)). Used only
    /// to bound the jitter amplitude, so an approximate minimum is sufficient.
    /// </summary>
    private static double ClosestPairLowerBound(IReadOnlyList<Point2> points)
    {
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        double span = Math.Max(maxX - minX, maxY - minY);
        if (span <= 0) return 0;
        double cell = span / Math.Max(1, Math.Sqrt(points.Count));
        if (cell <= 0) return span;

        var grid = new Dictionary<(long, long), List<int>>();
        (long, long) Cell(Point2 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));

        double best = span;
        for (int i = 0; i < points.Count; i++)
        {
            var (cx, cy) = Cell(points[i]);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((cx + dx, cy + dy), out var bucket))
                        foreach (int j in bucket)
                        {
                            double d = (points[i] - points[j]).Length;
                            if (d > 0 && d < best) best = d;
                        }
            if (!grid.TryGetValue((cx, cy), out var own)) grid[(cx, cy)] = own = new List<int>();
            own.Add(i);
        }
        return best;
    }

    /// <summary>Deterministic pseudo-random offset in (−0.5, 0.5) from a point index and salt.</summary>
    private static double Jitter(int index, int salt)
    {
        uint x = (uint)(index * 2654435761 + salt * 40503 + 0x9E3779B9);
        x ^= x >> 13; x *= 2246822519u; x ^= x >> 16;
        return (x % 1_000_000) / 1_000_000.0 - 0.5;
    }

    // ---------------- Predicates ----------------
    //
    // Sign decisions use per-evaluation floating-point error bounds (Shewchuk's static
    // filter constants), NOT a global epsilon. A global eps scaled by the domain diagonal
    // is catastrophically loose for features far smaller than the board: a nearly
    // collinear chain of jittered constraint points has orientation/in-circle
    // determinants far below diag-scaled thresholds yet far above the true rounding
    // error, and treating those decisive verdicts as "ambiguous" corrupts the incremental
    // insertion with flat overlapping triangles. The adaptive bound keeps every verdict
    // the arithmetic actually supports.

    /// <summary>(3 + 16ε)ε — Shewchuk's static filter constant for the 2D orientation test.</summary>
    private const double CcwErrBound = 3.3306690738754716e-16;

    /// <summary>(10 + 96ε)ε — Shewchuk's static filter constant for the in-circle test.</summary>
    private const double IccErrBound = 2.220446049250315e-15;

    private double Orient(int a, int b, Point2 p) => OrientVal(_points[a], _points[b], p);

    private static double OrientVal(Point2 pa, Point2 pb, Point2 p) =>
        (pb.X - pa.X) * (p.Y - pa.Y) - (pb.Y - pa.Y) * (p.X - pa.X);

    /// <summary>Rounding-error bound for <see cref="OrientVal"/> on these inputs; a result
    /// within ±bound has no reliable sign.</summary>
    private static double OrientErr(Point2 pa, Point2 pb, Point2 p) =>
        CcwErrBound * (Math.Abs((pb.X - pa.X) * (p.Y - pa.Y)) + Math.Abs((pb.Y - pa.Y) * (p.X - pa.X)));

    private double OrientErr(int a, int b, Point2 p) => OrientErr(_points[a], _points[b], p);

    /// <summary>Positive when p is strictly inside the circumcircle of CCW (a,b,c).</summary>
    private double InCircle(int a, int b, int c, Point2 p)
    {
        var pa = _points[a]; var pb = _points[b]; var pc = _points[c];
        double ax = pa.X - p.X, ay = pa.Y - p.Y;
        double bx = pb.X - p.X, by = pb.Y - p.Y;
        double cx = pc.X - p.X, cy = pc.Y - p.Y;
        return (ax * ax + ay * ay) * (bx * cy - by * cx)
             - (bx * bx + by * by) * (ax * cy - ay * cx)
             + (cx * cx + cy * cy) * (ax * by - ay * bx);
    }

    /// <summary>Rounding-error bound for <see cref="InCircle"/> on these inputs.</summary>
    private double InCircleErr(int a, int b, int c, Point2 p)
    {
        var pa = _points[a]; var pb = _points[b]; var pc = _points[c];
        double ax = pa.X - p.X, ay = pa.Y - p.Y;
        double bx = pb.X - p.X, by = pb.Y - p.Y;
        double cx = pc.X - p.X, cy = pc.Y - p.Y;
        double permanent = (ax * ax + ay * ay) * (Math.Abs(bx * cy) + Math.Abs(by * cx))
                         + (bx * bx + by * by) * (Math.Abs(ax * cy) + Math.Abs(ay * cx))
                         + (cx * cx + cy * cy) * (Math.Abs(ax * by) + Math.Abs(ay * bx));
        return IccErrBound * permanent;
    }

    /// <summary>
    /// The directed hull edge of a ghost triangle: the unique cyclically consecutive
    /// pair of finite vertices. The hull exterior lies to its left.
    /// </summary>
    private static (int U, int V) GhostEdge(in Tri t)
    {
        if (t.A == Inf) return (t.B, t.C);
        if (t.B == Inf) return (t.C, t.A);
        return (t.A, t.B);
    }

    private bool InCavity(int tri, Point2 p)
    {
        var t = _tris[tri];
        if (t.A == Inf || t.B == Inf || t.C == Inf)
        {
            var (u, v) = GhostEdge(t);
            return Orient(u, v, p) > -OrientErr(u, v, p);   // exterior side or on the hull edge
        }
        return InCircle(t.A, t.B, t.C, p) > InCircleErr(t.A, t.B, t.C, p);
    }

    // ---------------- Bootstrap ----------------

    /// <summary>Builds the first finite triangle from a non-degenerate triple of input points.</summary>
    private (int, int, int) Bootstrap()
    {
        int i0 = 0;
        int i1 = -1;
        for (int i = 1; i < _points.Count; i++)
            if ((_points[i] - _points[i0]).Length > 0) { i1 = i; break; }
        if (i1 < 0)
            throw new InvalidOperationException("All points coincide; cannot triangulate.");
        int i2 = -1;
        for (int i = 1; i < _points.Count; i++)
            if (i != i1 && Math.Abs(Orient(i0, i1, _points[i])) > OrientErr(i0, i1, _points[i])) { i2 = i; break; }
        if (i2 < 0)
            throw new InvalidOperationException("All points are collinear; cannot triangulate.");

        int a = i0, b = i1, c = i2;
        if (Orient(a, b, _points[c]) < 0) (b, c) = (c, b);   // make CCW

        // One finite triangle + three ghosts, one per hull edge (reversed direction so
        // the exterior is on the ghost edge's left).
        _tris.Add(new Tri { A = a, B = b, C = c, NA = 1, NB = 2, NC = 3 });          // 0: finite
        _tris.Add(new Tri { A = c, B = b, C = Inf, NA = 3, NB = 2, NC = 0 });        // 1: across (b,c)
        _tris.Add(new Tri { A = a, B = c, C = Inf, NA = 1, NB = 3, NC = 0 });        // 2: across (c,a)
        _tris.Add(new Tri { A = b, B = a, C = Inf, NA = 2, NB = 1, NC = 0 });        // 3: across (a,b)
        for (int i = 0; i < 4; i++) Register(i);
        _lastLive = 0;
        return (i0, i1, i2);
    }

    // ---------------- Insertion ----------------

    /// <summary>Inserts the already-stored vertex into the triangulation; -1 for a duplicate.</summary>
    private int InsertVertex(int newVertex)
    {
        var p = _points[newVertex];
        int seed = FindCavitySeed(p);
        if (seed < 0)
            return -1;

        // Grow the cavity by BFS, never crossing a constrained edge.
        var cavity = new List<int>();
        var stack = new Stack<int>();
        var inCavity = new HashSet<int>();
        stack.Push(seed);
        inCavity.Add(seed);
        while (stack.Count > 0)
        {
            int ti = stack.Pop();
            cavity.Add(ti);
            var t = _tris[ti];
            for (int e = 0; e < 3; e++)
            {
                int n = t.Neighbor(e);
                if (n < 0 || inCavity.Contains(n) || _tris[n].Dead) continue;
                int u = t.Vertex((e + 1) % 3);
                int v = t.Vertex((e + 2) % 3);
                if (u != Inf && v != Inf && _constrained.Contains(Key(u, v))) continue;
                if (!InCavity(n, p)) continue;
                inCavity.Add(n);
                stack.Push(n);
            }
        }

        // Boundary of the cavity: directed edges whose outer neighbor survives.
        var boundary = new List<(int U, int V, int Outside)>();
        foreach (int ti in cavity)
        {
            var t = _tris[ti];
            for (int e = 0; e < 3; e++)
            {
                int n = t.Neighbor(e);
                if (n >= 0 && inCavity.Contains(n)) continue;
                boundary.Add((t.Vertex((e + 1) % 3), t.Vertex((e + 2) % 3), n));
            }
        }

        foreach (int ti in cavity)
        {
            Unregister(ti);
            var t = _tris[ti];
            t.Dead = true;
            _tris[ti] = t;
        }

        // Fan: one new triangle (u, v, p) per boundary edge; cyclic orientation is
        // inherited from the dead triangles, so finite triangles stay CCW.
        var edgeToTri = new Dictionary<(int, int), int>();
        var created = new List<int>();
        foreach (var (u, v, outside) in boundary)
        {
            int ti = AddTri(u, v, newVertex);
            created.Add(ti);
            var t = _tris[ti];
            t.NC = outside;                                  // across (u,v), opposite p
            _tris[ti] = t;
            if (outside >= 0)
                SetNeighborAcross(outside, v, u, ti);
            // Register the two directed radial edges this triangle (u,v,p) owns.
            edgeToTri[(v, newVertex)] = ti;                  // B→C
            edgeToTri[(newVertex, u)] = ti;                  // C→A
        }
        // Stitch the fan. Triangle (u,v,p) owns (v,p) and (p,u); its neighbour across
        // {v,p} owns the reverse (p,v), and across {p,u} owns (u,p). The cavity boundary
        // is a closed loop, so both reversed keys always exist.
        foreach (int ti in created)
        {
            var t = _tris[ti];
            t.NA = edgeToTri[(t.C, t.B)];                    // across (v,p): neighbour owns (p,v)
            t.NB = edgeToTri[(t.A, t.C)];                    // across (p,u): neighbour owns (u,p)
            _tris[ti] = t;
        }

        _lastLive = created[0];
        return newVertex;
    }

    private int AddTri(int a, int b, int c)
    {
        _tris.Add(new Tri { A = a, B = b, C = c, NA = -1, NB = -1, NC = -1 });
        Register(_tris.Count - 1);
        return _tris.Count - 1;
    }

    private void SetNeighborAcross(int tri, int u, int v, int newNeighbor)
    {
        var t = _tris[tri];
        for (int e = 0; e < 3; e++)
        {
            if (t.Vertex((e + 1) % 3) == u && t.Vertex((e + 2) % 3) == v)
            {
                t.SetNeighbor(e, newNeighbor);
                _tris[tri] = t;
                return;
            }
        }
        throw new InvalidOperationException("Adjacency mismatch while stitching cavity boundary.");
    }

    private int FindCavitySeed(Point2 p)
    {
        // Walk from the last live triangle toward p, then verify with InCavity;
        // fall back to a linear scan (robust against degenerate walks).
        int walk = Walk(p);
        if (walk >= 0 && InCavity(walk, p)) return walk;
        for (int i = 0; i < _tris.Count; i++)
            if (!_tris[i].Dead && InCavity(i, p))
                return i;
        return -1;
    }

    private int Walk(Point2 p)
    {
        int current = _lastLive;
        if (current >= _tris.Count || _tris[current].Dead)
            current = _tris.FindIndex(t => !t.Dead);
        int steps = 8 * (int)Math.Sqrt(_tris.Count) + 32;
        while (steps-- > 0)
        {
            var t = _tris[current];
            if (t.A == Inf || t.B == Inf || t.C == Inf)
            {
                var (u, v) = GhostEdge(t);
                if (Orient(u, v, p) > -OrientErr(u, v, p)) return current;   // exterior: ghost region
                // Step inside through the finite edge.
                for (int e = 0; e < 3; e++)
                    if (t.Vertex(e) == Inf) { current = t.Neighbor(e); break; }
                continue;
            }
            int next = -1;
            for (int e = 0; e < 3; e++)
            {
                int u = t.Vertex((e + 1) % 3);
                int v = t.Vertex((e + 2) % 3);
                if (Orient(u, v, p) < -OrientErr(u, v, p)) { next = t.Neighbor(e); break; }
            }
            if (next < 0) return current;                            // contained
            current = next;
        }
        return -1;
    }

    // ---------------- Constraint recovery ----------------

    private static (int, int) Key(int u, int v) => u < v ? (u, v) : (v, u);

    private bool EdgeExists(int u, int v) =>
        _edgeTri.ContainsKey((u, v)) || _edgeTri.ContainsKey((v, u));

    /// <summary>
    /// Restores constraint edge (u,v) with Anglada's deferred-flip scheme: repeatedly
    /// flip the diagonals crossing the segment, but when a quad is momentarily non-convex
    /// defer it to the back of the queue rather than giving up — neighbouring flips make
    /// it convex later. If nothing crosses yet the edge is still absent, a vertex lies on
    /// the segment, so the constraint is split there and each half recovered. This is the
    /// robust recovery real (arc-tessellated, near-collinear) board outlines need.
    /// </summary>
    /// <summary>
    /// Iterative constraint recovery over a worklist of sub-segments — no recursion, so
    /// a long boundary through many collinear vertices (real drilled boards) can't blow
    /// the stack. Each sub-segment is recovered by deferred flips; when that stalls or the
    /// direct edge is inexplicably absent, the segment is split (at an on-segment vertex,
    /// else its midpoint) and both halves are pushed back.
    /// </summary>
    private void RecoverConstraint(int u0, int v0)
    {
        var work = new Stack<(int U, int V, int Depth)>();
        work.Push((u0, v0, 0));
        while (work.Count > 0)
        {
            var (u, v, depth) = work.Pop();
            // Both split paths increment depth; a legitimate recovery never nests deep (a
            // sub-segment either has its edge, flips into existence, or splits at most a
            // handful of times). Runaway depth means a degeneracy the flip/split scheme
            // can't resolve (some vertex orderings re-push the same segment) — bail with a
            // clear error instead of looping forever. SplitMidpoint caps at 64, so this only
            // fires on the pathological case, never on real work.
            if (depth > MaxRecoveryDepth)
                throw new ConstraintRecoveryException(
                    $"Constraint edge ({u},{v}) {Where(u, v)} could not be recovered (degenerate or self-intersecting input).");
            if (u == v || EdgeExists(u, v)) { _constrained.Add(Key(u, v)); continue; }

            var crossings = CollectCrossingEdges(u, v);
            if (crossings.Count == 0)
            {
                int w = VertexOnSegment(u, v);
                if (w < 0) w = SplitMidpoint(u, v, depth);
                work.Push((u, w, depth + 1));
                work.Push((w, v, depth + 1));
                continue;
            }

            if (RecoverByFlips(u, v, crossings))
                _constrained.Add(Key(u, v));
            else
            {
                int w = SplitMidpoint(u, v, depth);          // flips stalled: split and retry
                work.Push((u, w, depth + 1));
                work.Push((w, v, depth + 1));
            }
        }
    }

    /// <summary>Deferred-flip recovery of one sub-segment; false if it stalls (caller splits).</summary>
    private bool RecoverByFlips(int u, int v, List<(int A, int B)> crossings)
    {
        var pu = _points[u];
        var pv = _points[v];
        var queue = new LinkedList<(int A, int B)>(crossings);
        int guard = 40 * (queue.Count + 8) * (queue.Count + 8) + 256;
        while (queue.Count > 0)
        {
            if (guard-- <= 0) return false;
            var (a, b) = queue.First!.Value;
            queue.RemoveFirst();
            var loc = FindEdgeTriangles(a, b);
            if (loc is null) continue;
            if (!TryFlip(loc.Value.Tri, loc.Value.Edge, out int r, out int s))
            {
                queue.AddLast((a, b));
                continue;
            }
            if (r != u && r != v && s != u && s != v && SegmentsCross(pu, pv, _points[r], _points[s]))
                queue.AddLast((r, s));
        }
        return EdgeExists(u, v);
    }

    /// <summary>Endpoint coordinates for recovery errors, so a failure names a location, not just indices.</summary>
    private string Where(int u, int v) =>
        $"[({_points[u].X:g9}, {_points[u].Y:g9}) → ({_points[v].X:g9}, {_points[v].Y:g9})]";

    private int SplitMidpoint(int u, int v, int depth)
    {
        if (depth > 64)
            throw new ConstraintRecoveryException(
                $"Constraint edge ({u},{v}) {Where(u, v)} could not be recovered after repeated splitting; " +
                "the input polygon is likely self-intersecting.");
        int w = InsertPoint((_points[u] + _points[v]) * 0.5);
        if (w < 0)
            throw new ConstraintRecoveryException(
                $"Constraint edge ({u},{v}) {Where(u, v)} is degenerate (midpoint coincides with an endpoint).");
        return w;
    }

    /// <summary>Undirected edges of finite triangles that strictly cross segment (u,v), ordered along it.</summary>
    private List<(int A, int B)> CollectCrossingEdges(int u, int v) =>
        MarchCrossingEdges(u, v) ?? ScanCrossingEdges(u, v);

    /// <summary>
    /// Walks the triangle corridor from u toward v collecting the crossed edges — O(corridor)
    /// instead of a full-triangulation scan. Returns null when the walk cannot complete
    /// (stale vertex hint, a vertex almost on the segment, a ghost on the path): those rare
    /// near-degenerate cases fall back to the exhaustive scan so behaviour never degrades.
    /// </summary>
    private List<(int A, int B)>? MarchCrossingEdges(int u, int v)
    {
        var pu = _points[u];
        var pv = _points[v];

        // Entry: rotate the fan around u looking for the triangle the segment leaves through.
        if (!_vertexTri.TryGetValue(u, out int start) || _tris[start].Dead) return null;
        int cur = start, ca = -1, cb = -1, entry = -1;
        for (int steps = 0; steps <= _tris.Count; steps++)
        {
            var t = _tris[cur];
            int e = t.A == u ? 0 : t.B == u ? 1 : t.C == u ? 2 : -1;
            if (e < 0) return null;                              // stale hint / corrupt fan
            int x = t.Vertex((e + 1) % 3);
            int y = t.Vertex((e + 2) % 3);
            if (x != Inf && y != Inf && SegmentsCross(pu, pv, _points[x], _points[y]))
            {
                (ca, cb, entry) = (x, y, t.Neighbor(e));
                break;
            }
            cur = t.Neighbor((e + 2) % 3);                       // rotate: neighbour across (u, x)
            if (cur < 0 || _tris[cur].Dead) return null;
            if (cur == start) return null;                       // nothing strictly crosses the fan:
                                                                 // near-degenerate — use the scan
        }
        if (entry < 0) return null;                              // fan rotation exceeded budget

        // March across crossed edges until a triangle contains v.
        var result = new List<(int A, int B)>();
        var seen = new HashSet<(int, int)>();
        void Add(int a, int b)
        {
            if (!_constrained.Contains(Key(a, b)) && seen.Add(Key(a, b)))
                result.Add((a, b));
        }
        Add(ca, cb);
        cur = entry;
        for (int steps = 0; steps <= _tris.Count; steps++)
        {
            if (cur < 0) return null;
            var t = _tris[cur];
            if (t.Dead || t.A == Inf || t.B == Inf || t.C == Inf) return null;
            int w = t.A != ca && t.A != cb ? t.A : t.B != ca && t.B != cb ? t.B : t.C;
            if (w == v) return result;
            if (SegmentsCross(pu, pv, _points[ca], _points[w]))
            {
                cur = OppositeNeighbor(cur, cb);                 // exit across (ca, w)
                cb = w;
            }
            else if (SegmentsCross(pu, pv, _points[w], _points[cb]))
            {
                cur = OppositeNeighbor(cur, ca);                 // exit across (w, cb)
                ca = w;
            }
            else return null;                                    // pinched at a vertex on the segment
            Add(ca, cb);
        }
        return null;
    }

    private int OppositeNeighbor(int ti, int vertex)
    {
        var t = _tris[ti];
        return t.A == vertex ? t.NA : t.B == vertex ? t.NB : t.C == vertex ? t.NC : -1;
    }

    private List<(int A, int B)> ScanCrossingEdges(int u, int v)
    {
        var pu = _points[u];
        var pv = _points[v];
        var seen = new HashSet<(int, int)>();
        var found = new List<(int A, int B, double T)>();
        foreach (var t in _tris)
        {
            if (t.Dead || t.A == Inf || t.B == Inf || t.C == Inf) continue;
            for (int e = 0; e < 3; e++)
            {
                int a = t.Vertex((e + 1) % 3);
                int b = t.Vertex((e + 2) % 3);
                if (a == u || a == v || b == u || b == v) continue;
                if (_constrained.Contains(Key(a, b))) continue;
                if (!seen.Add(Key(a, b))) continue;
                if (SegmentsCross(pu, pv, _points[a], _points[b]))
                    found.Add((a, b, IntersectionParam(pu, pv, _points[a], _points[b])));
            }
        }
        found.Sort((x, y) => x.T.CompareTo(y.T));
        return found.Select(x => (x.A, x.B)).ToList();
    }

    /// <summary>A vertex lying on segment (u,v) strictly between the endpoints, or -1.</summary>
    private int VertexOnSegment(int u, int v)
    {
        var pu = _points[u];
        var pv = _points[v];
        var d = pv - pu;
        double dd = Point2.Dot(d, d);
        int best = -1;
        double bestT = double.MaxValue;
        for (int i = 0; i < _points.Count; i++)
        {
            if (i == u || i == v) continue;
            if (Math.Abs(Orient(u, v, _points[i])) > OrientErr(u, v, _points[i])) continue;
            double tp = Point2.Dot(_points[i] - pu, d) / dd;
            if (tp <= 1e-9 || tp >= 1 - 1e-9) continue;
            if (tp < bestT) { bestT = tp; best = i; }
        }
        return best;
    }

    /// <summary>Locates a finite triangle and edge index owning undirected edge (a,b), or null.</summary>
    private (int Tri, int Edge)? FindEdgeTriangles(int a, int b)
    {
        foreach (var key in new[] { (a, b), (b, a) })
        {
            if (!_edgeTri.TryGetValue(key, out int ti)) continue;
            var t = _tris[ti];
            if (t.Dead || t.A == Inf || t.B == Inf || t.C == Inf) continue;
            for (int e = 0; e < 3; e++)
                if (t.Vertex((e + 1) % 3) == key.Item1 && t.Vertex((e + 2) % 3) == key.Item2)
                    return (ti, e);
        }
        return null;
    }

    private static bool SegmentsCross(Point2 p1, Point2 p2, Point2 q1, Point2 q2)
    {
        // Strict crossing: each segment's endpoints on decisively opposite sides of the other.
        return OppositeSides(p1, p2, q1, q2) && OppositeSides(q1, q2, p1, p2);
    }

    private static bool OppositeSides(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        double o1 = OrientVal(a, b, c), e1 = OrientErr(a, b, c);
        double o2 = OrientVal(a, b, d), e2 = OrientErr(a, b, d);
        return (o1 > e1 && o2 < -e2) || (o1 < -e1 && o2 > e2);
    }

    /// <summary>Parameter along p1→p2 where it meets the line through q1,q2 (for ordering crossings).</summary>
    private static double IntersectionParam(Point2 p1, Point2 p2, Point2 q1, Point2 q2)
    {
        var d = p2 - p1;
        var e = q2 - q1;
        double denom = Point2.Cross(d, e);
        if (Math.Abs(denom) < 1e-300) return 0;
        return Point2.Cross(q1 - p1, e) / denom;
    }

    /// <summary>
    /// Flips the diagonal of the convex quad formed by <paramref name="tri"/> and its
    /// neighbor across <paramref name="edge"/>. On success outputs the new diagonal's
    /// endpoints (<paramref name="r"/>, <paramref name="s"/>).
    /// </summary>
    private bool TryFlip(int tri, int edge, out int r, out int s)
    {
        r = s = -1;
        var t1 = _tris[tri];
        int n = t1.Neighbor(edge);
        if (n < 0) return false;
        var t2 = _tris[n];
        if (t2.A == Inf || t2.B == Inf || t2.C == Inf) return false;

        r = t1.Vertex(edge);                             // apex of t1
        int p = t1.Vertex((edge + 1) % 3);
        int q = t1.Vertex((edge + 2) % 3);
        int e2 = -1;                                     // find apex of t2 (vertex not on shared edge)
        for (int e = 0; e < 3; e++)
            if (t2.Vertex(e) != p && t2.Vertex(e) != q) { e2 = e; break; }
        s = t2.Vertex(e2);

        // Convexity: p and q strictly on opposite sides of the new diagonal (r,s).
        if (!OppositeSides(_points[r], _points[s], _points[p], _points[q]))
            return false;

        // Old: t1 = (r,p,q)-cyclic, t2 = (s,q,p)-cyclic. New: (r,p,s) and (r,s,q).
        // t2's cyclic order is (s,q,p): its directed edges are (s,q), (q,p), (p,s).
        int nRp = t1.Neighbor((edge + 2) % 3);           // t1's neighbor across (r,p)
        int nQr = t1.Neighbor((edge + 1) % 3);           // t1's neighbor across (q,r)
        int nPs = t2.Neighbor(FindEdge(t2, p, s));       // t2's neighbor across (p,s)
        int nSq = t2.Neighbor(FindEdge(t2, s, q));       // t2's neighbor across (s,q)

        Unregister(tri);
        Unregister(n);
        var newT1 = new Tri { A = r, B = p, C = s, NA = nPs, NB = n, NC = nRp };
        var newT2 = new Tri { A = r, B = s, C = q, NA = nSq, NB = nQr, NC = tri };
        _tris[tri] = newT1;
        _tris[n] = newT2;
        Register(tri);
        Register(n);
        if (nRp >= 0) SetNeighborAcross(nRp, p, r, tri);
        if (nPs >= 0) SetNeighborAcross(nPs, s, p, tri);
        if (nQr >= 0) SetNeighborAcross(nQr, r, q, n);
        if (nSq >= 0) SetNeighborAcross(nSq, q, s, n);
        return true;
    }

    /// <summary>The edge index of <paramref name="t"/> whose opposite side is (u,v) in cyclic order.</summary>
    private static int FindEdge(in Tri t, int u, int v)
    {
        for (int e = 0; e < 3; e++)
            if (t.Vertex((e + 1) % 3) == u && t.Vertex((e + 2) % 3) == v)
                return e;
        throw new InvalidOperationException("Edge not found in triangle (adjacency corruption).");
    }
}
