using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;

namespace OpenSim.Rf;

/// <summary>
/// Maps a PCB trace chain (the PEEC path: bars at stackup z, via-barrel tubes) onto
/// thin-wire antenna segments: strips become their equivalent-radius wires (r = w/4 —
/// the standard flat-strip equivalence; copper thickness is negligible against width),
/// round profiles keep their surface radius. Chain junctions are welded to the shared
/// midpoint: the chain builder guarantees head-to-tail ORDER but its endpoints may
/// disagree by up to the junction tolerance (draws end anywhere on a pad), and the wire
/// grid demands exact connectivity.
/// </summary>
public static class TraceChainAntenna
{
    public static IReadOnlyList<WireSegment> FromChain(IReadOnlyList<TraceSegment3D> chain)
    {
        if (chain.Count == 0)
            throw new ArgumentException("The trace chain is empty.", nameof(chain));

        var starts = new Vector3D[chain.Count];
        var ends = new Vector3D[chain.Count];
        for (int i = 0; i < chain.Count; i++)
        {
            starts[i] = chain[i].Start;
            ends[i] = chain[i].End;
        }
        for (int i = 1; i < chain.Count; i++)
        {
            var joint = (ends[i - 1] + starts[i]) / 2;
            ends[i - 1] = joint;
            starts[i] = joint;
        }

        var wires = new List<WireSegment>(chain.Count);
        for (int i = 0; i < chain.Count; i++)
        {
            if ((ends[i] - starts[i]).Length <= 0) continue;      // welded away
            double radius = chain[i].Profile == SegmentProfile.Bar
                ? chain[i].Width / 4
                : chain[i].Width / 2;
            wires.Add(new WireSegment(starts[i], ends[i], radius));
        }
        return wires;
    }

    /// <summary>
    /// The WHOLE net as wires, branches included — the RF counterpart of
    /// <see cref="FromChain"/>. A pad-to-pad path with its side branches pruned is exact for
    /// a DC or low-frequency current (a dead-end branch carries none), and wrong for an
    /// antenna: an open stub carries a standing wave, loads the structure, and radiates. An
    /// inverted-F reduced to the path between its two farthest pads has lost its radiating
    /// arm.
    ///
    /// <para>Every graph segment becomes one wire between its two JUNCTION positions (the
    /// graph's own clustering, so wires that meet share a point exactly). If the net's
    /// centerlines fall into several disconnected pieces, one is kept — the piece nearest
    /// <paramref name="keepNear"/> when given, else the longest — and the rest is reported,
    /// never silently dropped.</para>
    /// </summary>
    public static TraceGraphAntenna FromGraph(TraceGraphResult graph, Vector3D? keepNear = null)
    {
        if (graph.Segments is null || graph.Ends is null || graph.Junctions is null)
            throw new ArgumentException(
                graph.FailureReason ?? "The trace graph is empty.", nameof(graph));
        int count = graph.Segments.Count;
        if (count == 0)
            throw new ArgumentException("The trace graph is empty.", nameof(graph));

        // Connected pieces over the junctions.
        var parent = new int[graph.Junctions.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int j)
        {
            while (parent[j] != j) j = parent[j] = parent[parent[j]];
            return j;
        }
        foreach (var (a, b) in graph.Ends) parent[Find(a)] = Find(b);

        var lengthOf = new Dictionary<int, double>();
        var nearestOf = new Dictionary<int, double>();
        for (int i = 0; i < count; i++)
        {
            int piece = Find(graph.Ends[i].Start);
            var a = graph.Junctions[graph.Ends[i].Start].Position;
            var b = graph.Junctions[graph.Ends[i].End].Position;
            lengthOf[piece] = lengthOf.GetValueOrDefault(piece) + (b - a).Length;
            if (keepNear is { } near)
            {
                double distance = Math.Min((a - near).Length, (b - near).Length);
                nearestOf[piece] = Math.Min(nearestOf.GetValueOrDefault(piece, double.MaxValue), distance);
            }
        }
        int kept = keepNear is null
            ? lengthOf.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key
            : nearestOf.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;

        var wires = new List<WireSegment>(count);
        var degree = new Dictionary<int, int>();
        int droppedSegments = 0;
        double droppedLength = 0;
        for (int i = 0; i < count; i++)
        {
            var (ja, jb) = graph.Ends[i];
            var a = graph.Junctions[ja].Position;
            var b = graph.Junctions[jb].Position;
            double length = (b - a).Length;
            if (Find(ja) != kept)
            {
                droppedSegments++;
                droppedLength += length;
                continue;
            }
            if (length <= 0) continue;
            var segment = graph.Segments[i];
            double radius = segment.Profile == SegmentProfile.Bar
                ? segment.Width / 4
                : segment.Width / 2;
            wires.Add(new WireSegment(a, b, radius));
            degree[ja] = degree.GetValueOrDefault(ja) + 1;
            degree[jb] = degree.GetValueOrDefault(jb) + 1;
        }
        if (wires.Count == 0)
            throw new ArgumentException("The trace graph has no segment of positive length.", nameof(graph));

        return new TraceGraphAntenna(wires,
            BranchNodes: degree.Count(kv => kv.Value >= 3),
            OpenEnds: degree.Count(kv => kv.Value == 1),
            DroppedPieces: lengthOf.Count - 1,
            DroppedSegments: droppedSegments,
            DroppedLengthMeters: droppedLength);
    }
}

/// <summary>A net's whole trace graph as antenna wires (<see cref="TraceChainAntenna.FromGraph"/>),
/// with what the topology was and what, if anything, was left out.</summary>
/// <param name="BranchNodes">Nodes where three or more wires meet.</param>
/// <param name="OpenEnds">Free wire ends (stub tips and the route's own ends).</param>
/// <param name="DroppedPieces">Disconnected pieces of the net that were NOT modelled.</param>
public sealed record TraceGraphAntenna(
    IReadOnlyList<WireSegment> Wires, int BranchNodes, int OpenEnds,
    int DroppedPieces, int DroppedSegments, double DroppedLengthMeters);
