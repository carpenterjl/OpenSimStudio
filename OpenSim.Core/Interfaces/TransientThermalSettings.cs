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

    /// <summary>Time step [s] of the fixed-step integrators (backward Euler, TR-BDF2), both
    /// stable at any size. For <see cref="TimeIntegrator.Adaptive"/> it is the output
    /// interval: the steps between are the integrator's own, and it lands on every
    /// stored frame's time.</summary>
    public required double TimeStep { get; init; }

    /// <summary>How time is integrated; see <see cref="TimeIntegrator"/>.</summary>
    public TimeIntegrator Integrator { get; init; } = TimeIntegrator.BackwardEuler;

    /// <summary><see cref="TimeIntegrator.Adaptive"/>: the largest temperature error [K]
    /// a step may add, as estimated by step doubling.</summary>
    public double Tolerance { get; init; } = 0.01;

    /// <summary>The temperature the run starts from, node by node — a steady solve's
    /// result, a measured map — in place of the uniform <see cref="InitialTemperature"/>.
    /// Nodes under a fixed temperature still start at their prescribed value. Null: uniform.</summary>
    public ThermalInitialState? InitialState { get; init; }

    /// <summary>How the heat capacity enters the step matrix; see
    /// <see cref="ThermalCapacity"/>.</summary>
    public ThermalCapacity Capacity { get; init; } = ThermalCapacity.Automatic;

    /// <summary>Store every Nth step as a result frame. 0 (default) picks a stride
    /// automatically so at most ~60 frames are stored. The initial state and the final
    /// step are always stored.</summary>
    public int OutputStride { get; init; }

    /// <summary>How the heat sources vary in time (duty cycle, pulse train, table). Null:
    /// constant, the solve as it always was.</summary>
    public PowerProfile? PowerProfile { get; init; }
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

/// <summary>How a transient thermal solve steps through time.</summary>
public enum TimeIntegrator
{
    /// <summary>Backward Euler at the fixed step: first order, damps everything, the
    /// solve as it always was. Takes every surface model (environment, radiation exchange,
    /// schedules).</summary>
    BackwardEuler,

    /// <summary>TR-BDF2 at the fixed step: a trapezoidal stage to 2 − √2 of the step, then
    /// BDF2 to its end — second order and L-stable (no ringing on a sudden load). Linear
    /// models only: conduction, convection conditions, a fixed film, lumped networks,
    /// power profiles.</summary>
    TrBdf2,

    /// <summary>TR-BDF2 with its step chosen by an error estimate (step doubling) to keep
    /// each step's error under <see cref="TransientThermalSettings.Tolerance"/>; it lands on
    /// every stored frame and every edge of the power profiles. Linear models only.</summary>
    Adaptive
}

/// <summary>
/// The temperature field a transient starts from.
/// </summary>
public sealed record ThermalInitialState
{
    /// <summary>Temperature of every mesh node [K].</summary>
    public required IReadOnlyList<double> NodalTemperature { get; init; }

    /// <summary>The thermal networks' node temperatures, by network and node name (a
    /// steady solve's <see cref="SolveOutput.Networks"/>). A node not listed starts at
    /// <see cref="TransientThermalSettings.InitialTemperature"/>; a node tied to its faces
    /// follows them.</summary>
    public IReadOnlyList<OpenSim.Core.Results.ThermalNetworkState>? Networks { get; init; }

    /// <summary>The state a steady thermal solve ended in: its temperature field and its
    /// networks' nodes.</summary>
    public static ThermalInitialState FromSteady(SolveOutput steady)
    {
        var field = steady.Fields.OfType<OpenSim.Core.Results.NodalScalarField>()
            .FirstOrDefault(f => f.Name == "Temperature")
            ?? throw new InvalidOperationException("The solve has no temperature field to start from.");
        return new ThermalInitialState { NodalTemperature = field.Values, Networks = steady.Networks };
    }
}
