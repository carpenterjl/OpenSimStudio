namespace OpenSim.Rf.Surface;

/// <summary>
/// A coaxial probe feed for the layered (microstrip) solver: a vertical PEC tube of
/// radius <see cref="RadiusMeters"/> running from the ground plane (z = 0) to the top
/// of the slab (z = d) at lateral position (<see cref="X"/>, <see cref="Y"/>), driven
/// by a delta-gap voltage at its BASE — a REAL port voltage between the probe and the
/// ground, retiring the series-gap ambiguity of the edge-fed model. The tube current
/// is discretized with <see cref="Segments"/> triangular rooftop bases; the top node
/// couples into the patch metal through the vertex-anchored attachment mode (the probe
/// position must be a mesh vertex — the mesh builder snaps or inserts it).
///
/// The reduced thin-wire kernel needs element length ≳ 2·radius, so a slab too thin
/// for the bore is a TYPED failure at assembly, never an approximation.
/// </summary>
public sealed record ProbeFeed(double X, double Y, double RadiusMeters, int Segments = 3)
{
    public double RadiusMeters { get; } = RadiusMeters > 0
        ? RadiusMeters
        : throw new ArgumentOutOfRangeException(nameof(RadiusMeters),
            "The probe radius must be positive.");

    public int Segments { get; } = Segments >= 2
        ? Segments
        : throw new ArgumentOutOfRangeException(nameof(Segments),
            "The probe needs at least 2 segments across the slab (junction + base need distinct elements).");
}
