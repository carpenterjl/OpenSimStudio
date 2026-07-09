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
        for (int i = 4; i < order.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Insert(order[i]);
        }

        return FiniteTets();
    }

    /// <summary>
    /// The live finite tetrahedra as index quadruples into the inserted point list
    /// (the original input followed by any <see cref="InsertPoint"/> additions).
    /// </summary>
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
    /// Returns false when the point conflicts with nothing (a duplicate or degenerate
    /// location) and was skipped. The point is appended to the internal list either
    /// way, so callers keeping a parallel point list stay index-aligned by always
    /// appending the candidate themselves.
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
            // p strictly outside the hull across this facet (facet wound outward).
            return GeometricPredicates.Orient3D(_points[t.A], _points[t.B], _points[t.C], p) > 0;
        }
        return GeometricPredicates.InSphere(_points[t.A], _points[t.B], _points[t.C], _points[t.D], p) > 0;
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

        // Shrink until every new finite tet would be strictly positive: for each finite
        // boundary face of a finite cavity tet, p must lie strictly on the cavity side.
        List<(int A, int B, int C)> boundary;
        while (true)
        {
            boundary = new List<(int, int, int)>();
            var violators = new HashSet<int>();
            var boundaryFaces = boundary;

            foreach (int t in inCavity)
            {
                var tet = _tets[t];

                void CheckFace(int fa, int fb, int fc, int opp)
                {
                    int neighbor = Opposite(SortedFace(fa, fb, fc), t);
                    if (neighbor >= 0 && inCavity.Contains(neighbor)) return;

                    if (opp != Inf && fa != Inf && fb != Inf && fc != Inf)
                    {
                        double sideTet = GeometricPredicates.Orient3D(_points[fa], _points[fb], _points[fc], _points[opp]);
                        double sideP = GeometricPredicates.Orient3D(_points[fa], _points[fb], _points[fc], point);
                        if (sideTet * sideP <= 0 && inCavity.Count > 1)
                        {
                            violators.Add(t);
                            return;
                        }
                    }
                    boundaryFaces.Add((fa, fb, fc));
                }

                CheckFace(tet.B, tet.C, tet.D, tet.A);
                CheckFace(tet.A, tet.C, tet.D, tet.B);
                CheckFace(tet.A, tet.B, tet.D, tet.C);
                CheckFace(tet.A, tet.B, tet.C, tet.D);
            }

            if (violators.Count == 0 || inCavity.Count <= 1)
                break;
            foreach (int t in violators)
                if (inCavity.Count > 1)
                    inCavity.Remove(t);
        }

        foreach (int t in inCavity)
            RemoveTet(t);
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
        return true;
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
        if (GeometricPredicates.Orient3D(_points[a], _points[b], _points[c], _points[d]) < 0)
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
