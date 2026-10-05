using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.Rf.Si.Layout;

/// <summary>
/// What the layout checks and the crosstalk scan ask of a board over and over: which copper
/// island lies under a point on a layer, which net an island belongs to, and which net each
/// trace centerline is part of. Built once per run.
/// </summary>
public sealed class BoardLayoutIndex
{
    private sealed record Entry(CopperIsland Island, PolygonSetIndex Shape,
        double MinX, double MinY, double MaxX, double MaxY, double Area);

    private readonly Dictionary<int, List<Entry>> _byLayer = new();
    private readonly Dictionary<CopperIsland, CopperNet> _netOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CopperIsland, double> _area = new(ReferenceEqualityComparer.Instance);

    public PcbBoard Board { get; }

    /// <summary>Copper layers of the board (what the islands show, or what the file's
    /// stackup lists when that is more).</summary>
    public int LayerCount { get; }

    /// <summary>Every centerline that lies in a net's copper, with that net.</summary>
    public IReadOnlyList<(TraceCenterline Trace, CopperNet Net)> Traces { get; }

    public BoardLayoutIndex(PcbBoard board)
    {
        Board = board;
        foreach (var island in board.Islands)
        {
            var (minX, minY, maxX, maxY) = island.Bounds();
            double area = island.Area;
            if (!_byLayer.TryGetValue(island.LayerOrder, out var list))
                _byLayer[island.LayerOrder] = list = new List<Entry>();
            list.Add(new Entry(island, new PolygonSetIndex(new[] { island.Shape }), minX, minY, maxX, maxY, area));
            _area[island] = area;
        }
        // Largest first, so a search for plane copper stops at the first island too small.
        foreach (var list in _byLayer.Values) list.Sort((a, b) => b.Area.CompareTo(a.Area));
        foreach (var net in board.Nets)
            foreach (var island in net.Islands)
                _netOf[island] = net;

        int fromCopper = board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder) : 1;
        int fromFile = board.Stackup is { DielectricGapThicknesses.Count: > 0 } s
            ? s.DielectricGapThicknesses.Count + 1 : 1;
        LayerCount = Math.Max(fromCopper, fromFile);

        var traces = new List<(TraceCenterline, CopperNet)>();
        foreach (var trace in board.TraceCenterlines)
            if (trace.Length > 0 && IslandAt(trace.LayerOrder, trace.Midpoint) is { } island
                && _netOf.TryGetValue(island, out var net))
                traces.Add((trace, net));
        Traces = traces;
    }

    /// <summary>The island under <paramref name="p"/> on a layer, or null over bare laminate.</summary>
    public CopperIsland? IslandAt(int layer, Point2 p)
    {
        if (!_byLayer.TryGetValue(layer, out var list)) return null;
        foreach (var e in list)
            if (p.X >= e.MinX && p.X <= e.MaxX && p.Y >= e.MinY && p.Y <= e.MaxY && e.Shape.Contains(p))
                return e.Island;
        return null;
    }

    /// <summary>The island of at least <paramref name="minArea"/> under <paramref name="p"/>,
    /// or null. Islands of one layer do not overlap, so this is <see cref="IslandAt"/> with
    /// the small ones left out — without visiting them, which is what makes a plane query
    /// on a routing layer of ten thousand islands affordable.</summary>
    public CopperIsland? LargeIslandAt(int layer, Point2 p, double minArea)
    {
        if (!_byLayer.TryGetValue(layer, out var list)) return null;
        foreach (var e in list)
        {
            if (e.Area < minArea) break;
            if (p.X >= e.MinX && p.X <= e.MaxX && p.Y >= e.MinY && p.Y <= e.MaxY && e.Shape.Contains(p))
                return e.Island;
        }
        return null;
    }

    public CopperNet? NetOf(CopperIsland island) => _netOf.TryGetValue(island, out var net) ? net : null;

    public double AreaOf(CopperIsland island) => _area.TryGetValue(island, out double a) ? a : island.Area;

    /// <summary>A name for the island's net, or for the island when it is in none.</summary>
    public string NameOf(CopperIsland island) =>
        NetOf(island) is { } net ? net.Name ?? $"Net {net.Id}" : $"copper island {island.Index} on L{island.LayerOrder}";

    public static string NameOf(CopperNet net) => net.Name ?? $"Net {net.Id}";
}
