using OpenSim.Pcb.Excellon;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Import;

/// <summary>A drilled hole as a circle to subtract from copper/board.</summary>
public readonly record struct Hole(Point2 Center, double Diameter);

/// <summary>All drilled features of one drill layer: round holes and routed slots.</summary>
public readonly record struct DrillFeatures(IReadOnlyList<Hole> Holes, IReadOnlyList<DrillSlot> Slots);

/// <summary>
/// Extracts drilled features from a drill file in either format: Excellon (M48 header)
/// or the Gerber-format drill Altium emits (circle apertures flashed at hole positions).
/// Slots only occur in Excellon — Gerber-drawn slots arrive as obround flashes/draws
/// through the normal Gerber copper path.
/// </summary>
public static class DrillExtractor
{
    public static DrillFeatures Extract(string content, double chordTolerance = 5e-6)
    {
        if (LooksExcellon(content))
        {
            var drills = new ExcellonParser().Parse(content);
            return new DrillFeatures(
                drills.Hits.Select(h => new Hole(h.Position, h.Diameter)).ToList(),
                drills.Slots);
        }

        // Gerber-format drill: each D03 flash of a circle aperture is a hole.
        var doc = new GerberParser(new GerberParseOptions { ChordTolerance = chordTolerance }).Parse(content);
        var holes = new List<Hole>();
        foreach (var op in doc.Ops)
            if (op is FlashOp flash && flash.Aperture is CircleAperture c)
                holes.Add(new Hole(flash.Position, c.Diameter));
        return new DrillFeatures(holes, Array.Empty<DrillSlot>());
    }

    private static bool LooksExcellon(string content)
    {
        // Excellon starts with M48 or has no Gerber format spec; Gerber drills carry %FS.
        int head = Math.Min(content.Length, 400);
        var prefix = content.AsSpan(0, head);
        if (prefix.Contains("%FSLA", StringComparison.Ordinal) || prefix.Contains("%MO", StringComparison.Ordinal))
            return false;
        return prefix.Contains("M48", StringComparison.Ordinal)
               || prefix.Contains("INCH", StringComparison.Ordinal)
               || prefix.Contains("METRIC", StringComparison.Ordinal);
    }
}
