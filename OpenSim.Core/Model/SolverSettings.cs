using OpenSim.Core.Interfaces;

namespace OpenSim.Core.Model;

/// <summary>
/// The analysis settings that are not part of the geometry or the conditions — the
/// transient's clock, the AC sweep's band, the modal count — saved with the project so a
/// study re-runs the same way from the application and from the command line (FU-46).
/// Null on a project — every older file — means these defaults, which are the values the
/// application's panels and the command line started with before they were persisted.
/// </summary>
public sealed record SolverSettings
{
    /// <summary>Transient: the uniform start temperature [K].</summary>
    public double InitialTemperature { get; init; } = 293.15;

    /// <summary>Transient: the simulated time [s].</summary>
    public double Duration { get; init; } = 10;

    /// <summary>Transient: the fixed step, or the adaptive integrator's output interval [s].</summary>
    public double TimeStep { get; init; } = 0.1;

    public TimeIntegrator Integrator { get; init; } = TimeIntegrator.BackwardEuler;

    /// <summary>Transient, adaptive: the temperature error a step may add [K].</summary>
    public double Tolerance { get; init; } = 0.01;

    public ThermalCapacity Capacity { get; init; } = ThermalCapacity.Automatic;

    /// <summary>AC electrical: the geometric sweep from <see cref="MinFrequency"/> to
    /// <see cref="MaxFrequency"/> [Hz] in <see cref="PointCount"/> points.</summary>
    public double MinFrequency { get; init; } = 1e3;
    public double MaxFrequency { get; init; } = 1e8;
    public int PointCount { get; init; } = 15;

    /// <summary>Modal: the number of modes, lowest first.</summary>
    public int ModeCount { get; init; } = 6;

    /// <summary>Electrostatic: the unclaimed outer boundary is open space (1/r) instead of
    /// a zero-flux wall.</summary>
    public bool OpenBoundary { get; init; }

    /// <summary>Environment and conjugate heat flow: the equilibrium instead of the time history.</summary>
    public bool EnvironmentSteady { get; init; }

    /// <summary>Joule heating: the thermal leg as a transient with the settings above.</summary>
    public bool JouleTransient { get; init; }
}
