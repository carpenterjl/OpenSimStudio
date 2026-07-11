namespace OpenSim.Rf;

/// <summary>
/// An infinite perfect-electric-conductor ground plane at z = <see cref="SurfaceZ"/>,
/// modeled by image theory: every source current is mirrored across the plane with its
/// horizontal components reversed and vertical component preserved, which is realized
/// geometrically by mirroring each element AND swapping its endpoints (the
/// <c>PlaneReturnComposer</c> convention) — no hand-tuned signs anywhere. The structure
/// must live strictly above the plane; an open wire END may sit exactly ON it, which
/// grounds that end (the monopole base). Fields below the plane are identically zero.
/// </summary>
public sealed record GroundPlane(double SurfaceZ);
