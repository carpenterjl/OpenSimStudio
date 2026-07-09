using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Gerber;

/// <summary>Outline generation for standard apertures (flash shapes).</summary>
public static class ApertureShapes
{
    /// <summary>
    /// The flash outline of an aperture centered at <paramref name="center"/> as ONE ring
    /// (holes are ignored here — they matter for the copper image, not for the footprint
    /// pad extraction and stroking use this for). A macro aperture outlines as the convex
    /// hull of its exposure-on primitives — exact for the rounded-rect pads real boards
    /// flash; the copper image uses <c>MacroFlattener</c> for the exact shape.
    /// </summary>
    public static IReadOnlyList<Point2> Outline(Aperture aperture, Point2 center, double chordTolerance)
    {
        return aperture switch
        {
            CircleAperture c => Circle(center, c.Diameter / 2, chordTolerance),
            RectangleAperture r => Rectangle(center, r.Width, r.Height),
            ObroundAperture o => Obround(center, o.Width, o.Height, chordTolerance),
            PolygonAperture p => RegularPolygon(center, p.OuterDiameter / 2, p.VertexCount, p.RotationDeg),
            MacroAperture m => MacroHull(m, center, chordTolerance),
            UnsupportedAperture u => Circle(center, u.ApproximateDiameter / 2, chordTolerance),
            _ => throw new NotSupportedException($"Aperture type {aperture.GetType().Name}.")
        };
    }

    /// <summary>A regular n-gon inscribed in the circle of the given radius, first vertex
    /// on +X before rotation (about its own center — the P-aperture convention).</summary>
    public static IReadOnlyList<Point2> RegularPolygon(Point2 center, double radius, int vertices, double rotationDeg)
    {
        double rot = rotationDeg * Math.PI / 180;
        var points = new Point2[vertices];
        for (int i = 0; i < vertices; i++)
        {
            double a = rot + 2 * Math.PI * i / vertices;
            points[i] = new Point2(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
        }
        return points;
    }

    private static IReadOnlyList<Point2> MacroHull(MacroAperture macro, Point2 center, double chordTolerance)
    {
        var points = new List<Point2>();
        foreach (var primitive in macro.Primitives)
        {
            if (!primitive.Exposure) continue;
            switch (primitive)
            {
                case MacroCircle c:
                    points.AddRange(Circle(c.Center, c.Diameter / 2, chordTolerance));
                    break;
                case MacroRing r:
                    points.AddRange(r.Vertices);
                    break;
                case MacroThermal t:
                    points.AddRange(Circle(t.Center, t.OuterDiameter / 2, chordTolerance));
                    break;
            }
        }
        if (points.Count < 3)
            return Circle(center, Math.Max(macro.BoundingSize / 2, 1e-6), chordTolerance);
        var hull = ConvexHull.Compute(points);
        return hull.Select(p => p + center).ToList();
    }

    /// <summary>A counter-clockwise circle tessellated at the given chord tolerance.</summary>
    public static IReadOnlyList<Point2> Circle(Point2 center, double radius, double chordTolerance)
    {
        int n = SegmentCount(radius, chordTolerance);
        var points = new Point2[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            points[i] = new Point2(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
        }
        return points;
    }

    private static IReadOnlyList<Point2> Rectangle(Point2 c, double w, double h) => new[]
    {
        new Point2(c.X - w / 2, c.Y - h / 2),
        new Point2(c.X + w / 2, c.Y - h / 2),
        new Point2(c.X + w / 2, c.Y + h / 2),
        new Point2(c.X - w / 2, c.Y + h / 2)
    };

    /// <summary>A stadium: rectangle with semicircular caps on the two short sides.</summary>
    private static IReadOnlyList<Point2> Obround(Point2 c, double w, double h, double chordTolerance)
    {
        bool horizontal = w >= h;
        double radius = (horizontal ? h : w) / 2;
        double half = Math.Abs(w - h) / 2;                        // half-length of the straight part
        var axis = horizontal ? new Point2(1, 0) : new Point2(0, 1);
        var c1 = c - axis * half;                                 // cap centers
        var c2 = c + axis * half;

        int perCap = Math.Max(4, SegmentCount(radius, chordTolerance) / 2);
        var points = new List<Point2>(2 * perCap + 2);
        double baseAngle = horizontal ? Math.PI / 2 : Math.PI;    // start tangent to the straight side
        for (int i = 0; i <= perCap; i++)
        {
            double a = baseAngle + Math.PI * i / perCap;
            points.Add(new Point2(c1.X + radius * Math.Cos(a), c1.Y + radius * Math.Sin(a)));
        }
        for (int i = 0; i <= perCap; i++)
        {
            double a = baseAngle + Math.PI + Math.PI * i / perCap;
            points.Add(new Point2(c2.X + radius * Math.Cos(a), c2.Y + radius * Math.Sin(a)));
        }
        return points;
    }

    /// <summary>
    /// A counter-clockwise capsule (stadium) along the segment a→b at the given radius:
    /// two straight sides plus semicircular caps tessellated at the chord tolerance.
    /// A zero-length segment degenerates to a circle. Used for routed drill slots and
    /// vector-line macro primitives.
    /// </summary>
    public static IReadOnlyList<Point2> Capsule(Point2 a, Point2 b, double radius, double chordTolerance)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-12)
            return Circle(a, radius, chordTolerance);
        double theta = Math.Atan2(dy, dx);

        int perCap = Math.Max(4, SegmentCount(radius, chordTolerance) / 2);
        var points = new List<Point2>(2 * perCap + 2);
        for (int i = 0; i <= perCap; i++)                          // cap around b: θ−π/2 → θ+π/2
        {
            double ang = theta - Math.PI / 2 + Math.PI * i / perCap;
            points.Add(new Point2(b.X + radius * Math.Cos(ang), b.Y + radius * Math.Sin(ang)));
        }
        for (int i = 0; i <= perCap; i++)                          // cap around a: θ+π/2 → θ+3π/2
        {
            double ang = theta + Math.PI / 2 + Math.PI * i / perCap;
            points.Add(new Point2(a.X + radius * Math.Cos(ang), a.Y + radius * Math.Sin(ang)));
        }
        return points;
    }

    /// <summary>Segment count from the sagitta bound: chord deviation ≤ tolerance.</summary>
    public static int SegmentCount(double radius, double chordTolerance)
    {
        if (radius <= chordTolerance)
            return 8;
        double maxStep = 2 * Math.Acos(1 - chordTolerance / radius);
        return Math.Clamp((int)Math.Ceiling(2 * Math.PI / maxStep), 12, 512);
    }
}
