using OpenSim.Core.Numerics;

namespace OpenSim.Core.Interfaces;

/// <summary>
/// Flux recovery for a scalar solve (DC conduction, steady heat conduction). Attached to
/// <see cref="SolveInput.FluxRecovery"/>; null is the solve as it always was, with the same
/// fields. When set, the solve also returns the recovered flux (superconvergent patch
/// recovery, one polynomial per material around every corner node), an element field of the
/// difference between the recovered and the computed flux, the estimated error of the flux
/// over the whole mesh in the energy norm, and the flux through every probe.
/// </summary>
public sealed record FluxRecoverySettings
{
    /// <summary>Surfaces whose integrated flux (current [A], heat flow [W]) is reported.</summary>
    public IReadOnlyList<FluxProbe> Probes { get; init; } = Array.Empty<FluxProbe>();
}

/// <summary>
/// A surface the flux is integrated over: a plane through <see cref="Origin"/> normal to
/// <see cref="Normal"/> (the flux counted is the one crossing in the normal's direction),
/// optionally only where it lies inside the box <see cref="BoxMin"/>…<see cref="BoxMax"/>;
/// or, when <see cref="FaceIds"/> is given, those boundary faces (the flux counted leaves the
/// body through them).
/// <para>
/// A whole plane is reported two ways. The conservative value weights the flux with a
/// function that steps from 0 to 1 across the plane, which is the discrete balance of the
/// part of the body on the plane's positive side — what the solve actually conserved, and
/// the more accurate of the two. The section value integrates the recovered flux over the
/// plane's cut through the elements. The two differ by the discretization error. A plane
/// limited to a box, or a set of faces, has the section value only: the box's sides inside
/// the body do not separate it into two parts.
/// </para>
/// </summary>
public sealed record FluxProbe
{
    public required string Name { get; init; }

    /// <summary>A point of the plane [m].</summary>
    public Vector3D Origin { get; init; }

    /// <summary>The plane's normal; need not be unit length.</summary>
    public Vector3D Normal { get; init; }

    /// <summary>The corners of an axis-aligned box the plane is limited to [m]; both or
    /// neither.</summary>
    public Vector3D? BoxMin { get; init; }
    public Vector3D? BoxMax { get; init; }

    /// <summary>Boundary faces instead of a plane.</summary>
    public IReadOnlyList<int>? FaceIds { get; init; }

    /// <summary>Whether this probe is a set of faces rather than a plane.</summary>
    public bool IsFaces => FaceIds is not null;

    /// <summary>The probe must name a surface: a non-zero normal, both box corners or none
    /// in the right order, or at least one face.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("A flux probe needs a name.");
        if (FaceIds is not null)
        {
            if (FaceIds.Count == 0)
                throw new InvalidOperationException($"Flux probe '{Name}' names no faces.");
            return;
        }
        if (!(Normal.Length > 0) || !double.IsFinite(Normal.Length))
            throw new InvalidOperationException($"Flux probe '{Name}' needs a non-zero normal.");
        if (BoxMin.HasValue != BoxMax.HasValue)
            throw new InvalidOperationException(
                $"Flux probe '{Name}' gives one corner of its box; give both or neither.");
        if (BoxMin is { } lo && BoxMax is { } hi && !(lo.X < hi.X && lo.Y < hi.Y && lo.Z < hi.Z))
            throw new InvalidOperationException(
                $"Flux probe '{Name}': every coordinate of the box's minimum corner must be below the maximum's.");
    }
}
