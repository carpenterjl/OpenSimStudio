using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Polygons;

/// <summary>
/// Flattens an evaluated macro aperture into its exact copper footprint at the ORIGIN:
/// primitives fold in file order — exposure-on unions, exposure-off subtracts (order
/// matters per spec; a later on-primitive repaints an earlier hole). Callers cache the
/// result per aperture and translate the rings to each flash position, because one
/// aperture typically flashes hundreds of times.
/// </summary>
public static class MacroFlattener
{
    public static IReadOnlyList<Polygon2> Flatten(MacroAperture aperture, IPolygonOps ops, double chordTolerance)
    {
        var image = new List<Polygon2>();
        var batch = new List<IReadOnlyList<Point2>>();
        bool batchOn = true;

        void Flush()
        {
            if (batch.Count == 0) return;
            image = batchOn
                ? ops.Union(image.SelectMany(Polygon2.OrientedRings).Concat(batch)).ToList()
                : ops.Difference(image, batch).ToList();
            batch.Clear();
        }

        foreach (var (on, ring) in aperture.Primitives.SelectMany(p => Steps(p, chordTolerance)))
        {
            if (on != batchOn)
            {
                Flush();
                batchOn = on;
            }
            batch.Add(ring);
        }
        Flush();
        return image;
    }

    /// <summary>
    /// A primitive as a sequence of (exposure, ring) boolean steps. A thermal is not one
    /// ring: it is outer circle on, then inner circle and the two crosshair gap
    /// rectangles off.
    /// </summary>
    private static IEnumerable<(bool On, IReadOnlyList<Point2> Ring)> Steps(
        MacroPrimitive primitive, double chordTolerance)
    {
        switch (primitive)
        {
            case MacroCircle c:
                yield return (c.Exposure, ApertureShapes.Circle(c.Center, c.Diameter / 2, chordTolerance));
                break;

            case MacroRing r:
                if (Math.Abs(Polygon2.RingArea(r.Vertices)) > 0)  // zero-area rings occur in real files
                    yield return (r.Exposure, r.Vertices);
                break;

            case MacroThermal t:
                yield return (true, ApertureShapes.Circle(t.Center, t.OuterDiameter / 2, chordTolerance));
                yield return (false, ApertureShapes.Circle(t.Center, t.InnerDiameter / 2, chordTolerance));
                // Crosshair gaps: two rectangles spanning the outer circle, oriented by
                // the primitive's rotation (the center is already origin-rotated).
                yield return (false, GapRectangle(t, horizontal: true));
                yield return (false, GapRectangle(t, horizontal: false));
                break;

            default:
                throw new NotSupportedException($"Macro primitive {primitive.GetType().Name}.");
        }
    }

    private static IReadOnlyList<Point2> GapRectangle(MacroThermal t, bool horizontal)
    {
        double half = t.OuterDiameter / 2 + t.GapWidth;           // overshoot: must fully cross the ring
        double w = horizontal ? half : t.GapWidth / 2;
        double h = horizontal ? t.GapWidth / 2 : half;
        double a = t.RotationDeg * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        Point2 Corner(double x, double y) => new(
            t.Center.X + x * cos - y * sin,
            t.Center.Y + x * sin + y * cos);
        return new[] { Corner(-w, -h), Corner(w, -h), Corner(w, h), Corner(-w, h) };
    }
}
