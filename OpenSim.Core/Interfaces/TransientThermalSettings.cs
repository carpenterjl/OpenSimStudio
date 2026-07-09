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

    /// <summary>Store every Nth step as a result frame. 0 (default) picks a stride
    /// automatically so at most ~60 frames are stored. The initial state and the final
    /// step are always stored.</summary>
    public int OutputStride { get; init; }
}
