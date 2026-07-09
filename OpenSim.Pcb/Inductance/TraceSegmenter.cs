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
/// copper thickness from the stackup. Only round-aperture draws become conductors —
/// they are the traces; flashes and regions are pads/pours handled elsewhere.
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
        foreach (var op in document.Ops)
        {
            if (op is not DrawOp draw) continue;
            double width = draw.Aperture switch
            {
                CircleAperture c => c.Diameter,
                _ => 0
            };
            if (width <= 0) continue;                          // only round-aperture traces

            for (int i = 1; i < draw.Path.Count; i++)
            {
                var a = draw.Path[i - 1];
                var b = draw.Path[i];
                if ((b - a).Length > 0)
                    emit(a, b, width);
            }
        }
    }
}
