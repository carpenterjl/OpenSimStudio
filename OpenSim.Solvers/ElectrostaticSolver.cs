using System.Globalization;
using System.Text;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Results;

namespace OpenSim.Solvers;

/// <summary>
/// The Maxwell capacitance matrix of a set of electrodes: Q = C·V, with C_ii the charge
/// on electrode i at 1 V with every other electrode at 0 V, and C_ij (i ≠ j, negative)
/// the charge induced on i by 1 V on j. The two-terminal capacitor a SPICE netlist wants
/// between i and j is <see cref="Mutual"/> = −C_ij.
/// <para>
/// The mesh boundary is a zero-flux wall (a symmetry plane, or the inside of an ideal
/// shield). When no electrode is that wall every row sums to zero: the charges of a closed
/// system balance, and a two-electrode capacitance is C_11 = −C_12.
/// </para>
/// </summary>
public sealed record CapacitanceMatrix(IReadOnlyList<string> Electrodes, double[,] Maxwell)
{
    public int Count => Electrodes.Count;

    public double this[int i, int j] => Maxwell[i, j];

    /// <summary>The capacitor between electrodes i and j in the equivalent network: −C_ij.</summary>
    public double Mutual(int i, int j) => -Maxwell[i, j];

    /// <summary>The capacitor from electrode i to the reference (the zero-potential
    /// surroundings): the row sum Σ_j C_ij. Zero when nothing but electrodes bounds the
    /// field, which is a closed system.</summary>
    public double ToReference(int i)
    {
        double sum = 0;
        for (int j = 0; j < Count; j++) sum += Maxwell[i, j];
        return sum;
    }

    /// <summary>
    /// The capacitance between electrodes a and b with every other electrode left floating
    /// (equipotential, zero net charge) — what a meter across the two terminals reads. For
    /// two electrodes alone this is C_aa.
    /// </summary>
    public double Between(int a, int b)
    {
        if (a == b) throw new ArgumentException("The two electrodes must differ.");
        var others = Enumerable.Range(0, Count).Where(k => k != a && k != b).ToArray();
        int m = others.Length;
        if (m == 0) return Maxwell[a, a];
        // C_ff·V_f = −C_fa (V_a = 1, V_b = 0), then Q_a = C_aa + C_af·V_f.
        var matrix = new double[m, m];
        var rhs = new double[m];
        for (int i = 0; i < m; i++)
        {
            rhs[i] = -Maxwell[others[i], a];
            for (int j = 0; j < m; j++) matrix[i, j] = Maxwell[others[i], others[j]];
        }
        var floating = SolveDense(matrix, rhs);
        double charge = Maxwell[a, a];
        for (int i = 0; i < m; i++) charge += Maxwell[a, others[i]] * floating[i];
        return charge;
    }

    /// <summary>Charge on each electrode for the given potentials, Q = C·V.</summary>
    public double[] Charges(IReadOnlyList<double> volts)
    {
        if (volts.Count != Count) throw new ArgumentException("One potential per electrode is needed.");
        var q = new double[Count];
        for (int i = 0; i < Count; i++)
            for (int j = 0; j < Count; j++) q[i] += Maxwell[i, j] * volts[j];
        return q;
    }

    /// <summary>Stored energy ½·Vᵀ·C·V for the given potentials.</summary>
    public double Energy(IReadOnlyList<double> volts)
    {
        var q = Charges(volts);
        double w = 0;
        for (int i = 0; i < Count; i++) w += 0.5 * volts[i] * q[i];
        return w;
    }

    /// <summary>The largest asymmetry |C_ij − C_ji| relative to the largest diagonal entry:
    /// a reciprocity check on the discretisation (exactly zero for a symmetric matrix).</summary>
    public double Asymmetry()
    {
        double scale = 0, worst = 0;
        for (int i = 0; i < Count; i++) scale = Math.Max(scale, Math.Abs(Maxwell[i, i]));
        for (int i = 0; i < Count; i++)
            for (int j = i + 1; j < Count; j++)
                worst = Math.Max(worst, Math.Abs(Maxwell[i, j] - Maxwell[j, i]));
        return scale > 0 ? worst / scale : 0;
    }

    public IReadOnlyList<string> Describe()
    {
        static string F(double v) => FormatCapacitance(v);
        var lines = new List<string> { $"Maxwell capacitance matrix, {Count} electrode(s) ({string.Join(", ", Electrodes)}):" };
        for (int i = 0; i < Count; i++)
        {
            var row = new StringBuilder($"  {Electrodes[i]}:");
            for (int j = 0; j < Count; j++) row.Append(' ').Append(F(Maxwell[i, j]));
            lines.Add(row.ToString());
        }
        for (int i = 0; i < Count; i++)
            for (int j = i + 1; j < Count; j++)
                lines.Add($"  C({Electrodes[i]}, {Electrodes[j]}) = {F(Mutual(i, j))}; with the rest floating {F(Between(i, j))}");
        return lines;
    }

    /// <summary>A capacitance with its natural prefix (pF, nF, µF), round-trip safe for data.</summary>
    public static string FormatCapacitance(double farads)
    {
        double a = Math.Abs(farads);
        var ci = CultureInfo.InvariantCulture;
        if (a == 0) return "0 F";
        if (a >= 1e-6) return (farads * 1e6).ToString("g5", ci) + " µF";
        if (a >= 1e-9) return (farads * 1e9).ToString("g5", ci) + " nF";
        if (a >= 1e-12) return (farads * 1e12).ToString("g5", ci) + " pF";
        return (farads * 1e15).ToString("g5", ci) + " fF";
    }

    /// <summary>Gaussian elimination with partial pivoting on a small dense system.</summary>
    private static double[] SolveDense(double[,] a, double[] b)
    {
        int n = b.Length;
        var x = (double[])b.Clone();
        var m = (double[,])a.Clone();
        for (int k = 0; k < n; k++)
        {
            int pivot = k;
            for (int i = k + 1; i < n; i++) if (Math.Abs(m[i, k]) > Math.Abs(m[pivot, k])) pivot = i;
            if (Math.Abs(m[pivot, k]) == 0)
                throw new InvalidOperationException("The capacitance matrix of the floating electrodes is singular.");
            if (pivot != k)
            {
                for (int j = 0; j < n; j++) (m[k, j], m[pivot, j]) = (m[pivot, j], m[k, j]);
                (x[k], x[pivot]) = (x[pivot], x[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                double f = m[i, k] / m[k, k];
                if (f == 0) continue;
                for (int j = k; j < n; j++) m[i, j] -= f * m[k, j];
                x[i] -= f * x[k];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = x[i];
            for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
        }
        return x;
    }
}

/// <summary>
/// Electrostatic solver over TET4 elements: ∇·(ε∇φ) = 0 in the dielectrics, with every
/// conductor an equipotential. Produces the potential, the electric field and its
/// magnitude, the energy density, the dielectric stress against each material's breakdown
/// field, the charge on every electrode and the Maxwell capacitance matrix.
/// <para>
/// <b>Electrodes.</b> Each voltage condition is one electrode. Its nodes are the faces it
/// names plus every conductor piece those faces touch: a conductor (conductivity at or
/// above <see cref="ConductorConductivity"/>) is not solved as a volume, its whole volume
/// is held at the electrode's potential, and the field lives in the dielectric around it.
/// Two conditions on one conductor piece are one electrode (and must agree on the volts).
/// A conductor piece no condition reaches floats: it is an equipotential of unknown
/// potential and zero net charge, solved as a dielectric <see cref="FloatingConductorContrast"/>
/// times more permittive than the most permittive dielectric, which holds it uniform to
/// one part in that factor.
/// </para>
/// <para>
/// <b>Capacitance.</b> One unit solve per electrode (1 V on it, 0 V on the others) gives
/// the matrix column by column; the field for the potentials asked for is the superposition
/// of those unit fields, so the matrix costs nothing beyond them. The boundary of the mesh
/// that no electrode claims is a zero-flux wall (D·n = 0): a symmetry plane, or the inside
/// of an ideal shield. An open structure needs enough surrounding dielectric meshed for the
/// wall not to matter, and the log says the wall is there.
/// </para>
/// </summary>
public sealed class ElectrostaticSolver : ISolver
{
    /// <summary>Conductivity [S/m] at or above which a material is an equipotential
    /// conductor in a static field. Anything above 1 S/m relaxes in ε/σ &lt; 10⁻¹⁰ s;
    /// a laminate at 10⁻¹⁴ S/m is a dielectric.</summary>
    public const double ConductorConductivity = 1.0;

    /// <summary>A floating conductor is solved as a dielectric this many times more
    /// permittive than the most permittive dielectric in the mesh.</summary>
    public const double FloatingConductorContrast = 1e4;

    /// <summary>Electrodes beyond this many are refused: one unit solve each.</summary>
    public const int MaxElectrodes = 64;

    private const double Epsilon0 = 8.8541878128e-12;

    public const string PotentialFieldName = "Electric potential";
    public const string FieldMagnitudeName = "Field magnitude |E|";
    public const string StressFieldName = "Dielectric stress";

    public string Name => "Electrostatic (capacitance, E-field)";

    public void Validate(SolveInput input)
    {
        if (input.Mesh.ElementCount == 0)
            throw new InvalidOperationException("The mesh has no elements. Generate a mesh first.");
        if (input.Mesh.IsQuadratic)
            throw new InvalidOperationException(
                "The electrostatic solver supports linear tetrahedral (TET4) meshes only; " +
                "re-generate the mesh with linear tetrahedral elements.");

        ValidateMaterial(input.Material);
        if (input.RegionMaterials is not null)
            foreach (var material in input.RegionMaterials.Values) ValidateMaterial(material);

        if (!input.BoundaryConditions.OfType<VoltagePotential>().Any())
            throw new InvalidOperationException(
                "At least one voltage potential is required: an electrostatic solve needs an electrode.");
        foreach (var bc in input.BoundaryConditions)
        {
            if (bc is not VoltagePotential)
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to an electrostatic " +
                    "solve. Electrodes are voltage potentials; a current has no meaning in a static field, " +
                    "and a prescribed charge is not supported.");
            BoundaryScope.Validate(bc, input.Mesh);
        }
        Electrodes.Of(input);
    }

    /// <summary>Conductor when σ ≥ 1 S/m; otherwise a dielectric that needs its ε_r.</summary>
    public static bool IsConductor(Material m) => m.ElectricalConductivity is >= ConductorConductivity;

    private static void ValidateMaterial(Material m)
    {
        if (IsConductor(m)) return;
        if (m.RelativePermittivity is not > 0)
            throw new InvalidOperationException(
                $"Material '{m.Name}' is a dielectric here (conductivity below {ConductorConductivity:g1} S/m) " +
                "and needs a positive relative permittivity for an electrostatic solve. Set RelativePermittivity, " +
                "or give it a conductivity of at least 1 S/m to make it an equipotential conductor.");
        if (m.DielectricStrength is <= 0)
            throw new InvalidOperationException($"Material '{m.Name}': the dielectric strength must be positive when set.");
    }

    private static double Permittivity(Material m) => Epsilon0 * (m.RelativePermittivity ?? 1);

    /// <summary>The electrodes of a solve: their names, potentials and node sets, and the
    /// conductor pieces that float.</summary>
    private sealed class Electrodes
    {
        public required IReadOnlyList<string> Names { get; init; }
        public required IReadOnlyList<double> Volts { get; init; }
        public required IReadOnlyList<IReadOnlyList<int>> Nodes { get; init; }
        public required bool[] ConductorElement { get; init; }
        public required bool[] ElectrodeElement { get; init; }
        public required int FloatingPieces { get; init; }
        public required int FloatingElements { get; init; }
        public required IReadOnlyList<string> Notes { get; init; }

        public static Electrodes Of(SolveInput input)
        {
            var mesh = input.Mesh;
            int elements = mesh.ElementCount, nodes = mesh.NodeCount;
            var conductor = new bool[elements];
            for (int e = 0; e < elements; e++) conductor[e] = IsConductor(input.MaterialOf(e));

            // Conductor pieces, connected through conductor elements only.
            var parent = new int[nodes];
            for (int i = 0; i < nodes; i++) parent[i] = i;
            for (int e = 0; e < elements; e++)
            {
                if (!conductor[e]) continue;
                var el = mesh.Elements[e];
                Union(parent, el.N0, el.N1); Union(parent, el.N0, el.N2); Union(parent, el.N0, el.N3);
            }
            var conductorNode = new bool[nodes];
            for (int e = 0; e < elements; e++)
            {
                if (!conductor[e]) continue;
                var el = mesh.Elements[e];
                conductorNode[el.N0] = conductorNode[el.N1] = conductorNode[el.N2] = conductorNode[el.N3] = true;
            }

            // Each condition claims its face nodes and the pieces they touch; conditions
            // sharing a piece are one electrode.
            var conditions = input.BoundaryConditions.OfType<VoltagePotential>().ToList();
            var pieceOwner = new Dictionary<int, int>();          // piece root → electrode index
            var electrodeOf = new int[conditions.Count];           // condition → electrode (merged)
            var names = new List<string>();
            var volts = new List<double>();
            var members = new List<List<int>>();                   // electrode → conditions
            var notes = new List<string>();
            for (int c = 0; c < conditions.Count; c++)
            {
                var scope = mesh.GetScopeNodes(conditions[c]);
                var pieces = scope.Where(n => conductorNode[n]).Select(n => Find(parent, n)).Distinct().ToList();
                int target = -1;
                foreach (int piece in pieces)
                    if (pieceOwner.TryGetValue(piece, out int owner))
                    {
                        if (target >= 0 && owner != target)
                            throw new InvalidOperationException(
                                $"Voltage '{conditions[c].Name}' touches conductor that already belongs to two " +
                                $"different electrodes ('{names[target]}' and '{names[owner]}'); the conductor " +
                                "joins them, so one conductor piece cannot carry two potentials.");
                        target = owner;
                    }
                if (target >= 0)
                {
                    if (volts[target] != conditions[c].Volts)
                        throw new InvalidOperationException(
                            $"Voltages '{names[target]}' ({volts[target]:g4} V) and '{conditions[c].Name}' " +
                            $"({conditions[c].Volts:g4} V) sit on the same conductor piece, which is one equipotential. " +
                            "Give them the same potential or separate the conductors.");
                    names[target] += " + " + conditions[c].Name;
                    members[target].Add(c);
                    notes.Add($"Voltage '{conditions[c].Name}' shares a conductor with '{names[target].Split(" + ")[0]}': one electrode.");
                }
                else
                {
                    target = names.Count;
                    names.Add(conditions[c].Name);
                    volts.Add(conditions[c].Volts);
                    members.Add(new List<int> { c });
                }
                electrodeOf[c] = target;
                foreach (int piece in pieces) pieceOwner[piece] = target;
            }
            if (names.Count > MaxElectrodes)
                throw new InvalidOperationException(
                    $"{names.Count} electrodes: the solver runs one unit solve per electrode and stops at {MaxElectrodes}.");

            // Node sets: face nodes of the conditions plus every node of an owned piece.
            var sets = new List<HashSet<int>>();
            for (int i = 0; i < names.Count; i++) sets.Add(new HashSet<int>());
            for (int c = 0; c < conditions.Count; c++)
                sets[electrodeOf[c]].UnionWith(mesh.GetScopeNodes(conditions[c]));
            for (int n = 0; n < nodes; n++)
                if (conductorNode[n] && pieceOwner.TryGetValue(Find(parent, n), out int owner))
                    sets[owner].Add(n);
            // A face node claimed by two electrodes is a conflict only if the volts differ.
            var claimed = new Dictionary<int, int>();
            for (int i = 0; i < sets.Count; i++)
                foreach (int n in sets[i])
                {
                    if (claimed.TryGetValue(n, out int other) && other != i && volts[other] != volts[i])
                        throw new InvalidOperationException(
                            $"Electrodes '{names[other]}' and '{names[i]}' share mesh nodes at different potentials; " +
                            "their faces meet. Separate them by at least one element of dielectric.");
                    claimed[n] = i;
                }

            var electrodeElement = new bool[elements];
            int floatingPieces = 0, floatingElements = 0;
            var floatingRoots = new HashSet<int>();
            for (int e = 0; e < elements; e++)
            {
                if (!conductor[e]) continue;
                int root = Find(parent, mesh.Elements[e].N0);
                if (pieceOwner.ContainsKey(root)) electrodeElement[e] = true;
                else { floatingElements++; floatingRoots.Add(root); }
            }
            floatingPieces = floatingRoots.Count;

            return new Electrodes
            {
                Names = names, Volts = volts, Nodes = sets.Select(s => (IReadOnlyList<int>)s.OrderBy(n => n).ToList()).ToList(),
                ConductorElement = conductor, ElectrodeElement = electrodeElement,
                FloatingPieces = floatingPieces, FloatingElements = floatingElements, Notes = notes
            };
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }
    }

    /// <summary>The unit solves and what is built from them.</summary>
    private sealed record Core(Electrodes Electrodes, CapacitanceMatrix Matrix, double[][] UnitPotentials,
        double[] Permittivity, ScalarDiffusionAssembler Assembler, CsrMatrix Stiffness, int Iterations);

    private static Core Run(SolveInput input, List<string> log, IProgress<SolverProgress>? progress,
        CancellationToken cancellationToken)
    {
        var mesh = input.Mesh;
        var electrodes = Electrodes.Of(input);
        int n = electrodes.Names.Count;

        // ε per element: the dielectric's own; zero in an electrode's conductor (its nodes
        // are all prescribed, so it carries nothing); a large value in a floating piece.
        double maxDielectric = 0;
        int dielectricElements = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
            if (!electrodes.ConductorElement[e])
            {
                maxDielectric = Math.Max(maxDielectric, Permittivity(input.MaterialOf(e)));
                dielectricElements++;
            }
        if (dielectricElements == 0)
            throw new InvalidOperationException(
                "Every element is a conductor: there is no dielectric to hold a field. Mesh the dielectric " +
                "(the laminate, the air gap) with the conductors as regions in it.");
        double floatingEps = FloatingConductorContrast * maxDielectric;
        var eps = new double[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
            eps[e] = electrodes.ElectrodeElement[e] ? 0.0
                : electrodes.ConductorElement[e] ? floatingEps
                : Permittivity(input.MaterialOf(e));

        int conductorElements = mesh.ElementCount - dielectricElements;
        log.Add($"{dielectricElements} dielectric elements; {conductorElements} conductor elements " +
                $"(σ ≥ {ConductorConductivity:g1} S/m) held as equipotentials, {electrodes.FloatingElements} of them in " +
                $"{electrodes.FloatingPieces} floating piece(s)" +
                (electrodes.FloatingPieces > 0
                    ? $" solved as a dielectric {FloatingConductorContrast:g1}× the most permittive one (uniform to 1e-4)."
                    : "."));
        foreach (var note in electrodes.Notes) log.Add(note);
        for (int i = 0; i < n; i++)
            log.Add($"Electrode '{electrodes.Names[i]}': {electrodes.Volts[i]:g4} V on {electrodes.Nodes[i].Count} nodes.");
        log.Add("The mesh boundary no electrode claims is a zero-flux wall (D·n = 0): a symmetry plane or the " +
                "inside of an ideal shield. An open structure needs enough surrounding dielectric meshed for the " +
                "wall not to matter.");

        progress?.Report(new SolverProgress("Assembling the permittivity matrix", 0.05));
        var assembler = new ScalarDiffusionAssembler(mesh, el => eps[el]);
        var stiffness = assembler.AssembleStiffness(cancellationToken: cancellationToken);
        log.Add($"Assembled {stiffness.RowCount} DOF system, {stiffness.NonZeroCount} non-zeros.");

        // One unit solve per electrode; the column of the matrix is the charge on every electrode.
        var unit = new double[n][];
        var maxwell = new double[n, n];
        var reactions = new double[mesh.NodeCount];
        int iterations = 0;
        for (int i = 0; i < n; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SolverProgress($"Unit solve for '{electrodes.Names[i]}'", 0.1 + 0.7 * i / n));
            var prescribed = new Dictionary<int, double>();
            for (int j = 0; j < n; j++)
                foreach (int node in electrodes.Nodes[j]) prescribed[node] = j == i ? 1.0 : 0.0;
            var result = ConstrainedSystemSolver.Solve(stiffness, new double[mesh.NodeCount], prescribed,
                cancellationToken: cancellationToken);
            iterations += result.Iterations.Iterations;
            unit[i] = result.Displacements;
            stiffness.Multiply(unit[i], reactions);
            for (int j = 0; j < n; j++)
            {
                double q = 0;
                foreach (int node in electrodes.Nodes[j]) q += reactions[node];
                maxwell[j, i] = q;
            }
        }
        log.Add($"{n} unit solve(s), {iterations} conjugate-gradient iterations in all.");
        var matrix = new CapacitanceMatrix(electrodes.Names, maxwell);
        return new Core(electrodes, matrix, unit, eps, assembler, stiffness, iterations);
    }

    /// <summary>The capacitance matrix alone: the unit solves without the field recovery.</summary>
    public static CapacitanceMatrix CapacitanceMatrixOf(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        new ElectrostaticSolver().Validate(input);
        return Run(input, new List<string>(), progress, cancellationToken).Matrix;
    }

    public SolveOutput Solve(SolveInput input, IProgress<SolverProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        var log = new List<string>();
        var mesh = input.Mesh;
        var core = Run(input, log, progress, cancellationToken);
        var electrodes = core.Electrodes;
        int n = electrodes.Names.Count;

        progress?.Report(new SolverProgress("Recovering the field", 0.85));
        // The potential for the asked-for volts is the superposition of the unit fields.
        var phi = new double[mesh.NodeCount];
        for (int i = 0; i < n; i++)
        {
            double v = electrodes.Volts[i];
            if (v == 0) continue;
            var u = core.UnitPotentials[i];
            for (int k = 0; k < phi.Length; k++) phi[k] += v * u[k];
        }
        var charges = core.Matrix.Charges(electrodes.Volts);
        double energyFromMatrix = core.Matrix.Energy(electrodes.Volts);

        var field = new Vector3D[mesh.ElementCount];
        var magnitude = new double[mesh.ElementCount];
        var energyDensity = new double[mesh.ElementCount];
        var stress = new double[mesh.ElementCount];
        double energy = 0, maxField = 0, maxStress = 0;
        int maxFieldElement = -1, maxStressElement = -1;
        bool anyStrength = false;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            if (electrodes.ConductorElement[e]) continue;         // E = 0 inside a conductor
            var grad = core.Assembler.ElementGradient(e, phi);
            field[e] = -grad;                                       // E = −∇φ
            magnitude[e] = grad.Length;
            var m = input.MaterialOf(e);
            double epsilon = Permittivity(m);
            energyDensity[e] = 0.5 * epsilon * grad.LengthSquared;  // ½ε|E|²
            energy += energyDensity[e] * mesh.ElementVolume(e);
            if (magnitude[e] > maxField) { maxField = magnitude[e]; maxFieldElement = e; }
            if (m.DielectricStrength is { } strength)
            {
                anyStrength = true;
                stress[e] = magnitude[e] / strength;
                if (stress[e] > maxStress) { maxStress = stress[e]; maxStressElement = e; }
            }
        }

        var summary = new Dictionary<string, double>();
        log.AddRange(core.Matrix.Describe());
        log.Add($"Reciprocity: largest |C_ij − C_ji| is {core.Matrix.Asymmetry():g2} of the largest diagonal entry.");
        for (int i = 0; i < n; i++)
        {
            log.Add($"Charge on '{electrodes.Names[i]}': {charges[i]:g5} C.");
            summary[$"Charge '{electrodes.Names[i]}' (C)"] = charges[i];
        }
        if (n == 2)
        {
            double c = core.Matrix.Between(0, 1);
            log.Add($"Capacitance between '{electrodes.Names[0]}' and '{electrodes.Names[1]}': " +
                    $"{CapacitanceMatrix.FormatCapacitance(c)} ({c:g6} F).");
            summary["Capacitance (F)"] = c;
        }
        log.Add($"Stored energy: {energy:g5} J from ½ε|E|² over the dielectric, {energyFromMatrix:g5} J from ½VᵀCV.");
        summary["Energy (J)"] = energy;
        if (maxFieldElement >= 0)
        {
            log.Add($"Largest field {maxField:g4} V/m at element {maxFieldElement} ({Centroid(mesh, maxFieldElement)}), " +
                    $"material '{input.MaterialOf(maxFieldElement).Name}'.");
            summary["Max |E| (V/m)"] = maxField;
        }
        if (anyStrength)
        {
            log.Add(maxStressElement >= 0
                ? $"Dielectric stress: {maxStress * 100:g3} % of the breakdown field at element {maxStressElement} " +
                  $"({Centroid(mesh, maxStressElement)}), material '{input.MaterialOf(maxStressElement).Name}'" +
                  (maxStress >= 1 ? " — ABOVE BREAKDOWN." : maxStress >= 0.5 ? " — above half the breakdown field." : ".")
                : "Dielectric stress: zero field in every material with a known strength.");
            summary["Max dielectric stress (fraction of breakdown)"] = maxStress;
        }
        else
            log.Add("No material carries a dielectric strength, so no stress ratio is reported; the field itself is.");

        var groups = ScalarSolverHelpers.MaterialGroups(input);
        if (groups is not null)
            log.Add(ScalarSolverHelpers.InterfaceNote("electric field, energy density and stress"));

        var fields = new List<IResultField>
        {
            new NodalScalarField(PotentialFieldName, "V", phi),
            new NodalVectorField("Electric field", "V/m", ScalarSolverHelpers.NodalAverage(mesh, field, groups)),
            new NodalScalarField(FieldMagnitudeName, "V/m", ScalarSolverHelpers.NodalAverage(mesh, magnitude, groups)),
            new ElementScalarField("Field magnitude (element)", "V/m", magnitude),
            new NodalScalarField("Energy density", "J/m³", ScalarSolverHelpers.NodalAverage(mesh, energyDensity, groups))
        };
        if (anyStrength)
            fields.Add(new ElementScalarField(StressFieldName, "fraction of breakdown", stress));

        progress?.Report(new SolverProgress("Done", 1.0));
        return new SolveOutput { Fields = fields, Log = log, Summary = summary };
    }

    private static string Centroid(FeMesh mesh, int element)
    {
        var el = mesh.Elements[element];
        var c = (mesh.Nodes[el.N0] + mesh.Nodes[el.N1] + mesh.Nodes[el.N2] + mesh.Nodes[el.N3]) * 0.25;
        return $"{c.X * 1e3:g4}, {c.Y * 1e3:g4}, {c.Z * 1e3:g4} mm";
    }
}
