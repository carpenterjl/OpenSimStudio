using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Import;

/// <summary>
/// The centerline of one straight trace draw on a copper layer, captured at import time
/// (a round-aperture Gerber draw, or an IPC-2581 Line/Arc conductor feature with arcs as
/// chords). Retained on <see cref="PcbBoard.TraceCenterlines"/> because the polygon union
/// that produces copper islands destroys the centerline information the PEEC inductance
/// estimator needs.
/// </summary>
public sealed record TraceCenterline(int LayerOrder, Point2 Start, Point2 End, double Width)
{
    public double Length => (End - Start).Length;
    public Point2 Midpoint => (Start + End) * 0.5;
}
