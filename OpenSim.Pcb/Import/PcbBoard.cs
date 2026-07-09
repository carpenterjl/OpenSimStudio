using OpenSim.Core.Model;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Import;

/// <summary>
/// One maximal connected copper region on a single layer (the polygon engine already
/// unions touching copper, so each island is electrically continuous within its layer).
/// </summary>
public sealed record CopperIsland(
    int Index,
    int LayerOrder,          // 1 = top … N = bottom
    string LayerName,
    Polygon2 Shape)
{
    public double Area => Shape.Area();

    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Shape.Outer)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return (minX, minY, maxX, maxY);
    }
}

/// <summary>A drilled hole. Plated holes electrically bridge the copper layers they pass through.</summary>
public sealed record Via(Point2 Position, double Diameter, bool Plated);

/// <summary>
/// A plated via that actually stitches a net's islands together (it has a concentric
/// annular ring on each <see cref="Layers"/> entry), together with the copper layer orders
/// it joins. Retained on the net so the mesher can extrude a real 3D barrel through the
/// dielectric between those layers rather than collapsing them to one plane.
/// </summary>
public sealed record ViaBridge(Via Via, IReadOnlyList<int> Layers);

/// <summary>
/// A copper pad = a pad-sized aperture flash (D03). Retained separately from the unioned
/// island geometry because a via only electrically bridges two layers where an actual pad
/// (annular ring) covers it — distinguishing a real via connection from a signal via that
/// merely passes through a plane. Also the selectable electrode footprint for the sim.
/// </summary>
public sealed record CopperPad(int LayerOrder, Point2 Center, Polygon2 Shape, double Size);

/// <summary>
/// A connected copper net: the set of islands joined into one conductor (islands on one
/// layer that touch are already merged; islands on different layers are joined by plated
/// vias). Spans one layer for a simple trace, several for a via-stitched net.
/// </summary>
public sealed record CopperNet(int Id, IReadOnlyList<CopperIsland> Islands)
{
    /// <summary>Plated vias (with annular rings) that join this net's islands across layers.</summary>
    public IReadOnlyList<ViaBridge> StitchingVias { get; init; } = Array.Empty<ViaBridge>();

    /// <summary>
    /// The design's net name when the source format carries one (IPC-2581); null for
    /// formats without netlists (Gerber), where the synthesized "Net {Id}" is used.
    /// </summary>
    public string? Name { get; init; }

    public double Area => Islands.Sum(i => i.Area);
    public IReadOnlyList<int> Layers => Islands.Select(i => i.LayerOrder).Distinct().OrderBy(l => l).ToList();
    public bool IsSingleLayer => Layers.Count == 1;

    /// <summary>A short human label, e.g. "RS+ — L1 (12.4 mm²)" or "Net 3 — L1 (12.4 mm²)".</summary>
    public string Label => $"{Name ?? $"Net {Id}"} — " +
                           $"{(IsSingleLayer ? $"L{Layers[0]}" : $"L{string.Join("+", Layers)}")}" +
                           $" ({Area * 1e6:g3} mm²)";
}

/// <summary>The parsed full board: outline, all copper islands, vias, and extracted nets.</summary>
public sealed class PcbBoard
{
    public required IReadOnlyList<Polygon2> Outline { get; init; }
    public required IReadOnlyList<CopperIsland> Islands { get; init; }
    public required IReadOnlyList<CopperPad> Pads { get; init; }
    public required IReadOnlyList<Via> Vias { get; init; }
    public required IReadOnlyList<CopperNet> Nets { get; init; }
    public required IReadOnlyList<BoardLayer> Layers { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// The physical stackup read from the source file (per-layer copper and per-gap
    /// dielectric thicknesses), when the format carries one (IPC-2581). Null for Gerber
    /// sets, which have no stackup information — the UI then falls back to defaults.
    /// </summary>
    public PcbStackupSettings? Stackup { get; init; }

    /// <summary>
    /// Trace centerlines captured at import time, for the PEEC impedance estimator: from
    /// Gerber draw records (round apertures only — the traces) or from IPC-2581 Line/Arc
    /// conductor features. Empty when the file holds only flashes, pours, and regions.
    /// </summary>
    public IReadOnlyList<TraceCenterline> TraceCenterlines { get; init; } = Array.Empty<TraceCenterline>();
}
