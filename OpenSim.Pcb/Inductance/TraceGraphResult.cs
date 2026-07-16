using OpenSim.Core.Numerics;

namespace OpenSim.Pcb.Inductance;

/// <summary>
/// A net's conductor topology as a graph — the junction-clustered segment list the
/// chain builder constructs internally, exposed for consumers that need the whole
/// network rather than one ordered path. The DC resistance solve is the consumer:
/// branches and parallel paths (the chain extraction's typed "a network solve is
/// required" failure) are exactly what a nodal analysis handles. Either the graph
/// fields or <see cref="FailureReason"/> are non-null, never both.
/// </summary>
/// <param name="Segments">The composed conductor segments (traces at their layer
/// mid-plane z, via barrels as vertical tubes, copper bridges as bars) — the same
/// recipe the 3D chain uses, so the two agree on what copper exists.</param>
/// <param name="Ends">Per segment, the two junction indices its endpoints clustered
/// to — the graph's edge list, parallel to <paramref name="Segments"/>.</param>
/// <param name="Junctions">The clustered junction nodes (position + the matching
/// tolerance the junction absorbed).</param>
/// <param name="LayerZ">The stackup z frame the graph was built in: copper layer
/// order → (zLo, zHi) — needed to map pad terminals onto junctions.</param>
/// <param name="CopperBridges">How many straight bars were composed through
/// connecting copper (trace → pad/fill → trace routing), surviving the stub drop —
/// surfaced so consumers can state the approximation.</param>
/// <param name="FirstBridgeIndex">Index of the first bridge bar in
/// <paramref name="Segments"/> (bridges are appended, so they are contiguous at the
/// tail); equals the segment count when no bridges survive.</param>
public sealed record TraceGraphResult(
    IReadOnlyList<TraceSegment3D>? Segments,
    IReadOnlyList<(int Start, int End)>? Ends,
    IReadOnlyList<(Vector3D Position, double Tolerance)>? Junctions,
    IReadOnlyDictionary<int, (double zLo, double zHi)>? LayerZ,
    int CopperBridges,
    int FirstBridgeIndex,
    string? FailureReason)
{
    public static TraceGraphResult Failure(string reason) =>
        new(null, null, null, null, 0, 0, reason);
}
