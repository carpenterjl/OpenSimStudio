using OpenSim.Core.Geometry2D;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>The axis a flat part's plane is normal to.</summary>
public enum SheetNormal { X, Y, Z }

/// <summary>A flat part of any outline (holes allowed) in the plane where the coordinate along
/// <see cref="Normal"/> equals <see cref="Offset"/>. The outline's (u, v) are the plane's other two
/// coordinates in order: (y, z) for an x-plane, (x, z) for a y-plane, (x, y) for a z-plane.</summary>
public sealed record SheetPart(string Name, SheetNormal Normal, double Offset, Polygon2 Outline)
{
    internal Vector3D To3D(Point2 p) => Normal switch
    {
        SheetNormal.X => new Vector3D(Offset, p.X, p.Y),
        SheetNormal.Y => new Vector3D(p.X, Offset, p.Y),
        _ => new Vector3D(p.X, p.Y, Offset)
    };

    internal Point2 To2D(Vector3D p) => Normal switch
    {
        SheetNormal.X => new Point2(p.Y, p.Z),
        SheetNormal.Y => new Point2(p.X, p.Z),
        _ => new Point2(p.X, p.Y)
    };

    internal double Across(Vector3D p) => Normal switch
    {
        SheetNormal.X => p.X,
        SheetNormal.Y => p.Y,
        _ => p.Z
    };

    /// <summary>A rectangle as a part (the <see cref="SheetRectangle"/> form).</summary>
    public static SheetPart FromRectangle(SheetRectangle r)
    {
        Polygon2 Rect(double u0, double v0, double u1, double v1) =>
            new(new[] { new Point2(u0, v0), new Point2(u1, v0), new Point2(u1, v1), new Point2(u0, v1) });
        if (r.Max.X == r.Min.X) return new(r.Name, SheetNormal.X, r.Min.X, Rect(r.Min.Y, r.Min.Z, r.Max.Y, r.Max.Z));
        if (r.Max.Y == r.Min.Y) return new(r.Name, SheetNormal.Y, r.Min.Y, Rect(r.Min.X, r.Min.Z, r.Max.X, r.Max.Z));
        return new(r.Name, SheetNormal.Z, r.Min.Z, Rect(r.Min.X, r.Min.Y, r.Max.X, r.Max.Y));
    }
}

/// <summary>
/// FU-36 — the air model with parts of any outline, each meshed on its own instead of on one
/// shared grid. <see cref="SheetMetalAssembly"/> puts every part on ONE tensor grid of x, y and z
/// lines so that parts meet vertex for vertex — which also carries every fine line across every
/// part in its plane (a 1 mm feed strip refines a 100 mm plate along its whole width). Here the
/// meeting places are found first and given their vertices once, and each part is triangulated
/// around them:
///  • a SEAM is where a part's boundary edge lies on another part (a strip standing on a plate:
///    its foot is a line inside the plate), or where two coplanar parts share boundary; a port line
///    is a seam on its part;
///  • every boundary edge and seam is cut at every feature point on it (corners, seam ends) and
///    each piece resampled by one canonical rule (the piece's lexicographically first end first),
///    so a piece shared by two parts gets the same vertices in both;
///  • each part is then a constrained Delaunay triangulation of its resampled boundary, its seams
///    as interior constraints, and an interior fill whose spacing follows the size field.
/// The size field is the grading of <see cref="SheetMetalAssembly"/> measured from the seams and
/// ports in 3D — min(h_max, h_min + (g − 1)·distance) — so fine elements stay near where parts meet
/// and the open plate stays coarse. Vertices are shared through their 3D coordinates; junction
/// edges (three or more triangles) carry one basis per extra part, as on the grid.
/// </summary>
public static class SheetPartsAssembly
{
    public static IReadOnlyList<string> Assumptions { get; } = new[]
    {
        "Free space: no dielectric anywhere; the ground is a finite meshed plate.",
        "Perfect conductors of zero thickness; flat parts of any outline in axis-aligned planes, each meshed on its own and joined vertex for vertex along the lines where they meet.",
        "Where three or more parts meet along an edge, the current divides between them (a junction edge carries one basis per extra part).",
        "Ports are delta gaps along a line across a part."
    };

    public static SheetAssemblyResult Build(IReadOnlyList<SheetPart> parts, IReadOnlyList<SheetPortLine> ports,
        double maxEdgeLength, int maxUnknowns = 3000, double? minEdgeLength = null, double growth = 1.5,
        GroundPlane? imageGround = null)
    {
        if (parts.Count == 0) throw new ArgumentException("No parts.", nameof(parts));
        if (!(maxEdgeLength > 0)) throw new ArgumentOutOfRangeException(nameof(maxEdgeLength));
        double hMax = maxEdgeLength, hMin = Math.Min(minEdgeLength ?? maxEdgeLength, maxEdgeLength);
        double scale = 0;
        foreach (var p in parts)
            foreach (var q in p.Outline.Outer) scale = Math.Max(scale, Math.Abs(q.X) + Math.Abs(q.Y) + Math.Abs(p.Offset));
        double tol = 1e-9 * Math.Max(scale, 1e-12);

        // Boundary segments of every part, in 3D.
        var boundary = parts.Select(p => Polygon2.OrientedRings(p.Outline)
            .SelectMany(ring => ring.Select((a, i) => (A: p.To3D(a), B: p.To3D(ring[(i + 1) % ring.Count])))).ToList()).ToList();
        var inside = parts.Select(p => new PolygonSetIndex(new[] { p.Outline })).ToList();

        bool OnPart(int k, Vector3D x) => Math.Abs(parts[k].Across(x) - parts[k].Offset) <= tol
            && (inside[k].Contains(parts[k].To2D(x)) || OnBoundary(k, x));
        bool OnBoundary(int k, Vector3D x)
        {
            foreach (var (a, b) in boundary[k])
                if (DistanceToSegment(x, a, b) <= tol) return true;
            return false;
        }

        // Seams: pieces of a part's boundary lying on another part, and port lines.
        var seams = new List<(Vector3D A, Vector3D B)>();
        for (int k = 0; k < parts.Count; k++)
            foreach (var (a, b) in boundary[k])
                for (int other = 0; other < parts.Count; other++)
                {
                    if (other == k) continue;
                    if (Math.Abs(parts[other].Across(a) - parts[other].Offset) > tol
                        || Math.Abs(parts[other].Across(b) - parts[other].Offset) > tol) continue;
                    foreach (var piece in ClipToPart(other, a, b)) seams.Add(piece);
                }
        foreach (var port in ports) seams.Add((port.From, port.To));

        // Feature points: corners, seam ends, port ends.
        var features = new List<Vector3D>();
        foreach (var list in boundary) foreach (var (a, _) in list) features.Add(a);
        foreach (var (a, b) in seams) { features.Add(a); features.Add(b); }

        double Size(Vector3D x)
        {
            if (hMin >= hMax || seams.Count == 0) return hMax;
            double d = double.MaxValue;
            foreach (var (a, b) in seams) d = Math.Min(d, DistanceToSegment(x, a, b));
            return Math.Min(hMax, hMin + (growth - 1) * d);
        }

        // One canonical resampling per piece: points strictly between its ends.
        List<Vector3D> Resample(Vector3D a, Vector3D b)
        {
            bool flip = Compare(a, b) > 0;
            var (p, q) = flip ? (b, a) : (a, b);
            double length = (q - p).Length;
            var steps = new List<double>();
            double u = 0;
            while (u < length - 1e-9 * length)
            {
                var at = p + (q - p) * (u / length);
                double size = Math.Max(Size(at), 1e-3 * hMin);
                steps.Add(size);
                u += size;
            }
            double stretch = length / Math.Max(steps.Sum(), 1e-300);
            var points = new List<Vector3D>();
            double s = 0;
            for (int i = 0; i + 1 < steps.Count; i++)
            {
                s += steps[i] * stretch;
                points.Add(p + (q - p) * (s / length));
            }
            if (flip) points.Reverse();
            return points;
        }
        // A segment cut at every feature point on it, each piece resampled: the full point chain.
        List<Vector3D> Chain(Vector3D a, Vector3D b)
        {
            var dir = b - a;
            double length = dir.Length;
            var cuts = new List<double> { 0, 1 };
            foreach (var f in features)
            {
                double t = Vector3D.Dot(f - a, dir) / (length * length);
                if (t > 1e-12 && t < 1 - 1e-12 && DistanceToSegment(f, a, b) <= tol) cuts.Add(t);
            }
            cuts.Sort();
            var chain = new List<Vector3D> { a };
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                if (cuts[i + 1] - cuts[i] < 1e-12) continue;
                var from = a + dir * cuts[i];
                var to = a + dir * cuts[i + 1];
                chain.AddRange(Resample(from, to));
                chain.Add(to);
            }
            return chain;
        }

        var vertexIndex = new Dictionary<(long, long, long), int>();
        var vertices = new List<Vector3D>();
        int Vertex(Vector3D x)
        {
            // Quantized at 10·tol; the neighbouring cells are searched too, so two computations of one
            // point that straddle a cell edge still meet.
            double q = tol * 10;
            var key = ((long)Math.Round(x.X / q), (long)Math.Round(x.Y / q), (long)Math.Round(x.Z / q));
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (vertexIndex.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out int near)
                            && (vertices[near] - x).Length <= 2 * q)
                            return near;
            vertexIndex[key] = vertices.Count;
            vertices.Add(x);
            return vertices.Count - 1;
        }

        var triangles = new List<(int, int, int)>();
        var notes = new List<string>();
        for (int k = 0; k < parts.Count; k++)
        {
            var part = parts[k];
            var points = new List<Point2>();
            var global = new List<int>();
            var constraints = new List<(int, int)>();
            var local = new Dictionary<int, int>();
            int Local(Vector3D x)
            {
                int g = Vertex(x);
                if (local.TryGetValue(g, out int l)) return l;
                local[g] = points.Count;
                points.Add(part.To2D(vertices[g]));
                global.Add(g);
                return points.Count - 1;
            }
            var segments2D = new List<(Point2 A, Point2 B)>();
            void AddChain(Vector3D a, Vector3D b)
            {
                var chain = Chain(a, b);
                for (int i = 0; i + 1 < chain.Count; i++)
                {
                    int u = Local(chain[i]), w = Local(chain[i + 1]);
                    if (u != w) constraints.Add((u, w));
                    segments2D.Add((part.To2D(chain[i]), part.To2D(chain[i + 1])));
                }
            }
            foreach (var (a, b) in boundary[k]) AddChain(a, b);
            foreach (var (a, b) in seams)
                if (OnPart(k, a) && OnPart(k, b) && !OnBoundary(k, (a + b) * 0.5)) AddChain(a, b);

            // Interior fill following the size field: a fine lattice, each candidate kept only when
            // it clears the constraints and every kept point by its local size.
            var accepted = new List<Point2>();
            var bucket = new Dictionary<(int, int), List<Point2>>();
            double cell = hMin;
            (int, int) Cell(Point2 p) => ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell));
            var grid = new SegmentGrid(segments2D, 0.45 * hMax * (1 + 1e-9));
            double minU = points.Min(p => p.X), maxU = points.Max(p => p.X), minV = points.Min(p => p.Y), maxV = points.Max(p => p.Y);
            double spacing = 0.87 * hMin;
            int row = 0;
            for (double v = minV + spacing / 2; v < maxV; v += spacing, row++)
                for (double u = minU + spacing / 2 + (row % 2) * spacing / 2; u < maxU; u += spacing)
                {
                    var c = new Point2(u, v);
                    if (!inside[k].Contains(c)) continue;
                    double h = Size(part.To3D(c));
                    if (grid.AnyWithin(c, 0.45 * h)) continue;
                    int reach = (int)Math.Ceiling(0.87 * h / cell);
                    var (cx, cy) = Cell(c);
                    bool clear = true;
                    for (int dx = -reach; dx <= reach && clear; dx++)
                        for (int dy = -reach; dy <= reach && clear; dy++)
                            if (bucket.TryGetValue((cx + dx, cy + dy), out var list))
                                foreach (var o in list)
                                    if ((o - c).Length < 0.87 * h) { clear = false; break; }
                    if (!clear) continue;
                    accepted.Add(c);
                    if (!bucket.TryGetValue((cx, cy), out var mine)) bucket[(cx, cy)] = mine = new List<Point2>();
                    mine.Add(c);
                }
            foreach (var c in accepted) Local(part.To3D(c));

            var cdt = new Cdt2D();
            try { cdt.Triangulate(points, constraints); }
            catch (Exception ex) { throw new InvalidOperationException($"Part '{part.Name}': triangulation failed — {ex.Message}", ex); }
            int before = triangles.Count;
            foreach (var (a, b, c) in cdt.Triangles())
            {
                if (a >= global.Count || b >= global.Count || c >= global.Count)
                    throw new InvalidOperationException($"Part '{part.Name}': the triangulation added points; refine the part.");
                var centroid = new Point2((points[a].X + points[b].X + points[c].X) / 3, (points[a].Y + points[b].Y + points[c].Y) / 3);
                if (!inside[k].Contains(centroid)) continue;
                // The triangulator works on jittered points: three consecutive points of a straight
                // boundary can close a hull sliver that is flat in true coordinates. It is not metal.
                double area2 = Math.Abs((points[b].X - points[a].X) * (points[c].Y - points[a].Y)
                    - (points[c].X - points[a].X) * (points[b].Y - points[a].Y));
                if (area2 <= 1e-12 * hMin * hMin) continue;
                triangles.Add((global[a], global[b], global[c]));
            }
            if (triangles.Count == before) throw new InvalidOperationException($"Part '{part.Name}' produced no triangles.");
        }

        var structure = SurfaceStructure.Assembly(vertices, triangles, imageGround);
        if (structure.BasisCount > maxUnknowns)
            throw new InvalidOperationException(
                $"{structure.BasisCount} unknowns exceed the {maxUnknowns} cap (dense LU) — use a larger element size.");

        var named = new List<NamedSurfacePort>();
        foreach (var port in ports)
        {
            var axis = port.To - port.From;
            double length = axis.Length;
            if (length <= tol) throw new ArgumentException($"Port '{port.Name}' has zero length.", nameof(ports));
            var bases = new List<int>();
            var used = new HashSet<(int, int)>();
            for (int e = 0; e < structure.Edges.Count; e++)
            {
                var edge = structure.Edges[e];
                if (DistanceToSegment(structure.Vertices[edge.V1], port.From, port.To) > tol
                    || DistanceToSegment(structure.Vertices[edge.V2], port.From, port.To) > tol) continue;
                if (!used.Add((edge.V1, edge.V2)))
                    throw new ArgumentException($"Port '{port.Name}' lies on a junction edge; put the gap on a single part.", nameof(ports));
                bases.Add(e);
            }
            if (bases.Count == 0) throw new ArgumentException($"Port '{port.Name}' crosses no interior mesh edge.", nameof(ports));
            named.Add(new NamedSurfacePort(port.Name, new SurfacePort(bases, port.Direction)));
        }
        notes.Add($"{vertices.Count} nodes, {triangles.Count} triangles, {structure.BasisCount} unknowns, "
                  + $"{structure.JunctionEdgeCount} junction edge(s); {seams.Count} seam(s).");
        return new SheetAssemblyResult(structure, named, notes);

        IEnumerable<(Vector3D A, Vector3D B)> ClipToPart(int k, Vector3D a, Vector3D b)
        {
            // The parameter intervals of a→b inside part k's outline (in its plane).
            var pa = parts[k].To2D(a); var pb = parts[k].To2D(b);
            var ts = new List<double> { 0, 1 };
            foreach (var (s0, s1) in boundary[k])
            {
                var q0 = parts[k].To2D(s0); var q1 = parts[k].To2D(s1);
                double ex = q1.X - q0.X, ey = q1.Y - q0.Y, dx = pb.X - pa.X, dy = pb.Y - pa.Y;
                double det = dx * (-ey) - dy * (-ex);
                if (Math.Abs(det) < 1e-30) continue;
                double rx = q0.X - pa.X, ry = q0.Y - pa.Y;
                double t = (rx * (-ey) - ry * (-ex)) / det, w = (dx * ry - dy * rx) / det;
                if (t > 0 && t < 1 && w >= -1e-12 && w <= 1 + 1e-12) ts.Add(t);
            }
            ts.Sort();
            for (int i = 0; i + 1 < ts.Count; i++)
            {
                if (ts[i + 1] - ts[i] < 1e-12) continue;
                var mid = pa + (pb - pa) * (0.5 * (ts[i] + ts[i + 1]));
                if (inside[k].Contains(mid))
                    yield return (a + (b - a) * ts[i], a + (b - a) * ts[i + 1]);
            }
        }
    }

    private static int Compare(Vector3D a, Vector3D b) =>
        a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);

    private static double DistanceToSegment(Vector3D x, Vector3D a, Vector3D b)
    {
        var d = b - a;
        double len2 = Vector3D.Dot(d, d);
        double t = len2 == 0 ? 0 : Math.Clamp(Vector3D.Dot(x - a, d) / len2, 0, 1);
        return (x - (a + d * t)).Length;
    }
}
