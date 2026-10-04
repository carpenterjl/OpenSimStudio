namespace OpenSim.Core.Interfaces;

/// <summary>
/// Time-integration settings for a transient thermal solve. Attached to
/// <see cref="SolveInput.TransientThermal"/>; ignored by every other solver.
/// </summary>
public sealed record TransientThermalSettings
{
    /// <summary>Uniform initial temperature [K] (nodes under a fixed-temperature
    /// condition start at their prescribed value instead).</summary>
    public required double InitialTemperature { get; init; }

    /// <summary>Total simulated time [s].</summary>
    public required double Duration { get; init; }

    /// <summary>Backward-Euler time step [s]. Unconditionally stable at any size;
    /// smaller steps only improve accuracy (O(Δt) truncation).</summary>
    public required double TimeStep { get; init; }

    /// <summary>How the heat capacity enters the step matrix; see
    /// <see cref="ThermalCapacity"/>.</summary>
    public ThermalCapacity Capacity { get; init; } = ThermalCapacity.Automatic;

    /// <summary>Store every Nth step as a result frame. 0 (default) picks a stride
    /// automatically so at most ~60 frames are stored. The initial state and the final
    /// step are always stored.</summary>
    public int OutputStride { get; init; }
}

/// <summary>
/// The heat-capacity matrix of the transient thermal solve.
/// <para>
/// The CONSISTENT matrix (∫ρc·NᵢNⱼ) couples neighbouring nodes. Backward Euler with it is
/// stable at any step but not monotone at a small one: below Δt ≈ h²/(6α) for element
/// size h and diffusivity α, the node next to a suddenly heated one first moves the
/// WRONG way, by up to about 1 % of the step. FR4 on 1 mm elements is below that bound
/// for any step under 1.2 s. The LUMPED matrix (each node holds its own share, ρc·V/4
/// per element) has no such coupling and keeps a step response between its bounds on a
/// mesh without obtuse angles, at the price of a little more spatial smearing.
/// </para>
/// </summary>
public enum ThermalCapacity
{
    /// <summary>Lumped when the time step is below the monotone bound of some element,
    /// consistent otherwise. The log says which.</summary>
    Automatic,

    /// <summary>Always the consistent matrix; a step below the bound is warned about.</summary>
    Consistent,

    /// <summary>Always the lumped matrix.</summary>
    Lumped
}
