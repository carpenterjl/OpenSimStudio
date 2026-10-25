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

    /// <summary>FU-36's printed monopole as a two-layer Gerber board: on L1 a 1 × 30 mm trace
    /// (net ANT) standing 1 mm above a 40 × 30 mm pour (net GND), the feed between them at
    /// (0, 0.5 mm); on L2 a plane (net GND2), so the stackup panel has one gap.</summary>
    public static PcbBoard PrintedMonopole()
    {
        var trace = new CopperIsland(0, 1, "L1", Rect(-0.5e-3, 1e-3, 0.5e-3, 31e-3));
        var pour = new CopperIsland(1, 1, "L1", Rect(-20e-3, -30e-3, 20e-3, 0));
        var plane = new CopperIsland(2, 2, "L2", Rect(-20e-3, -30e-3, 20e-3, 0));
        return new PcbBoard
        {
            Outline = new[] { Rect(-25e-3, -35e-3, 25e-3, 35e-3) },
            Islands = new[] { trace, pour, plane },
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = new[]
            {
                new CopperNet(1, new[] { trace }) { Name = "ANT" },
                new CopperNet(2, new[] { pour }) { Name = "GND" },
                new CopperNet(3, new[] { plane }) { Name = "GND2" },
            },
            Layers = new[]
            {
                new BoardLayer("top.gbr", GerberLayerType.CopperSignal, 1, IsTopSide: true),
                new BoardLayer("bottom.gbr", GerberLayerType.CopperPlane, 2, IsTopSide: false),
            },
            Warnings = Array.Empty<string>(),
            Stackup = null,
        };
    }
}
