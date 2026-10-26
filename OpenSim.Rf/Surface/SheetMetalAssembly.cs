using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Surface;

/// <summary>One flat rectangular part of a sheet-metal assembly, axis-aligned: exactly one of
/// the three extents is zero (a plate in a plane of constant x, y or z).</summary>
public sealed record SheetRectangle(string Name, Vector3D Min, Vector3D Max);

/// <summary>A delta-gap port along a straight line across a part (for example across a feed
/// strip just above the ground plate); <see cref="Direction"/> is the positive current
/// direction through the gap.</summary>
public sealed record SheetPortLine(string Name, Vector3D From, Vector3D To, Vector3D Direction);

public sealed record SheetAssemblyResult(SurfaceStructure Structure, IReadOnlyList<NamedSurfacePort> Ports,
    IReadOnlyList<string> Notes);

/// <summary>
/// The air model of a finite-ground antenna: plates and strips in free space, the ground among
/// them as meshed metal. Every part is an axis-aligned rectangle, and all of them are meshed on
/// ONE grid of x, y and z lines that contains every part's edges and every port line, so parts
/// meet vertex for vertex wherever they touch — a strip standing on a plate shares the plate's
/// nodes along its foot, and the edge it stands on becomes a junction edge
/// (<see cref="SurfaceStructure.Assembly"/>). Intervals longer than the element size are
/// divided evenly, or graded from the part edges and port lines when a minimum element size is
/// given (see <see cref="Build"/>).
///
/// <para>No dielectric: a patch over a finite ground, a PIFA with its shorting wall, or a
/// monopole on a finite plate is solved in air. A part with a cut-out is several rectangles.</para>
/// </summary>
public static class SheetMetalAssembly
{
    public static IReadOnlyList<string> Assumptions { get; } = new[]
    {
        "Free space: no dielectric anywhere; the ground is a finite meshed plate.",
        "Perfect conductors of zero thickness; rectangular axis-aligned parts on one shared grid.",
        "Where three or more parts meet along an edge, the current divides between them (a junction edge carries one basis per extra part).",
        "Ports are delta gaps along a mesh line across a part."
    };

    /// <param name="minEdgeLength">When given, grid lines are graded: spacing starts at this value
    /// on every part edge and port end and grows by <paramref name="growth"/> per element away from
    /// it, up to <paramref name="maxEdgeLength"/> — fine where a strip lands on a plate, coarse
    /// across the open plate. The grading is the same at both ends of every interval and the same
    /// for every interval long enough to hold it: the first three cells at each end are exactly the
    /// ramp's (hMin, then hMin + (growth − 1) × distance), and only the cells between them are
    /// stretched to land on the far end — so two lines of different length carry identical cells
    /// next to their ports (TC-1, 2026-10-25: the far-end remainder cell used to depend on the
    /// interval's length, and a two-length calibration read it as a difference between the lines'
    /// ports). No cell is wider than <paramref name="maxEdgeLength"/> except where an interval's
    /// remainder under half a cell is absorbed by its interior cells, which may then exceed it by
    /// up to a quarter. <paramref name="growth"/> must be 1 or more and the minimum positive.</param>
    /// <param name="imageGround">An infinite ground plane instead of a meshed one (strips standing
    /// on it end in half-RWGs into their image) — the reference against which a finite ground
    /// plate's own effect is measured.</param>
    public static SheetAssemblyResult Build(IReadOnlyList<SheetRectangle> parts,
        IReadOnlyList<SheetPortLine> ports, double maxEdgeLength, int maxUnknowns = 3000,
        double? minEdgeLength = null, double growth = 1.5, GroundPlane? imageGround = null)
    {
        if (parts.Count == 0) throw new ArgumentException("No parts.", nameof(parts));
        if (!(maxEdgeLength > 0)) throw new ArgumentOutOfRangeException(nameof(maxEdgeLength));
        double scale = 0;
        foreach (var p in parts)
        {
            if (p.Max.X < p.Min.X || p.Max.Y < p.Min.Y || p.Max.Z < p.Min.Z)
                throw new ArgumentException($"Part '{p.Name}': Max must not be below Min.", nameof(parts));
            int flat = (p.Max.X == p.Min.X ? 1 : 0) + (p.Max.Y == p.Min.Y ? 1 : 0) + (p.Max.Z == p.Min.Z ? 1 : 0);
            if (flat != 1)
                throw new ArgumentException($"Part '{p.Name}' must be flat in exactly one axis.", nameof(parts));
            scale = Math.Max(scale, (p.Max - p.Min).Length);
        }
        double tol = 1e-9 * scale;
        if (minEdgeLength is { } given && !(given > 0))
            throw new ArgumentException("The minimum element size must be positive.", nameof(minEdgeLength));
        if (!(growth >= 1))
            throw new ArgumentException("The grading growth must be 1 or more.", nameof(growth));

        // Grid lines per axis: every part edge and port end, then each gap split evenly.
        double[] Lines(Func<Vector3D, double> axis)
        {
            var raw = new List<double>();
            foreach (var p in parts) { raw.Add(axis(p.Min)); raw.Add(axis(p.Max)); }
            foreach (var port in ports) { raw.Add(axis(port.From)); raw.Add(axis(port.To)); }
            raw.Sort();
            var unique = new List<double>();
            foreach (double v in raw)
                if (unique.Count == 0 || v - unique[^1] > tol) unique.Add(v);
            var lines = new List<double> { unique[0] };
            for (int i = 1; i < unique.Count; i++)
            {
                double gap = unique[i] - unique[i - 1];
                if (minEdgeLength is { } hMin && hMin < maxEdgeLength)
                {
                    double at = unique[i - 1];
                    foreach (double step in GradedSteps(gap, hMin, maxEdgeLength, growth))
                    {
                        at += step;
                        lines.Add(at);
                    }
                    lines.RemoveAt(lines.Count - 1);     // the far end is added below, exactly
                }
                else
                {
                    int n = Math.Max(1, (int)Math.Ceiling(gap / maxEdgeLength - 1e-9));
                    for (int k = 1; k < n; k++) lines.Add(unique[i - 1] + gap * k / n);
                }
                lines.Add(unique[i]);
            }
            return lines.ToArray();
        }
        var xs = Lines(v => v.X);
        var ys = Lines(v => v.Y);
        var zs = Lines(v => v.Z);

        int Index(double[] lines, double value)
        {
            int i = Array.BinarySearch(lines, value);
            if (i >= 0) return i;
            i = ~i;
            if (i < lines.Length && Math.Abs(lines[i] - value) <= tol) return i;
            if (i > 0 && Math.Abs(lines[i - 1] - value) <= tol) return i - 1;
            throw new InvalidOperationException($"{value} is not a grid line.");
        }

        var vertexIndex = new Dictionary<(int, int, int), int>();
        var vertices = new List<Vector3D>();
        int Vertex(int ix, int iy, int iz)
        {
            if (vertexIndex.TryGetValue((ix, iy, iz), out int v)) return v;
            vertexIndex[(ix, iy, iz)] = vertices.Count;
            vertices.Add(new Vector3D(xs[ix], ys[iy], zs[iz]));
            return vertices.Count - 1;
        }

        var triangles = new List<(int, int, int)>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var p in parts)
        {
            int x0 = Index(xs, p.Min.X), x1 = Index(xs, p.Max.X);
            int y0 = Index(ys, p.Min.Y), y1 = Index(ys, p.Max.Y);
            int z0 = Index(zs, p.Min.Z), z1 = Index(zs, p.Max.Z);
            // (u, v) span the part's plane; w is fixed.
            Func<int, int, (int, int, int)> at;
            int u0, u1, v0, v1;
            if (x0 == x1) { (u0, u1, v0, v1) = (y0, y1, z0, z1); at = (u, v) => (x0, u, v); }
            else if (y0 == y1) { (u0, u1, v0, v1) = (x0, x1, z0, z1); at = (u, v) => (u, y0, v); }
            else { (u0, u1, v0, v1) = (x0, x1, y0, y1); at = (u, v) => (u, v, z0); }
            for (int u = u0; u < u1; u++)
                for (int v = v0; v < v1; v++)
                {
                    int Get(int du, int dv) { var (a, b, c) = at(u + du, v + dv); return Vertex(a, b, c); }
                    int p00 = Get(0, 0), p10 = Get(1, 0), p11 = Get(1, 1), p01 = Get(0, 1);
                    var pair = ((u + v) % 2 == 0)
                        ? new[] { (p00, p10, p11), (p00, p11, p01) }
                        : new[] { (p00, p10, p01), (p10, p11, p01) };
                    foreach (var tri in pair)
                    {
                        var key = Sorted(tri);
                        if (!seen.Add(key))
                            throw new ArgumentException(
                                $"Part '{p.Name}' overlaps another part in the same plane; split the shape into non-overlapping rectangles.",
                                nameof(parts));
                        triangles.Add(tri);
                    }
                }
        }

        var structure = SurfaceStructure.Assembly(vertices, triangles, imageGround);
        if (structure.BasisCount > maxUnknowns)
            throw new InvalidOperationException(
                $"{structure.BasisCount} unknowns exceed the {maxUnknowns} cap (dense LU) — use a larger element size.");

        // Ports: every basis whose edge lies on the port line.
        var named = new List<NamedSurfacePort>();
        foreach (var port in ports)
        {
            var axis = port.To - port.From;
            double length = axis.Length;
            if (length <= tol) throw new ArgumentException($"Port '{port.Name}' has zero length.", nameof(ports));
            var dir = axis / length;
            bool OnLine(Vector3D q)
            {
                var d = q - port.From;
                double t = Vector3D.Dot(d, dir);
                return t >= -tol && t <= length + tol && (d - dir * t).Length <= tol;
            }
            var bases = new List<int>();
            var edgesUsed = new HashSet<(int, int)>();
            for (int e = 0; e < structure.Edges.Count; e++)
            {
                var edge = structure.Edges[e];
                if (!OnLine(structure.Vertices[edge.V1]) || !OnLine(structure.Vertices[edge.V2])) continue;
                if (!edgesUsed.Add((edge.V1, edge.V2)))
                    throw new ArgumentException(
                        $"Port '{port.Name}' lies on a junction edge; put the gap on a single part.", nameof(ports));
                bases.Add(e);
            }
            if (bases.Count == 0)
                throw new ArgumentException($"Port '{port.Name}' crosses no interior mesh edge.", nameof(ports));
            named.Add(new NamedSurfacePort(port.Name, new SurfacePort(bases, port.Direction)));
        }

        var notes = new List<string>
        {
            $"{vertices.Count} nodes, {triangles.Count} triangles, {structure.BasisCount} unknowns, "
            + $"{structure.JunctionEdgeCount} junction edge(s)."
        };
        return new SheetAssemblyResult(structure, named, notes);
    }

    private static (int, int, int) Sorted((int A, int B, int C) t)
    {
        int a = t.A, b = t.B, c = t.C;
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// <summary>
    /// The cells of one graded interval of length <paramref name="gap"/>, summing to it exactly:
    /// a ramp from each end — hMin, then min(hMax, hMin + (growth − 1) × distance from that end) —
    /// mirrored, with the middle filled by cells of about the ramp's next size. The first three
    /// cells at each end are exactly the ramp's; the stretch that lands the sum on the far end is
    /// spread over the cells between them only, so every interval long enough for six ramp cells
    /// and something between them carries the same cells next to its ends whatever its length.
    /// An interval with nothing between its six ramp cells keeps two exact cells at each end and
    /// stretches the third pair; a shorter one keeps as many exact end cells as fit and stretches
    /// the rest; one no longer than the maximum (and than two minimum cells) is one cell.
    /// </summary>
    internal static IReadOnlyList<double> GradedSteps(double gap, double hMin, double hMax, double growth)
    {
        if (gap <= Math.Min(2 * hMin, hMax)) return new[] { gap };
        var ramp = new List<double>();
        double u = 0, sum = 0;
        while (true)
        {
            double size = Math.Min(hMax, hMin + (growth - 1) * u);
            if (2 * (sum + size) > gap) break;
            ramp.Add(size);
            sum += size;
            u += size;
        }
        double middle = gap - 2 * sum;
        double target = Math.Min(hMax, hMin + (growth - 1) * u);
        // Fill the middle with cells of about the ramp's next size: as many as fit by rounding
        // (one more when that would leave them wider than 1.25× the target), sized to the
        // remainder exactly; a remainder under half a cell is absorbed by the interior cells
        // instead of becoming a sliver.
        int fillers = (int)Math.Round(middle / target);
        if (fillers > 0) fillers = Math.Max(fillers, (int)Math.Ceiling(middle / hMax - 1e-9));   // never wider than the maximum
        var steps = new List<double>(2 * ramp.Count + fillers);
        steps.AddRange(ramp);
        for (int k = 0; k < fillers; k++) steps.Add(middle / fillers);
        for (int k = ramp.Count - 1; k >= 0; k--) steps.Add(ramp[k]);

        // Stretch only the interior (beyond the exact end cells) onto what the ends leave; an
        // interval too short to have an interior is stretched as a whole.
        int exact = Math.Min(3, ramp.Count);
        double interior = steps.Skip(exact).Take(steps.Count - 2 * exact).Sum();
        if (interior <= 0 && exact > 0)
        {
            // Nothing between the exact cells: give up the innermost exact pair to the stretch.
            exact--;
            interior = steps.Skip(exact).Take(steps.Count - 2 * exact).Sum();
        }
        if (interior <= 0) { exact = 0; interior = steps.Sum(); }
        double ends = 0;
        for (int k = 0; k < exact; k++) ends += 2 * ramp[k];
        double stretch = (gap - ends) / interior;
        for (int k = exact; k < steps.Count - exact; k++) steps[k] *= stretch;
        return steps;
    }
}
