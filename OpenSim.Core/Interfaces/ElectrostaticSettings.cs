using OpenSim.Core.Numerics;

namespace OpenSim.Core.Interfaces;

/// <summary>
/// Settings of an electrostatic solve. Attached to <see cref="SolveInput.Electrostatic"/>;
/// null (or <see cref="OpenBoundary"/> false) is the solve as it always was: the mesh
/// boundary no electrode claims is a zero-flux wall.
/// </summary>
public sealed record ElectrostaticSettings
{
    /// <summary>
    /// Treat the outer boundary of the mesh as open space rather than a zero-flux wall.
    /// Every boundary triangle no electrode claims carries the asymptotic condition
    /// ε·∂φ/∂n = −ε·φ·(r̂·n)/r about <see cref="Centre"/> — the exact condition for the
    /// 1/r field of the net charge, which a zero-flux wall cannot pass at all (an isolated
    /// conductor has no capacitance against a wall). The dipole and higher terms of the
    /// field are not absorbed; their energy beyond the boundary falls as (a/R)³ for a
    /// structure of size a inside a boundary at distance R, and the solve states a/R.
    /// </summary>
    public bool OpenBoundary { get; init; }

    /// <summary>Where the 1/r field is centred: null takes the centroid of every
    /// electrode's nodes, which is where the net charge sits to first order.</summary>
    public Vector3D? Centre { get; init; }
}
