using OpenSim.Pcb.Excellon;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Import;

/// <summary>A drilled hole as a circle to subtract from copper/board.</summary>
public readonly record struct Hole(Point2 Center, double Diameter);

/// <summary>All drilled features of one drill layer: round holes and routed slots, and
/// whatever the parser could not use.</summary>
public readonly record struct DrillFeatures(IReadOnlyList<Hole> Holes, IReadOnlyList<DrillSlot> Slots,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Extracts drilled features from a drill file in either format: Excellon (M48 header)
/// or the Gerber-format drill Altium emits (circle apertures flashed at hole positions).
/// A slot is a G85 or a rout-mode move in Excellon, and a DRAW with a circle aperture in
/// a Gerber-format drill file (each leg of the drawn path is one slot at the aperture's
/// diameter).
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
                drills.Slots, drills.Warnings);
        }

        // Gerber-format drill: each D03 flash of a circle aperture is a hole, and each leg
        // of a path drawn with one is a slot. Anything else in a drill file is said, not
        // dropped — a hole that silently goes missing changes the copper image.
        var doc = new GerberParser(new GerberParseOptions { ChordTolerance = chordTolerance }).Parse(content);
        var holes = new List<Hole>();
        var slots = new List<DrillSlot>();
        int unusable = 0;
        foreach (var op in doc.Ops)
        {
            if (op is FlashOp { Aperture: CircleAperture c } flash)
                holes.Add(new Hole(flash.Position, c.Diameter));
            else if (op is DrawOp { Aperture: CircleAperture d } draw)
                for (int k = 1; k < draw.Path.Count; k++)
                    slots.Add(new DrillSlot(draw.Path[k - 1], draw.Path[k], d.Diameter));
            else
                unusable++;
        }
        var warnings = new List<string>();
        if (unusable > 0)
            warnings.Add($"{unusable} drill-file object(s) are neither a round hole nor a slot drawn " +
                         "with a round tool (non-circular flashes, regions) and were not drilled.");
        return new DrillFeatures(holes, slots, warnings);
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
