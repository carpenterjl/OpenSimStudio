using System.Globalization;
using System.Xml;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// Streaming reader for the IPC-2581 polyline shape grammar shared by <c>Profile</c>,
/// <c>Contour</c>, and dictionary <c>Contour</c> primitives: a <c>Polygon</c> (or
/// <c>Cutout</c>) element containing <c>PolyBegin</c> followed by <c>PolyStepSegment</c>
/// and <c>PolyStepCurve</c> steps. Curves are tessellated to chords at the same sagitta
/// bound as the Gerber arc path (see <see cref="ApertureShapes.SegmentCount"/>).
/// </summary>
public static class PolyShapeReader
{
    /// <summary>Chord tolerance for arc tessellation [m]; matches the Gerber parser default.</summary>
    public const double ChordTolerance = 5e-6;

    /// <summary>
    /// Reads one ring from a reader positioned at a <c>Polygon</c>/<c>Cutout</c> element,
    /// consuming that element. Coordinates are multiplied by <paramref name="scale"/>
    /// (file units → meters). Returns null (with a warning) when the ring is unusable —
    /// a bad number or fewer than 3 distinct points. Non-step child elements (style refs
    /// like <c>FillDescRef</c>/<c>LineDescRef</c>, which the Cadence dialect nests INSIDE
    /// the ring element) are passed to <paramref name="onStyleElement"/> when provided,
    /// else ignored.
    /// </summary>
    public static IReadOnlyList<Point2>? ReadRing(XmlReader reader, double scale,
        Ipc2581Diagnostics diag, Action<XmlReader>? onStyleElement = null)
    {
        string owner = reader.LocalName;
        var ring = new List<Point2>();
        bool valid = true;

        using (var sub = reader.ReadSubtree())
        {
            sub.Read();                                          // enter the Polygon/Cutout element
            while (sub.Read())
            {
                if (sub.NodeType != XmlNodeType.Element) continue;
                switch (sub.LocalName)
                {
                    case "PolyBegin":
                        if (TryPoint(sub, scale, diag, owner, out var start)) ring.Add(start);
                        else valid = false;
                        break;
                    case "PolyStepSegment":
                        if (TryPoint(sub, scale, diag, owner, out var p)) ring.Add(p);
                        else valid = false;
                        break;
                    case "PolyStepCurve":
                        if (ring.Count > 0
                            && TryPoint(sub, scale, diag, owner, out var end)
                            && TryAttr(sub, "centerX", scale, out double cx)
                            && TryAttr(sub, "centerY", scale, out double cy))
                        {
                            bool clockwise = string.Equals(sub.GetAttribute("clockwise"), "true",
                                StringComparison.OrdinalIgnoreCase);
                            AppendArc(ring, ring[^1], end, new Point2(cx, cy), clockwise);
                        }
                        else
                        {
                            diag.Warn($"IPC-2581: invalid PolyStepCurve in <{owner}> skipped.");
                            valid = false;
                        }
                        break;
                    default:
                        onStyleElement?.Invoke(sub);
                        break;
                }
            }
        }
        // The grammar closes the ring implicitly (last step returns to PolyBegin).
        if (ring.Count >= 2 && (ring[^1] - ring[0]).Length < 1e-12)
            ring.RemoveAt(ring.Count - 1);

        if (!valid || ring.Count < 3)
        {
            if (valid)
                diag.Warn($"IPC-2581: <{owner}> ring with fewer than 3 points skipped.");
            return null;
        }
        return ring;
    }

    /// <summary>
    /// Appends the arc from <paramref name="from"/> to <paramref name="to"/> around
    /// <paramref name="center"/> (excluding <paramref name="from"/>, including
    /// <paramref name="to"/>). Identical endpoints trace a full circle — IPC-2581 draws
    /// circles this way, mirroring the Gerber G75 convention.
    /// </summary>
    public static void AppendArc(List<Point2> ring, Point2 from, Point2 to, Point2 center, bool clockwise)
    {
        double radius = (from - center).Length;
        if (radius <= 0) { ring.Add(to); return; }

        double a0 = Math.Atan2(from.Y - center.Y, from.X - center.X);
        double a1 = Math.Atan2(to.Y - center.Y, to.X - center.X);
        double sweep = clockwise ? a0 - a1 : a1 - a0;
        sweep = (sweep % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
        bool fullCircle = sweep < 1e-12;
        if (fullCircle) sweep = 2 * Math.PI;

        double maxStep = 2 * Math.Acos(Math.Max(0.0, 1 - ChordTolerance / radius));
        int steps = Math.Max(2, (int)Math.Ceiling(sweep / Math.Max(maxStep, 1e-4)));
        for (int s = 1; s <= steps; s++)
        {
            double a = a0 + (clockwise ? -1 : 1) * sweep * s / steps;
            ring.Add(new Point2(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a)));
        }
        if (!fullCircle)
            ring[^1] = to;                                       // land exactly on the endpoint
        else
            ring.RemoveAt(ring.Count - 1);                       // a full circle must not repeat its start
    }

    /// <summary>Reads the (x, y) attribute pair of the current element, scaled to meters.</summary>
    private static bool TryPoint(XmlReader reader, double scale, Ipc2581Diagnostics diag,
        string owner, out Point2 point)
    {
        if (TryAttr(reader, "x", scale, out double x) && TryAttr(reader, "y", scale, out double y))
        {
            point = new Point2(x, y);
            return true;
        }
        diag.Warn($"IPC-2581: <{reader.LocalName}> in <{owner}> has a malformed coordinate" +
                     $"{LinePosition(reader)}; ring skipped.");
        point = default;
        return false;
    }

    /// <summary>Parses a numeric attribute with invariant culture, scaled by <paramref name="scale"/>.</summary>
    public static bool TryAttr(XmlReader reader, string name, double scale, out double value)
    {
        string? text = reader.GetAttribute(name);
        if (text is not null &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
        {
            value = raw * scale;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>" (line N)" when the reader can report positions, else empty.</summary>
    public static string LinePosition(XmlReader reader) =>
        reader is IXmlLineInfo info && info.HasLineInfo() ? $" (line {info.LineNumber})" : "";
}
