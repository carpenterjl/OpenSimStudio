using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Inductance;

/// <summary>A straight rectangular conductor segment on a copper layer.</summary>
public sealed record TraceSegment(Point2 Start, Point2 End, double Width, double Thickness)
{
    public double Length => (End - Start).Length;

    /// <summary>Unit direction from start to end.</summary>
    public Point2 Direction
    {
        get
        {
            double l = Length;
            return l > 0 ? (End - Start) * (1.0 / l) : new Point2(1, 0);
        }
    }
}

/// <summary>
/// Extracts straight rectangular trace segments from a parsed Gerber layer's draw
/// records (the pre-polygonization centrelines the parser retains), giving each the
/// copper thickness from the stackup. Only DARK round-aperture draws that are not the
/// outline of a filled region become conductors — they are the traces; flashes and
/// regions are pads/pours handled elsewhere.
///
/// <list type="bullet">
/// <item>A CLEAR draw removes copper. The common "clear a wide channel, then draw the
/// trace in it" idiom used to leave two coincident centerlines, and the wider (the
/// clearance) won the de-duplication — a 0.2 mm trace priced as 0.6 mm.</item>
/// <item>A draw that runs along a dark region's edge is that pour's outline stroke (how
/// older CAD output rounds a zone's corners), not a trace: counting it made a pour net
/// "have traces" whose length was the pour's perimeter. A segment is the outline when its
/// ends and three interior points all lie within <see cref="OutlineTolerance"/> of a
/// region's boundary — half the stroke width, so a stroke with its edge on the region's
/// edge counts too, plus 20 µm for the two tessellations — whatever vertices it was drawn
/// with (FU-23: matching the region's own edges exactly missed an outline drawn with an
/// extra vertex, in two halves, or with arcs tessellated differently). A trace running
/// beside a pour at any real clearance is farther from the edge than that.</item>
/// <item>Arc chords are tessellated far finer than a trace width. The chain builder
/// drops segments shorter than half the narrowest width as stubs, which removed whole
/// arcs of wide traces; chords are therefore accumulated until they clear half the
/// draw's own width, the tail folded into the last chord so the path's ends stay where
/// the file put them (the rule the IPC-2581 reader already applied).</item>
/// </list>
/// </summary>
public sealed class TraceSegmenter
{
    public IReadOnlyList<TraceSegment> Segment(GerberDocument document, double copperThickness)
    {
        var segments = new List<TraceSegment>();
        Visit(document, (a, b, width) => segments.Add(new TraceSegment(a, b, width, copperThickness)));
        return segments;
    }

    /// <summary>
    /// The same aperture rule as <see cref="Segment"/>, but producing layer-tagged
    /// centerlines for retention on the imported board (thickness is not yet known at
    /// import time — the stackup supplies it later).
    /// </summary>
    public static IReadOnlyList<Import.TraceCenterline> Centerlines(GerberDocument document, int layerOrder)
    {
        var centerlines = new List<Import.TraceCenterline>();
        Visit(document, (a, b, width) =>
            centerlines.Add(new Import.TraceCenterline(layerOrder, a, b, width)));
        return centerlines;
    }

    private static void Visit(GerberDocument document, Action<Point2, Point2, double> emit)
    {
        if (document.IsNegative) return;                       // draws are clearances, not traces
        var draws = document.Ops
            .OfType<DrawOp>()
            .Where(d => d.Polarity == GerberPolarity.Dark && d.Aperture is CircleAperture { Diameter: > 0 })
            .ToList();                                         // only round-aperture traces
        if (draws.Count == 0) return;
        var boundary = RegionBoundary.Of(document,
            cell: draws.Max(d => OutlineTolerance(((CircleAperture)d.Aperture).Diameter)));

        var run = new List<Point2>();
        foreach (var draw in draws)
        {
            double width = ((CircleAperture)draw.Aperture).Diameter;
            double tolerance = OutlineTolerance(width);

            // Split the path at the segments that run along a region's edge; each
            // remaining run is trace.
            run.Clear();
            run.Add(draw.Path[0]);
            for (int i = 1; i < draw.Path.Count; i++)
            {
                if (boundary is not null && boundary.Hugs(draw.Path[i - 1], draw.Path[i], tolerance))
                {
                    EmitRun(run, width, emit);
                    run.Clear();
                }
                run.Add(draw.Path[i]);
            }
            EmitRun(run, width, emit);
        }
    }

    /// <summary>How far a stroke's centreline may lie from a region's edge and still be that
    /// region's outline stroke [m]: half the stroke (its edge on the region's edge) plus
    /// <see cref="OutlineSlack"/>.</summary>
    public static double OutlineTolerance(double width) => width / 2 + OutlineSlack;

    /// <summary>Room for the region's and the stroke's arc tessellations to differ [m]: two
    /// chord deviations at the parser's 5 µm, doubled. Any real clearance is far wider.</summary>
    public const double OutlineSlack = 20e-6;

    /// <summary>Emits one polyline run as chords longer than half the width (see the
    /// class remarks); a run shorter than that overall is kept as its single segment and
    /// the chain builder decides its fate.</summary>
    private static void EmitRun(List<Point2> path, double width, Action<Point2, Point2, double> emit)
    {
        if (path.Count < 2) return;
        double tolerance = width / 2;
        var chords = new List<(Point2 A, Point2 B)>();
        var anchor = path[0];
        for (int i = 1; i < path.Count; i++)
        {
            if ((path[i] - anchor).Length <= tolerance) continue;
            chords.Add((anchor, path[i]));
            anchor = path[i];
        }
        var last = path[^1];
        if ((last - anchor).Length > 0)
        {
            if (chords.Count > 0) chords[^1] = (chords[^1].A, last);
            else chords.Add((anchor, last));
        }
        foreach (var (a, b) in chords)
            if ((b - a).Length > 0)
                emit(a, b, width);
    }

    /// <summary>The edges of every dark region contour of a document, indexed for "does a
    /// point lie within r of the boundary" at r up to the grid cell.</summary>
    private sealed class RegionBoundary
    {
        private readonly SegmentGrid _grid;

        private RegionBoundary(List<(Point2 A, Point2 B)> edges, double cell) =>
            _grid = new SegmentGrid(edges, cell);

        public static RegionBoundary? Of(GerberDocument document, double cell)
        {
            var edges = new List<(Point2, Point2)>();
            foreach (var op in document.Ops)
            {
                if (op is not RegionOp { Polarity: GerberPolarity.Dark } region) continue;
                foreach (var contour in region.Contours)
                    for (int i = 0; i < contour.Count; i++)
                        edges.Add((contour[i], contour[(i + 1) % contour.Count]));
            }
            return edges.Count == 0 ? null : new RegionBoundary(edges, cell);
        }

        /// <summary>Whether the segment a→b runs along the boundary: its ends and the three
        /// quarter points all within <paramref name="tolerance"/> of some region edge. A
        /// segment that merely starts or ends on the boundary (a trace leaving the pour) or
        /// cuts across a corner has an interior point well off it.</summary>
        public bool Hugs(Point2 a, Point2 b, double tolerance)
        {
            for (int k = 0; k <= 4; k++)
                if (!_grid.AnyWithin(a + (b - a) * (k / 4.0), tolerance))
                    return false;
            return true;
        }
    }
}
