namespace OpenSim.Pcb.Import;

/// <summary>
/// The board-preview visibility rule, kept UI-free so it is testable: an island is shown
/// when its net's own toggle is on AND its specific layer is enabled. Filtering happens
/// per island (not per net) so a multi-layer net loses exactly the copper on a disabled
/// layer while its other layers stay visible.
/// </summary>
public static class NetVisibility
{
    public static IEnumerable<CopperIsland> VisibleIslands(
        IEnumerable<CopperNet> nets,
        Func<CopperNet, bool> netVisible,
        Func<int, bool> layerEnabled) =>
        nets.Where(netVisible)
            .SelectMany(net => net.Islands)
            .Where(island => layerEnabled(island.LayerOrder));

    /// <summary>
    /// Whether a net belongs in the net list at all: a net whose copper lives only on
    /// disabled layers is delisted, but a net with stitching vias always stays — it
    /// spans the stack, so no single layer toggle can remove all of it physically.
    /// </summary>
    public static bool IsListed(CopperNet net, Func<int, bool> layerEnabled) =>
        net.StitchingVias.Count > 0 || net.Layers.Any(layerEnabled);
}
