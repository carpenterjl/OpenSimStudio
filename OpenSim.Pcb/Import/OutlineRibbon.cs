using OpenSim.Core.Geometry2D;

namespace OpenSim.Pcb.Import;

/// <summary>
/// In-plane ribbon quads for drawing a polygon outline as ordinary triangles instead of
/// screen-space lines. Helix's LinesVisual3D re-tessellates every segment on every
/// camera move (cost ∝ segment count, on the UI thread); a ribbon mesh is built once and
/// frozen, so camera interaction costs no geometry work at all. The width is fixed in
/// world units — the preview reads like a line near top-down, and thins honestly when
/// zoomed out instead of pretending to be screen-space.
/// </summary>
public static class OutlineRibbon
{
    /// <summary>One outline edge widened into a quad: corners in fan order
    /// (A−n, A+n, B+n, B−n), n = the edge's in-plane unit normal × half-width.</summary>
    public readonly record struct Quad(Point2 A0, Point2 A1, Point2 B1, Point2 B0);

    /// <summary>
    /// The ribbon quads of one closed ring (the ring's closing edge included). Degenerate
    /// edges — shorter than a millionth of the ribbon width — carry no drawable area and
    /// are skipped rather than producing zero-normal quads.
    /// </summary>
    public static IEnumerable<Quad> Quads(IReadOnlyList<Point2> ring, double halfWidth)
    {
        double minLength = halfWidth * 1e-6;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= minLength) continue;

            double nx = -dy / length * halfWidth;
            double ny = dx / length * halfWidth;
            yield return new Quad(
                new Point2(a.X - nx, a.Y - ny),
                new Point2(a.X + nx, a.Y + ny),
                new Point2(b.X + nx, b.Y + ny),
                new Point2(b.X - nx, b.Y - ny));
        }
    }
}
