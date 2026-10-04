using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// One terminal of a multi-terminal DC conduction problem: a set of faces held at ONE
/// potential by whatever is soldered to them (a pin on its pad, several pins of one
/// regulator output).
/// <para>
/// A source is a voltage behind a series resistance: the terminal sits at
/// <see cref="SourceVolts"/> − <see cref="SeriesResistance"/>·I. A sink draws
/// <see cref="LoadCurrent"/> out of the conductor at whatever potential that leaves it.
/// </para>
/// </summary>
public sealed record ConductionTerminal
{
    public required string Name { get; init; }

    /// <summary>Mesh face ids that make up the terminal.</summary>
    public required IReadOnlyList<int> FaceIds { get; init; }

    /// <summary>Open-circuit voltage of a source [V]; null makes the terminal a sink.</summary>
    public double? SourceVolts { get; init; }

    /// <summary>Series (output) resistance of a source [Ω]; 0 = an ideal voltage.</summary>
    public double SeriesResistance { get; init; }

    /// <summary>Current a sink draws out of the conductor [A].</summary>
    public double LoadCurrent { get; init; }

    public bool IsSource => SourceVolts is not null;
}

/// <summary>What one terminal ended up at.</summary>
/// <param name="Volts">Potential of the terminal's faces [V]; NaN when the terminal's
/// conductor reaches no source.</param>
/// <param name="Amps">Current the terminal delivers INTO the conductor [A] (positive for
/// a source that supplies, negative for a sink that draws).</param>
public sealed record TerminalState(string Name, bool IsSource, double Volts, double Amps, bool Connected);

/// <summary>The solved multi-terminal field.</summary>
public sealed record TerminalConductionResult
{
    public required double[] Potential { get; init; }
    public required Vector3D[] ElementCurrentDensity { get; init; }
    public required double[] ElementPowerDensity { get; init; }

    /// <summary>Power dissipated in the conductor [W].</summary>
    public required double TotalPower { get; init; }

    public required IReadOnlyList<TerminalState> Terminals { get; init; }

    /// <summary>Terminal conductance matrix [S], row-major N×N in terminal order:
    /// current into the conductor at terminal i per volt on terminal j, the others at
    /// 0 V. Rows and columns of a terminal that reaches no source are zero.</summary>
    public required double[] Conductance { get; init; }

    /// <summary>Potential, current density and power density, in the form the DC
    /// conduction solver reports them.</summary>
    public required IReadOnlyList<IResultField> Fields { get; init; }

    public required IReadOnlyList<string> Log { get; init; }
}

/// <summary>
/// DC conduction with any number of equipotential terminals: voltage sources with a series
/// resistance and current sinks.
/// <para>
/// The field equation is linear, so the solve is by superposition. One unit solve per
/// terminal (that terminal at 1 V, the others at 0 V) gives the terminal conductance
/// matrix G and a field per terminal; the terminal voltages then follow from an N×N system
/// — V + R·(G·V) = V_source at a source, (G·V) = −I_load at a sink — and the field is the
/// same combination of the unit fields. Every terminal is an equipotential, which is what
/// a soldered pin makes of its pad; an injected current of uniform density is not, and
/// reads high by the spreading resistance under the pad.
/// </para>
/// <para>
/// Elements with zero conductivity take no part (laminate in a board mesh): their nodes
/// are left out of the system and shown at 0 V.
/// </para>
/// </summary>
public static class TerminalConductionSolver
{
    /// <param name="elementConductivity">σ per element [S/m]; 0 marks an insulator.</param>
    /// <param name="elementGroups">Material id per element for the nodal averages (see
    /// the DC conduction solver); null averages over everything.</param>
    public static TerminalConductionResult Solve(FeMesh mesh, IReadOnlyList<double> elementConductivity,
        IReadOnlyList<ConductionTerminal> terminals, IReadOnlyList<int>? elementGroups = null,
        CancellationToken cancellationToken = default)
    {
        if (mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The terminal conduction solve supports linear tetrahedral (TET4) meshes only.");
        if (elementConductivity.Count != mesh.ElementCount)
            throw new ArgumentException(
                $"elementConductivity has {elementConductivity.Count} entries but the mesh has " +
                $"{mesh.ElementCount} elements.");
        if (!terminals.Any(t => t.IsSource))
            throw new InvalidOperationException(
                "At least one source is required; without one the potential has no level.");

        var log = new List<string>();
        int nodes = mesh.NodeCount;
        int count = terminals.Count;

        // ---- Conductor pieces (connected through conducting elements only).
        var parent = new int[nodes];
        for (int i = 0; i < nodes; i++) parent[i] = i;
        var active = new bool[nodes];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double sigma = elementConductivity[e];
            if (!(sigma >= 0) || double.IsInfinity(sigma))
                throw new InvalidOperationException(
                    $"Element {e} has conductivity {sigma:g3} S/m; it must be zero or positive.");
            if (sigma == 0) continue;
            var el = mesh.Elements[e];
            active[el.N0] = active[el.N1] = active[el.N2] = active[el.N3] = true;
            Union(parent, el.N0, el.N1);
            Union(parent, el.N0, el.N2);
            Union(parent, el.N0, el.N3);
        }

        // ---- Terminal nodes. A terminal is one equipotential, so it belongs to one piece.
        var terminalNodes = new int[count][];
        var terminalOf = new int[nodes];
        Array.Fill(terminalOf, -1);
        var piece = new int[count];
        for (int k = 0; k < count; k++)
        {
            var terminal = terminals[k];
            if (terminal.IsSource && !(terminal.SeriesResistance >= 0))
                throw new InvalidOperationException(
                    $"Source '{terminal.Name}': the series resistance must be zero or positive.");
            var onConductor = mesh.GetFaceNodes(terminal.FaceIds).Where(n => active[n]).OrderBy(n => n).ToArray();
            if (onConductor.Length == 0)
                throw new InvalidOperationException(
                    $"Terminal '{terminal.Name}' lies on no conductor (its faces are missing from the " +
                    "mesh, or they belong to an insulator).");
            foreach (int n in onConductor)
            {
                if (terminalOf[n] >= 0)
                    throw new InvalidOperationException(
                        $"Terminals '{terminals[terminalOf[n]].Name}' and '{terminal.Name}' share a mesh " +
                        "node: two terminals cannot hold the same copper at two potentials. Use separate pads.");
                terminalOf[n] = k;
            }
            terminalNodes[k] = onConductor;
            piece[k] = Find(parent, onConductor[0]);
            if (onConductor.Any(n => Find(parent, n) != piece[k]))
                throw new InvalidOperationException(
                    $"Terminal '{terminal.Name}' spans conductor pieces that are not connected to each " +
                    "other; make it one terminal per piece.");
        }

        // ---- A piece is solvable when it holds a source. Sinks elsewhere have no supply.
        var supplied = new HashSet<int>(Enumerable.Range(0, count).Where(k => terminals[k].IsSource)
            .Select(k => piece[k]));
        var connected = new bool[count];
        for (int k = 0; k < count; k++)
        {
            connected[k] = supplied.Contains(piece[k]);
            if (connected[k]) continue;
            if (terminals[k].LoadCurrent != 0)
                throw new InvalidOperationException(
                    $"Sink '{terminals[k].Name}' draws {terminals[k].LoadCurrent:g4} A from copper that " +
                    "reaches no source, so the current has nowhere to come from. The net is in separate " +
                    "pieces (a missing via, or layers the import did not stitch), or the source is on " +
                    "another net.");
            log.Add($"Terminal '{terminals[k].Name}' is on copper that reaches no source; it carries " +
                    "no current and its potential has no level.");
        }

        // ---- One matrix; every node outside a supplied piece, and every terminal node, is
        //      held. The unit solves differ only in the value on one terminal.
        var assembler = new ScalarDiffusionAssembler(mesh, e => elementConductivity[e]);
        var conductance = assembler.AssembleStiffness(cancellationToken: cancellationToken);
        var held = new Dictionary<int, double>();
        int free = 0;
        for (int n = 0; n < nodes; n++)
        {
            if (!active[n] || !supplied.Contains(Find(parent, n)) || terminalOf[n] >= 0) held[n] = 0.0;
            else free++;
        }
        var reduced = ConstrainedSystemSolver.Reduce(conductance, held, allowUnconstrained: true);
        double[]? preconditioner = free > 0
            ? ConjugateGradientSolver.BuildJacobiPreconditioner(reduced.Reduced) : null;
        log.Add($"{count} terminals on {supplied.Count} supplied conductor piece(s); {free} free nodes " +
                $"of {nodes}, {conductance.NonZeroCount} non-zeros.");

        // In each piece the unit fields sum to one, so the last terminal's is not solved.
        var solve = new bool[count];
        foreach (int root in supplied)
        {
            var members = Enumerable.Range(0, count).Where(k => piece[k] == root).ToList();
            foreach (int k in members.Take(members.Count - 1)) solve[k] = true;
        }

        var unit = new double[count][];
        var iterations = new int[count];
        Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellationToken }, k =>
        {
            if (!solve[k]) return;
            var imposed = new double[nodes];
            foreach (int n in terminalNodes[k]) imposed[n] = 1.0;
            var load = new double[nodes];
            conductance.Multiply(imposed, load);
            for (int n = 0; n < nodes; n++) load[n] = -load[n];
            var x = new double[reduced.FreeCount];
            if (free > 0)
            {
                var cg = new ConjugateGradientSolver
                {
                    Tolerance = 1e-10, MaxIterations = Math.Max(4 * reduced.FreeCount, 1000)
                };
                var result = cg.Solve(reduced.Reduced, reduced.ReduceLoads(load), x, preconditioner!,
                    cancellationToken);
                if (!result.Converged)
                    throw new InvalidOperationException(
                        $"The unit solve for terminal '{terminals[k].Name}' did not converge after " +
                        $"{result.Iterations} iterations (residual {result.ResidualNorm:g3}). Check the mesh quality.");
                iterations[k] = result.Iterations;
            }
            var field = reduced.Expand(x);
            foreach (int n in terminalNodes[k]) field[n] = 1.0;
            unit[k] = field;
        });
        foreach (int root in supplied)
        {
            var members = Enumerable.Range(0, count).Where(k => piece[k] == root).ToList();
            int last = members[^1];
            var field = new double[nodes];
            for (int n = 0; n < nodes; n++)
            {
                if (!active[n] || Find(parent, n) != root) continue;
                double sum = 1.0;
                foreach (int k in members)
                    if (k != last) sum -= unit[k][n];
                field[n] = sum;
            }
            foreach (int n in terminalNodes[last]) field[n] = 1.0;
            foreach (int k in members)
                if (k != last)
                    foreach (int n in terminalNodes[k]) field[n] = 0.0;
            unit[last] = field;
        }
        log.Add($"{solve.Count(s => s)} unit solve(s), {iterations.Sum()} conjugate-gradient iterations in total.");

        // ---- Terminal conductance matrix: G[i,j] = current into the conductor at i per
        //      volt on j. Symmetric by reciprocity; the two halves are averaged.
        var g = new double[count * count];
        var product = new double[nodes];
        for (int j = 0; j < count; j++)
        {
            if (!connected[j]) continue;
            conductance.Multiply(unit[j], product);
            for (int i = 0; i < count; i++)
            {
                if (!connected[i]) continue;
                double sum = 0;
                foreach (int n in terminalNodes[i]) sum += product[n];
                g[i * count + j] = sum;
            }
        }
        for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                double mean = 0.5 * (g[i * count + j] + g[j * count + i]);
                g[i * count + j] = g[j * count + i] = mean;
            }

        // ---- Terminal voltages.
        var volts = SolveTerminalVoltages(terminals, connected, g);

        var phi = new double[nodes];
        for (int k = 0; k < count; k++)
        {
            if (!connected[k]) continue;
            double v = volts[k];
            var field = unit[k];
            for (int n = 0; n < nodes; n++) phi[n] += v * field[n];
        }
        for (int k = 0; k < count; k++)
            if (connected[k])
                foreach (int n in terminalNodes[k]) phi[n] = volts[k];

        var states = new TerminalState[count];
        for (int i = 0; i < count; i++)
        {
            double amps = 0;
            for (int j = 0; j < count; j++) amps += g[i * count + j] * (connected[j] ? volts[j] : 0);
            states[i] = new TerminalState(terminals[i].Name, terminals[i].IsSource,
                connected[i] ? volts[i] : double.NaN, connected[i] ? amps : 0, connected[i]);
        }

        // ---- Field recovery.
        var current = new Vector3D[mesh.ElementCount];
        var power = new double[mesh.ElementCount];
        double totalPower = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            double sigma = elementConductivity[e];
            if (sigma == 0) continue;
            var gradient = assembler.ElementGradient(e, phi);
            current[e] = gradient * -sigma;
            power[e] = sigma * Vector3D.Dot(gradient, gradient);
            totalPower += power[e] * mesh.ElementVolume(e);
        }
        double terminalPower = states.Where(s => s.Connected).Sum(s => s.Volts * s.Amps);
        log.Add($"Dissipated in the conductor: {totalPower:g5} W (terminal balance ΣV·I = {terminalPower:g5} W).");
        foreach (var s in states.Where(s => s.Connected))
            log.Add($"{(s.IsSource ? "Source" : "Sink")} '{s.Name}': {s.Volts:g6} V, {s.Amps:g5} A into the conductor.");

        return new TerminalConductionResult
        {
            Potential = phi,
            ElementCurrentDensity = current,
            ElementPowerDensity = power,
            TotalPower = totalPower,
            Terminals = states,
            Conductance = g,
            Fields = new IResultField[]
            {
                new NodalScalarField("Electric potential", "V", phi),
                new NodalVectorField("Current density", "A/m²",
                    ScalarSolverHelpers.NodalAverage(mesh, current, elementGroups)),
                new NodalScalarField("Power density", "W/m³",
                    ScalarSolverHelpers.NodalAverage(mesh, power, elementGroups)),
                new ElementScalarField(ElectricalConductionSolver.ElementPowerFieldName, "W/m³", power)
            },
            Log = log
        };
    }

    /// <summary>
    /// The N×N terminal system. A source row is V_i + R_i·Σ_j G_ij·V_j = V_source,i (the
    /// terminal sits below its set-point by the drop in its own output resistance); a sink
    /// row is Σ_j G_ij·V_j = −I_load,i. Rows are scaled to volts so an ideal source and a
    /// milliohm conductance sit in one well-conditioned system.
    /// </summary>
    private static double[] SolveTerminalVoltages(IReadOnlyList<ConductionTerminal> terminals,
        bool[] connected, double[] g)
    {
        int count = terminals.Count;
        var index = Enumerable.Range(0, count).Where(k => connected[k]).ToArray();
        int n = index.Length;
        var a = new double[n * n];
        var b = new double[n];
        for (int r = 0; r < n; r++)
        {
            int i = index[r];
            var terminal = terminals[i];
            if (terminal.IsSource)
            {
                for (int c = 0; c < n; c++)
                    a[r * n + c] = terminal.SeriesResistance * g[i * count + index[c]];
                a[r * n + r] += 1.0;
                b[r] = terminal.SourceVolts!.Value;
            }
            else
            {
                // Divide the row by its own conductance: volts, like the source rows.
                double scale = g[i * count + i];
                if (!(scale > 0))
                    throw new InvalidOperationException(
                        $"Sink '{terminal.Name}' has no conductance to the rest of the conductor.");
                for (int c = 0; c < n; c++) a[r * n + c] = g[i * count + index[c]] / scale;
                b[r] = -terminal.LoadCurrent / scale;
            }
        }
        var x = new double[n];
        DenseLu.FactorInPlace(a, n, new int[n]).Solve(b, x);
        var volts = new double[count];
        for (int r = 0; r < n; r++) volts[index[r]] = x[r];
        return volts;
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
