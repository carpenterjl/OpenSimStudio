using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Meshing2D;

namespace OpenSim.Pcb.Import;

/// <summary>
/// Groups copper islands into connected nets. Islands on one layer are already maximal
/// (touching copper was unioned upstream), so the only cross-layer joins are plated vias.
/// A via bridges two layers ONLY where an actual pad (annular ring) covers it — a real via
/// connection has a pad flash on each connected layer, whereas a signal via merely passing
/// through a plane has an antipad (no pad) and must not merge the signal net into the plane.
/// Requiring a pad is what stops the whole board collapsing into one giant net.
/// </summary>
public static class NetExtractor
{
    public static IReadOnlyList<CopperNet> Extract(
        IReadOnlyList<CopperIsland> islands, IReadOnlyList<Via> vias, IReadOnlyList<CopperPad> pads)
    {
        var parent = Enumerable.Range(0, islands.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        // Index pads and islands by layer for fast per-via lookup.
        var padsByLayer = new Dictionary<int, List<CopperPad>>();
        foreach (var pad in pads)
            padsByLayer.GetOrAdd(pad.LayerOrder).Add(pad);

        var bounds = islands.Select(i => i.Bounds()).ToArray();
        var islandsByLayer = new Dictionary<int, List<int>>();
        for (int i = 0; i < islands.Count; i++)
            islandsByLayer.GetOrAdd(islands[i].LayerOrder).Add(i);

        // Vias that actually stitch two+ islands together, kept so the net can grow a 3D
        // barrel later. The owning net's root can still change as further vias union, so
        // resolve each bridge to its final root only after every union is done.
        var pendingBridges = new List<(int Island, ViaBridge Bridge)>();
        foreach (var via in vias)
        {
            if (!via.Plated) continue;
            var connect = new List<int>();
            var connectLayers = new List<int>();
            foreach (var (layer, layerPads) in padsByLayer)
            {
                if (!HasAnnularRing(layerPads, via)) continue;                  // concentric pad required
                int island = IslandContaining(via.Position, islandsByLayer, bounds, islands, layer);
                if (island >= 0) { connect.Add(island); connectLayers.Add(layer); }
            }
            for (int k = 1; k < connect.Count; k++)
                Union(connect[0], connect[k]);
            if (connect.Count >= 2)
                pendingBridges.Add((connect[0],
                    new ViaBridge(via, connectLayers.Distinct().OrderBy(l => l).ToList())));
        }

        var groups = new Dictionary<int, List<CopperIsland>>();
        for (int i = 0; i < islands.Count; i++)
            groups.GetOrAdd(Find(i)).Add(islands[i]);

        var bridgesByRoot = new Dictionary<int, List<ViaBridge>>();
        foreach (var (island, bridge) in pendingBridges)
            bridgesByRoot.GetOrAdd(Find(island)).Add(bridge);

        // Largest nets first, so the interesting conductors surface at the top of a list.
        return groups
            .OrderByDescending(kv => kv.Value.Sum(i => i.Area))
            .Select((kv, id) => new CopperNet(id + 1, kv.Value)
            {
                StitchingVias = bridgesByRoot.TryGetValue(kv.Key, out var b)
                    ? b : (IReadOnlyList<ViaBridge>)Array.Empty<ViaBridge>()
            })
            .ToList();
    }

    /// <summary>
    /// True when a pad forms an annular ring around the via on this layer: a pad roughly
    /// CONCENTRIC with the drill (centre within a registration tolerance) and larger than
    /// the hole. This is what a real via connection looks like; a plane's pour-fill flash
    /// that merely happens to overlap the drill is offset and gets rejected, so a signal
    /// via passing through a pour no longer merges its net into the plane.
    /// </summary>
    private static bool HasAnnularRing(List<CopperPad> layerPads, Via via)
    {
        // Allow slight drill-to-pad registration error; a true via pad is placed on the drill.
        double tol = 0.5 * via.Diameter + 0.1e-3;
        foreach (var pad in layerPads)
        {
            if ((pad.Center - via.Position).Length > tol) continue;             // must be concentric
            if (pad.Size < via.Diameter) continue;                              // pad must exceed the hole
            return true;
        }
        return false;
    }

    private static int IslandContaining(Point2 p, Dictionary<int, List<int>> islandsByLayer,
        (double MinX, double MinY, double MaxX, double MaxY)[] bounds,
        IReadOnlyList<CopperIsland> islands, int layer)
    {
        if (!islandsByLayer.TryGetValue(layer, out var list)) return -1;
        foreach (int i in list)
        {
            var b = bounds[i];
            if (p.X < b.MinX || p.X > b.MaxX || p.Y < b.MinY || p.Y > b.MaxY) continue;
            if (Contains(islands[i].Shape, p)) return i;
        }
        return -1;
    }

    private static bool Contains(Polygon2 polygon, Point2 p) =>
        PlanarMesher.ContainsPoint(new[] { polygon }, p);

    private static List<T> GetOrAdd<T>(this Dictionary<int, List<T>> map, int key)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>();
        return list;
    }
}
