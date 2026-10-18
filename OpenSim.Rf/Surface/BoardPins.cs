using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.Rf.Surface;

/// <summary>A plated via that can stand as a vertical pin of a pin-array solve on a board island: it
/// lands on the island's copper and its barrel reaches down to the ground layer.</summary>
/// <param name="Position">The via's centre (board coordinates) [m].</param>
/// <param name="RadiusMeters">The drill radius, taken as the pin's tube radius [m].</param>
/// <param name="ShortsToGround">The net's own via joins the island to the ground layer's copper —
/// a shorting pin. Otherwise the barrel passes the ground layer through an antipad, the way a feed
/// from the far side of the plane does — a port.</param>
public sealed record BoardPinCandidate(Point2 Position, double RadiusMeters, bool ShortsToGround, string Description);

/// <summary>
/// Pins from a board's own vias: on a patch or an inverted-F printed over a plane, a shorting via
/// joins the radiator to the plane — so the importer puts both in ONE net, and the via is one of
/// that net's stitching vias between the two layers — while a feed via rises through the plane's
/// antipad without touching it. Both are vertical currents from the ground to the island, which is
/// what <see cref="SurfaceMomSolver.SolvePins(SurfaceStructure, OpenSim.Rf.Layered.LayeredKernelTable, IReadOnlyList{VerticalPin}, IReadOnlyList{NamedSurfacePort})"/> models.
/// </summary>
public static class BoardPins
{
    /// <summary>Every plated via inside <paramref name="island"/> (not in one of its holes) whose
    /// plated barrel spans from the island's layer to <paramref name="groundLayer"/>, in the board's
    /// via order.</summary>
    public static IReadOnlyList<BoardPinCandidate> OnIsland(PcbBoard board, CopperNet net, CopperIsland island,
        int groundLayer)
    {
        if (groundLayer <= island.LayerOrder)
            throw new ArgumentOutOfRangeException(nameof(groundLayer),
                $"The ground layer L{groundLayer} must lie below the island's layer L{island.LayerOrder}.");
        int layerCount = board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder) : 1;
        var result = new List<BoardPinCandidate>();
        foreach (var via in board.Vias)
        {
            if (!via.Plated || !(via.Diameter > 0)) continue;
            if (!island.Shape.Contains(via.Position)) continue;
            if (via.PlatedSpan(layerCount) is not { } span || span.Top > island.LayerOrder || span.Bottom < groundLayer)
                continue;
            bool shorts = net.StitchingVias.Any(b =>
                (ReferenceEquals(b.Via, via) || (b.Via.Position - via.Position).Length < 1e-9)
                && b.Layers.Contains(island.LayerOrder) && b.Layers.Contains(groundLayer));
            result.Add(new BoardPinCandidate(via.Position, via.Diameter / 2, shorts,
                $"via at ({via.Position.X * 1e3:g5}, {via.Position.Y * 1e3:g5}) mm, ⌀{via.Diameter * 1e3:g3} mm — "
                + (shorts
                    ? $"joins L{island.LayerOrder} to the ground on L{groundLayer} (shorting pin)"
                    : $"passes L{groundLayer} without touching it (feed)")));
        }
        return result;
    }
}
