using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Steady-state heat conduction solver over TET4 elements: ∇·(k∇T) + q = 0 with
/// prescribed temperatures (Dirichlet), heat flows (Neumann), convection (Robin),
/// and an optional volumetric source (e.g. Joule heating). Produces temperature
/// and heat flux result fields.
/// </summary>
public sealed class HeatConductionSolver : ISolver
{
    public string Name => "Steady-state heat conduction (thermal)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The thermal solver supports linear (TET4) meshes only; " +
                "re-generate the mesh with linear elements.");

        input.Material.ValidateThermal();
        if (input.RegionMaterials is not null)
            foreach (var material in input.RegionMaterials.Values)
                material.ValidateThermal();

        if (!input.BoundaryConditions.Any(bc => bc is FixedTemperature or Convection))
            throw new InvalidOperationException(
                "At least one fixed temperature or convection condition is required; " +
                "with only heat inflows the temperature level is undetermined.");

        if (input.ElementHeatSource is not null && input.ElementHeatSource.Count != input.Mesh.ElementCount)
            throw new InvalidOperationException(
                $"ElementHeatSource has {input.ElementHeatSource.Count} entries but the mesh has " +
                $"{input.Mesh.ElementCount} elements.");

        foreach (var bc in input.BoundaryConditions)
        {
            if (bc is not (FixedTemperature or HeatFlux or Convection))
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to a thermal solve. " +
                    "Use fixed temperatures, heat fluxes, or convection.");
            if (bc.FaceIds.Count == 0)
                throw new InvalidOperationException($"Boundary condition '{bc.Name}' has no faces assigned.");
            if (input.Mesh.GetFaceNodes(bc.FaceIds).Count == 0)
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' targets faces that do not exist on the mesh.");
            if (bc is Convection { Coefficient: <= 0 } c)
                throw new InvalidOperationException(
                    $"Convection '{c.Name}': the heat transfer coefficient must be positive.");
        }
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;

        progress?.Report(new SolverProgress("Assembling conduction matrix", 0.05));
        var assembler = new ScalarDiffusionAssembler(mesh,
            el => input.MaterialOf(el).ThermalConductivity!.Value);

        // Robin terms regularize the matrix, so they are assembled with it (keeps SPD).
        var robin = new List<ScalarDiffusionAssembler.RobinTerm>();
        foreach (var convection in input.BoundaryConditions.OfType<Convection>())
            foreach (var t in mesh.GetFaceTriangles(convection.FaceIds))
                robin.Add(new ScalarDiffusionAssembler.RobinTerm(t, convection.Coefficient));
        var conduction = assembler.AssembleStiffness(robin, cancellationToken);
        log.Add($"Assembled {conduction.RowCount} DOF system, {conduction.NonZeroCount} non-zeros" +
                (robin.Count > 0 ? $" including {robin.Count} convective surface triangles." : "."));

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.25));
        var loads = ScalarSolverHelpers.AssembleThermalLoads(input, log);

        var prescribed = new Dictionary<int, double>();
        foreach (var temperature in input.BoundaryConditions.OfType<FixedTemperature>())
        {
            var nodes = mesh.GetFaceNodes(temperature.FaceIds);
            foreach (int node in nodes)
                prescribed[node] = temperature.Kelvin;
            log.Add($"Temperature '{temperature.Name}': {temperature.Kelvin:g4} K on {nodes.Count} nodes.");
        }

        progress?.Report(new SolverProgress("Solving linear system", 0.35));
        var result = ConstrainedSystemSolver.Solve(conduction, loads, prescribed,
            cancellationToken: cancellationToken, allowUnconstrained: robin.Count > 0);
        log.Add($"Conjugate gradient converged in {result.Iterations.Iterations} iterations " +
                $"(residual {result.Iterations.ResidualNorm:g3}).");
        var temperatureField = result.Displacements;

        progress?.Report(new SolverProgress("Recovering heat flux", 0.85));
        var flux_ = new Vector3D[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
            flux_[e] = assembler.ElementGradient(e, temperatureField)
                       * -input.MaterialOf(e).ThermalConductivity!.Value;   // q = −k∇T

        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput
        {
            Fields = new IResultField[]
            {
                new NodalScalarField("Temperature", "K", temperatureField),
                new NodalVectorField("Heat flux", "W/m²", ScalarSolverHelpers.NodalAverage(mesh, flux_))
            },
            Log = log
        };
    }
}
