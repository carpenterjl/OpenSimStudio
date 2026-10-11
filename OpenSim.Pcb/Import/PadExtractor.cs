using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Import;

/// <summary>
/// Extracts pads from a copper layer: the dark aperture flashes (D03) whose aperture is
/// pad-sized. The size threshold is what separates real pads and via/thermal-relief
/// flashes (which legitimately connect a via) from large plane-pour flashes (which must
/// NOT count as pads, or every signal via through a pour would falsely stitch nets).
/// Draws (traces) are ignored here on purpose.
/// <para>
/// Copper the CAD names a component pin (X2 <c>%TO.P</c>) is a pad whatever its size and
/// whether it is a flash or a G36 region — a custom or rounded pad written as a contour, a
/// large through-hole pad. Unnamed copper that is not a pad-sized flash (a region, a flash
/// over the size limit) is only a <see cref="DrillCandidates">candidate</see>: it becomes a
/// pad where a plated drill sits at its centre (<see cref="ConfirmedByDrills"/>), which is
/// what a through-hole or via pad is and a pour is not (PCB-16).
/// </para>
/// </summary>
public static class PadExtractor
{
    /// <summary>Maximum pad footprint (largest aperture dimension) treated as a pad [m].</summary>
    public const double PadSizeMax = 5e-3;

    /// <summary>A candidate larger than <see cref="PadSizeMax"/> is a drill's pad only up to
    /// this many drill diameters across — an annular ring at most a diameter wide.</summary>
    public const double LargePadDrillRatio = 3;

    private const double ChordTolerance = 5e-6;

    public static IReadOnlyList<CopperPad> Extract(GerberDocument document, int layerOrder)
    {
        var pads = new List<CopperPad>();
        // A negative file's flashes are antipads and thermal reliefs — absences of
        // copper. Counting one as a pad would stitch the via it isolates.
        if (document.IsNegative) return pads;
        foreach (var op in document.Ops)
        {
            if (op.Polarity != GerberPolarity.Dark) continue;
            if (op is FlashOp flash)
            {
                double size = ApertureSize(flash.Aperture);
                if (size <= 0 || (size > PadSizeMax && flash.Pin is null)) continue;
                pads.Add(FlashPad(flash, layerOrder, size));
            }
            else if (op is RegionOp { Pin: not null } region)
                pads.AddRange(RegionPads(region, layerOrder));
        }
        return pads;
    }

    /// <summary>Copper that is a pad only if a plated drill says so: dark flashes over the
    /// size limit and dark regions, neither named a pin.</summary>
    public static IReadOnlyList<CopperPad> DrillCandidates(GerberDocument document, int layerOrder)
    {
        var candidates = new List<CopperPad>();
        if (document.IsNegative) return candidates;
        foreach (var op in document.Ops)
        {
            if (op.Polarity != GerberPolarity.Dark || op.Pin is not null) continue;
            if (op is FlashOp flash && ApertureSize(flash.Aperture) is var size && size > PadSizeMax)
                candidates.Add(FlashPad(flash, layerOrder, size));
            else if (op is RegionOp region)
                candidates.AddRange(RegionPads(region, layerOrder));
        }
        return candidates;
    }

    /// <summary>The candidates with a plated drill at their centre (the tolerance the net
    /// extractor's annular-ring test uses), inside them, smaller than them, and — over
    /// <see cref="PadSizeMax"/> — at most <see cref="LargePadDrillRatio"/> drill diameters
    /// across. A pour is never centred on one drill at that ratio.</summary>
    public static IReadOnlyList<CopperPad> ConfirmedByDrills(IReadOnlyList<CopperPad> candidates,
        IReadOnlyList<Via> vias)
    {
        var confirmed = new List<CopperPad>();
        foreach (var pad in candidates)
            foreach (var via in vias)
            {
                if (!via.Plated || !via.Reaches(pad.LayerOrder)) continue;
                if ((pad.Center - via.Position).Length > 0.5 * via.Diameter + 0.1e-3) continue;
                if (pad.Size <= via.Diameter) continue;
                if (pad.Size > PadSizeMax && pad.Size > LargePadDrillRatio * via.Diameter) continue;
                if (!Meshing2D.PlanarMesher.ContainsPoint(new[] { pad.Shape }, via.Position)) continue;
                confirmed.Add(pad);
                break;
            }
        return confirmed;
    }

    private static CopperPad FlashPad(FlashOp flash, int layerOrder, double size) =>
        new(layerOrder, flash.Position,
            new Polygon2(ApertureShapes.Outline(flash.Aperture, flash.Position, ChordTolerance)), size)
        {
            ComponentRef = flash.ComponentRef, Pin = flash.Pin, NetName = flash.Net
        };

    /// <summary>One pad per contour: centred on its area centroid, sized by its bounding box.</summary>
    private static IEnumerable<CopperPad> RegionPads(RegionOp region, int layerOrder)
    {
        foreach (var contour in region.Contours)
        {
            double area = Polygon2.RingArea(contour);
            if (Math.Abs(area) <= 0) continue;
            double cx = 0, cy = 0;
            for (int i = 0; i < contour.Count; i++)
            {
                var a = contour[i];
                var b = contour[(i + 1) % contour.Count];
                double cross = Point2.Cross(a, b);
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            var centre = new Point2(cx / (6 * area), cy / (6 * area));
            double size = Math.Max(contour.Max(p => p.X) - contour.Min(p => p.X),
                contour.Max(p => p.Y) - contour.Min(p => p.Y));
            yield return new CopperPad(layerOrder, centre, new Polygon2(contour), size)
            {
                ComponentRef = region.ComponentRef, Pin = region.Pin, NetName = region.Net
            };
        }
    }

    /// <summary>Largest footprint dimension of an aperture [m]. Every aperture kind that
    /// can flash a pad MUST size here — a 0 silently drops the pad from extraction and
    /// with it the via stitching it provides.</summary>
    public static double ApertureSize(Aperture aperture) => aperture switch
    {
        CircleAperture c => c.Diameter,
        RectangleAperture r => Math.Max(r.Width, r.Height),
        ObroundAperture o => Math.Max(o.Width, o.Height),
        PolygonAperture p => p.OuterDiameter,
        MacroAperture m => m.BoundingSize,
        UnsupportedAperture u => u.ApproximateDiameter,
        _ => 0
    };
}
