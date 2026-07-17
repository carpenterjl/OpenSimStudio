using System.Xml;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// The IPC-2581 standard-primitive dictionary: <c>EntryStandard id</c> → a reusable pad
/// shape, later flashed by <c>StandardPrimitiveRef</c> at a location with an optional
/// Xform rotation/mirror. The FULL revision-B shape family is supported exactly
/// (<see cref="Ipc2581StandardShapes"/>); Circle/RectCenter/Oval keep the Gerber aperture
/// tessellation (<see cref="ApertureShapes"/>) so IPC pads and Gerber pads produce
/// identical polygon quality (and the pre-family boards stay bitwise unchanged).
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
    /// <summary>Any other family member, pre-tessellated in local coordinates — possibly
    /// several disjoint copper pieces (Thermal segments, Butterfly quadrants, Moire
    /// rings), each possibly carrying holes (Donut).</summary>
    public sealed record PiecesPrimitive(IReadOnlyList<Polygon2> Pieces) : Primitive;

    private readonly Dictionary<string, Primitive> _entries = new();

    public int Count => _entries.Count;

    /// <summary>
    /// Parses a <c>DictionaryStandard</c> subtree (reader positioned at the element,
    /// which is consumed). Unknown primitive kinds are recorded with a warning and no
    /// entry, so later references fail loudly rather than silently misrender.
    /// </summary>
    public void Read(XmlReader reader, double scale, Ipc2581Diagnostics diag)
    {
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "EntryStandard") continue;
            string? id = sub.GetAttribute("id");
            if (string.IsNullOrEmpty(id))
            {
                diag.Warn($"IPC-2581: EntryStandard without an id skipped{PolyShapeReader.LinePosition(sub)}.");
                continue;
            }

            using var entry = sub.ReadSubtree();
            entry.Read();
            while (entry.Read())
            {
                if (entry.NodeType != XmlNodeType.Element) continue;
                // Style/transform children are not the entry's shape (exporters embed
                // FillDescRef inside the primitive; an Xform sibling would be rare but
                // legal) — skip them, never mistake them for an unsupported primitive.
                if (entry.LocalName is "Xform" or "FillDescRef" or "LineDescRef" or "ColorRef"
                    or "FillDesc" or "LineDesc")
                    continue;
                var primitive = TryReadPrimitive(entry, scale, diag);
                if (primitive is not null)
                    _entries[id] = primitive;
                else
                    diag.Warn($"IPC-2581: dictionary entry '{id}' uses unsupported primitive " +
                                 $"<{entry.LocalName}>; the entry is ignored.");
                break;                                           // one primitive per entry
            }
        }
    }

    /// <summary>
    /// Reads one primitive element of the revision-B family. Returns null for
    /// unrecognized elements. Shared by the dictionary and by inline feature primitives.
    /// </summary>
    public static Primitive? TryReadPrimitive(XmlReader reader, double scale, Ipc2581Diagnostics diag)
    {
        switch (reader.LocalName)
        {
            case "Circle":
                return PolyShapeReader.TryAttr(reader, "diameter", scale, out double d) && d > 0
                    ? new CirclePrimitive(d) : Invalid(reader, diag);
            case "RectCenter":
                return PolyShapeReader.TryAttr(reader, "width", scale, out double w) && w > 0
                       && PolyShapeReader.TryAttr(reader, "height", scale, out double h) && h > 0
                    ? new RectPrimitive(w, h) : Invalid(reader, diag);
            case "Oval":
                return PolyShapeReader.TryAttr(reader, "width", scale, out double ow) && ow > 0
                       && PolyShapeReader.TryAttr(reader, "height", scale, out double oh) && oh > 0
                    ? new OvalPrimitive(ow, oh) : Invalid(reader, diag);
            case "Contour":
                return ReadContourPrimitive(reader, scale, diag);

            case "RectRound":
                return PolyShapeReader.TryAttr(reader, "width", scale, out double rw) && rw > 0
                       && PolyShapeReader.TryAttr(reader, "height", scale, out double rh) && rh > 0
                       && PolyShapeReader.TryAttr(reader, "radius", scale, out double rr) && rr >= 0
                    ? Pieces(Ipc2581StandardShapes.RectRound(rw, rh, rr,
                        CornerFlag(reader, "upperLeft"), CornerFlag(reader, "upperRight"),
                        CornerFlag(reader, "lowerLeft"), CornerFlag(reader, "lowerRight")))
                    : Invalid(reader, diag);
            case "RectCham":
                return PolyShapeReader.TryAttr(reader, "width", scale, out double cw) && cw > 0
                       && PolyShapeReader.TryAttr(reader, "height", scale, out double ch) && ch > 0
                       && PolyShapeReader.TryAttr(reader, "chamfer", scale, out double cc) && cc >= 0
                    ? Pieces(Ipc2581StandardShapes.RectCham(cw, ch, cc,
                        CornerFlag(reader, "upperLeft"), CornerFlag(reader, "upperRight"),
                        CornerFlag(reader, "lowerLeft"), CornerFlag(reader, "lowerRight")))
                    : Invalid(reader, diag);
            case "RectCorner":
                return PolyShapeReader.TryAttr(reader, "lowerLeftX", scale, out double llx)
                       && PolyShapeReader.TryAttr(reader, "lowerLeftY", scale, out double lly)
                       && PolyShapeReader.TryAttr(reader, "upperRightX", scale, out double urx)
                       && PolyShapeReader.TryAttr(reader, "upperRightY", scale, out double ury)
                       && urx > llx && ury > lly
                    ? Pieces(Ipc2581StandardShapes.RectCorner(llx, lly, urx, ury))
                    : Invalid(reader, diag);
            case "Diamond":
                return PolyShapeReader.TryAttr(reader, "width", scale, out double dw) && dw > 0
                       && PolyShapeReader.TryAttr(reader, "height", scale, out double dh) && dh > 0
                    ? Pieces(Ipc2581StandardShapes.Diamond(dw, dh)) : Invalid(reader, diag);
            case "Triangle":
            {
                if (!PolyShapeReader.TryAttr(reader, "base", scale, out double tb))
                    PolyShapeReader.TryAttr(reader, "width", scale, out tb);
                return tb > 0
                       && PolyShapeReader.TryAttr(reader, "height", scale, out double th) && th > 0
                    ? Pieces(Ipc2581StandardShapes.Triangle(tb, th)) : Invalid(reader, diag);
            }
            case "Ellipse":
                return PolyShapeReader.TryAttr(reader, "width", scale, out double ew) && ew > 0
                       && PolyShapeReader.TryAttr(reader, "height", scale, out double eh) && eh > 0
                    ? Pieces(Ipc2581StandardShapes.Ellipse(ew, eh)) : Invalid(reader, diag);
            case "Hexagon":
                return PolyShapeReader.TryAttr(reader, "length", scale, out double hl) && hl > 0
                    ? Pieces(Ipc2581StandardShapes.Hexagon(hl)) : Invalid(reader, diag);
            case "Octagon":
                return PolyShapeReader.TryAttr(reader, "length", scale, out double ol) && ol > 0
                    ? Pieces(Ipc2581StandardShapes.Octagon(ol)) : Invalid(reader, diag);
            case "Butterfly":
            {
                bool round = !string.Equals(reader.GetAttribute("shape"), "SQUARE",
                    StringComparison.OrdinalIgnoreCase);
                bool ok = round
                    ? PolyShapeReader.TryAttr(reader, "diameter", scale, out double bs) && bs > 0
                    : PolyShapeReader.TryAttr(reader, "side", scale, out bs) && bs > 0;
                return ok ? new PiecesPrimitive(Ipc2581StandardShapes.Butterfly(round, bs))
                    : Invalid(reader, diag);
            }
            case "Donut":
            {
                string shape = reader.GetAttribute("shape") ?? "ROUND";
                bool ok = PolyShapeReader.TryAttr(reader, "outerDiameter", scale, out double od) && od > 0;
                PolyShapeReader.TryAttr(reader, "innerDiameter", scale, out double id2);
                return ok && id2 < od
                    ? Pieces(Ipc2581StandardShapes.Donut(shape, od, id2)) : Invalid(reader, diag);
            }
            case "Thermal":
            {
                string shape = reader.GetAttribute("shape") ?? "ROUND";
                bool ok = PolyShapeReader.TryAttr(reader, "outerDiameter", scale, out double od)
                          & PolyShapeReader.TryAttr(reader, "innerDiameter", scale, out double id2);
                ok = ok && od > 0 && id2 < od;
                PolyShapeReader.TryAttr(reader, "spokeCount", 1.0, out double spokes);
                PolyShapeReader.TryAttr(reader, "gap", scale, out double gap);
                if (!PolyShapeReader.TryAttr(reader, "spokeStartAngle", 1.0, out double start))
                    PolyShapeReader.TryAttr(reader, "angle", 1.0, out start);
                return ok
                    ? new PiecesPrimitive(Ipc2581StandardShapes.Thermal(shape, od, id2,
                        (int)Math.Round(spokes), gap, start))
                    : Invalid(reader, diag);
            }
            case "Moire":
            {
                bool ok = PolyShapeReader.TryAttr(reader, "diameter", scale, out double md)
                          & PolyShapeReader.TryAttr(reader, "ringWidth", scale, out double mw);
                ok = ok && md > 0 && mw > 0;
                PolyShapeReader.TryAttr(reader, "ringGap", scale, out double mg);
                if (!PolyShapeReader.TryAttr(reader, "ringNumber", 1.0, out double mn)) mn = 1;
                PolyShapeReader.TryAttr(reader, "lineWidth", scale, out double lw);
                PolyShapeReader.TryAttr(reader, "lineLength", scale, out double ll);
                PolyShapeReader.TryAttr(reader, "lineAngle", 1.0, out double la);
                return ok
                    ? new PiecesPrimitive(Ipc2581StandardShapes.Moire(md, mw, mg,
                        (int)Math.Round(mn), lw, ll, la))
                    : Invalid(reader, diag);
            }
            default:
                return null;
        }
    }

    /// <summary>Per-corner flags default to TRUE when absent — rounding/chamfering is the
    /// shape's point; exporters that want a subset write every flag explicitly
    /// (KiCad writes all-false rounded rects, which are exactly plain rectangles).</summary>
    private static bool CornerFlag(XmlReader reader, string name) =>
        !string.Equals(reader.GetAttribute(name), "false", StringComparison.OrdinalIgnoreCase);

    private static PiecesPrimitive Pieces(Polygon2 single) => new(new[] { single });

    private static Primitive? ReadContourPrimitive(XmlReader reader, double scale, Ipc2581Diagnostics diag)
    {
        IReadOnlyList<Point2>? outer = null;
        var holes = new List<IReadOnlyList<Point2>>();
        using var sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;
            if (sub.LocalName == "Polygon")
                outer ??= PolyShapeReader.ReadRing(sub, scale, diag);
            else if (sub.LocalName == "Cutout")
            {
                var hole = PolyShapeReader.ReadRing(sub, scale, diag);
                if (hole is not null) holes.Add(hole);
            }
        }
        return outer is null ? null : new ContourPrimitive(new Polygon2(outer, holes));
    }

    private static Primitive? Invalid(XmlReader reader, Ipc2581Diagnostics diag)
    {
        diag.Warn($"IPC-2581: <{reader.LocalName}> has missing or invalid dimensions" +
                     $"{PolyShapeReader.LinePosition(reader)}; skipped.");
        return null;
    }

    /// <summary>
    /// Flashes a dictionary entry at <paramref name="location"/> with a rotation (degrees,
    /// counter-clockwise, about the location) and optional mirror. Returns null with a
    /// warning when the id is unknown — never a silent misrender.
    /// </summary>
    public IReadOnlyList<Polygon2>? Flash(string id, Point2 location, double rotationDeg,
        Ipc2581Diagnostics diag, bool mirror = false)
    {
        if (!_entries.TryGetValue(id, out var primitive))
        {
            diag.Warn($"IPC-2581: StandardPrimitiveRef '{id}' not found in the dictionary; flash skipped.");
            return null;
        }
        return Flash(primitive, location, rotationDeg, mirror);
    }

    /// <summary>
    /// Tessellates a primitive at a location with an Xform placement: rotation FIRST,
    /// then mirror (about the y-axis through the location, local x → −x), then the
    /// translation — the order pinned empirically by the transform oracle (see
    /// <see cref="Ipc2581Transform"/>). Mirror about the center is the identity for the
    /// symmetric Circle/RectCenter/Oval, which keep their historical code path bitwise.
    /// </summary>
    public static IReadOnlyList<Polygon2> Flash(Primitive primitive, Point2 location,
        double rotationDeg, bool mirror = false)
    {
        switch (primitive)
        {
            case CirclePrimitive c:
                return new[] { new Polygon2(
                    ApertureShapes.Circle(location, c.Diameter / 2, PolyShapeReader.ChordTolerance)) };
            case RectPrimitive r:
                return new[] { new Polygon2(
                    Rotate(RectangleRing(location, r.Width, r.Height), location, rotationDeg)) };
            case OvalPrimitive o:
                var ring = ApertureShapes.Outline(new ObroundAperture(0, o.Width, o.Height),
                    location, PolyShapeReader.ChordTolerance);
                return new[] { new Polygon2(Rotate(ring, location, rotationDeg)) };
            case ContourPrimitive p when !mirror:
                // The historical Contour path (translate, then rotate about the location)
                // — kept verbatim so existing boards stay bitwise identical.
                var outer = Rotate(Translate(p.Shape.Outer, location), location, rotationDeg);
                var holes = p.Shape.Holes
                    .Select(hole => Rotate(Translate(hole, location), location, rotationDeg))
                    .ToList();
                return new[] { new Polygon2(outer, holes) };
            case ContourPrimitive p:
                return new[] { Place(p.Shape, location, rotationDeg, mirror: true) };
            case PiecesPrimitive pieces:
                return pieces.Pieces.Select(piece => Place(piece, location, rotationDeg, mirror)).ToList();
            default:
                throw new NotSupportedException($"Primitive {primitive.GetType().Name}.");
        }
    }

    /// <summary>Places one local-space piece: mirror → rotate (about the origin) → translate
    /// (the shared <see cref="Ipc2581Transform"/> placement).</summary>
    private static Polygon2 Place(Polygon2 piece, Point2 location, double rotationDeg, bool mirror) =>
        new Ipc2581Transform(location, rotationDeg, mirror).Apply(piece);

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
