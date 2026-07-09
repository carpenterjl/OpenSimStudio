using OpenSim.Core.Numerics;

namespace OpenSim.Pcb.Inductance;

/// <summary>Loop inductance of a chain returning through a copper plane, or the typed
/// reason it could not be composed. Exactly one of the two is non-null.</summary>
public sealed record PlaneReturnReport(
    double? LoopInductanceHenries,
    string? FailureReason,
    IReadOnlyList<string> Assumptions);

/// <summary>
/// Loop inductance of a chain over an infinite perfect-conductor return plane by image
/// theory: every segment is mirrored across the plane's copper surface FACING the chain
/// with its current reversed (mirror z and swap the endpoints: horizontal current flips,
/// vertical current is preserved — the correct PEC image), and
/// <c>L_plane = L_partial(chain) + M(chain, image)</c>. NOT the wire+image PAIR
/// inductance L_chain + L_image − 2|M|: by flux symmetry the pair links exactly twice
/// the flux of the wire-over-plane loop, so the plane loop is the ½-pair — for a single
/// wire at height h this reduces to L_self − M(2h) and the classic (µ₀/2π)·ln(2h/r)
/// per unit length.
/// </summary>
public sealed class PlaneReturnComposer
{
    private readonly LoopComposer _composer = new();

    /// <summary>
    /// <paramref name="planeSurfaceZ"/> is the z of the plane copper surface facing the
    /// chain (image theory mirrors across the conductor boundary — the classic ln(2h/r)
    /// measures h from wire axis to plane surface).
    /// </summary>
    public PlaneReturnReport Compose(IReadOnlyList<TraceSegment3D> chain, double planeSurfaceZ)
    {
        if (chain.Count == 0)
            return Failure("the chain is empty");

        // Every segment must sit strictly on ONE side of the plane surface — a chain
        // touching or crossing its own return plane has no image-theory answer.
        double minZ = chain.Min(s => Math.Min(s.Start.Z, s.End.Z));
        double maxZ = chain.Max(s => Math.Max(s.Start.Z, s.End.Z));
        if (minZ <= planeSurfaceZ && maxZ >= planeSurfaceZ)
            return Failure(
                $"the chain touches or crosses the return plane at z = {planeSurfaceZ * 1e3:g4} mm " +
                "(image theory needs the whole chain on one side)");
        double clearance = Math.Min(Math.Abs(minZ - planeSurfaceZ), Math.Abs(maxZ - planeSurfaceZ));
        if (clearance <= 0)
            return Failure("the chain lies in the plane surface");

        var image = chain
            .Select(s => s with
            {
                // Geometric mirror flips z; swapping the endpoints then reverses the
                // horizontal current components and preserves the vertical ones.
                Start = Mirror(s.End, planeSurfaceZ),
                End = Mirror(s.Start, planeSurfaceZ)
            })
            .ToList();

        double partial = _composer.Compose(chain).LoopInductance;
        double mutual = new MutualCouplingAnalyzer().MutualBetween(chain, image);
        double loop = partial + mutual;

        var assumptions = new[]
        {
            "Infinite perfect-conductor return plane; the return current flows entirely in the plane.",
            $"Image conductors mirrored at the plane surface z = {planeSurfaceZ * 1e3:g4} mm " +
            "(the copper face toward the chain).",
            "DC / uniform current distribution (no skin or proximity effect).",
            "Mutual terms are the exact straight-filament Neumann solution (Grover)."
        };
        return new PlaneReturnReport(loop, null, assumptions);
    }

    private static Vector3D Mirror(Vector3D p, double planeZ) =>
        new(p.X, p.Y, 2 * planeZ - p.Z);

    private static PlaneReturnReport Failure(string reason) =>
        new(null, reason, Array.Empty<string>());
}
