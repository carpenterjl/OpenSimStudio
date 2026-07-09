using OpenSim.Pcb.Import;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Meshing2D;

namespace OpenSim.Pcb.Inductance;

/// <summary>
/// Selects the board's trace centerlines that belong to one net: a centerline is the
/// net's when its midpoint lies inside one of the net's islands on the same layer
/// (the same containment rule the net extractor itself uses).
/// </summary>
public static class NetTraceExtractor
{
    public static IReadOnlyList<TraceCenterline> ForNet(PcbBoard board, CopperNet net)
    {
        var islandsByLayer = net.Islands
            .GroupBy(i => i.LayerOrder)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Shape).ToList());

        var result = new List<TraceCenterline>();
        foreach (var centerline in board.TraceCenterlines)
        {
            if (!islandsByLayer.TryGetValue(centerline.LayerOrder, out var shapes))
                continue;
            if (PlanarMesher.ContainsPoint(shapes, centerline.Midpoint))
                result.Add(centerline);
        }
        return result;
    }

    /// <summary>The board pads that belong to the net, by the same island-containment
    /// rule (pad center inside a net island on the pad's layer). These are the physical
    /// terminals a chain build can anchor to when no explicit pair is selected.</summary>
    public static IReadOnlyList<CopperPad> PadsForNet(PcbBoard board, CopperNet net)
    {
        var islandsByLayer = net.Islands
            .GroupBy(i => i.LayerOrder)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Shape).ToList());

        return board.Pads
            .Where(p => islandsByLayer.TryGetValue(p.LayerOrder, out var shapes)
                        && PlanarMesher.ContainsPoint(shapes, p.Center))
            .ToList();
    }

    /// <summary>The net's two pads farthest apart (planar distance, deterministic) as
    /// default chain terminals: the longest pad-to-pad run is the net's "main" current
    /// path, and anchoring there lets branched nets compose (dead branches pruned)
    /// instead of refusing. Null when the net has fewer than two pads — the chain
    /// builder then requires the classic non-branching open chain.</summary>
    public static (ChainTerminal Source, ChainTerminal Sink)? FarthestPadTerminals(
        PcbBoard board, CopperNet net)
    {
        var pads = PadsForNet(board, net);
        if (pads.Count < 2) return null;
        double best = -1;
        int bestI = 0, bestJ = 1;
        for (int i = 0; i < pads.Count; i++)
            for (int j = i + 1; j < pads.Count; j++)
            {
                double dx = pads[i].Center.X - pads[j].Center.X;
                double dy = pads[i].Center.Y - pads[j].Center.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 > best) { best = d2; bestI = i; bestJ = j; }
            }
        return (new ChainTerminal(pads[bestI].Center, pads[bestI].LayerOrder),
                new ChainTerminal(pads[bestJ].Center, pads[bestJ].LayerOrder));
    }
}
