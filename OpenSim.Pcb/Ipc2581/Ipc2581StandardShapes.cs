using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// Closed-form polygon generators for the full IPC-2581B <c>EntryStandard</c> shape
/// family, in LOCAL coordinates (centered on the flash location; placement transform is
/// applied at flash time). Curved boundaries tessellate at the shared
/// <see cref="PolyShapeReader.ChordTolerance"/> so IPC pads match Gerber pad quality.
/// A shape may be several DISJOINT copper pieces (a Thermal's annulus segments, a
/// Butterfly's opposite quadrants, a Moire's rings), hence the list return; pieces may
/// carry holes (a Donut).
/// </summary>
public static class Ipc2581StandardShapes
{
    private const double Tol = PolyShapeReader.ChordTolerance;

    /// <summary>Rectangle with per-corner fillets. A disabled corner stays sharp — real
    /// exporters use the flags (KiCad writes all-false rounded rects; Cadence all-true).</summary>
    public static Polygon2 RectRound(double w, double h, double r,
        bool upperLeft, bool upperRight, bool lowerLeft, bool lowerRight)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        if (r <= 0 || !(upperLeft || upperRight || lowerLeft || lowerRight))
            return CenteredRect(w, h);

        double hw = w / 2, hh = h / 2;
        var ring = new List<Point2> { new(-hw + (lowerLeft ? r : 0), -hh) };
        Corner(ring, new Point2(hw, -hh), new Point2(-r, 0), new Point2(0, r), lowerRight, r);
        Corner(ring, new Point2(hw, hh), new Point2(0, -r), new Point2(-r, 0), upperRight, r);
        Corner(ring, new Point2(-hw, hh), new Point2(r, 0), new Point2(0, -r), upperLeft, r);
        Corner(ring, new Point2(-hw, -hh), new Point2(0, r), new Point2(r, 0), lowerLeft, r);
        return CloseRing(ring);

        // Walks one corner CCW: approach point, then either the fillet arc or the sharp
        // vertex. <paramref name="inDir"/>/<paramref name="outDir"/> are the offsets from
        // the sharp corner to the arc's start/end; the arc center is their sum's corner.
        static void Corner(List<Point2> ring, Point2 corner, Point2 inDir, Point2 outDir,
            bool rounded, double r)
        {
            if (!rounded) { ring.Add(corner); return; }
            var from = corner + inDir;
            var to = corner + outDir;
            var center = corner + inDir + outDir;
            if ((ring[^1] - from).Length > 1e-15) ring.Add(from);
            PolyShapeReader.AppendArc(ring, from, to, center, clockwise: false);
        }
    }

    /// <summary>Rectangle with per-corner 45° chamfer cuts of leg length <paramref name="c"/>.</summary>
    public static Polygon2 RectCham(double w, double h, double c,
        bool upperLeft, bool upperRight, bool lowerLeft, bool lowerRight)
    {
        c = Math.Min(c, Math.Min(w, h) / 2);
        if (c <= 0 || !(upperLeft || upperRight || lowerLeft || lowerRight))
            return CenteredRect(w, h);

        double hw = w / 2, hh = h / 2;
        var ring = new List<Point2>();
        void Corner(Point2 corner, Point2 inDir, Point2 outDir, bool cut)
        {
            if (!cut) { ring.Add(corner); return; }
            ring.Add(corner + inDir);
            ring.Add(corner + outDir);
        }
        Corner(new Point2(-hw, -hh), new Point2(0, c), new Point2(c, 0), lowerLeft);
        Corner(new Point2(hw, -hh), new Point2(-c, 0), new Point2(0, c), lowerRight);
        Corner(new Point2(hw, hh), new Point2(0, -c), new Point2(-c, 0), upperRight);
        Corner(new Point2(-hw, hh), new Point2(c, 0), new Point2(0, -c), upperLeft);
        return CloseRing(ring);
    }

    /// <summary>Corner-defined rectangle — NOT centered: the coordinates are offsets from
    /// the flash location, so an asymmetric pad stays asymmetric under rotation/mirror.</summary>
    public static Polygon2 RectCorner(double llx, double lly, double urx, double ury) =>
        new(new[]
        {
            new Point2(llx, lly), new Point2(urx, lly),
            new Point2(urx, ury), new Point2(llx, ury)
        });

    /// <summary>Rhombus with axis-aligned diagonals w (horizontal) and h (vertical).</summary>
    public static Polygon2 Diamond(double w, double h) =>
        new(new[] { new Point2(w / 2, 0), new Point2(0, h / 2), new Point2(-w / 2, 0), new Point2(0, -h / 2) });

    /// <summary>Isoceles triangle, base down, bounding box centered on the flash point.</summary>
    public static Polygon2 Triangle(double baseWidth, double height) =>
        new(new[]
        {
            new Point2(-baseWidth / 2, -height / 2),
            new Point2(baseWidth / 2, -height / 2),
            new Point2(0, height / 2)
        });

    /// <summary>Axis-aligned ellipse with overall width w and height h.</summary>
    public static Polygon2 Ellipse(double w, double h)
    {
        int n = ApertureShapes.SegmentCount(Math.Max(w, h) / 2, Tol);
        var ring = new Point2[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            ring[i] = new Point2(w / 2 * Math.Cos(a), h / 2 * Math.Sin(a));
        }
        return new Polygon2(ring);
    }

    /// <summary>Regular hexagon; <paramref name="length"/> is the overall (vertex-to-vertex)
    /// size, a vertex on +x — the circle-diameter convention the donut variants share.</summary>
    public static Polygon2 Hexagon(double length) =>
        new(ApertureShapes.RegularPolygon(new Point2(0, 0), length / 2, 6, 0));

    /// <summary>Regular octagon; <paramref name="length"/> is the across-flats (bounding box)
    /// size with flats facing the axes — the shape octagonal pads are drawn as.</summary>
    public static Polygon2 Octagon(double length) =>
        new(ApertureShapes.RegularPolygon(new Point2(0, 0), length / 2 / Math.Cos(Math.PI / 8), 8, 22.5));

    /// <summary>Two opposite filled quadrants (1st and 3rd) of a circle of the given
    /// diameter, or of a square of the given side — two disjoint pieces meeting at the
    /// flash point.</summary>
    public static IReadOnlyList<Polygon2> Butterfly(bool round, double size)
    {
        if (!round)
        {
            double s = size / 2;
            return new[]
            {
                new Polygon2(new[] { new Point2(0, 0), new Point2(s, 0), new Point2(s, s), new Point2(0, s) }),
                new Polygon2(new[] { new Point2(0, 0), new Point2(-s, 0), new Point2(-s, -s), new Point2(0, -s) }),
            };
        }
        double r = size / 2;
        return new[] { Quarter(r, 0), Quarter(r, Math.PI) };

        static Polygon2 Quarter(double r, double startAngle)
        {
            var ring = new List<Point2>
            {
                new(0, 0),
                new(r * Math.Cos(startAngle), r * Math.Sin(startAngle))
            };
            PolyShapeReader.AppendArc(ring, ring[1],
                new Point2(r * Math.Cos(startAngle + Math.PI / 2), r * Math.Sin(startAngle + Math.PI / 2)),
                new Point2(0, 0), clockwise: false);
            return new Polygon2(ring);
        }
    }

    /// <summary>An annular ring: the outer shape (ROUND/SQUARE/HEXAGON/OCTAGON) with the
    /// concentric inner opening as a real hole — the exact copper footprint.</summary>
    public static Polygon2 Donut(string shape, double outerDiameter, double innerDiameter)
    {
        var outer = DonutRing(shape, outerDiameter);
        if (innerDiameter <= 0) return new Polygon2(outer);
        return new Polygon2(outer, new[] { DonutRing(shape, innerDiameter) });
    }

    private static IReadOnlyList<Point2> DonutRing(string shape, double diameter) =>
        shape.ToUpperInvariant() switch
        {
            "SQUARE" => CenteredRect(diameter, diameter).Outer,
            "HEXAGON" => Hexagon(diameter).Outer,
            "OCTAGON" => Octagon(diameter).Outer,
            _ => ApertureShapes.Circle(new Point2(0, 0), diameter / 2, Tol),
        };

    /// <summary>
    /// A thermal-relief footprint: the <see cref="Donut"/> annulus broken by
    /// <paramref name="spokeCount"/> radial air gaps of width <paramref name="gap"/>,
    /// the first centered at <paramref name="spokeStartAngleDeg"/> — the copper is the
    /// remaining arc segments (computed by exact polygon boolean, one piece per segment).
    /// </summary>
    public static IReadOnlyList<Polygon2> Thermal(string shape, double outerDiameter,
        double innerDiameter, int spokeCount, double gap, double spokeStartAngleDeg)
    {
        var annulus = Donut(shape, outerDiameter, innerDiameter);
        if (spokeCount <= 0 || gap <= 0) return new[] { annulus };

        // Each cut is a half-strip from the center outward (a full-crossing rectangle
        // would cut the opposite side too, doubling the gap count).
        var cuts = new List<IReadOnlyList<Point2>>();
        for (int k = 0; k < spokeCount; k++)
        {
            double a = (spokeStartAngleDeg + 360.0 * k / spokeCount) * Math.PI / 180;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            double len = outerDiameter;                          // safely past the rim
            Point2 Local(double x, double y) => new(x * cos - y * sin, x * sin + y * cos);
            cuts.Add(new[]
            {
                Local(0, -gap / 2), Local(len, -gap / 2),
                Local(len, gap / 2), Local(0, gap / 2)
            });
        }
        return new ClipperPolygonOps().Difference(new[] { annulus }, cuts);
    }

    /// <summary>
    /// A moiré alignment target: <paramref name="ringNumber"/> concentric annular rings
    /// stepping inward from <paramref name="diameter"/>, plus a crosshair of two lines
    /// (length × width) at <paramref name="lineAngleDeg"/> and +90° — unioned into one
    /// copper set because the crosshair overlaps the rings.
    /// </summary>
    public static IReadOnlyList<Polygon2> Moire(double diameter, double ringWidth,
        double ringGap, int ringNumber, double lineWidth, double lineLength, double lineAngleDeg)
    {
        var rings = new List<IReadOnlyList<Point2>>();
        double outer = diameter / 2;
        for (int i = 0; i < Math.Max(1, ringNumber) && outer > 0; i++)
        {
            double inner = Math.Max(0, outer - ringWidth);
            foreach (var ring in Polygon2.OrientedRings(Donut("ROUND", outer * 2, inner * 2)))
                rings.Add(ring);
            outer = inner - ringGap;
        }
        if (lineWidth > 0 && lineLength > 0)
        {
            for (int k = 0; k < 2; k++)
            {
                double a = (lineAngleDeg + 90.0 * k) * Math.PI / 180;
                double cos = Math.Cos(a), sin = Math.Sin(a);
                Point2 Local(double x, double y) => new(x * cos - y * sin, x * sin + y * cos);
                rings.Add(new[]
                {
                    Local(-lineLength / 2, -lineWidth / 2), Local(lineLength / 2, -lineWidth / 2),
                    Local(lineLength / 2, lineWidth / 2), Local(-lineLength / 2, lineWidth / 2)
                });
            }
        }
        return new ClipperPolygonOps().Union(rings);
    }

    private static Polygon2 CenteredRect(double w, double h) =>
        new(new[]
        {
            new Point2(-w / 2, -h / 2), new Point2(w / 2, -h / 2),
            new Point2(w / 2, h / 2), new Point2(-w / 2, h / 2)
        });

    /// <summary>Drops a duplicated closing point (arc walks land exactly on the start).</summary>
    private static Polygon2 CloseRing(List<Point2> ring)
    {
        if (ring.Count >= 2 && (ring[^1] - ring[0]).Length < 1e-15)
            ring.RemoveAt(ring.Count - 1);
        return new Polygon2(ring);
    }
}
