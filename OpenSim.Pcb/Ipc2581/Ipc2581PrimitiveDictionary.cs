using System.Xml;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// The IPC-2581 standard-primitive dictionary: <c>EntryStandard id</c> → a reusable pad
/// shape (Circle, RectCenter, Oval, Donut, Contour), later flashed by
/// <c>StandardPrimitiveRef</c> at a location with an optional Xform rotation. Reuses the
/// Gerber aperture tessellation (<see cref="ApertureShapes"/>) so IPC pads and Gerber
/// pads produce identical polygon quality.
/// </summary>
public sealed class Ipc2581PrimitiveDictionary
{
    /// <summary>A shape descriptor in local coordinates (centered on the flash location).</summary>
    public abstract record Primitive;

    public sealed record CirclePrimitive(double Diameter) : Primitive;
    public sealed record RectPrimitive(double Width, double Height) : Primitive;
    public sealed record OvalPrimitive(double Width, double Height) : Primitive;
    /// <summary>Contour in local coordinates; flashed by translation + rotation.</summary>
    public sealed record ContourPrimitive(Polygon2 Shape) : Primitive;

    private readonly Dictionary<string, Primitive> _entries = new();

    public int Count => _entries.Count;

    /// <summary>
    /// Parses a <c>DictionaryStandard</c> subtree (reader positioned at the element,
    /// which is consumed). Unknown primitive kinds are recorded with a warning and no
    /// entry, so later references fail loudly rather than silently misrender.
    /// </summary>
    public void Read(XmlReader reader, double scale, List<string> warnings)
    {
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "EntryStandard") continue;
            string? id = sub.GetAttribute("id");
            if (string.IsNullOrEmpty(id))
            {
                warnings.Add($"IPC-2581: EntryStandard without an id skipped{PolyShapeReader.LinePosition(sub)}.");
                continue;
            }

            using var entry = sub.ReadSubtree();
            entry.Read();
            while (entry.Read())
            {
                if (entry.NodeType != XmlNodeType.Element) continue;
                var primitive = TryReadPrimitive(entry, scale, warnings);
                if (primitive is not null)
                    _entries[id] = primitive;
                else
                    warnings.Add($"IPC-2581: dictionary entry '{id}' uses unsupported primitive " +
                                 $"<{entry.LocalName}>; the entry is ignored.");
                break;                                           // one primitive per entry
            }
        }
    }

    /// <summary>
    /// Reads one primitive element (Circle, RectCenter, Oval, Donut, Contour, or a
    /// RectRound/RectCham treated as a plain rectangle with a warning). Returns null for
    /// unrecognized elements. Shared by the dictionary and by inline feature primitives.
    /// </summary>
    public static Primitive? TryReadPrimitive(XmlReader reader, double scale, List<string> warnings)
    {
        switch (reader.LocalName)
        {
            case "Circle":
                return PolyShapeReader.TryAttr(reader, "diameter", scale, out double d) && d > 0
                    ? new CirclePrimitive(d) : Invalid(reader, warnings);
            case "RectCenter":
                return ReadRect(reader, scale, warnings);
            case "RectRound" or "RectCham":
                // Corner rounding/chamfer is below meshing resolution for pads; the
                // bounding rectangle is the correct conservative copper footprint.
                warnings.Add($"IPC-2581: <{reader.LocalName}> flashed as a plain rectangle " +
                             "(corner rounding ignored).");
                return ReadRect(reader, scale, warnings);
            case "Oval":
                return PolyShapeReader.TryAttr(reader, "width", scale, out double ow) && ow > 0
                       && PolyShapeReader.TryAttr(reader, "height", scale, out double oh) && oh > 0
                    ? new OvalPrimitive(ow, oh) : Invalid(reader, warnings);
            case "Donut":
                // The copper footprint of a donut is its outer circle; the inner clearance
                // only matters for mask/paste layers, which are not imported.
                warnings.Add("IPC-2581: <Donut> flashed as its outer circle (inner hole ignored).");
                return PolyShapeReader.TryAttr(reader, "outerDiameter", scale, out double od) && od > 0
                    ? new CirclePrimitive(od) : Invalid(reader, warnings);
            case "Contour":
                return ReadContourPrimitive(reader, scale, warnings);
            default:
                return null;
        }
    }

    private static Primitive? ReadRect(XmlReader reader, double scale, List<string> warnings) =>
        PolyShapeReader.TryAttr(reader, "width", scale, out double w) && w > 0
        && PolyShapeReader.TryAttr(reader, "height", scale, out double h) && h > 0
            ? new RectPrimitive(w, h) : Invalid(reader, warnings);

    private static Primitive? ReadContourPrimitive(XmlReader reader, double scale, List<string> warnings)
    {
        IReadOnlyList<Point2>? outer = null;
        var holes = new List<IReadOnlyList<Point2>>();
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "Polygon")
                outer ??= PolyShapeReader.ReadRing(sub, scale, warnings);
            else if (sub.LocalName == "Cutout")
            {
                var hole = PolyShapeReader.ReadRing(sub, scale, warnings);
                if (hole is not null) holes.Add(hole);
            }
        }
        return outer is null ? null : new ContourPrimitive(new Polygon2(outer, holes));
    }

    private static Primitive? Invalid(XmlReader reader, List<string> warnings)
    {
        warnings.Add($"IPC-2581: <{reader.LocalName}> has missing or invalid dimensions" +
                     $"{PolyShapeReader.LinePosition(reader)}; skipped.");
        return null;
    }

    /// <summary>
    /// Flashes a dictionary entry at <paramref name="location"/> with a rotation (degrees,
    /// counter-clockwise, about the location). Returns null with a warning when the id is
    /// unknown — never a silent misrender.
    /// </summary>
    public Polygon2? Flash(string id, Point2 location, double rotationDeg, List<string> warnings)
    {
        if (!_entries.TryGetValue(id, out var primitive))
        {
            warnings.Add($"IPC-2581: StandardPrimitiveRef '{id}' not found in the dictionary; flash skipped.");
            return null;
        }
        return Flash(primitive, location, rotationDeg);
    }

    /// <summary>Tessellates a primitive at a location with a rotation about that location.</summary>
    public static Polygon2 Flash(Primitive primitive, Point2 location, double rotationDeg)
    {
        switch (primitive)
        {
            case CirclePrimitive c:
                return new Polygon2(ApertureShapes.Circle(location, c.Diameter / 2, PolyShapeReader.ChordTolerance));
            case RectPrimitive r:
                return new Polygon2(Rotate(RectangleRing(location, r.Width, r.Height), location, rotationDeg));
            case OvalPrimitive o:
                var ring = ApertureShapes.Outline(new ObroundAperture(0, o.Width, o.Height),
                    location, PolyShapeReader.ChordTolerance);
                return new Polygon2(Rotate(ring, location, rotationDeg));
            case ContourPrimitive p:
                var outer = Rotate(Translate(p.Shape.Outer, location), location, rotationDeg);
                var holes = p.Shape.Holes
                    .Select(h => Rotate(Translate(h, location), location, rotationDeg))
                    .ToList();
                return new Polygon2(outer, holes);
            default:
                throw new NotSupportedException($"Primitive {primitive.GetType().Name}.");
        }
    }

    private static IReadOnlyList<Point2> RectangleRing(Point2 c, double w, double h) => new[]
    {
        new Point2(c.X - w / 2, c.Y - h / 2),
        new Point2(c.X + w / 2, c.Y - h / 2),
        new Point2(c.X + w / 2, c.Y + h / 2),
        new Point2(c.X - w / 2, c.Y + h / 2)
    };

    private static IReadOnlyList<Point2> Translate(IReadOnlyList<Point2> ring, Point2 by) =>
        ring.Select(p => p + by).ToList();

    private static IReadOnlyList<Point2> Rotate(IReadOnlyList<Point2> ring, Point2 about, double degrees)
    {
        if (Math.Abs(degrees % 360) < 1e-9) return ring;
        double a = degrees * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        return ring.Select(p =>
        {
            var v = p - about;
            return about + new Point2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }).ToList();
    }
}
