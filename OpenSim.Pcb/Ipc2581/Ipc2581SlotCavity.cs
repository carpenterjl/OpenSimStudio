using OpenSim.Core.Geometry2D;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// A routed slot/cavity aggregated across its per-layer <c>SlotCavity</c> occurrences
/// (Altium repeats one named slot in every LayerFeature it passes through; Cadence puts
/// it in a DRILL-layer Set). The outline is a board CUTOUT for every slot; a PLATED
/// slot additionally bridges the copper it touches — the builder synthesizes a chain of
/// plated barrels along the outline's principal axis, reusing the existing via
/// machinery (a stated approximation for non-oblong outlines).
/// </summary>
public sealed record Ipc2581SlotCavity(
    string Name,
    bool Plated,
    IReadOnlyList<Point2> Outline,
    IReadOnlyList<string> ConductorLayers,
    string SpanFrom,
    string SpanTo);
