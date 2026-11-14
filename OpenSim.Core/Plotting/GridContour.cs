using OpenSim.Core.Geometry2D;

namespace OpenSim.Core.Plotting;

/// <summary>The iso-line of one level: unordered segments, each crossing one triangle.</summary>
public sealed record ContourLine(double Level, IReadOnlyList<(Point2 A, Point2 B)> Segments);

/// <summary>The region where the field lies between two levels, as convex polygons (one or none
/// per triangle of the grid).</summary>
public sealed record ContourBand(double Lower, double Upper, IReadOnlyList<Point2[]> Polygons);

/// <summary>
/// Line and filled contours of a <see cref="GridField"/>. Each grid cell is split along its
/// rising diagonal into two triangles and the field is taken as linear on each, the same
/// marching-triangles model <see cref="PostProcessing.IsoLineExtractor"/> uses on the mesh skin.
/// Triangles rather than marching squares because a linear triangle has exactly one answer:
/// there is no saddle case to disambiguate, and lines and bands are the same surface, so a line
/// at a band edge is the band's boundary to rounding.
/// </summary>
public static class GridContour
{
    /// <summary><paramref name="count"/> evenly spaced levels strictly inside (min, max), never the
    /// degenerate extremes themselves (the rule of <see cref="PostProcessing.IsoLineExtractor"/>).</summary>
    public static IReadOnlyList<double> InteriorLevels(double min, double max, int count)
    {
        if (!(max > min) || count <= 0) return Array.Empty<double>();
        var levels = new double[count];
        for (int k = 1; k <= count; k++) levels[k - 1] = min + k * (max - min) / (count + 1);
        return levels;
    }

    /// <summary>Round-number levels (<see cref="AxisTicks.Linear"/>) strictly inside (min, max),
    /// about <paramref name="target"/> of them: what a contour plot labels its lines with.</summary>
    public static IReadOnlyList<double> NiceLevels(double min, double max, int target)
    {
        if (!(max > min) || target <= 0) return Array.Empty<double>();
        return AxisTicks.Linear(min, max, target + 1).Where(t => t > min && t < max).ToArray();
    }

    /// <summary>The band edges for filled contours: min, then each level strictly inside, then max.</summary>
    public static IReadOnlyList<double> BandEdges(double min, double max, IReadOnlyList<double> levels)
    {
        var edges = new List<double> { min };
        foreach (double level in levels.OrderBy(l => l))
            if (level > edges[^1] && level < max) edges.Add(level);
        if (max > min) edges.Add(max);
        return edges;
    }

    /// <summary>Index k of the band [edges[k], edges[k+1]] holding <paramref name="value"/>, the top
    /// edge belonging to the last band; -1 outside or for a non-finite value.</summary>
    public static int BandIndex(double value, IReadOnlyList<double> edges)
    {
        if (!double.IsFinite(value) || edges.Count < 2 || value < edges[0] || value > edges[^1]) return -1;
        for (int k = edges.Count - 2; k >= 0; k--)
            if (value >= edges[k]) return k;
        return 0;
    }

    /// <summary>The iso-lines of <paramref name="field"/> at each of <paramref name="levels"/>.</summary>
    public static IReadOnlyList<ContourLine> Lines(GridField field, IReadOnlyList<double> levels)
    {
        var (min, max) = field.FiniteRange();
        // A vertex exactly on a level is nudged off it so a triangle always yields 0 or 2
        // crossings (the IsoLineExtractor rule).
        double epsilon = double.IsFinite(max - min) && max > min ? 1e-12 * (max - min) : 1e-300;
        var segments = new List<(Point2, Point2)>[levels.Count];
        for (int k = 0; k < levels.Count; k++) segments[k] = new List<(Point2, Point2)>();

        var p = new Point2[3];
        var s = new double[3];
        var hits = new Point2[3];
        foreach (var _ in Triangles(field, p, s))
            for (int k = 0; k < levels.Count; k++)
            {
                double level = levels[k];
                if ((s[0] < level && s[1] < level && s[2] < level) || (s[0] > level && s[1] > level && s[2] > level))
                    continue;
                int count = 0;
                for (int i = 0; i < 3; i++)
                {
                    int j = (i + 1) % 3;
                    double si = s[i] == level ? level + epsilon : s[i];
                    double sj = s[j] == level ? level + epsilon : s[j];
                    if ((si - level) * (sj - level) >= 0) continue;
                    double f = (level - si) / (sj - si);
                    hits[count++] = p[i] + (p[j] - p[i]) * f;
                }
                if (count == 2) segments[k].Add((hits[0], hits[1]));
            }

        var lines = new ContourLine[levels.Count];
        for (int k = 0; k < levels.Count; k++) lines[k] = new ContourLine(levels[k], segments[k]);
        return lines;
    }

    /// <summary>
    /// The filled bands between consecutive <paramref name="edges"/> (ascending). Each triangle is
    /// clipped against lower ≤ s ≤ upper by Sutherland-Hodgman on the linear field, so a band's
    /// polygons tile exactly the part of the grid where the interpolated field lies in it; over
    /// edges spanning the full range the bands tile every cell with data.
    /// </summary>
    public static IReadOnlyList<ContourBand> Bands(GridField field, IReadOnlyList<double> edges)
    {
        for (int k = 1; k < edges.Count; k++)
            if (!(edges[k] > edges[k - 1]))
                throw new ArgumentException($"Band edges must ascend strictly; edge {k} is {edges[k]} after {edges[k - 1]}.",
                    nameof(edges));
        int bands = Math.Max(0, edges.Count - 1);
        var polygons = new List<Point2[]>[bands];
        for (int k = 0; k < bands; k++) polygons[k] = new List<Point2[]>();

        var p = new Point2[3];
        var s = new double[3];
        var work = new List<(Point2 P, double S)>(8);
        var next = new List<(Point2 P, double S)>(8);
        foreach (var _ in Triangles(field, p, s))
        {
            double tmin = Math.Min(s[0], Math.Min(s[1], s[2])), tmax = Math.Max(s[0], Math.Max(s[1], s[2]));
            for (int k = 0; k < bands; k++)
            {
                double lo = edges[k], hi = edges[k + 1];
                if (tmax < lo || tmin > hi) continue;
                work.Clear();
                for (int i = 0; i < 3; i++) work.Add((p[i], s[i]));
                if (tmin < lo) { Clip(work, next, lo, keepAbove: true); (work, next) = (next, work); }
                if (tmax > hi) { Clip(work, next, hi, keepAbove: false); (work, next) = (next, work); }
                if (work.Count >= 3) polygons[k].Add(work.Select(w => w.P).ToArray());
            }
        }

        var result = new ContourBand[bands];
        for (int k = 0; k < bands; k++) result[k] = new ContourBand(edges[k], edges[k + 1], polygons[k]);
        return result;
    }

    /// <summary>Area of a simple polygon (shoelace), positive for either winding.</summary>
    public static double Area(IReadOnlyList<Point2> polygon)
    {
        double twice = 0;
        for (int i = 0; i < polygon.Count; i++)
            twice += Point2.Cross(polygon[i], polygon[(i + 1) % polygon.Count]);
        return Math.Abs(twice) / 2;
    }

    private static void Clip(List<(Point2 P, double S)> input, List<(Point2 P, double S)> output, double level, bool keepAbove)
    {
        output.Clear();
        bool Inside(double v) => keepAbove ? v >= level : v <= level;
        for (int i = 0; i < input.Count; i++)
        {
            var a = input[i];
            var b = input[(i + 1) % input.Count];
            bool ina = Inside(a.S), inb = Inside(b.S);
            if (ina) output.Add(a);
            if (ina != inb)
            {
                double f = (level - a.S) / (b.S - a.S);
                output.Add((a.P + (b.P - a.P) * f, level));
            }
        }
    }

    /// <summary>Fills <paramref name="p"/> and <paramref name="s"/> with each data-bearing triangle
    /// in turn: cell (r, c) gives (r,c)(r,c+1)(r+1,c+1) and (r,c)(r+1,c+1)(r+1,c).</summary>
    private static IEnumerable<int> Triangles(GridField field, Point2[] p, double[] s)
    {
        var v = field.Values;
        for (int r = 0; r < field.Rows - 1; r++)
        {
            double ya = field.YAt(r), yb = field.YAt(r + 1);
            for (int c = 0; c < field.Columns - 1; c++)
            {
                double xa = field.XAt(c), xb = field.XAt(c + 1);
                double s00 = v[r, c], s01 = v[r, c + 1], s10 = v[r + 1, c], s11 = v[r + 1, c + 1];
                if (!double.IsFinite(s00) || !double.IsFinite(s11)) continue;
                if (double.IsFinite(s01))
                {
                    p[0] = new Point2(xa, ya); p[1] = new Point2(xb, ya); p[2] = new Point2(xb, yb);
                    s[0] = s00; s[1] = s01; s[2] = s11;
                    yield return 0;
                }
                if (double.IsFinite(s10))
                {
                    p[0] = new Point2(xa, ya); p[1] = new Point2(xb, yb); p[2] = new Point2(xa, yb);
                    s[0] = s00; s[1] = s11; s[2] = s10;
                    yield return 1;
                }
            }
        }
    }
}
