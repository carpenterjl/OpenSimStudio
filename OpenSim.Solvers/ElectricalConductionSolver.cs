using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Steady DC electrical conduction solver over TET4 elements: ∇·(σ∇φ) = 0 with
/// prescribed potentials (Dirichlet) and injected currents (Neumann). Produces
/// electric potential, current density, and Joule power density result fields.
/// </summary>
public sealed class ElectricalConductionSolver : ISolver
{
    /// <summary>Name of the per-element power density field consumed by Joule coupling.</summary>
    public const string ElementPowerFieldName = "Power density (element)";

    public string Name => "DC conduction (electrical)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The electrical solver supports linear (TET4) meshes only; " +
                "re-generate the mesh with linear elements.");

        input.Material.ValidateElectrical();
        if (input.RegionMaterials is not null)
            foreach (var material in input.RegionMaterials.Values)
                material.ValidateElectrical();

        if (!input.BoundaryConditions.OfType<VoltagePotential>().Any())
            throw new InvalidOperationException(
                "At least one voltage potential is required; without a reference potential the solution is not unique.");

        foreach (var bc in input.BoundaryConditions)
        {
            if (bc is not (VoltagePotential or CurrentFlow))
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to an electrical solve. " +
                    "Use voltage potentials and current flows.");
            if (bc.FaceIds.Count == 0)
                throw new InvalidOperationException($"Boundary condition '{bc.Name}' has no faces assigned.");
            if (input.Mesh.GetFaceNodes(bc.FaceIds).Count == 0)
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' targets faces that do not exist on the mesh.");
        }
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;

        progress?.Report(new SolverProgress("Assembling conductance matrix", 0.05));
        var assembler = new ScalarDiffusionAssembler(mesh,
            el => input.MaterialOf(el).ElectricalConductivity!.Value);
        var conductance = assembler.AssembleStiffness(cancellationToken: cancellationToken);
        log.Add($"Assembled {conductance.RowCount} DOF system, {conductance.NonZeroCount} non-zeros.");

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.25));
        var loads = new double[mesh.NodeCount];
        foreach (var current in input.BoundaryConditions.OfType<CurrentFlow>())
        {
            ScalarSolverHelpers.DistributeOverFaces(mesh, current.FaceIds, current.TotalCurrent, loads, current.Name);
            log.Add($"Current '{current.Name}': {current.TotalCurrent:g4} A injected.");
        }

        var prescribed = new Dictionary<int, double>();
        foreach (var voltage in input.BoundaryConditions.OfType<VoltagePotential>())
        {
            var nodes = mesh.GetFaceNodes(voltage.FaceIds);
            foreach (int node in nodes)
                prescribed[node] = voltage.Volts;
            log.Add($"Voltage '{voltage.Name}': {voltage.Volts:g4} V on {nodes.Count} nodes.");
        }

        progress?.Report(new SolverProgress("Solving linear system", 0.35));
        var result = ConstrainedSystemSolver.Solve(conductance, loads, prescribed,
            cancellationToken: cancellationToken);
        log.Add($"Conjugate gradient converged in {result.Iterations.Iterations} iterations " +
                $"(residual {result.Iterations.ResidualNorm:g3}).");
        var phi = result.Displacements;

        progress?.Report(new SolverProgress("Recovering current density", 0.85));
        var current_ = new Vector3D[mesh.ElementCount];
        var power = new double[mesh.ElementCount];
        double totalPower = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double sigma = input.MaterialOf(e).ElectricalConductivity!.Value;
            var gradPhi = assembler.ElementGradient(e, phi);
            current_[e] = gradPhi * -sigma;                          // J = −σ∇φ
            power[e] = sigma * Vector3D.Dot(gradPhi, gradPhi);       // q = σ|∇φ|², exact per constant-gradient tet
            totalPower += power[e] * mesh.ElementVolume(e);
        }
        log.Add($"Total dissipated power: {totalPower:g4} W.");
        var summary = new Dictionary<string, double> { ["Total power (W)"] = totalPower };
        LogElectrodeCurrents(input, conductance, phi, loads, totalPower, log, summary);

        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput
        {
            Fields = new IResultField[]
            {
                new NodalScalarField("Electric potential", "V", phi),
                new NodalVectorField("Current density", "A/m²", ScalarSolverHelpers.NodalAverage(mesh, current_)),
                new NodalScalarField("Power density", "W/m³", ScalarSolverHelpers.NodalAverage(mesh, power)),
                new ElementScalarField(ElementPowerFieldName, "W/m³", power)
            },
            Log = log,
            Summary = summary
        };
    }

    /// <summary>
    /// Net current entering each electrode = sum of nodal reactions K·φ − f over its
    /// nodes. With exactly two voltage electrodes this also yields R = ΔV/I; with a
    /// single injected current against a one-potential ground it yields R = P/I²
    /// (exact for a two-terminal DC network). Both are recorded in the log and summary.
    /// </summary>
    private static void LogElectrodeCurrents(SolveInput input, CsrMatrix conductance,
        double[] phi, double[] loads, double totalPower, List<string> log,
        Dictionary<string, double> summary)
    {
        var reactions = new double[phi.Length];
        conductance.Multiply(phi, reactions);
        for (int i = 0; i < reactions.Length; i++)
            reactions[i] -= loads[i];

        var electrodes = input.BoundaryConditions.OfType<VoltagePotential>()
            .Select(v => (v.Name, v.Volts,
                Current: input.Mesh.GetFaceNodes(v.FaceIds).Sum(n => reactions[n])))
            .ToList();
        foreach (var e in electrodes)
            log.Add($"Electrode '{e.Name}' ({e.Volts:g4} V): net current {e.Current:g4} A.");

        if (electrodes.Count == 2 && electrodes[0].Volts != electrodes[1].Volts)
        {
            double deltaV = Math.Abs(electrodes[0].Volts - electrodes[1].Volts);
            double currentMag = 0.5 * (Math.Abs(electrodes[0].Current) + Math.Abs(electrodes[1].Current));
            if (currentMag > 0)
            {
                log.Add($"Resistance between electrodes: {deltaV / currentMag:g4} Ω.");
                summary["Resistance (Ω)"] = deltaV / currentMag;
                summary["Current (A)"] = currentMag;
            }
            return;
        }

        // Current-driven test: one injected current against a single-potential ground.
        // P = I²R holds exactly for a two-terminal DC network, so R = P/I² — the same
        // power-based resistance the voltage benchmarks validate against.
        var currents = input.BoundaryConditions.OfType<CurrentFlow>().ToList();
        if (currents.Count == 1 && currents[0].TotalCurrent != 0
            && electrodes.Select(e => e.Volts).Distinct().Count() == 1)
        {
            double injected = Math.Abs(currents[0].TotalCurrent);
            double resistance = totalPower / (injected * injected);
            log.Add($"Resistance between electrodes: {resistance:g4} Ω (from P = I²R).");
            summary["Resistance (Ω)"] = resistance;
            summary["Current (A)"] = injected;
        }
    }
}
