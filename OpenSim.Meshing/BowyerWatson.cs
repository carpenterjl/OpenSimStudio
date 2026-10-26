using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Incremental Bowyer–Watson Delaunay tetrahedralization with a symbolic vertex at
/// infinity (the CGAL approach): the convex hull is wrapped in "infinite" tetrahedra
/// that share one conceptual infinite vertex, so hull growth is handled by the same
/// cavity mechanism as interior insertion and no finite super-tetrahedron can distort
/// near-hull connectivity.
///
/// Predicates:
///  - finite tet: point is in conflict when inside the circumsphere;
///  - infinite tet: point is in conflict when strictly outside the hull across the
///    tet's finite (hull) facet.
/// </summary>
public sealed class BowyerWatson
{
    private const int Inf = -1;

    private readonly List<Vector3D> _points = new();
    private readonly List<Tet> _tets = new();
    // Sorted face key → the (up to 2) alive tets sharing it. In a complete
    // triangulation of hull + infinite wrap, every face has exactly two.
    private readonly Dictionary<(int, int, int), FaceLink> _faces = new();
    private int _lastAlive = -1;
    private Vector3D _interiorPoint; // any point strictly inside the current hull

    private struct Tet
    {
        public int A, B, C, D; // D == Inf for infinite tets; (A,B,C) is then the hull facet wound outward
        public bool Alive;
    }

    private struct FaceLink
    {
        public int Tet0, Tet1;
    }

    /// <summary>
    /// Triangulates the points. Returns positively oriented tetrahedra as index
    /// quadruples into the input list. Points must not be all coplanar.
    /// </summary>
    public List<(int, int, int, int)> Triangulate(IReadOnlyList<Vector3D> inputPoints,
        CancellationToken cancellationToken = default)
    {
        if (inputPoints.Count < 4)
            throw new ArgumentException("At least 4 points are required.", nameof(inputPoints));

        _points.Clear();
        _tets.Clear();
        _faces.Clear();
        _lastAlive = -1;
        _points.AddRange(inputPoints);

        var order = BuildInsertionOrder();
        InitializeFirstTet(order);
        SkippedPoints.Clear();
        for (int i = 4; i < order.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Insert(order[i]);
        }
        // A point left out for lying on several hull facets at once is usually no hull case once
        // the hull is complete: try each once more, in the original order, and keep only what
        // still cannot be placed (with the retry's reason; one that now conflicts with nothing is a
        // duplicate within round-off and is dropped without a record, as a duplicate is).
        if (SkippedPoints.Count > 0)
        {
            var retry = SkippedPoints.Select(s => s.Index).ToList();
            SkippedPoints.Clear();
            foreach (int index in retry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Insert(index);
            }
        }

        return FiniteTets();
    }

    /// <summary>A point the triangulation left out because its cavity could not be made
    /// star-shaped (a degenerate or near-degenerate placement), with the reason.</summary>
    public sealed record SkippedPoint(int Index, Vector3D Position, string Reason);
    // (FiniteTets' own summary follows its declaration below.)

    /// <summary>The points left out so far (duplicates are not listed: they conflict with
    /// nothing and <see cref="InsertPoint"/> reports them by returning false).</summary>
    public List<SkippedPoint> SkippedPoints { get; } = new();

    /// <summary>
    /// Checks the live triangulation and names the first defect found, or null when it is
    /// sound: every finite tetrahedron strictly positive, every face shared by at most two live
    /// tetrahedra with both of them live and the face theirs, the hull closed (every face with one
    /// live finite tetrahedron borders an infinite one), and — when <paramref name="checkDelaunay"/> —
    /// no point inside any finite tetrahedron's circumsphere (O(n²); for tests).
    /// </summary>
    public string? Audit(bool checkDelaunay = false)
    {
        var faceUsers = new Dictionary<(int, int, int), List<int>>();
        var finite = new List<int>();
        for (int i = 0; i < _tets.Count; i++)
        {
            var t = _tets[i];
            if (!t.Alive) continue;
            if (t.D != Inf)
            {
                finite.Add(i);
                double orientation = GeometricPredicates.Orient3D(_points[t.A], _points[t.B], _points[t.C], _points[t.D]);
                if (orientation < 0 || Coplanar(orientation, _points[t.A], _points[t.B], _points[t.C], _points[t.D]))
                    return $"tetrahedron {i} ({t.A}, {t.B}, {t.C}, {t.D}) is flat or inverted";
            }
            foreach (var face in TetFaces(i))
            {
                if (!faceUsers.TryGetValue(face, out var users)) faceUsers[face] = users = new List<int>();
                users.Add(i);
            }
        }
        foreach (var (face, users) in faceUsers)
        {
            if (users.Count > 2)
                return $"face ({face.Item1}, {face.Item2}, {face.Item3}) is shared by {users.Count} tetrahedra";
            if (users.Count == 1)
                return $"face ({face.Item1}, {face.Item2}, {face.Item3}) of tetrahedron {users[0]} has no neighbour";
            if (!_faces.TryGetValue(face, out var link) || link.Tet0 < 0 || link.Tet1 < 0
                || !users.Contains(link.Tet0) || !users.Contains(link.Tet1))
                return $"face ({face.Item1}, {face.Item2}, {face.Item3}): the adjacency table disagrees with the tetrahedra";
        }
        // The hull: every facet wound outward (the interior reference point behind it), and no
        // reflex edge — the facet across each hull edge lies on or behind the other's plane.
        for (int i = 0; i < _tets.Count; i++)
        {
            var t = _tets[i];
            if (!t.Alive || t.D != Inf) continue;
            if (GeometricPredicates.Orient3D(_points[t.A], _points[t.B], _points[t.C], _interiorPoint) >= 0)
                return $"hull facet {i} ({t.A}, {t.B}, {t.C}) is not wound outward";
            foreach (var (u, v, w) in new[] { (t.A, t.B, t.C), (t.B, t.C, t.A), (t.C, t.A, t.B) })
            {
                int other = Opposite(SortedFace(u, v, Inf), i);
                if (other < 0) return $"hull edge ({u}, {v}) has no second facet";
                var o = _tets[other];
                int x = o.A != u && o.A != v ? o.A : o.B != u && o.B != v ? o.B : o.C;
                double bend = GeometricPredicates.Orient3D(_points[u], _points[v], _points[w], _points[x]);
                if (bend > 0 && !Coplanar(bend, _points[u], _points[v], _points[w], _points[x]))
                    return $"hull edge ({u}, {v}) is reflex: facet ({u}, {v}, {w}) sees vertex {x} of its neighbour";
            }
        }
        if (checkDelaunay)
        {
            // The empty-circumsphere test with the builder's own tie band: cospherical points (a
            // lattice, points placed on faces and edges) sit ON spheres to round-off, and only a
            // point clearly inside one is a defect.
            var inserted = new HashSet<int>();
            foreach (int i in finite) { var t = _tets[i]; inserted.Add(t.A); inserted.Add(t.B); inserted.Add(t.C); inserted.Add(t.D); }
            foreach (int i in finite)
            {
                var t = _tets[i];
                Vector3D a = _points[t.A], b = _points[t.B], c = _points[t.C], d = _points[t.D];
                foreach (int p in inserted)
                {
                    if (p == t.A || p == t.B || p == t.C || p == t.D) continue;
                    if (StrictlyInside(GeometricPredicates.InSphere(a, b, c, d, _points[p]), a, b, c, d, _points[p]))
                        return $"point {p} lies inside the circumsphere of tetrahedron {i}";
                }
            }
        }
        return null;
    }

    /// <summary>For the audit's own test: turns live finite tetrahedron <paramref name="index"/>
    /// inside out (its last two vertices swapped), which a sound triangulation never contains;
    /// false when that index is no live finite tetrahedron.</summary>
    internal bool InvertForAudit(int index)
    {
        if (index < 0 || index >= _tets.Count) return false;
        var t = _tets[index];
        if (!t.Alive || t.D == Inf) return false;
        (t.C, t.D) = (t.D, t.C);
        _tets[index] = t;
        return true;
    }

    /// <summary>The live finite tetrahedra as index quadruples into the inserted point list (the
    /// original input followed by any <see cref="InsertPoint"/> additions).</summary>
    public List<(int, int, int, int)> FiniteTets()
    {
        var result = new List<(int, int, int, int)>();
        foreach (var t in _tets)
        {
            if (!t.Alive || t.D == Inf) continue;
            result.Add((t.A, t.B, t.C, t.D));
        }
        return result;
    }

    /// <summary>
    /// Incrementally inserts one point into an existing triangulation, re-establishing
    /// the Delaunay property through the same cavity mechanism as bulk construction.
    /// Returns false when the point conflicts with nothing (a duplicate) or when no sound cavity
    /// could be built for it (then it is recorded in <see cref="SkippedPoints"/>). The point is
    /// appended to the internal list either way, so callers keeping a parallel point list stay
    /// index-aligned by always appending the candidate themselves.
    /// </summary>
    public bool InsertPoint(Vector3D p)
    {
        if (_tets.Count == 0)
            throw new InvalidOperationException("Triangulate must be called before InsertPoint.");
        _points.Add(p);
        return Insert(_points.Count - 1);
    }

    /// <summary>
    /// Picks four non-coplanar points to seed the triangulation and returns the full
    /// insertion order with those four first.
    /// </summary>
    private List<int> BuildInsertionOrder()
    {
        int n = _points.Count;
        double scale = Aabb.FromPoints(_points).Diagonal;
        double eps = Math.Max(scale * scale * scale * 1e-12, double.Epsilon);

        int i0 = 0;
        int i1 = -1;
        for (int i = 1; i < n && i1 < 0; i++)
            if (Vector3D.DistanceSquared(_points[i0], _points[i]) > eps * eps)
                i1 = i;
        if (i1 < 0) throw new InvalidOperationException("All points are coincident.");

        int i2 = -1;
        for (int i = 1; i < n && i2 < 0; i++)
        {
            if (i == i1) continue;
            var cross = Vector3D.Cross(_points[i1] - _points[i0], _points[i] - _points[i0]);
            if (cross.Length > eps) i2 = i;
        }
        if (i2 < 0) throw new InvalidOperationException("All points are collinear.");

        int i3 = -1;
        for (int i = 1; i < n && i3 < 0; i++)
        {
            if (i == i1 || i == i2) continue;
            if (Math.Abs(GeometricPredicates.Orient3D(_points[i0], _points[i1], _points[i2], _points[i])) > eps)
                i3 = i;
        }
        if (i3 < 0) throw new InvalidOperationException("All points are coplanar; cannot tetrahedralize.");

        var order = new List<int>(n) { i0, i1, i2, i3 };
        for (int i = 0; i < n; i++)
            if (i != i0 && i != i1 && i != i2 && i != i3)
                order.Add(i);
        return order;
    }

    private void InitializeFirstTet(List<int> order)
    {
        int a = order[0], b = order[1], c = order[2], d = order[3];
        if (GeometricPredicates.Orient3D(_points[a], _points[b], _points[c], _points[d]) < 0)
            (c, d) = (d, c);
        _interiorPoint = (_points[a] + _points[b] + _points[c] + _points[d]) / 4.0;

        AddFiniteTet(a, b, c, d);
        // Wrap each face in an infinite tet. Faces wound outward (away from centroid).
        AddInfiniteTet(b, c, d);
        AddInfiniteTet(a, c, d);
        AddInfiniteTet(a, b, d);
        AddInfiniteTet(a, b, c);
    }

    private bool IsInfinite(int tet) => _tets[tet].D == Inf;

    /// <summary>Conflict test: does inserting p require removing this tet?</summary>
    private bool InConflict(int tet, Vector3D p)
    {
        var t = _tets[tet];
        if (t.D == Inf)
        {
            // p strictly outside the hull across this facet (facet wound outward) — or ON the
            // facet's plane and inside its circumcircle: the infinite tet's "circumsphere" is the
            // outer half-space plus that disc, so a point on a hull face or edge splits the facet
            // instead of being left to make a second hull facet over the same edge (D20: that
            // produced a third tetrahedron on the edge's face, once silently).
            Vector3D a = _points[t.A], b = _points[t.B], c = _points[t.C];
            double side = GeometricPredicates.Orient3D(a, b, c, p);
            if (!Coplanar(side, a, b, c, p)) return side > 0;
            var normal = Vector3D.Cross(b - a, c - a);
            double scale = Math.Max((b - a).Length, (c - a).Length);
            if (normal.Length == 0) return false;
            var apex = a + normal * (scale / normal.Length);             // off the plane, on the facet's positive side
            return StrictlyInside(GeometricPredicates.InSphere(a, b, c, apex, p), a, b, c, apex, p);
        }
        return StrictlyInside(GeometricPredicates.InSphere(_points[t.A], _points[t.B], _points[t.C], _points[t.D], p),
            _points[t.A], _points[t.B], _points[t.C], _points[t.D], p);
    }

    private bool Insert(int p)
    {
        Vector3D point = _points[p];
        int start = Locate(point);
        if (!InConflict(start, point))
        {
            // Point duplicates an existing vertex or lies exactly on a degenerate
            // configuration; find any conflict tet by scan, or skip the point.
            start = -1;
            for (int i = 0; i < _tets.Count; i++)
            {
                if (_tets[i].Alive && InConflict(i, point))
                {
                    start = i;
                    break;
                }
            }
            if (start < 0)
                return false; // no conflict anywhere — duplicate point; safe to skip
        }

        // Grow the cavity of conflicting tets.
        var inCavity = new HashSet<int> { start };
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            int t = stack.Pop();
            foreach (var face in TetFaces(t))
            {
                int neighbor = Opposite(face, t);
                if (neighbor < 0 || inCavity.Contains(neighbor)) continue;
                if (InConflict(neighbor, point))
                {
                    inCavity.Add(neighbor);
                    stack.Push(neighbor);
                }
            }
        }

        // Make the cavity star-shaped from p before anything is removed. Every new finite tet is
        // (boundary face, p): a face p lies ON would give a flat tet, so the tet across that face
        // joins the cavity (p is then strictly inside its circumsphere — the in-sphere test missed
        // it only by round-off); a face p lies BEYOND (seen from the cavity side) would give an
        // inverted tet, so the tet carrying it leaves the cavity (p sits on that tet's circumsphere,
        // a tie, so the result stays Delaunay). Repeat until neither happens; a point that never
        // settles is left out and recorded (D20: this used to exit with the violating tet and
        // build the flat or inverted tet silently).
        // With exact predicates a tet that joins by extension holds p in its closure and can never
        // be dropped again, so the loop ends; round-off can make the two rules disagree, and an
        // extension back into a tet that was dropped is the sign of it — the point is then left out.
        List<(int A, int B, int C)> boundary;
        var dropped = new HashSet<int>();
        while (true)
        {
            boundary = new List<(int, int, int)>();
            var violators = new HashSet<int>();
            var extensions = new HashSet<int>();
            var boundaryFaces = boundary;

            foreach (int t in inCavity)
            {
                var tet = _tets[t];

                void CheckFace(int fa, int fb, int fc, int opp)
                {
                    int neighbor = Opposite(SortedFace(fa, fb, fc), t);
                    if (neighbor >= 0 && inCavity.Contains(neighbor)) return;

                    if (fa != Inf && fb != Inf && fc != Inf)
                    {
                        Vector3D a = _points[fa], b = _points[fb], c = _points[fc];
                        double sideP = GeometricPredicates.Orient3D(a, b, c, point);
                        if (Coplanar(sideP, a, b, c, point))
                        {
                            // On the face itself: the tet across it has p on its boundary too and
                            // joins the cavity. On the face's plane but outside it: no tet with this
                            // face can hold p, so the tet carrying it leaves the cavity.
                            if (neighbor >= 0 && InTriangle(a, b, c, point)) { extensions.Add(neighbor); return; }
                            violators.Add(t);
                            return;
                        }
                        if (opp != Inf)
                        {
                            double sideTet = GeometricPredicates.Orient3D(_points[fa], _points[fb], _points[fc], _points[opp]);
                            if (sideTet * sideP < 0)
                            {
                                violators.Add(t);
                                return;
                            }
                        }
                    }
                    boundaryFaces.Add((fa, fb, fc));
                }

                CheckFace(tet.B, tet.C, tet.D, tet.A);
                CheckFace(tet.A, tet.C, tet.D, tet.B);
                CheckFace(tet.A, tet.B, tet.D, tet.C);
                CheckFace(tet.A, tet.B, tet.C, tet.D);
            }

            if (extensions.Count > 0)
            {
                if (extensions.Overlaps(dropped))
                {
                    SkippedPoints.Add(new SkippedPoint(p, point, "no star-shaped cavity could be settled on (the on-face and beyond-face tests disagree about a tetrahedron, within round-off)"));
                    return false;
                }
                foreach (int t in extensions) inCavity.Add(t);
                continue;
            }
            if (violators.Count == 0)
                break;
            if (inCavity.Count <= violators.Count)
            {
                // Nothing would be left: the point lies on or beyond a face of every conflicting
                // tet, so no star-shaped cavity exists for it. Left out and recorded.
                SkippedPoints.Add(new SkippedPoint(p, point, "the cavity could not be made star-shaped from the point (it lies on or beyond a face of its last conflicting tetrahedron)"));
                return false;
            }
            foreach (int t in violators)
            {
                inCavity.Remove(t);
                dropped.Add(t);
            }
        }

        // The hull facets p will get: one fan, (u, v, p) for each hull edge (u, v) on the cavity's
        // rim. The rim must be one closed cycle of hull edges; with p coplanar with several hull
        // facets at once (a lattice growing a slab) the tie-breaking can leave a rim that visits a
        // vertex more than once, and two new facets would then share the edge from that vertex to
        // p — the overlap D20 found. Such a point is left out and recorded (Triangulate retries it
        // once the hull is complete, where it is no longer a hull case).
        if (!RimIsOneCycle(boundary))
        {
            SkippedPoints.Add(new SkippedPoint(p, point, "the hull edges around its cavity do not form one cycle (the point is coplanar with several hull facets at once)"));
            return false;
        }

        var removed = inCavity.Select(t => _tets[t]).Select(t => (t.A, t.B, t.C, t.D)).ToList();
        foreach (int t in inCavity)
            RemoveTet(t);
        try
        {
            foreach (var (fa, fb, fc) in boundary)
            {
                if (fa == Inf || fb == Inf || fc == Inf)
                {
                    // Boundary face shared with a surviving infinite tet: the two finite
                    // vertices plus p form a new hull facet whose infinite tet we create.
                    int u = fa == Inf ? fb : fa;
                    int v = fa == Inf || fb == Inf ? fc : fb;
                    AddInfiniteTet(u, v, p);
                }
                else
                {
                    AddFiniteTet(fa, fb, fc, p);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Inserting point {p} at ({point.X:g9}, {point.Y:g9}, {point.Z:g9}): {ex.Message} Cavity: " +
                string.Join(" ", removed.Select(t => $"({t.A},{t.B},{t.C},{t.D})")) + "; boundary: " +
                string.Join(" ", boundary.Select(f => $"({f.A},{f.B},{f.C})")), ex);
        }
        return true;
    }

    /// <summary>Relative round-off allowance of the double-precision predicates: a determinant
    /// below this fraction of its natural scale is read as zero (coplanar, cospherical). The 3-D
    /// kernel has no exact predicates (evaluation D20); this keeps the degenerate cases — four
    /// coplanar points, five cospherical ones — on the degenerate branch instead of a random side.</summary>
    private const double Epsilon = 1e-12;

    private static double Scale(Vector3D a, Vector3D b, Vector3D c, Vector3D d) =>
        Math.Max(Math.Max((b - a).Length, (c - a).Length), (d - a).Length);

    /// <summary>Orient3D read as zero within round-off. Orient3D(a, b, c, d) is twice the triangle's
    /// area times d's height over its plane, so the band is a height of Epsilon × the longest
    /// distance involved — it does not grow faster than the distance when d is far from a small
    /// face (a far point beyond a small hull facet stays "beyond").</summary>
    private static bool Coplanar(double orient, Vector3D a, Vector3D b, Vector3D c, Vector3D d)
    {
        double doubleArea = Vector3D.Cross(b - a, c - a).Length;
        return Math.Abs(orient) <= Epsilon * doubleArea * Scale(a, b, c, d);
    }

    /// <summary>InSphere clearly positive: p inside the sphere, not on it. InSphere is Orient3D
    /// times (r² − |p − centre|²) in size, so the tie band is Epsilon × |Orient3D| × length², which
    /// keeps a sliver's verdict as sharp as its volume allows.</summary>
    private static bool StrictlyInside(double insphere, Vector3D a, Vector3D b, Vector3D c, Vector3D d, Vector3D p)
    {
        double orient = Math.Abs(GeometricPredicates.Orient3D(a, b, c, d));
        double l = Math.Max(Scale(a, b, c, d), (p - a).Length);
        return insphere > Epsilon * orient * l * l;
    }

    /// <summary>A point in the plane of triangle abc: inside it or on its boundary, with an
    /// in-plane slack of Epsilon × the longest distance involved (each edge test is twice the
    /// triangle's area × the edge length × the point's distance past the edge).</summary>
    private static bool InTriangle(Vector3D a, Vector3D b, Vector3D c, Vector3D p)
    {
        var n = Vector3D.Cross(b - a, c - a);
        double l = Scale(a, b, c, p);
        double tol = -Epsilon * n.Length * l * l;
        return Vector3D.Dot(Vector3D.Cross(b - a, p - a), n) >= tol
            && Vector3D.Dot(Vector3D.Cross(c - b, p - b), n) >= tol
            && Vector3D.Dot(Vector3D.Cross(a - c, p - c), n) >= tol;
    }

    /// <summary>True when the boundary faces that carry the infinite vertex form no edges at all
    /// (an interior point) or exactly one closed cycle of hull edges, every rim vertex on two of them.</summary>
    private static bool RimIsOneCycle(List<(int A, int B, int C)> boundary)
    {
        var edges = new List<(int, int)>();
        foreach (var (a, b, c) in boundary)
        {
            if (a != Inf && b != Inf && c != Inf) continue;
            int u = a == Inf ? b : a;
            int v = a == Inf || b == Inf ? c : b;
            edges.Add((u, v));
        }
        if (edges.Count == 0) return true;
        var degree = new Dictionary<int, int>();
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (u, v) in edges)
        {
            degree[u] = degree.GetValueOrDefault(u) + 1;
            degree[v] = degree.GetValueOrDefault(v) + 1;
            if (!adjacency.TryGetValue(u, out var lu)) adjacency[u] = lu = new List<int>();
            if (!adjacency.TryGetValue(v, out var lv)) adjacency[v] = lv = new List<int>();
            lu.Add(v); lv.Add(u);
        }
        if (degree.Values.Any(d => d != 2)) return false;
        // One component: walk from any vertex and count what is reached.
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(edges[0].Item1);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            if (!seen.Add(x)) continue;
            foreach (int y in adjacency[x]) stack.Push(y);
        }
        return seen.Count == degree.Count;
    }

    /// <summary>Walks from the last created finite tet towards the point.</summary>
    private int Locate(Vector3D p)
    {
        int current = _lastAlive;
        if (current < 0 || !_tets[current].Alive || IsInfinite(current))
            current = FindAnyAliveFinite();
        if (current < 0)
            return FindAnyAlive();

        Span<(int, int, int)> faces = stackalloc (int, int, int)[4];
        for (int step = 0; step < _tets.Count + 16; step++)
        {
            var t = _tets[current];
            int next = -1;
            // Each face wound so Orient3D(face, oppositeVertex) > 0: interior is positive side.
            faces[0] = (t.B, t.D, t.C);
            faces[1] = (t.A, t.C, t.D);
            faces[2] = (t.A, t.D, t.B);
            faces[3] = (t.A, t.B, t.C);
            foreach (var (fa, fb, fc) in faces)
            {
                if (GeometricPredicates.Orient3D(_points[fa], _points[fb], _points[fc], p) < 0)
                {
                    int neighbor = Opposite(SortedFace(fa, fb, fc), current);
                    if (neighbor >= 0)
                    {
                        next = neighbor;
                        break;
                    }
                }
            }
            if (next < 0)
                return current;          // no face separates p from this tet ⇒ p inside
            if (IsInfinite(next))
                return next;             // walked out of the hull ⇒ p conflicts with this infinite tet
            current = next;
        }

        // Degenerate walk; caller falls back to a linear conflict scan.
        return current;
    }

    private int FindAnyAliveFinite()
    {
        for (int i = _tets.Count - 1; i >= 0; i--)
            if (_tets[i].Alive && !IsInfinite(i))
                return i;
        return -1;
    }

    private int FindAnyAlive()
    {
        for (int i = _tets.Count - 1; i >= 0; i--)
            if (_tets[i].Alive)
                return i;
        throw new InvalidOperationException("Triangulation has no live tetrahedra.");
    }

    private void AddFiniteTet(int a, int b, int c, int d)
    {
        double orientation = GeometricPredicates.Orient3D(_points[a], _points[b], _points[c], _points[d]);
        if (Coplanar(orientation, _points[a], _points[b], _points[c], _points[d]))
            throw new InvalidOperationException(
                $"Tetrahedron ({a}, {b}, {c}, {d}) is flat: the cavity check should have kept it from being built.");
        if (orientation < 0)
            (c, d) = (d, c);
        int index = _tets.Count;
        _tets.Add(new Tet { A = a, B = b, C = c, D = d, Alive = true });
        _lastAlive = index;
        foreach (var face in TetFaces(index))
            LinkFace(face, index);
    }

    private void AddInfiniteTet(int a, int b, int c)
    {
        // Wind the hull facet outward: the interior reference point on the negative side.
        if (GeometricPredicates.Orient3D(_points[a], _points[b], _points[c], _interiorPoint) > 0)
            (b, c) = (c, b);
        int index = _tets.Count;
        _tets.Add(new Tet { A = a, B = b, C = c, D = Inf, Alive = true });
        foreach (var face in TetFaces(index))
            LinkFace(face, index);
    }

    private void RemoveTet(int index)
    {
        var t = _tets[index];
        t.Alive = false;
        _tets[index] = t;
        foreach (var face in TetFaces(index))
            UnlinkFace(face, index);
    }

    private IEnumerable<(int, int, int)> TetFaces(int index)
    {
        var t = _tets[index];
        yield return SortedFace(t.B, t.C, t.D);
        yield return SortedFace(t.A, t.C, t.D);
        yield return SortedFace(t.A, t.B, t.D);
        yield return SortedFace(t.A, t.B, t.C);
    }

    private static (int, int, int) SortedFace(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private void LinkFace((int, int, int) face, int tet)
    {
        if (_faces.TryGetValue(face, out var link))
        {
            if (link.Tet1 >= 0)
                throw new InvalidOperationException(
                    $"Face ({face.Item1}, {face.Item2}, {face.Item3}) would have a third tetrahedron ({link.Tet0}, {link.Tet1}, {tet}): the triangulation overlaps.");
            link.Tet1 = tet;
            _faces[face] = link;
        }
        else
        {
            _faces[face] = new FaceLink { Tet0 = tet, Tet1 = -1 };
        }
    }

    private void UnlinkFace((int, int, int) face, int tet)
    {
        if (!_faces.TryGetValue(face, out var link)) return;
        if (link.Tet0 == tet) link.Tet0 = link.Tet1;
        else if (link.Tet1 != tet) return;
        link.Tet1 = -1;
        if (link.Tet0 < 0) _faces.Remove(face);
        else _faces[face] = link;
    }

    /// <summary>The alive tet sharing this face other than <paramref name="tet"/>, or -1.</summary>
    private int Opposite((int, int, int) face, int tet)
    {
        if (!_faces.TryGetValue(face, out var link)) return -1;
        return link.Tet0 == tet ? link.Tet1 : link.Tet0;
    }
}
