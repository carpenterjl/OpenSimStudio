using OpenSim.Core.Numerics;

namespace OpenSim.Pcb.Inductance;

/// <summary>Conductor cross-section profile of a <see cref="TraceSegment3D"/>.</summary>
public enum SegmentProfile
{
    /// <summary>Rectangular bar, Width × Thickness — a copper trace.</summary>
    Bar,
    /// <summary>Solid round wire of diameter Width (uniform current); Thickness unused.</summary>
    RoundWire,
    /// <summary>Thin round tube of diameter Width with the current in the shell —
    /// a plated via barrel; Thickness is the wall (plating) thickness.</summary>
    RoundTube
}

/// <summary>
/// A straight conductor segment in 3D for PEEC composition: planar traces at their layer's
/// copper mid-plane z, via barrels as vertical tubes. For round profiles Width is the
/// DIAMETER. The direction Start → End is the assumed current direction — mutual-term
/// signs come from it, so chain builders must orient segments head-to-tail.
/// </summary>
public sealed record TraceSegment3D(Vector3D Start, Vector3D End, double Width, double Thickness,
    SegmentProfile Profile = SegmentProfile.Bar)
{
    public double Length => (End - Start).Length;

    /// <summary>Unit direction from start to end.</summary>
    public Vector3D Direction
    {
        get
        {
            double l = Length;
            return l > 0 ? (End - Start) / l : Vector3D.UnitX;
        }
    }
}
