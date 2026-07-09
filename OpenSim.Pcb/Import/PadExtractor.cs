using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Import;

/// <summary>
/// Extracts pads from a copper layer: the dark aperture flashes (D03) whose aperture is
/// pad-sized. The size threshold is what separates real pads and via/thermal-relief
/// flashes (which legitimately connect a via) from large plane-pour flashes (which must
/// NOT count as pads, or every signal via through a pour would falsely stitch nets).
/// Draws (traces) and G36 regions (pours) are ignored here on purpose.
/// </summary>
public static class PadExtractor
{
    /// <summary>Maximum pad footprint (largest aperture dimension) treated as a pad [m].</summary>
    public const double PadSizeMax = 5e-3;

    private const double ChordTolerance = 5e-6;

    public static IReadOnlyList<CopperPad> Extract(GerberDocument document, int layerOrder)
    {
        var pads = new List<CopperPad>();
        foreach (var op in document.Ops)
        {
            if (op is not FlashOp { Polarity: GerberPolarity.Dark } flash) continue;
            double size = ApertureSize(flash.Aperture);
            if (size <= 0 || size > PadSizeMax) continue;
            var shape = new Polygon2(ApertureShapes.Outline(flash.Aperture, flash.Position, ChordTolerance));
            pads.Add(new CopperPad(layerOrder, flash.Position, shape, size));
        }
        return pads;
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
