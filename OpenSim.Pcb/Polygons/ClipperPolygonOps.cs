using Clipper2Lib;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Polygons;

/// <summary>
/// Polygon boolean/offset operations backed by Clipper2 (integer snap-rounded, robust
/// against the coincident-edge and self-touching cases Gerber strokes produce).
/// Clipper2 types never leave this class.
/// </summary>
public sealed class ClipperPolygonOps : IPolygonOps
{
    /// <summary>Integer grid: 1 unit = 1 nm. Robust snap-rounding at far below copper feature size.</summary>
    private const double Scale = 1e9;

    public IReadOnlyList<Polygon2> Union(IEnumerable<IReadOnlyList<Point2>> rings) =>
        Execute(ClipType.Union, ToPaths(rings), new Paths64());

    public IReadOnlyList<Polygon2> Difference(IEnumerable<Polygon2> subjects, IEnumerable<IReadOnlyList<Point2>> clips) =>
        Execute(ClipType.Difference, ToPaths(subjects.SelectMany(AllRings)), ToPaths(clips));

    public IReadOnlyList<Polygon2> Intersect(IEnumerable<Polygon2> subjects, IEnumerable<IReadOnlyList<Point2>> clips) =>
        Execute(ClipType.Intersection, ToPaths(subjects.SelectMany(AllRings)), ToPaths(clips));

    public IReadOnlyList<IReadOnlyList<Point2>> DifferenceRings(
        IEnumerable<IReadOnlyList<Point2>> subjects, IEnumerable<IReadOnlyList<Point2>> clips) =>
        Execute(ClipType.Difference, ToPaths(subjects), ToPaths(clips))
            .SelectMany(AllRings)   // enforced winding: outers CCW, holes CW
            .ToList();

    public IReadOnlyList<IReadOnlyList<Point2>> StrokeOpenPath(IReadOnlyList<Point2> path, double radius)
    {
        if (radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Stroke radius must be positive.");
        var open = new Paths64 { ToPath(path) };
        var inflated = Clipper.InflatePaths(open, radius * Scale, JoinType.Round, EndType.Round);
        return inflated.Select(FromPath).ToList();
    }

    /// <summary>
    /// Holes participate in the boolean as reverse-wound rings under NonZero fill.
    /// Winding is enforced (not trusted) so a mis-wound upstream hole can never turn
    /// into filled copper.
    /// </summary>
    private static IEnumerable<IReadOnlyList<Point2>> AllRings(Polygon2 polygon) =>
        Polygon2.OrientedRings(polygon);

    private static IReadOnlyList<Polygon2> Execute(ClipType op, Paths64 subjects, Paths64 clips)
    {
        var clipper = new Clipper64();
        clipper.AddSubject(subjects);
        if (clips.Count > 0)
            clipper.AddClip(clips);
        var tree = new PolyTree64();
        clipper.Execute(op, FillRule.NonZero, tree);

        var result = new List<Polygon2>();
        CollectOuters(tree, result);
        return result;
    }

    /// <summary>Walks the poly tree: outers at even depth, their direct children are holes.</summary>
    private static void CollectOuters(PolyPath64 node, List<Polygon2> result)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var outer = node[i];
            var holes = new List<IReadOnlyList<Point2>>();
            for (int j = 0; j < outer.Count; j++)
            {
                var hole = outer[j];
                holes.Add(FromPath(hole.Polygon!));
                // Islands inside holes are new outers.
                CollectOuters(hole, result);
            }
            result.Add(new Polygon2(FromPath(outer.Polygon!), holes));
        }
    }

    private static Paths64 ToPaths(IEnumerable<IReadOnlyList<Point2>> rings)
    {
        var paths = new Paths64();
        foreach (var ring in rings)
            paths.Add(ToPath(ring));
        return paths;
    }

    private static Path64 ToPath(IReadOnlyList<Point2> ring)
    {
        var path = new Path64(ring.Count);
        foreach (var p in ring)
            path.Add(new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale)));
        return path;
    }

    private static IReadOnlyList<Point2> FromPath(Path64 path) =>
        path.Select(p => new Point2(p.X / Scale, p.Y / Scale)).ToList();
}
