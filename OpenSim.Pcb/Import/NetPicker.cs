using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Import;

/// <summary>
/// Viewport net picking, kept UI-free so it is testable. The click ray is intersected
/// with every copper layer's own z-plane — not one shared board plane, because with an
/// angled camera the parallax between layer heights shifts each layer's hit point
/// laterally, and testing all islands at a single plane's hit point selects whatever
/// happens to sit there instead of the trace under the cursor. Each island is
/// containment-tested at its own layer's hit point; the smallest containing island wins
/// so a trace is picked over the plane beneath it.
/// </summary>
public static class NetPicker
{
    public static CopperNet? Pick(
        IEnumerable<CopperNet> nets,
        Func<CopperNet, bool> netVisible,
        Func<int, bool> layerEnabled,
        IReadOnlyDictionary<int, double> layerZ,
        (double X, double Y, double Z) rayOrigin,
        (double X, double Y, double Z) rayDirection)
    {
        // A ray grazing the board plane has no usable per-layer intersection.
        if (Math.Abs(rayDirection.Z) < 1e-12) return null;

        var hits = new Dictionary<int, Point2>();
        foreach (var (layer, z) in layerZ)
        {
            double t = (z - rayOrigin.Z) / rayDirection.Z;
            hits[layer] = new Point2(rayOrigin.X + t * rayDirection.X,
                                     rayOrigin.Y + t * rayDirection.Y);
        }

        CopperNet? best = null;
        double bestArea = double.MaxValue;
        foreach (var net in nets)
        {
            if (!netVisible(net)) continue;
            foreach (var island in net.Islands)
            {
                if (!layerEnabled(island.LayerOrder) || island.Area >= bestArea) continue;
                if (!hits.TryGetValue(island.LayerOrder, out var p)) continue;
                if (Meshing2D.PlanarMesher.ContainsPoint(new[] { island.Shape }, p))
                {
                    bestArea = island.Area;
                    best = net;
                }
            }
        }
        return best;
    }
}
