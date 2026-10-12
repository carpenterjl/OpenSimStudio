using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Pcb.Import;

/// <summary>
/// Judges whether a plane layer that states no polarity — plain RS-274X, no
/// <c>%TF.FilePolarity</c>, no <c>%IPNEG%</c> — is a negative plane. Protel/Altium internal
/// planes export that way: the file draws the antipads and thermal reliefs and nothing else,
/// and the fabricator knows the convention from the layer. Read positive, such a file is a
/// sprinkle of copper discs on the holes and no plane at all (audit PCB-02; FU-22).
/// <para>
/// The evidence is what a negative plane's drawing cannot help having and a positive plane's
/// cannot have: it covers little of the board, every dark flash sits on a drilled hole (an
/// antipad or a thermal relief is concentric with its hole), and no region is large enough to
/// be a plane's pour. A positive plane layer, however sparse, is a pour: a region or a flash
/// covering most of the board. Only layers the set itself calls planes (X2 <c>Plane</c>, a
/// Protel <c>.gpN</c> extension, "plane" in the name) are judged; a signal layer that is only
/// through-hole pads looks the same and is positive.
/// </para>
/// </summary>
internal static class UndeclaredNegativePlane
{
    /// <summary>The drawing of a negative plane covers at most this fraction of the board.</summary>
    public const double MaxCoverage = 0.5;

    /// <summary>Of the dark flashes, at least this fraction must sit on a drilled hole.</summary>
    public const double MinFlashesOnHoles = 0.9;

    /// <summary>A dark region covering more of the board than this is a pour: the layer is positive.</summary>
    public const double MaxRegionCoverage = 0.05;

    /// <summary>
    /// Whether the layer is read as negative, with the evidence for the log. When the layer
    /// looks negative but the set has no drill file to check its flashes against, it stays
    /// positive and the note says so.
    /// </summary>
    public static (bool Negative, string? Note) Judge(GerberDocument document, LayerImage drawn,
        IReadOnlyList<Polygon2> outline, IReadOnlyList<Via> holes)
    {
        if (outline.Count == 0) return (false, null);           // nothing to invert within anyway
        double board = outline.Sum(p => p.Area());
        if (board <= 0) return (false, null);
        double coverage = drawn.TotalArea() / board;
        if (coverage > MaxCoverage) return (false, null);

        var dark = document.Ops.Where(op => op.Polarity == GerberPolarity.Dark).ToList();
        if (dark.Count == 0) return (false, null);              // an empty layer says nothing
        double largestRegion = dark.OfType<RegionOp>().SelectMany(r => r.Contours)
            .Select(c => Math.Abs(Polygon2.RingArea(c))).DefaultIfEmpty(0).Max();
        if (largestRegion > MaxRegionCoverage * board) return (false, null);

        var flashes = dark.OfType<FlashOp>().ToList();
        string drawing = $"its drawing covers {coverage * 100:g3} % of the board";
        if (flashes.Count == 0)
            // Only strokes: plane splits and the pullback from the edge. A positive plane
            // made of a few thin lines is not a plane.
            return (true, $"{drawing} with {dark.Count} stroke(s) and no flash or pour");
        if (holes.Count == 0)
            return (false, $"{drawing} with {flashes.Count} flash(es) and no pour, which is what a negative " +
                           "plane's drawing looks like, but the set has no drill file to check the flashes " +
                           "against; imaged as drawn");
        int onHoles = flashes.Count(f => holes.Any(h =>
            (h.Position - f.Position).Length <= 0.5 * h.Diameter + 0.1e-3));
        if (onHoles < MinFlashesOnHoles * flashes.Count) return (false, null);
        return (true, $"{drawing}, {onHoles} of its {flashes.Count} flashes sit on drilled holes and no " +
                      "region is a plane's pour");
    }
}
