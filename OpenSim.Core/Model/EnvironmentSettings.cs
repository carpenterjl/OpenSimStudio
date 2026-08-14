using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>What surrounds the bodies being simulated.</summary>
public enum MediumKind
{
    /// <summary>Free space: no fluid at all, so the only path to the surroundings is
    /// thermal RADIATION. Convection is not "small" here, it is absent.</summary>
    Vacuum,

    /// <summary>A quiescent fluid: heat leaves by NATURAL convection (buoyancy-driven,
    /// so the film coefficient depends on the surface-to-ambient temperature difference)
    /// plus radiation.</summary>
    StillFluid,

    /// <summary>A fluid moving past the bodies: FORCED convection at the given free-stream
    /// velocity, blended with the natural-convection contribution, plus radiation.</summary>
    MovingFluid
}

/// <summary>
/// The environment a heat-flow study runs in. Null on a project means "no environment" —
/// every existing analysis keeps its exact behaviour, driven only by the boundary
/// conditions the user placed by hand.
/// <para>
/// The environment produces boundary conditions, not a separate physics: convection
/// enters as a Robin term with a film coefficient computed from correlations, radiation
/// as a Robin term with the factored coefficient h_r = εσ(T_s+T_a)(T_s²+T_a²). Both
/// depend on the surface temperature, which is what makes the thermal solve nonlinear
/// and is why the solvers iterate inside each time step.
/// </para>
/// </summary>
public sealed record EnvironmentSettings
{
    /// <summary>What the bodies sit in.</summary>
    public MediumKind Medium { get; init; } = MediumKind.StillFluid;

    /// <summary>Ambient (far-field) temperature [K]. This is both the convective sink and
    /// the radiative surroundings temperature — a separate sky temperature is a deliberate
    /// omission, not an oversight: it only matters outdoors.</summary>
    public double AmbientTemperature { get; init; } = 293.15;

    /// <summary>Name of the built-in fluid (see <see cref="FluidLibrary"/>), or null when
    /// <see cref="CustomFluid"/> carries a user-entered constant-property fluid.</summary>
    public string? FluidName { get; init; } = FluidLibrary.Air.Name;

    /// <summary>A user-entered fluid, carried by value so a project stays self-describing
    /// even if the built-in library later changes.</summary>
    public FluidProperties? CustomFluid { get; init; }

    /// <summary>Gravity vector [m/s²] — its DIRECTION classifies surfaces as vertical,
    /// upward-facing or downward-facing, which selects the natural-convection correlation.
    /// Default is −Z at standard gravity.</summary>
    public Vector3D Gravity { get; init; } = new(0, 0, -9.80665);

    /// <summary>Free-stream velocity [m/s] for <see cref="MediumKind.MovingFluid"/>; its
    /// magnitude sets the Reynolds number and its direction the streamwise length scale.</summary>
    public Vector3D FlowVelocity { get; init; } = new(0, 0, 0);

    /// <summary>Whether surfaces radiate to the ambient. On by default — in a vacuum it
    /// is the ONLY heat path, and in air it is typically 20–40% of the loss at modest
    /// temperature rises, so silently dropping it would flatter every result.</summary>
    public bool IncludeRadiation { get; init; } = true;

    /// <summary>The fluid this environment is filled with; null in a vacuum.</summary>
    public FluidProperties? ResolveFluid() => Medium == MediumKind.Vacuum
        ? null
        : CustomFluid ?? FluidLibrary.Find(FluidName) ?? FluidLibrary.Air;

    /// <summary>Free-stream speed [m/s]; zero unless the medium is moving.</summary>
    public double FlowSpeed => Medium == MediumKind.MovingFluid ? FlowVelocity.Length : 0.0;

    /// <summary>One-line description of the environment for the solve log — every solve
    /// states the environment it ran in.</summary>
    public string Describe()
    {
        string radiation = IncludeRadiation ? "with radiation" : "radiation off";
        return Medium switch
        {
            MediumKind.Vacuum =>
                $"vacuum at {AmbientTemperature:F2} K, {radiation}",
            MediumKind.StillFluid =>
                $"still {ResolveFluid()!.Name} at {AmbientTemperature:F2} K (natural convection), {radiation}",
            _ =>
                $"{ResolveFluid()!.Name} at {AmbientTemperature:F2} K flowing at {FlowSpeed:G4} m/s " +
                $"(forced convection), {radiation}"
        };
    }
}
