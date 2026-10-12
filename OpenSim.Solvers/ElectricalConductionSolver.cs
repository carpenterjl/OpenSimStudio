using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Steady DC electrical conduction solver over TET4 elements: ∇·(σ∇φ) = 0 with
/// prescribed potentials (Dirichlet), injected currents (Neumann, uniform over the face)
/// and equipotential current terminals (one free potential per face carrying the total
/// current — <see cref="CurrentFlow.Equipotential"/>). Produces electric potential, current
/// density, and Joule power density result fields.
/// <para>
/// A mesh that holds conductors AND insulators (copper on FR4: a conductivity ratio of
/// 10²¹) is not solved as one system. The iterative solve stops on the residual relative
/// to the load, and every entry an insulator contributes is 10⁻²¹ of that — it would be
/// declared converged with the insulator's potential wherever the iteration left it.
/// Instead the conductors that reach a voltage are solved first, with the insulators
/// carrying nothing (which is what they do, to one part in the ratio), and the rest of
/// the mesh is solved second with the conductor potentials imposed on it. Each stage is
/// an ordinarily conditioned problem and together they are the same field.
/// </para>
/// <para>
/// An assembly of separately meshed bodies (<see cref="FeMeshAssembler"/>) conducts across
/// its joints only through <see cref="SolveInput.ElectricalContacts"/>: each contact stamp
/// adds G·w·s·sᵀ with G the joint's conductance per area [S/m²], the same operator the
/// thermal solvers use for a contact conductance.
/// </para>
/// </summary>
public sealed class ElectricalConductionSolver : ISolver
{
    /// <summary>Name of the per-element power density field consumed by Joule coupling.</summary>
    public const string ElementPowerFieldName = "Power density (element)";

    /// <summary>The largest conductivity ratio solved as ONE system — the same limit the
    /// AC solver puts on its admittivity. An element further than this below the best
    /// conductor is an insulator and is solved in the second stage.</summary>
    public const double ConductivitySpreadLimit = ConductorPartition.SpreadLimit;

    /// <summary>In the second stage a conductor that reaches no voltage stands this far
    /// above the best insulator: enough to make it an equipotential to 10⁻⁴ of the field
    /// around it without bringing the 10²¹ back.</summary>
    internal const double FloatingConductorContrast = 1e4;

    public string Name => "DC conduction (electrical)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The electrical solver supports linear tetrahedral (TET4) meshes only; " +
                "re-generate the mesh with linear tetrahedral elements.");

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
            BoundaryScope.Validate(bc, input.Mesh);
        }

        if (input.ElectricalContacts is not null)
            foreach (var contact in input.ElectricalContacts)
                contact.Validate(input.Mesh.NodeCount);

        Partition(input);
        EquipotentialTerminals.Of(input.Mesh, input.BoundaryConditions);
    }

    private static ConductorPartition Partition(SolveInput input) =>
        ConductorPartition.Of(input.Mesh, e => input.MaterialOf(e).ElectricalConductivity!.Value,
            input.BoundaryConditions, input.ElectricalContacts);

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;
        var partition = Partition(input);
        var terminals = EquipotentialTerminals.Of(mesh, input.BoundaryConditions);
        var contacts = input.ElectricalContacts;

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.05));
        var loads = new double[mesh.NodeCount];
        foreach (var current in input.BoundaryConditions.OfType<CurrentFlow>())
        {
            // A terminal's load is summed into its one DOF by the tying, so how it is spread
            // over the face is immaterial there; a uniform injection is spread as it says.
            ScalarSolverHelpers.DistributeOverFaces(mesh, current.FaceIds, current.TotalCurrent, loads, current.Name);
            log.Add(current.Equipotential
                ? $"Terminal '{current.Name}': {current.TotalCurrent:g4} A into one equipotential face (its potential is solved for)."
                : $"Current '{current.Name}': {current.TotalCurrent:g4} A injected at uniform density.");
        }

        var prescribed = new Dictionary<int, double>();
        foreach (var voltage in input.BoundaryConditions.OfType<VoltagePotential>())
        {
            var nodes = mesh.GetScopeNodes(voltage);
            foreach (int node in nodes)
                prescribed[node] = voltage.Volts;
            log.Add($"Voltage '{voltage.Name}': {voltage.Volts:g4} V on {nodes.Count} nodes.");
        }
        if (contacts is not null)
            foreach (var contact in contacts)
                log.Add($"Electrical contact bodies {contact.BodyA}–{contact.BodyB}: G = {contact.Conductance:g4} S/m² " +
                        $"over {contact.CoupledArea:g4} m² (the joint's resistance is " +
                        $"{1 / (contact.Conductance * contact.CoupledArea):g4} Ω).");

        // The conductivity each element is actually solved with (the given one everywhere
        // except a conductor piece that floats inside an insulator — see the second stage).
        var solved = (double[])partition.Sigma.Clone();
        double[] phi;
        double[] reactions;
        double[]? leakage = null;
        ScalarDiffusionAssembler assembler;

        if (!partition.Staged)
        {
            progress?.Report(new SolverProgress("Assembling conductance matrix", 0.15));
            assembler = new ScalarDiffusionAssembler(mesh, el => partition.Sigma[el]);
            var conductance = assembler.AssembleStiffness(null, contacts, cancellationToken);
            log.Add($"Assembled {conductance.RowCount} DOF system, {conductance.NonZeroCount} non-zeros" +
                    (terminals is null ? "." : $"; {terminals.Terminals.Count} equipotential terminal(s) tie it to {terminals.TiedCount} unknowns."));

            progress?.Report(new SolverProgress("Solving linear system", 0.35));
            var (solution, iterations) = SolveTied(conductance, loads, prescribed, terminals, cancellationToken);
            log.Add($"Conjugate gradient converged in {iterations.Iterations} iterations " +
                    $"(residual {iterations.ResidualNorm:g3}).");
            phi = solution;
            reactions = new double[phi.Length];
            conductance.Multiply(phi, reactions);

            if (partition.FloatingRegions.Count > 0)
                log.Add($"Mesh region(s) {string.Join(", ", partition.FloatingRegions)} hold conductor " +
                        "that reaches no voltage potential: it carries no current, and its potential " +
                        "(shown as 0 V) has no level.");
        }
        else
        {
            (phi, reactions, leakage, assembler) = SolveStaged(input, partition, terminals, loads, prescribed,
                solved, log, progress, cancellationToken);
        }
        for (int i = 0; i < reactions.Length; i++)
            reactions[i] -= loads[i];

        progress?.Report(new SolverProgress("Recovering current density", 0.85));
        var current_ = new Vector3D[mesh.ElementCount];
        var power = new double[mesh.ElementCount];
        double totalPower = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double sigma = solved[e];
            var gradPhi = assembler.ElementGradient(e, phi);
            current_[e] = gradPhi * -sigma;                          // J = −σ∇φ
            power[e] = sigma * Vector3D.Dot(gradPhi, gradPhi);       // q = σ|∇φ|², exact per constant-gradient tet
            totalPower += power[e] * mesh.ElementVolume(e);
        }
        if (contacts is not null)
        {
            // The joints dissipate too: Σ G·w·(Δφ)² per stamp, which is J·Δφ over the area.
            double contactPower = 0;
            foreach (var contact in contacts)
                foreach (var s in contact.Stamps)
                {
                    double jump = s.S0 * phi[s.Node0] + s.S1 * phi[s.Node1] + s.S2 * phi[s.Node2] + s.S3 * phi[s.Node3];
                    contactPower += contact.Conductance * s.Weight * jump * jump;
                }
            totalPower += contactPower;
            log.Add($"Dissipated in the contacts: {contactPower:g4} W.");
        }
        log.Add($"Total dissipated power: {totalPower:g4} W.");
        var summary = new Dictionary<string, double> { ["Total power (W)"] = totalPower };
        LogElectrodeCurrents(input, partition, reactions, leakage, totalPower, phi, terminals, log, summary);

        var groups = ScalarSolverHelpers.MaterialGroups(input, out var groupNames);
        if (groups is not null)
            log.Add(ScalarSolverHelpers.InterfaceNote("current density and power density"));
        var (currentNodal, currentSides) = ScalarSolverHelpers.NodalAverageWithSides(mesh, current_, groups, groupNames);
        var (powerNodal, powerSides) = ScalarSolverHelpers.NodalAverageWithSides(mesh, power, groups, groupNames);

        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput
        {
            Fields = new IResultField[]
            {
                new NodalScalarField("Electric potential", "V", phi),
                new NodalVectorField("Current density", "A/m²", currentNodal, currentSides),
                new NodalScalarField("Power density", "W/m³", powerNodal, powerSides),
                new ElementScalarField(ElementPowerFieldName, "W/m³", power)
            },
            Log = log,
            Summary = summary
        };
    }

    /// <summary>
    /// K·φ = f with prescribed potentials and, when there are equipotential terminals, the
    /// terminal nodes tied to one unknown each. The tied system is Tᵀ·K·T (still SPD), its
    /// solution is expanded back to the nodes, so the caller sees a nodal potential either
    /// way. Without terminals this is the plain constrained solve, bitwise.
    /// </summary>
    internal static (double[] Phi, IterativeSolveResult Iterations) SolveTied(CsrMatrix stiffness, double[] loads,
        IReadOnlyDictionary<int, double> prescribed, EquipotentialTerminals? terminals,
        CancellationToken cancellationToken, bool allowUnconstrained = false)
    {
        if (terminals is null)
        {
            var plain = ConstrainedSystemSolver.Solve(stiffness, loads, prescribed,
                cancellationToken: cancellationToken, allowUnconstrained: allowUnconstrained);
            return (plain.Displacements, plain.Iterations);
        }
        var tied = ConstrainedSystemSolver.Solve(terminals.Tie(stiffness), terminals.TieLoads(loads),
            terminals.TiePrescribed(prescribed), cancellationToken: cancellationToken,
            allowUnconstrained: allowUnconstrained);
        return (terminals.Expand(tied.Displacements), tied.Iterations);
    }

    /// <summary>
    /// Conductors first, then everything else with the conductor potentials imposed.
    /// Returns the potential, K·φ over the whole mesh (both stages' matrices), the
    /// current each voltage electrode supplies to the insulator through its conductor,
    /// and an assembler for the gradient recovery.
    /// </summary>
    private static (double[] Phi, double[] Reactions, double[] Leakage,
        ScalarDiffusionAssembler Assembler) SolveStaged(
        SolveInput input, ConductorPartition partition, EquipotentialTerminals? terminals, double[] loads,
        Dictionary<int, double> prescribed, double[] solved, List<string> log,
        IProgress<SolverProgress>? progress, CancellationToken cancellationToken)
    {
        var mesh = input.Mesh;
        int nodes = mesh.NodeCount;
        var phi = new double[nodes];
        var reactions = new double[nodes];
        foreach (var (node, value) in prescribed) phi[node] = value;

        log.Add($"{partition.InsulatorElements} of {mesh.ElementCount} elements are insulators " +
                $"(conductivity below 1/{ConductivitySpreadLimit:G1} of the best conductor). The " +
                "conductors are solved first and the insulators second, with the conductor " +
                "potentials imposed on them — the same field, without asking one iterative solve " +
                "to resolve the whole conductivity ratio.");

        // ---- Stage one: the conductors that reach a voltage. Every other node is held
        //      out of the system (it has no entry in this matrix at all).
        progress?.Report(new SolverProgress("Solving the conductors", 0.2));
        var conductors = new ScalarDiffusionAssembler(mesh,
            el => partition.Anchored[el] ? partition.Sigma[el] : 0.0);
        var first = conductors.AssembleStiffness(null, partition.StageOneContacts, cancellationToken);
        var held = new Dictionary<int, double>(prescribed);
        int conductorNodes = 0;
        for (int n = 0; n < nodes; n++)
        {
            if (partition.NodeAnchored[n]) conductorNodes++;
            else held.TryAdd(n, 0.0);
        }
        if (held.Count < nodes)
        {
            var (solution, iterations) = SolveTied(first, loads, held, terminals, cancellationToken);
            log.Add($"Conductors: {conductorNodes} nodes, conjugate gradient converged in " +
                    $"{iterations.Iterations} iterations (residual {iterations.ResidualNorm:g3}).");
            for (int n = 0; n < nodes; n++)
                if (partition.NodeAnchored[n]) phi[n] = solution[n];
        }
        first.Multiply(phi, reactions);

        // A conductor piece held at ONE potential with nothing injected carries no
        // conduction current: its potential is that voltage exactly, and its reactions are
        // exactly zero. Left as computed they are the rounding of σ·φ sums — 10⁻¹² A against
        // a laminate leakage of 10⁻¹⁷ A, which is the whole answer when two such pieces
        // face each other across the insulator.
        var (pieceVolts, driven) = partition.SnapPieces(prescribed, n => loads[n] != 0);
        for (int n = 0; n < nodes; n++)
        {
            if (!partition.NodeAnchored[n] || driven.Contains(partition.Piece[n])) continue;
            phi[n] = pieceVolts[partition.Piece[n]];
            reactions[n] = 0;
        }

        // ---- Stage two: everything else. A conductor piece that reaches no voltage floats
        //      with the insulator around it; at its own conductivity it would bring the
        //      ratio back, so it stands a fixed factor above the best insulator instead.
        progress?.Report(new SolverProgress("Solving the insulators", 0.6));
        double floatingSigma = FloatingConductorContrast * partition.InsulatorMaxSigma;
        for (int e = 0; e < solved.Length; e++)
            if (!partition.Anchored[e])
                solved[e] = Math.Min(partition.Sigma[e], floatingSigma);
        var rest = new ScalarDiffusionAssembler(mesh, el => partition.Anchored[el] ? 0.0 : solved[el]);
        var second = rest.AssembleStiffness(null, partition.StageTwoContacts, cancellationToken);
        var imposed = new Dictionary<int, double>(prescribed);
        for (int n = 0; n < nodes; n++)
            if (partition.NodeAnchored[n]) imposed[n] = phi[n];
        if (imposed.Count < nodes && partition.InsulatorMaxSigma > 0)
        {
            var result = ConstrainedSystemSolver.Solve(second, new double[nodes], imposed,
                cancellationToken: cancellationToken, allowUnconstrained: true);
            log.Add($"Insulators: {nodes - imposed.Count} nodes, conjugate gradient converged in " +
                    $"{result.Iterations.Iterations} iterations (residual {result.Iterations.ResidualNorm:g3}).");
            phi = result.Displacements;
        }
        var restReactions = new double[nodes];
        second.Multiply(phi, restReactions);

        // What the insulator draws from a conductor's surface is supplied through the
        // conductor by its electrode — a current stage one left out by construction (it is
        // 10⁻²¹ of a conduction current, and ALL the current when two conductors are joined
        // only through the insulator). It is added to the electrode's own, shared equally
        // when one piece carries several electrodes.
        var electrodes = input.BoundaryConditions.OfType<VoltagePotential>().ToList();
        var leakage = new double[electrodes.Count];
        var drawn = new Dictionary<int, double>();
        for (int n = 0; n < nodes; n++)
        {
            if (partition.NodeAnchored[n] && !prescribed.ContainsKey(n))
                drawn[partition.Piece[n]] = drawn.GetValueOrDefault(partition.Piece[n]) + restReactions[n];
            else
                reactions[n] += restReactions[n];
        }
        var electrodesOfPiece = partition.ElectrodesOfPieces(mesh, electrodes);
        foreach (var (piece, current) in drawn)
            if (electrodesOfPiece.TryGetValue(piece, out var owners))
                foreach (int i in owners) leakage[i] += current / owners.Count;

        if (partition.FloatingRegions.Count > 0)
            log.Add($"Mesh region(s) {string.Join(", ", partition.FloatingRegions)} hold conductor " +
                    "that reaches no voltage potential: it carries no conduction current and its " +
                    "potential floats with the insulator around it (solved as an equipotential, " +
                    $"{FloatingConductorContrast:G1}× the insulator's conductivity).");

        // Gradients do not depend on the coefficient, so either assembler recovers them.
        return (phi, reactions, leakage, conductors);
    }

    /// <summary>
    /// Net current entering each electrode = sum of nodal reactions K·φ − f over its
    /// nodes. With exactly two voltage electrodes and nothing injected this also yields
    /// R = ΔV/I; with a single injected current against a one-potential ground it yields
    /// the resistance the source sees — (V_terminal − V_ground)/I for an equipotential
    /// terminal, P/I² for a current entering at UNIFORM density over its face. Both are
    /// recorded in the log and summary, each saying which it is.
    /// </summary>
    private static void LogElectrodeCurrents(SolveInput input, ConductorPartition partition, double[] reactions,
        double[]? leakage, double totalPower, double[] phi, EquipotentialTerminals? terminals,
        List<string> log, Dictionary<string, double> summary)
    {
        var voltages = input.BoundaryConditions.OfType<VoltagePotential>().ToList();
        var electrodes = voltages
            .Select((v, i) => (v.Name, v.Volts,
                Current: input.Mesh.GetScopeNodes(v).Sum(n => reactions[n]) + (leakage?[i] ?? 0)))
            .ToList();
        foreach (var e in electrodes)
            log.Add($"Electrode '{e.Name}' ({e.Volts:g4} V): net current {e.Current:g4} A.");

        // Two electrodes on conductor pieces that nothing joins — separately meshed bodies
        // with no contact between them — exchange no current. Their reactions are the
        // rounding of K·φ on a uniform piece, and ΔV over that would print as a resistance
        // of 10²⁰ Ω; it is no resistance at all.
        if (electrodes.Count == 2 && !partition.Staged)
        {
            var pieces = voltages.Select(v => input.Mesh.GetScopeNodes(v)
                .Where(n => partition.NodeAnchored[n]).Select(n => partition.Piece[n]).ToHashSet()).ToList();
            if (!pieces[0].Overlaps(pieces[1]))
            {
                log.Add($"Electrodes '{electrodes[0].Name}' and '{electrodes[1].Name}' sit on conductors that " +
                        "nothing joins (no element and no electrical contact connects them), so no current " +
                        "flows between them and no resistance is reported.");
                return;
            }
        }

        if (terminals is not null)
            for (int t = 0; t < terminals.Terminals.Count; t++)
            {
                var terminal = terminals.Terminals[t];
                double volts = terminals.PotentialOf(t, phi, input.Mesh);
                log.Add($"Terminal '{terminal.Name}' ({terminal.TotalCurrent:g4} A): equipotential at {volts:g6} V.");
                summary[$"Terminal '{terminal.Name}' potential (V)"] = volts;
            }

        var currents = input.BoundaryConditions.OfType<CurrentFlow>()
            .Where(c => c.TotalCurrent != 0).ToList();

        if (electrodes.Count == 2 && electrodes[0].Volts != electrodes[1].Volts)
        {
            // With a current injected as well, the two electrode currents differ and
            // ΔV over their mean is not the resistance of anything.
            if (currents.Count > 0)
            {
                log.Add("No resistance is reported: with a current injected as well as two voltage " +
                        "electrodes this is a three-terminal problem, and the two electrode currents " +
                        "differ. Read the electrode currents above.");
                return;
            }
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
        if (currents.Count == 1
            && electrodes.Select(e => e.Volts).Distinct().Count() == 1)
        {
            double injected = Math.Abs(currents[0].TotalCurrent);
            if (currents[0].Equipotential && terminals is not null)
            {
                // The terminal is one potential; R = ΔV/I is the terminal-to-terminal value,
                // the same number a voltage on that face gives.
                int t = terminals.Terminals.ToList().IndexOf(currents[0]);
                double volts = terminals.PotentialOf(t, phi, input.Mesh);
                double resistance = Math.Abs(volts - electrodes[0].Volts) / injected;
                log.Add($"Resistance seen by the equipotential terminal: {resistance:g4} Ω (from ΔV/I " +
                        "with the terminal's own potential — the terminal-to-terminal value, the same " +
                        "as a voltage drive on that face).");
                summary["Resistance (Ω)"] = resistance;
                summary["Current (A)"] = injected;
                return;
            }
            // P = I²R defines the resistance the source sees. The current enters at uniform
            // density over its face, so the face is NOT an equipotential: where the current
            // would rather crowd (a pad feeding a narrow trace from one side) this reads
            // above the equipotential-terminal resistance by the spreading term. The two
            // agree when the face is naturally uniform — the end of a bar.
            double uniform = totalPower / (injected * injected);
            log.Add($"Resistance seen by the injected current: {uniform:g4} Ω (from P = I²R, " +
                    "current entering at uniform density over its face — not an equipotential " +
                    "terminal; a voltage on that face, or the terminal's Equipotential option, gives " +
                    "the terminal-to-terminal value).");
            summary["Resistance (Ω)"] = uniform;
            summary["Current (A)"] = injected;
        }
    }
}
