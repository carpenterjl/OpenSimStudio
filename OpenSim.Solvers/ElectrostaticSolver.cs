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
/// Against a zero-flux wall (a symmetry plane, or the inside of an ideal shield) every row
/// sums to zero when no electrode is that wall: the charges of a closed system balance,
/// and a two-electrode capacitance is C_11 = −C_12. With an open outer boundary the row sum
/// <see cref="ToReference"/> is the electrode's capacitance to the zero potential at
/// infinity, and an isolated conductor's C_11 is its self-capacitance.
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
    /// field, which is a closed system; the capacitance to infinity on an open boundary.</summary>
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
        var floating = SolveDense(matrix, rhs, "The capacitance matrix of the floating electrodes is singular.");
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
    internal static double[] SolveDense(double[,] a, double[] b, string singularMessage)
    {
        int n = b.Length;
        var x = (double[])b.Clone();
        var m = (double[,])a.Clone();
        for (int k = 0; k < n; k++)
        {
            int pivot = k;
            for (int i = k + 1; i < n; i++) if (Math.Abs(m[i, k]) > Math.Abs(m[pivot, k])) pivot = i;
            if (Math.Abs(m[pivot, k]) == 0)
                throw new InvalidOperationException(singularMessage);
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
/// <b>Charge.</b> An <see cref="ElectricCharge"/> whose faces touch a conductor makes that
/// piece an electrode of unknown potential carrying the given net charge; its potential
/// comes out of the capacitance matrix (Q = C·V solved for the charged rows). An
/// <see cref="ElectricCharge"/> on a dielectric surface is a uniform surface charge density,
/// the Neumann condition D·n = σ, carried as a nodal load (area-weighted over the free nodes
/// of its faces so the total is exact); its field is one more solve with every electrode at
/// zero, and the charge it induces on each electrode is added to C·V.
/// </para>
/// <para>
/// <b>Capacitance.</b> One unit solve per electrode (1 V on it, 0 V on the others) gives
/// the matrix column by column; the field for the potentials asked for is the superposition
/// of those unit fields, so the matrix costs nothing beyond them. The boundary of the mesh
/// that no electrode claims is a zero-flux wall (D·n = 0): a symmetry plane, or the inside
/// of an ideal shield. An open structure needs enough surrounding dielectric meshed for the
/// wall not to matter, and the log says the wall is there — or the boundary is declared open
/// (<see cref="ElectrostaticSettings.OpenBoundary"/>): every free boundary triangle then
/// carries the asymptotic condition ε∂φ/∂n = −εφ(r̂·n)/r, exact for the 1/r field of the net
/// charge, with the dipole and higher terms left to decay inside the mesh; the log states
/// a/R, the size of the electrodes against the distance to the boundary.
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

        if (!input.BoundaryConditions.Any(bc => bc is VoltagePotential or ElectricCharge))
            throw new InvalidOperationException(
                "At least one voltage potential or charge is required: an electrostatic solve needs an electrode.");
        foreach (var bc in input.BoundaryConditions)
        {
            if (bc is not (VoltagePotential or ElectricCharge))
                throw new InvalidOperationException(
                    $"Boundary condition '{bc.Name}' ({bc.GetType().Name}) does not apply to an electrostatic " +
                    "solve. Electrodes are voltage potentials or charges; a current has no meaning in a static field.");
            BoundaryScope.Validate(bc, input.Mesh);
        }
        var electrodes = Electrodes.Of(input);
        bool open = input.Electrostatic?.OpenBoundary == true;
        if (!open && !electrodes.Volts.Any(v => v.HasValue))
            throw new InvalidOperationException(
                "No voltage potential: against a zero-flux wall the potential has no reference (only charges are " +
                "given, and a closed system's charges would have to sum to zero). Give one electrode a potential, " +
                "or make the outer boundary open (ElectrostaticSettings.OpenBoundary), which puts zero potential at infinity.");
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

    /// <summary>The electrodes of a solve: their names, potentials or charges and node sets,
    /// the conductor pieces that float, and the charges laid on dielectric surfaces.</summary>
    private sealed class Electrodes
    {
        public required IReadOnlyList<string> Names { get; init; }
        /// <summary>The prescribed potential, or null for an electrode whose charge is prescribed.</summary>
        public required IReadOnlyList<double?> Volts { get; init; }
        /// <summary>The prescribed net charge, or null for an electrode whose potential is prescribed.</summary>
        public required IReadOnlyList<double?> Charges { get; init; }
        public required IReadOnlyList<IReadOnlyList<int>> Nodes { get; init; }
        public required bool[] ConductorElement { get; init; }
        public required bool[] ElectrodeElement { get; init; }
        public required int FloatingPieces { get; init; }
        public required int FloatingElements { get; init; }
        /// <summary>Charges on dielectric surfaces: no conductor under them.</summary>
        public required IReadOnlyList<ElectricCharge> SurfaceCharges { get; init; }
        public required IReadOnlyList<string> Notes { get; init; }

        public int Count => Names.Count;

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
            // sharing a piece are one electrode. A charge touching no conductor is a surface charge.
            var conditions = input.BoundaryConditions.Where(bc => bc is VoltagePotential or ElectricCharge).ToList();
            var pieceOwner = new Dictionary<int, int>();          // piece root → electrode index
            var electrodeOf = new int[conditions.Count];           // condition → electrode (merged); −1 = surface charge
            var names = new List<string>();
            var volts = new List<double?>();
            var charges = new List<double?>();
            var surface = new List<ElectricCharge>();
            var notes = new List<string>();
            for (int c = 0; c < conditions.Count; c++)
            {
                var condition = conditions[c];
                var scope = mesh.GetScopeNodes(condition);
                var pieces = scope.Where(n => conductorNode[n]).Select(n => Find(parent, n)).Distinct().ToList();
                if (condition is ElectricCharge sheet && pieces.Count == 0)
                {
                    surface.Add(sheet);
                    electrodeOf[c] = -1;
                    continue;
                }
                double? v = condition is VoltagePotential vp ? vp.Volts : null;
                double? q = condition is ElectricCharge ec ? ec.TotalCharge : null;
                int target = -1;
                foreach (int piece in pieces)
                    if (pieceOwner.TryGetValue(piece, out int owner))
                    {
                        if (target >= 0 && owner != target)
                            throw new InvalidOperationException(
                                $"Condition '{condition.Name}' touches conductor that already belongs to two " +
                                $"different electrodes ('{names[target]}' and '{names[owner]}'); the conductor " +
                                "joins them, so one conductor piece cannot carry two potentials.");
                        target = owner;
                    }
                if (target >= 0)
                {
                    if (volts[target].HasValue != v.HasValue)
                        throw new InvalidOperationException(
                            $"'{names[target]}' and '{condition.Name}' sit on the same conductor piece, which is one " +
                            "equipotential: it cannot carry both a potential and a charge. Give it one or the other.");
                    if (v.HasValue && volts[target] != v)
                        throw new InvalidOperationException(
                            $"Voltages '{names[target]}' ({volts[target]:g4} V) and '{condition.Name}' " +
                            $"({v:g4} V) sit on the same conductor piece, which is one equipotential. " +
                            "Give them the same potential or separate the conductors.");
                    if (q.HasValue)
                    {
                        charges[target] = charges[target]!.Value + q.Value;
                        notes.Add($"Charge '{condition.Name}' shares a conductor with '{names[target].Split(" + ")[0]}': " +
                                  $"one electrode carrying {charges[target]:g5} C in all.");
                    }
                    else
                        notes.Add($"Voltage '{condition.Name}' shares a conductor with '{names[target].Split(" + ")[0]}': one electrode.");
                    names[target] += " + " + condition.Name;
                }
                else
                {
                    target = names.Count;
                    names.Add(condition.Name);
                    volts.Add(v);
                    charges.Add(q);
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
                if (electrodeOf[c] >= 0) sets[electrodeOf[c]].UnionWith(mesh.GetScopeNodes(conditions[c]));
            for (int n = 0; n < nodes; n++)
                if (conductorNode[n] && pieceOwner.TryGetValue(Find(parent, n), out int owner))
                    sets[owner].Add(n);
            // A face node claimed by two electrodes is a conflict unless both prescribe the same volts.
            var claimed = new Dictionary<int, int>();
            for (int i = 0; i < sets.Count; i++)
                foreach (int n in sets[i])
                {
                    if (claimed.TryGetValue(n, out int other) && other != i
                        && !(volts[other].HasValue && volts[i].HasValue && volts[other] == volts[i]))
                        throw new InvalidOperationException(
                            $"Electrodes '{names[other]}' and '{names[i]}' share mesh nodes at different potentials; " +
                            "their faces meet. Separate them by at least one element of dielectric.");
                    claimed[n] = i;
                }

            var electrodeElement = new bool[elements];
            int floatingElements = 0;
            var floatingRoots = new HashSet<int>();
            for (int e = 0; e < elements; e++)
            {
                if (!conductor[e]) continue;
                int root = Find(parent, mesh.Elements[e].N0);
                if (pieceOwner.ContainsKey(root)) electrodeElement[e] = true;
                else { floatingElements++; floatingRoots.Add(root); }
            }

            return new Electrodes
            {
                Names = names, Volts = volts, Charges = charges,
                Nodes = sets.Select(s => (IReadOnlyList<int>)s.OrderBy(n => n).ToList()).ToList(),
                ConductorElement = conductor, ElectrodeElement = electrodeElement,
                FloatingPieces = floatingRoots.Count, FloatingElements = floatingElements,
                SurfaceCharges = surface, Notes = notes
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

    /// <summary>What the open boundary amounted to: the triangles it was put on, where its
    /// 1/r field is centred, the electrodes' reach a from that centre, the boundary's
    /// closest distance R, and how many triangles faced the centre (left as a wall).</summary>
    private sealed record OpenBoundaryInfo(int Triangles, Vector3D Centre, double Reach, double Distance,
        int FacingCentre, int OnFloatingConductor);

    /// <summary>The unit solves and what is built from them.</summary>
    private sealed record Core(Electrodes Electrodes, CapacitanceMatrix Matrix, double[] Volts, double[][] UnitPotentials,
        double[] SurfacePotential, double[] SurfaceLoad, double[] SurfaceInduced, double[] Permittivity,
        ScalarDiffusionAssembler Assembler, CsrMatrix Stiffness, int Iterations, OpenBoundaryInfo? Open);

    private static Core Run(SolveInput input, List<string> log, IProgress<SolverProgress>? progress,
        CancellationToken cancellationToken)
    {
        var mesh = input.Mesh;
        var electrodes = Electrodes.Of(input);
        int n = electrodes.Count;

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
            log.Add(electrodes.Volts[i] is { } v
                ? $"Electrode '{electrodes.Names[i]}': {v:g4} V on {electrodes.Nodes[i].Count} nodes."
                : $"Electrode '{electrodes.Names[i]}': {electrodes.Charges[i]:g5} C on {electrodes.Nodes[i].Count} nodes, " +
                  "potential to be found.");

        // Every electrode node is prescribed in every solve.
        var electrodeNode = new bool[mesh.NodeCount];
        for (int i = 0; i < n; i++) foreach (int node in electrodes.Nodes[i]) electrodeNode[node] = true;

        progress?.Report(new SolverProgress("Assembling the permittivity matrix", 0.05));
        var assembler = new ScalarDiffusionAssembler(mesh, el => eps[el]);
        // The volume matrix alone measures the electrodes' charges (the flux into them); the
        // system solved adds the open boundary's surface term when there is one.
        var stiffness = assembler.AssembleStiffness(cancellationToken: cancellationToken);
        var system = stiffness;
        OpenBoundaryInfo? open = null;
        if (input.Electrostatic?.OpenBoundary == true)
        {
            var (terms, info) = OpenBoundaryTerms(input, electrodes, eps, electrodeNode);
            open = info;
            system = assembler.AssembleStiffness(terms, cancellationToken: cancellationToken);
            log.Add($"Open outer boundary: the asymptotic condition D·n = −ε·φ·(r̂·n)/r on {info.Triangles} boundary " +
                    $"triangle(s) about ({info.Centre.X * 1e3:g4}, {info.Centre.Y * 1e3:g4}, {info.Centre.Z * 1e3:g4}) mm — " +
                    "exact for the 1/r field of the net charge; the dipole and higher terms are not absorbed.");
            log.Add($"The electrodes reach a = {info.Reach * 1e3:g3} mm from that centre; the boundary is R = " +
                    $"{info.Distance * 1e3:g3} mm away at the closest, a/R = {info.Reach / info.Distance:g2}: the energy of a " +
                    $"dipole beyond the boundary is of order (a/R)³ = {Math.Pow(info.Reach / info.Distance, 3):g2} of its total.");
            if (info.FacingCentre > 0)
                log.Add($"{info.FacingCentre} boundary triangle(s) face the centre (a concave boundary, or the centre " +
                        "outside the mesh) and were left as a zero-flux wall.");
            if (info.OnFloatingConductor > 0)
                log.Add($"{info.OnFloatingConductor} boundary triangle(s) lie on a floating conductor and were left as a " +
                        "zero-flux wall: a conductor reaching the open boundary has no finite extent.");
        }
        else
            log.Add("The mesh boundary no electrode claims is a zero-flux wall (D·n = 0): a symmetry plane or the " +
                    "inside of an ideal shield. An open structure needs enough surrounding dielectric meshed for the " +
                    "wall not to matter.");
        log.Add($"Assembled {system.RowCount} DOF system, {system.NonZeroCount} non-zeros.");

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
            var result = ConstrainedSystemSolver.Solve(system, new double[mesh.NodeCount], prescribed,
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

        // The charges laid on dielectric surfaces: one more solve, every electrode at zero.
        var surfaceLoad = new double[mesh.NodeCount];
        var surfacePotential = new double[mesh.NodeCount];
        var surfaceInduced = new double[n];
        if (electrodes.SurfaceCharges.Count > 0)
        {
            foreach (var sheet in electrodes.SurfaceCharges)
                DistributeSurfaceCharge(mesh, sheet, electrodeNode, surfaceLoad, log);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SolverProgress("Solve for the surface charges", 0.8));
            var zero = new Dictionary<int, double>();
            for (int j = 0; j < n; j++) foreach (int node in electrodes.Nodes[j]) zero[node] = 0.0;
            var result = ConstrainedSystemSolver.Solve(system, surfaceLoad, zero, cancellationToken: cancellationToken,
                allowUnconstrained: true);
            iterations += result.Iterations.Iterations;
            surfacePotential = result.Displacements;
            stiffness.Multiply(surfacePotential, reactions);
            for (int j = 0; j < n; j++)
                foreach (int node in electrodes.Nodes[j]) surfaceInduced[j] += reactions[node];
            log.Add($"Surface charges: one solve, {result.Iterations.Iterations} iterations; they induce " +
                    string.Join(", ", electrodes.Names.Select((name, j) => $"{surfaceInduced[j]:g4} C on '{name}'")) +
                    (n > 0 ? "." : "nothing (no electrode)."));
        }

        // Potentials: prescribed where given; for the charged electrodes, from Q = C·V + Q_induced.
        var volts = new double[n];
        var charged = Enumerable.Range(0, n).Where(i => !electrodes.Volts[i].HasValue).ToArray();
        for (int i = 0; i < n; i++) volts[i] = electrodes.Volts[i] ?? 0;
        if (charged.Length > 0)
        {
            int m = charged.Length;
            var cqq = new double[m, m];
            var rhs = new double[m];
            for (int a = 0; a < m; a++)
            {
                int i = charged[a];
                rhs[a] = electrodes.Charges[i]!.Value - surfaceInduced[i];
                for (int j = 0; j < n; j++)
                    if (electrodes.Volts[j] is { } v) rhs[a] -= maxwell[i, j] * v;
                for (int b = 0; b < m; b++) cqq[a, b] = maxwell[i, charged[b]];
            }
            var vq = CapacitanceMatrix.SolveDense(cqq, rhs,
                "The capacitance matrix of the charged electrodes is singular: against a zero-flux wall with no " +
                "voltage electrode the potential has no reference. Give one electrode a potential or open the boundary.");
            for (int a = 0; a < m; a++)
            {
                volts[charged[a]] = vq[a];
                log.Add($"Electrode '{electrodes.Names[charged[a]]}' carrying {electrodes.Charges[charged[a]]:g5} C sits at " +
                        $"{vq[a]:g5} V.");
            }
        }

        return new Core(electrodes, matrix, volts, unit, surfacePotential, surfaceLoad, surfaceInduced, eps, assembler,
            stiffness, iterations, open);
    }

    /// <summary>
    /// A uniform surface charge density over the faces of a charge condition, as consistent
    /// nodal loads ∫σNᵢdA = σ·A/3 per triangle node — over the FREE nodes only (a node an
    /// electrode holds absorbs no load), re-scaled so the total is the condition's charge exactly.
    /// </summary>
    private static void DistributeSurfaceCharge(FeMesh mesh, ElectricCharge sheet, bool[] electrodeNode,
        double[] loads, List<string> log)
    {
        var triangles = mesh.GetFaceTriangles(sheet.FaceIds);
        var weight = new Dictionary<int, double>();
        double area = 0;
        int held = 0;
        foreach (var t in triangles)
        {
            double share = ScalarDiffusionAssembler.SurfaceArea(mesh, t) / 3.0;
            area += 3 * share;
            foreach (int node in new[] { t.A, t.B, t.C })
            {
                if (electrodeNode[node]) { held++; continue; }
                weight[node] = weight.GetValueOrDefault(node) + share;
            }
        }
        double total = weight.Values.Sum();
        if (total <= 0)
            throw new InvalidOperationException(
                $"Charge '{sheet.Name}' lies on a dielectric surface with no free node to carry it: its faces have " +
                "no area, or every node of them belongs to an electrode.");
        foreach (var (node, w) in weight) loads[node] += sheet.TotalCharge * w / total;
        log.Add($"Surface charge '{sheet.Name}': {sheet.TotalCharge:g5} C over {area * 1e6:g4} mm² " +
                $"(σ = {sheet.TotalCharge / area:g4} C/m²) on {weight.Count} free nodes" +
                (held > 0 ? $"; {held} node share(s) fall on an electrode and were folded into the free nodes." : "."));
    }

    /// <summary>
    /// The Robin terms of the open boundary: on every triangle of the mesh's true outer skin
    /// (element faces that appear once) that is not wholly an electrode's, the coefficient
    /// ε·(r̂·n)/r at the triangle's centroid, n pointing out of the owning element. A triangle
    /// facing the centre (r̂·n ≤ 0) or lying on a floating conductor stays a wall. R is the
    /// closest distance from the centre to the plane of an open triangle.
    /// </summary>
    private static (List<ScalarDiffusionAssembler.RobinTerm> Terms, OpenBoundaryInfo Info) OpenBoundaryTerms(
        SolveInput input, Electrodes electrodes, double[] eps, bool[] electrodeNode)
    {
        var mesh = input.Mesh;
        var skin = new Dictionary<(int, int, int), (int Count, int Element, int A, int B, int C)>();
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            foreach (var (a, b, c) in new[] { (el.N1, el.N2, el.N3), (el.N0, el.N2, el.N3), (el.N0, el.N1, el.N3), (el.N0, el.N1, el.N2) })
            {
                var key = Sorted(a, b, c);
                skin[key] = skin.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.Element, seen.A, seen.B, seen.C) : (1, e, a, b, c);
            }
        }

        // The centre: the user's, or the centroid of every electrode node.
        Vector3D centre;
        if (input.Electrostatic?.Centre is { } given) centre = given;
        else
        {
            var sum = Vector3D.Zero;
            int count = 0;
            for (int i = 0; i < electrodes.Count; i++)
                foreach (int node in electrodes.Nodes[i]) { sum += mesh.Nodes[node]; count++; }
            if (count == 0)
                foreach (var sheet in electrodes.SurfaceCharges)
                    foreach (int node in mesh.GetScopeNodes(sheet)) { sum += mesh.Nodes[node]; count++; }
            centre = count > 0 ? sum / count : mesh.Nodes.Aggregate(Vector3D.Zero, (s, p) => s + p) / mesh.NodeCount;
        }
        double reach = 0;
        for (int i = 0; i < electrodes.Count; i++)
            foreach (int node in electrodes.Nodes[i]) reach = Math.Max(reach, (mesh.Nodes[node] - centre).Length);
        foreach (var sheet in electrodes.SurfaceCharges)
            foreach (int node in mesh.GetScopeNodes(sheet)) reach = Math.Max(reach, (mesh.Nodes[node] - centre).Length);

        var terms = new List<ScalarDiffusionAssembler.RobinTerm>();
        double closest = double.PositiveInfinity;
        int facing = 0, onFloating = 0;
        foreach (var (_, face) in skin)
        {
            if (face.Count != 1) continue;
            if (electrodeNode[face.A] && electrodeNode[face.B] && electrodeNode[face.C]) continue;
            if (electrodes.ConductorElement[face.Element]) { onFloating++; continue; }
            var pa = mesh.Nodes[face.A]; var pb = mesh.Nodes[face.B]; var pc = mesh.Nodes[face.C];
            var centroid = (pa + pb + pc) / 3;
            var normal = Vector3D.Cross(pb - pa, pc - pa);
            if (normal.Length <= 0) continue;
            normal = normal.Normalized();
            var el = mesh.Elements[face.Element];
            var elementCentroid = (mesh.Nodes[el.N0] + mesh.Nodes[el.N1] + mesh.Nodes[el.N2] + mesh.Nodes[el.N3]) * 0.25;
            if (Vector3D.Dot(normal, centroid - elementCentroid) < 0) normal = -normal;
            var r = centroid - centre;
            double distance = r.Length;
            double cosine = distance > 0 ? Vector3D.Dot(r, normal) / distance : 0;
            if (cosine <= 0 || distance <= 0) { facing++; continue; }
            closest = Math.Min(closest, Vector3D.Dot(r, normal));      // to the triangle's plane
            terms.Add(new ScalarDiffusionAssembler.RobinTerm(new BoundaryTriangle(face.A, face.B, face.C, -1),
                eps[face.Element] * cosine / distance));
        }
        if (terms.Count == 0)
            throw new InvalidOperationException(
                "The open boundary found no free outer triangle: every face of the mesh boundary belongs to an electrode.");
        return (terms, new OpenBoundaryInfo(terms.Count, centre, reach, closest, facing, onFloating));
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
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
        int n = electrodes.Count;

        progress?.Report(new SolverProgress("Recovering the field", 0.85));
        // The potential for the asked-for volts is the superposition of the unit fields,
        // plus the field of the surface charges with every electrode at zero.
        var phi = (double[])core.SurfacePotential.Clone();
        for (int i = 0; i < n; i++)
        {
            double v = core.Volts[i];
            if (v == 0) continue;
            var u = core.UnitPotentials[i];
            for (int k = 0; k < phi.Length; k++) phi[k] += v * u[k];
        }
        var charges = core.Matrix.Charges(core.Volts);
        for (int i = 0; i < n; i++) charges[i] += core.SurfaceInduced[i];
        // ½ΣQφ over every charge: the electrodes' and the surface sheets'.
        double energyFromCharges = 0;
        for (int i = 0; i < n; i++) energyFromCharges += 0.5 * core.Volts[i] * charges[i];
        for (int k = 0; k < phi.Length; k++) energyFromCharges += 0.5 * core.SurfaceLoad[k] * phi[k];

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
            log.Add($"Charge on '{electrodes.Names[i]}': {charges[i]:g5} C" +
                    (electrodes.Volts[i].HasValue ? "." : $" at {core.Volts[i]:g5} V."));
            summary[$"Charge '{electrodes.Names[i]}' (C)"] = charges[i];
            if (!electrodes.Volts[i].HasValue) summary[$"Potential '{electrodes.Names[i]}' (V)"] = core.Volts[i];
        }
        if (n == 2)
        {
            double c = core.Matrix.Between(0, 1);
            log.Add($"Capacitance between '{electrodes.Names[0]}' and '{electrodes.Names[1]}': " +
                    $"{CapacitanceMatrix.FormatCapacitance(c)} ({c:g6} F).");
            summary["Capacitance (F)"] = c;
        }
        if (core.Open is not null)
        {
            for (int i = 0; i < n; i++)
            {
                double c = core.Matrix.ToReference(i);
                log.Add($"Capacitance of '{electrodes.Names[i]}' to infinity (the others grounded): " +
                        $"{CapacitanceMatrix.FormatCapacitance(c)} ({c:g6} F).");
                summary[$"Capacitance to infinity '{electrodes.Names[i]}' (F)"] = c;
            }
            summary["Open boundary a/R"] = core.Open.Reach / core.Open.Distance;
        }
        log.Add($"Stored energy: {energy:g5} J from ½ε|E|² over the meshed dielectric, {energyFromCharges:g5} J from ½ΣQφ" +
                (core.Open is not null ? " (which includes the field beyond the open boundary)." : "."));
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
