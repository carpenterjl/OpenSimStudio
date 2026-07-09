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
}
