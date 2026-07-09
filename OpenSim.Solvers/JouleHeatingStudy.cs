using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// One-way coupled Joule heating study: a DC conduction solve produces the per-element
/// dissipated power density σ|∇φ|² (exact for constant-gradient TET4), which drives a
/// steady-state heat conduction solve as a volumetric source on the same mesh.
/// Not an <see cref="ISolver"/> — solvers stay single-physics; this orchestrates two.
/// </summary>
public sealed class JouleHeatingStudy
{
    private readonly ElectricalConductionSolver _electrical = new();
    private readonly HeatConductionSolver _thermal = new();
    private readonly TransientThermalSolver _transientThermal = new();

    public string Name => "Joule heating (electrical → thermal)";

    /// <summary>
    /// Validates that the boundary conditions cover both physics. The input carries the
    /// combined BC list; it is partitioned by type before delegating to each solver.
    /// The thermal leg is transient when <see cref="SolveInput.TransientThermal"/> is set.
    /// </summary>
    public void Validate(SolveInput input)
    {
        var (electrical, thermal) = Partition(input);
        _electrical.Validate(electrical);
        // Thermal validation runs without the (not yet computed) heat source; lengths
        // are checked again when the real source is attached in Solve.
        if (input.TransientThermal is not null)
            _transientThermal.Validate(thermal);
        else
            _thermal.Validate(thermal);
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (electricalInput, thermalInput) = Partition(input);

        var electricalProgress = progress is null ? null : new Progress<SolverProgress>(
            p => progress.Report(p with { Fraction = p.Fraction * 0.5 }));
        var electricalOutput = _electrical.Solve(electricalInput, electricalProgress, cancellationToken);

        // Joule source; adds to any user-supplied base source (e.g. component losses).
        var power = (ElementScalarField)electricalOutput.Fields.Single(
            f => f.Name == ElectricalConductionSolver.ElementPowerFieldName);
        var source = new double[input.Mesh.ElementCount];
        for (int e = 0; e < source.Length; e++)
            source[e] = power.Values[e] + (input.ElementHeatSource?[e] ?? 0);

        var thermalProgress = progress is null ? null : new Progress<SolverProgress>(
            p => progress.Report(p with { Fraction = 0.5 + p.Fraction * 0.5 }));
        var thermalLog = new List<string>();
        SolveOutput thermalOutput;
        if (input.TransientThermal is not null)
        {
            // One-way coupling with a single DC solve: the dissipated power is applied
            // as a CONSTANT source over the whole transient (no σ(T) feedback).
            thermalLog.Add("Constant DC power assumed over the transient.");
            thermalOutput = _transientThermal.Solve(thermalInput with { ElementHeatSource = source },
                thermalProgress, cancellationToken);
        }
        else
        {
            thermalOutput = _thermal.Solve(thermalInput with { ElementHeatSource = source },
                thermalProgress, cancellationToken);
        }
        thermalLog.AddRange(thermalOutput.Log);

        // Transient frames are re-wrapped so every frame carries the (constant) electrical
        // fields alongside its thermal state — the frame contract requires identical field
        // sets across frames, and Fields must stay the default frame's list.
        IReadOnlyList<ResultFrame>? frames = thermalOutput.Frames?
            .Select(frame => frame with
            {
                Fields = electricalOutput.Fields.Concat(frame.Fields).ToList()
            })
            .ToList();

        return new SolveOutput
        {
            Fields = frames is null
                ? electricalOutput.Fields.Concat(thermalOutput.Fields).ToList()
                : frames[^1].Fields,
            Log = electricalOutput.Log.Select(l => $"[Electrical] {l}")
                .Concat(thermalLog.Select(l => $"[Thermal] {l}"))
                .ToList(),
            Frames = frames,
            FrameAxis = thermalOutput.FrameAxis,
            Summary = thermalOutput.Summary
        };
    }

    /// <summary>Splits the combined BC list into the electrical and thermal sub-problems.</summary>
    private static (SolveInput Electrical, SolveInput Thermal) Partition(SolveInput input)
    {
        foreach (var bc in input.BoundaryConditions)
            if (bc is not (VoltagePotential or CurrentFlow or FixedTemperature or HeatFlux or Convection))
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to a Joule heating " +
                    "study. Use voltage/current conditions for the electrical part and temperature/heat " +
                    "flux/convection conditions for the thermal part.");

        var electrical = input with
        {
            BoundaryConditions = input.BoundaryConditions
                .Where(bc => bc is VoltagePotential or CurrentFlow).ToList(),
            ElementHeatSource = null
        };
        var thermal = input with
        {
            BoundaryConditions = input.BoundaryConditions
                .Where(bc => bc is FixedTemperature or HeatFlux or Convection).ToList(),
            ElementHeatSource = null
        };
        return (electrical, thermal);
    }
}
