using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// Steady DC electrical conduction solver over TET4 elements: ∇·(σ∇φ) = 0 with
/// prescribed potentials (Dirichlet) and injected currents (Neumann). Produces
/// electric potential, current density, and Joule power density result fields.
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
/// </summary>
public sealed class ElectricalConductionSolver : ISolver
{
    /// <summary>Name of the per-element power density field consumed by Joule coupling.</summary>
    public const string ElementPowerFieldName = "Power density (element)";

    /// <summary>The largest conductivity ratio solved as ONE system — the same limit the
    /// AC solver puts on its admittivity. An element further than this below the best
    /// conductor is an insulator and is solved in the second stage.</summary>
    public const double ConductivitySpreadLimit = 1e8;

    /// <summary>In the second stage a conductor that reaches no voltage stands this far
    /// above the best insulator: enough to make it an equipotential to 10⁻⁴ of the field
    /// around it without bringing the 10²¹ back.</summary>
    private const double FloatingConductorContrast = 1e4;

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

        Partition.Of(input);
    }

    /// <summary>
    /// Which elements are solved in which stage, and whether every injected current has
    /// somewhere to go.
    /// </summary>
    private sealed class Partition
    {
        /// <summary>σ per element as given.</summary>
        public required double[] Sigma { get; init; }

        /// <summary>Element belongs to a conductor that reaches a voltage (stage one).</summary>
        public required bool[] Anchored { get; init; }

        /// <summary>Node belongs to at least one stage-one element.</summary>
        public required bool[] NodeAnchored { get; init; }

        /// <summary>The conductor piece of each node (a representative node index);
        /// meaningful where <see cref="NodeAnchored"/> is set.</summary>
        public required int[] Piece { get; init; }

        /// <summary>True when some element is an insulator, so the solve is staged.</summary>
        public required bool Staged { get; init; }

        public required int InsulatorElements { get; init; }
        public required double InsulatorMaxSigma { get; init; }

        /// <summary>Mesh regions holding conductor that reaches no voltage.</summary>
        public required IReadOnlyList<int> FloatingRegions { get; init; }

        public static Partition Of(SolveInput input)
        {
            var mesh = input.Mesh;
            int elements = mesh.ElementCount;
            var sigma = new double[elements];
            double max = 0;
            for (int e = 0; e < elements; e++)
            {
                sigma[e] = input.MaterialOf(e).ElectricalConductivity!.Value;
                max = Math.Max(max, sigma[e]);
            }

            var conductor = new bool[elements];
            int insulators = 0;
            double insulatorMax = 0;
            for (int e = 0; e < elements; e++)
            {
                conductor[e] = sigma[e] * ConductivitySpreadLimit >= max;
                if (conductor[e]) continue;
                insulators++;
                insulatorMax = Math.Max(insulatorMax, sigma[e]);
            }

            // Conductor pieces: connected through conductor elements only.
            var parent = new int[mesh.NodeCount];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            for (int e = 0; e < elements; e++)
            {
                if (!conductor[e]) continue;
                var el = mesh.Elements[e];
                Union(parent, el.N0, el.N1);
                Union(parent, el.N0, el.N2);
                Union(parent, el.N0, el.N3);
            }
            var anchoredRoots = new HashSet<int>();
            foreach (var voltage in input.BoundaryConditions.OfType<VoltagePotential>())
                foreach (int node in mesh.GetScopeNodes(voltage))
                    anchoredRoots.Add(Find(parent, node));

            var anchored = new bool[elements];
            var nodeAnchored = new bool[mesh.NodeCount];
            var nodeFloating = new bool[mesh.NodeCount];
            var floatingRegions = new SortedSet<int>();
            for (int e = 0; e < elements; e++)
            {
                if (!conductor[e]) continue;
                var el = mesh.Elements[e];
                anchored[e] = anchoredRoots.Contains(Find(parent, el.N0));
                var flags = anchored[e] ? nodeAnchored : nodeFloating;
                flags[el.N0] = flags[el.N1] = flags[el.N2] = flags[el.N3] = true;
                if (!anchored[e]) floatingRegions.Add(mesh.RegionOf(e));
            }

            // An injected current must land on a conductor that reaches a voltage. Anywhere
            // else it has no path: on an isolated conductor the system is singular with a
            // non-zero load (the iteration used to run to its cap and fail without saying
            // why), and on an insulator it asks 10⁻¹⁴ S/m to carry amperes.
            foreach (var current in input.BoundaryConditions.OfType<CurrentFlow>())
            {
                if (current.TotalCurrent == 0) continue;
                foreach (var t in mesh.GetFaceTriangles(current.FaceIds))
                {
                    if (nodeAnchored[t.A] && nodeAnchored[t.B] && nodeAnchored[t.C]) continue;
                    bool isolated = nodeFloating[t.A] || nodeFloating[t.B] || nodeFloating[t.C];
                    throw new InvalidOperationException(isolated
                        ? $"Current '{current.Name}' is injected into a conductor that reaches no " +
                          "voltage potential, so the current has nowhere to go and the potential " +
                          "there has no level. Put a voltage potential on that conductor, or move " +
                          "the current to one that has it."
                        : $"Current '{current.Name}' is injected into an insulator (its conductivity " +
                          $"is below 1/{ConductivitySpreadLimit:G1} of the best conductor in the " +
                          "mesh). Inject it on a conductor face.");
                }
            }

            return new Partition
            {
                Sigma = sigma,
                Anchored = anchored,
                NodeAnchored = nodeAnchored,
                Piece = Enumerable.Range(0, parent.Length).Select(n => Find(parent, n)).ToArray(),
                Staged = insulators > 0,
                InsulatorElements = insulators,
                InsulatorMaxSigma = insulatorMax,
                FloatingRegions = floatingRegions.ToList()
            };
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;
        var partition = Partition.Of(input);

        progress?.Report(new SolverProgress("Applying boundary conditions", 0.05));
        var loads = new double[mesh.NodeCount];
        foreach (var current in input.BoundaryConditions.OfType<CurrentFlow>())
        {
            ScalarSolverHelpers.DistributeOverFaces(mesh, current.FaceIds, current.TotalCurrent, loads, current.Name);
            log.Add($"Current '{current.Name}': {current.TotalCurrent:g4} A injected.");
        }

        var prescribed = new Dictionary<int, double>();
        foreach (var voltage in input.BoundaryConditions.OfType<VoltagePotential>())
        {
            var nodes = mesh.GetScopeNodes(voltage);
            foreach (int node in nodes)
                prescribed[node] = voltage.Volts;
            log.Add($"Voltage '{voltage.Name}': {voltage.Volts:g4} V on {nodes.Count} nodes.");
        }

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
            var conductance = assembler.AssembleStiffness(cancellationToken: cancellationToken);
            log.Add($"Assembled {conductance.RowCount} DOF system, {conductance.NonZeroCount} non-zeros.");

            progress?.Report(new SolverProgress("Solving linear system", 0.35));
            var result = ConstrainedSystemSolver.Solve(conductance, loads, prescribed,
                cancellationToken: cancellationToken);
            log.Add($"Conjugate gradient converged in {result.Iterations.Iterations} iterations " +
                    $"(residual {result.Iterations.ResidualNorm:g3}).");
            phi = result.Displacements;
            reactions = new double[phi.Length];
            conductance.Multiply(phi, reactions);

            if (partition.FloatingRegions.Count > 0)
                log.Add($"Mesh region(s) {string.Join(", ", partition.FloatingRegions)} hold conductor " +
                        "that reaches no voltage potential: it carries no current, and its potential " +
                        "(shown as 0 V) has no level.");
        }
        else
        {
            (phi, reactions, leakage, assembler) = SolveStaged(input, partition, loads, prescribed,
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
        log.Add($"Total dissipated power: {totalPower:g4} W.");
        var summary = new Dictionary<string, double> { ["Total power (W)"] = totalPower };
        LogElectrodeCurrents(input, reactions, leakage, totalPower, log, summary);

        var groups = ScalarSolverHelpers.MaterialGroups(input);
        if (groups is not null)
            log.Add(ScalarSolverHelpers.InterfaceNote("current density and power density"));

        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput
        {
            Fields = new IResultField[]
            {
                new NodalScalarField("Electric potential", "V", phi),
                new NodalVectorField("Current density", "A/m²",
                    ScalarSolverHelpers.NodalAverage(mesh, current_, groups)),
                new NodalScalarField("Power density", "W/m³",
                    ScalarSolverHelpers.NodalAverage(mesh, power, groups)),
                new ElementScalarField(ElementPowerFieldName, "W/m³", power)
            },
            Log = log,
            Summary = summary
        };
    }

    /// <summary>
    /// Conductors first, then everything else with the conductor potentials imposed.
    /// Returns the potential, K·φ over the whole mesh (both stages' matrices), the
    /// current each voltage electrode supplies to the insulator through its conductor,
    /// and an assembler for the gradient recovery.
    /// </summary>
    private static (double[] Phi, double[] Reactions, double[] Leakage,
        ScalarDiffusionAssembler Assembler) SolveStaged(
        SolveInput input, Partition partition, double[] loads,
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
        var first = conductors.AssembleStiffness(cancellationToken: cancellationToken);
        var held = new Dictionary<int, double>(prescribed);
        int conductorNodes = 0;
        for (int n = 0; n < nodes; n++)
        {
            if (partition.NodeAnchored[n]) conductorNodes++;
            else held.TryAdd(n, 0.0);
        }
        if (held.Count < nodes)
        {
            var result = ConstrainedSystemSolver.Solve(first, loads, held,
                cancellationToken: cancellationToken);
            log.Add($"Conductors: {conductorNodes} nodes, conjugate gradient converged in " +
                    $"{result.Iterations.Iterations} iterations (residual {result.Iterations.ResidualNorm:g3}).");
            for (int n = 0; n < nodes; n++)
                if (partition.NodeAnchored[n]) phi[n] = result.Displacements[n];
        }
        first.Multiply(phi, reactions);

        // A conductor piece held at ONE potential with nothing injected carries no
        // conduction current: its potential is that voltage exactly, and its reactions are
        // exactly zero. Left as computed they are the rounding of σ·φ sums — 10⁻¹² A against
        // a laminate leakage of 10⁻¹⁷ A, which is the whole answer when two such pieces
        // face each other across the insulator.
        var pieceVolts = new Dictionary<int, double>();
        var driven = new HashSet<int>();
        foreach (var (node, value) in prescribed)
        {
            if (!partition.NodeAnchored[node]) continue;
            int piece = partition.Piece[node];
            if (pieceVolts.TryGetValue(piece, out double seen) && seen != value) driven.Add(piece);
            pieceVolts[piece] = value;
        }
        for (int n = 0; n < nodes; n++)
            if (loads[n] != 0 && partition.NodeAnchored[n]) driven.Add(partition.Piece[n]);
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
        var second = rest.AssembleStiffness(cancellationToken: cancellationToken);
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
        var electrodesOfPiece = new Dictionary<int, List<int>>();
        for (int i = 0; i < electrodes.Count; i++)
        {
            var pieces = mesh.GetScopeNodes(electrodes[i])
                .Where(n => partition.NodeAnchored[n]).Select(n => partition.Piece[n]).Distinct();
            foreach (int piece in pieces)
            {
                if (!electrodesOfPiece.TryGetValue(piece, out var list))
                    electrodesOfPiece[piece] = list = new List<int>();
                list.Add(i);
            }
        }
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
    /// R = P/I², which is the resistance seen by a current entering at UNIFORM density
    /// over its face. Both are recorded in the log and summary.
    /// </summary>
    private static void LogElectrodeCurrents(SolveInput input, double[] reactions,
        double[]? leakage, double totalPower, List<string> log, Dictionary<string, double> summary)
    {
        var electrodes = input.BoundaryConditions.OfType<VoltagePotential>()
            .Select((v, i) => (v.Name, v.Volts,
                Current: input.Mesh.GetScopeNodes(v).Sum(n => reactions[n]) + (leakage?[i] ?? 0)))
            .ToList();
        foreach (var e in electrodes)
            log.Add($"Electrode '{e.Name}' ({e.Volts:g4} V): net current {e.Current:g4} A.");

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
        // P = I²R defines the resistance the source sees. The current enters at uniform
        // density over its face, so the face is NOT an equipotential: where the current
        // would rather crowd (a pad feeding a narrow trace from one side) this reads
        // above the equipotential-terminal resistance by the spreading term. The two
        // agree when the face is naturally uniform — the end of a bar.
        if (currents.Count == 1
            && electrodes.Select(e => e.Volts).Distinct().Count() == 1)
        {
            double injected = Math.Abs(currents[0].TotalCurrent);
            double resistance = totalPower / (injected * injected);
            log.Add($"Resistance seen by the injected current: {resistance:g4} Ω (from P = I²R, " +
                    "current entering at uniform density over its face — not an equipotential " +
                    "terminal; a voltage on that face gives the terminal-to-terminal value).");
            summary["Resistance (Ω)"] = resistance;
            summary["Current (A)"] = injected;
        }
    }
}
