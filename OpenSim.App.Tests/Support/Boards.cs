using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;

namespace OpenSim.App.Tests.Support;

/// <summary>Small synthetic boards: one rectangular island per copper layer, each its own net.</summary>
public static class Boards
{
    public static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    /// <summary>A board with copper on layers 1..<paramref name="copperLayers"/>. With
    /// <paramref name="stackup"/> it stands for an IPC-2581 import (named layers, a file
    /// stackup); without, for a Gerber set (layers "L&lt;n&gt;", no stackup).</summary>
    public static PcbBoard Layers(int copperLayers, PcbStackupSettings? stackup = null)
    {
        var islands = new List<CopperIsland>();
        var nets = new List<CopperNet>();
        var layers = new List<BoardLayer>();
        for (int layer = 1; layer <= copperLayers; layer++)
        {
            // Smaller islands on deeper layers so the picker's "smallest containing island" is unambiguous.
            double half = 20e-3 - layer * 1e-3;
            var island = new CopperIsland(islands.Count, layer, $"L{layer}", Rect(-half, -half, half, half));
            islands.Add(island);
            nets.Add(new CopperNet(nets.Count + 1, new[] { island }) { Name = $"NET{layer}" });
            layers.Add(new BoardLayer($"layer{layer}.gbr",
                layer == 1 || layer == copperLayers ? GerberLayerType.CopperSignal : GerberLayerType.CopperPlane,
                layer, IsTopSide: layer == 1));
        }
        return new PcbBoard
        {
            Outline = new[] { Rect(-25e-3, -25e-3, 25e-3, 25e-3) },
            Islands = islands,
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = nets,
            Layers = layers,
            Warnings = Array.Empty<string>(),
            Stackup = stackup,
        };
    }
}
