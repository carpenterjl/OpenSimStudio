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
/// <item>A draw that runs edge by edge along a dark region's contour is that pour's
/// outline stroke (how older CAD output rounds a zone's corners), not a trace: counting
/// it made a pour net "have traces" whose length was the pour's perimeter.</item>
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
        var outlineEdges = RegionEdges(document);

        var run = new List<Point2>();
        foreach (var op in document.Ops)
        {
            if (op is not DrawOp { Polarity: GerberPolarity.Dark } draw) continue;
            double width = draw.Aperture switch
            {
                CircleAperture c => c.Diameter,
                _ => 0
            };
            if (width <= 0) continue;                          // only round-aperture traces

            // Split the path at region-outline edges; each remaining run is trace.
            run.Clear();
            run.Add(draw.Path[0]);
            for (int i = 1; i < draw.Path.Count; i++)
            {
                if (outlineEdges.Contains(EdgeKey(draw.Path[i - 1], draw.Path[i])))
                {
                    EmitRun(run, width, emit);
                    run.Clear();
                }
                run.Add(draw.Path[i]);
            }
            EmitRun(run, width, emit);
        }
    }

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

    /// <summary>Every edge of every dark region contour, direction-free, on the parser's
    /// own coordinates (a stroke along a contour is emitted from the same vertex list, so
    /// the match is exact up to rounding — keyed on a 1 nm grid).</summary>
    private static HashSet<(long, long, long, long)> RegionEdges(GerberDocument document)
    {
        var edges = new HashSet<(long, long, long, long)>();
        foreach (var op in document.Ops)
        {
            if (op is not RegionOp { Polarity: GerberPolarity.Dark } region) continue;
            foreach (var contour in region.Contours)
                for (int i = 0; i < contour.Count; i++)
                    edges.Add(EdgeKey(contour[i], contour[(i + 1) % contour.Count]));
        }
        return edges;
    }

    private static (long, long, long, long) EdgeKey(Point2 a, Point2 b)
    {
        long ax = (long)Math.Round(a.X * 1e9), ay = (long)Math.Round(a.Y * 1e9);
        long bx = (long)Math.Round(b.X * 1e9), by = (long)Math.Round(b.Y * 1e9);
        return (ax, ay).CompareTo((bx, by)) <= 0 ? (ax, ay, bx, by) : (bx, by, ax, ay);
    }
}
