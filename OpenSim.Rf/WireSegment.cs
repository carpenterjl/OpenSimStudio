using OpenSim.Core.Numerics;

namespace OpenSim.Rf;

/// <summary>A straight round wire piece for the thin-wire solver: axis from
/// <paramref name="A"/> to <paramref name="B"/>, surface radius
/// <paramref name="Radius"/> [m]. Strips are represented by their thin-wire
/// equivalent radius (w/4 for a flat strip of width w).</summary>
public sealed record WireSegment(Vector3D A, Vector3D B, double Radius)
{
    public double Length => (B - A).Length;
}
