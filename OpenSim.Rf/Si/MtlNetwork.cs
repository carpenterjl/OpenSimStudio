using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Si;

/// <summary>A cascadable section of an MTL network. Every section in one cascade must present
/// the same conductor COUNT — the chain matrices multiply — but they need not all be coupled
/// or all the same length per line.</summary>
public abstract record MtlSectionBase
{
    public abstract int ConductorCount { get; }

    /// <summary>The longest conductor path through this section (the coupled length for a
    /// uniform section; the longest lead for an uncoupled one). Reporting only.</summary>
    public abstract double LongestLengthMeters { get; }

    internal abstract ComplexDenseMatrix Chain(double frequencyHz);

    /// <summary>The slowest mode's one-way delay through this section at f [s].</summary>
    internal abstract double DelaySeconds(double frequencyHz);

    /// <summary>The slowest quasi-TEM mode's delay per metre, √λ_max(L·C) [s/m], with the
    /// internal inductance at f included. L·C is a product of two symmetric positive-definite
    /// matrices, so its eigenvalues are real and positive (the squared modal slownesses) and
    /// power iteration finds the largest.</summary>
    internal static double SlownessSecondsPerMeter(RlgcResult rlgc, double frequencyHz)
    {
        int n = rlgc.ConductorCount;
        var c = rlgc.CapacitancePerMeter(frequencyHz);
        var internalL = rlgc.InternalInductanceHenriesPerMeter?.Invoke(frequencyHz);
        var lc = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int k = 0; k < n; k++)
                    lc[i, j] += (rlgc.InductanceHenriesPerMeter[i, k] + (internalL?[i, k] ?? 0))
                                * c[k, j];
        var v = Enumerable.Repeat(1.0, n).ToArray();
        double lambda = 0;
        for (int iteration = 0; iteration < 200; iteration++)
        {
            var w = new double[n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) w[i] += lc[i, j] * v[j];
            double norm = Math.Sqrt(w.Sum(x => x * x));
            if (!(norm > 0)) return 0;
            double next = norm / Math.Sqrt(v.Sum(x => x * x));
            for (int i = 0; i < n; i++) v[i] = w[i] / norm;
            if (Math.Abs(next - lambda) <= 1e-12 * next) { lambda = next; break; }
            lambda = next;
        }
        return Math.Sqrt(lambda);
    }
}

/// <summary>One uniform coupled-line section: RLGC matrices over a length.</summary>
public sealed record MtlSection(RlgcResult Rlgc, double LengthMeters) : MtlSectionBase
{
    public override int ConductorCount => Rlgc.ConductorCount;

    public override double LongestLengthMeters => LengthMeters;

    internal override ComplexDenseMatrix Chain(double frequencyHz) =>
        MtlNetwork.UniformSectionChain(this, frequencyHz);

    internal override double DelaySeconds(double frequencyHz) =>
        SlownessSecondsPerMeter(Rlgc, frequencyHz) * LengthMeters;
}

/// <summary>
/// An UNCOUPLED section: every conductor runs its own single-conductor line over its OWN
/// length, with no coupling between them. This is what a board's non-overlapping lead tails
/// are — the stretch where one trace has ended and the others run on alone.
///
/// <para>It exists because those leads have DIFFERENT lengths per line, which no uniform
/// section can express: a uniform section carries one length for all conductors. Modelling
/// them as an N-line section with wide gaps fails for the same reason (still one length), and
/// cascading genuinely separate per-line 1-conductor networks would break the equal-conductor
/// -count rule that lets chain matrices multiply. A block-diagonal section satisfies that rule
/// — it still has N conductors — while carrying a length per line.</para>
///
/// <para>The chain matrix is exactly N independent 2×2 line chains placed at
/// (i, i), (i, N+i), (N+i, i), (N+i, N+i), so a zero-length lead contributes an exact identity
/// block and a whole zero-length section is the exact identity — which is what keeps a board
/// with no leads on the same arithmetic it had before leads existed.</para>
/// </summary>
public sealed record MtlLeadSection : MtlSectionBase
{
    private readonly (RlgcResult Rlgc, double LengthMeters)[] _lines;

    /// <param name="perLine">One entry per conductor: its own single-conductor RLGC and its
    /// own length (0 = this conductor does not extend into the lead region).</param>
    public MtlLeadSection(IReadOnlyList<(RlgcResult Rlgc, double LengthMeters)> perLine)
    {
        if (perLine is null || perLine.Count == 0)
            throw new ArgumentException("A lead section needs at least one conductor.", nameof(perLine));
        for (int i = 0; i < perLine.Count; i++)
        {
            if (perLine[i].Rlgc.ConductorCount != 1)
                throw new ArgumentException(
                    $"Lead conductor {i} carries a {perLine[i].Rlgc.ConductorCount}-conductor "
                    + "cross-section; an uncoupled lead is single-conductor by definition "
                    + "(that is what makes the section block-diagonal).", nameof(perLine));
            if (perLine[i].LengthMeters < 0 || double.IsNaN(perLine[i].LengthMeters))
                throw new ArgumentException(
                    $"Lead length {perLine[i].LengthMeters} for conductor {i} is not a "
                    + "non-negative length.", nameof(perLine));
        }
        _lines = perLine.ToArray();
    }

    public override int ConductorCount => _lines.Length;

    public override double LongestLengthMeters => _lines.Max(l => l.LengthMeters);

    /// <summary>True when no conductor extends into this section — the chain is the identity
    /// and the section can be dropped entirely.</summary>
    public bool IsEmpty => _lines.All(l => l.LengthMeters <= 0);

    public IReadOnlyList<double> LengthsMeters => _lines.Select(l => l.LengthMeters).ToArray();

    internal override double DelaySeconds(double frequencyHz) =>
        _lines.Max(l => l.LengthMeters <= 0 ? 0 : SlownessSecondsPerMeter(l.Rlgc, frequencyHz) * l.LengthMeters);

    internal override ComplexDenseMatrix Chain(double frequencyHz)
    {
        int n = _lines.Length;
        var total = new ComplexDenseMatrix(2 * n, 2 * n);
        for (int i = 0; i < n; i++)
        {
            if (_lines[i].LengthMeters <= 0)
            {
                // Exact identity block — never expm of a zero generator, which would be the
                // same value reached through scaling-and-squaring rounding.
                total[i, i] = Complex.One;
                total[n + i, n + i] = Complex.One;
                continue;
            }
            var block = MtlNetwork.UniformSectionChain(
                new MtlSection(_lines[i].Rlgc, _lines[i].LengthMeters), frequencyHz);
            total[i, i] = block[0, 0];
            total[i, n + i] = block[0, 1];
            total[n + i, i] = block[1, 0];
            total[n + i, n + i] = block[1, 1];
        }
        return total;
    }
}

/// <summary>
/// A section where SOME of the conductors run coupled over one length and the rest pass
/// through it untouched: three nets of which two run side by side for a while. The coupled
/// group's chain sits in the rows and columns of its conductors; every other conductor gets an
/// exact identity block (it advances in a lead section of its own, before or after).
/// </summary>
public sealed record MtlGroupSection : MtlSectionBase
{
    private readonly int _total;

    /// <param name="group">The coupled section of the group's conductors.</param>
    /// <param name="conductors">Which of the cascade's conductors the group's are, in the
    /// group section's own conductor order.</param>
    /// <param name="total">How many conductors the cascade has.</param>
    public MtlGroupSection(MtlSection group, IReadOnlyList<int> conductors, int total)
    {
        if (conductors.Count != group.ConductorCount)
            throw new ArgumentException($"The group has {group.ConductorCount} conductors but {conductors.Count} indices.", nameof(conductors));
        if (conductors.Distinct().Count() != conductors.Count || conductors.Any(c => c < 0 || c >= total))
            throw new ArgumentException("The group's conductor indices must be distinct and within the cascade.", nameof(conductors));
        Group = group;
        Conductors = conductors.ToArray();
        _total = total;
    }

    public MtlSection Group { get; }
    public IReadOnlyList<int> Conductors { get; }

    public override int ConductorCount => _total;
    public override double LongestLengthMeters => Group.LengthMeters;

    internal override double DelaySeconds(double frequencyHz) => Group.DelaySeconds(frequencyHz);

    internal override ComplexDenseMatrix Chain(double frequencyHz)
    {
        int n = _total, k = Conductors.Count;
        var total = new ComplexDenseMatrix(2 * n, 2 * n);
        for (int i = 0; i < n; i++)
        {
            total[i, i] = Complex.One;
            total[n + i, n + i] = Complex.One;
        }
        var block = Group.Chain(frequencyHz);
        for (int a = 0; a < k; a++)
        {
            int i = Conductors[a];
            total[i, i] = Complex.Zero;
            total[n + i, n + i] = Complex.Zero;
        }
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
            {
                int i = Conductors[a], j = Conductors[b];
                total[i, j] = block[a, b];
                total[i, n + j] = block[a, k + b];
                total[n + i, j] = block[k + a, b];
                total[n + i, n + j] = block[k + a, k + b];
            }
        return total;
    }
}

/// <summary>Per-line linear terminations: a Thevenin driver resistance at the near end
/// and an R∥C receiver at the far end (R may be PositiveInfinity for an open).</summary>
public sealed record LineTermination(
    double SourceResistanceOhms, double LoadResistanceOhms, double LoadCapacitanceFarads = 0);

/// <summary>A terminated-network solution at one frequency: voltages/currents at both
/// ends of every line. Near currents flow INTO the network, far currents OUT toward
/// the loads (the ABCD chain convention).</summary>
public sealed record MtlSolution(
    Complex[] NearVoltages, Complex[] NearCurrents,
    Complex[] FarVoltages, Complex[] FarCurrents);

/// <summary>
/// The frequency-domain multiconductor transmission-line network (SI Stage S4): a
/// cascade of uniform coupled sections with linear terminations, solved exactly per
/// frequency. The section chain matrix is the matrix exponential of the telegrapher
/// generator — [V(0); I(0)] = expm([[0, Z′], [Y′, 0]]·ℓ)·[V(ℓ); I(ℓ)] — computed by
/// <see cref="ComplexMatrixExponential"/> rather than modal eigendecomposition: expm
/// has no defective-eigenstructure failure mode, and the matrices are tiny. Cascades
/// multiply chain matrices; ports are 0..N−1 = near ends, N..2N−1 = far ends.
/// S-parameters come from the terminated boundary solve (never from inverting the
/// chain's C block, which is singular for degenerate lengths).
/// </summary>
public sealed class MtlNetwork
{
    private readonly IReadOnlyList<MtlSectionBase> _sections;

    public MtlNetwork(IReadOnlyList<MtlSectionBase> sections)
    {
        if (sections is null || sections.Count == 0)
            throw new ArgumentException("At least one section is required.", nameof(sections));
        int n = sections[0].ConductorCount;
        foreach (var section in sections)
        {
            if (section.ConductorCount != n)
                throw new ArgumentException(
                    "Every cascaded section must carry the same number of conductors "
                    + "(uncoupled leads of differing per-line length are an "
                    + nameof(MtlLeadSection) + ", which is block-diagonal and still N-conductor).",
                    nameof(sections));
            if (section is MtlSection uniform && uniform.LengthMeters <= 0)
                throw new ArgumentException("Section lengths must be positive.", nameof(sections));
        }
        _sections = sections.ToArray();
        ConductorCount = n;
    }

    public int ConductorCount { get; }

    /// <summary>End-to-end length of the cascade [m], along the longest line of each
    /// section.</summary>
    public double TotalLengthMeters => _sections.Sum(s => s.LongestLengthMeters);

    /// <summary>An upper bound on the one-way delay at f [s]: each section's slowest mode,
    /// summed along the cascade. What the nonlinear engine sizes its FIR window from.</summary>
    public double LongestOneWayDelaySeconds(double frequencyHz) =>
        _sections.Sum(s => s.DelaySeconds(frequencyHz));

    /// <summary>The 2N×2N chain (ABCD) matrix of the whole cascade at one frequency:
    /// [V_near; I_near] = T·[V_far; I_far].</summary>
    public ComplexDenseMatrix ChainMatrix(double frequencyHz)
    {
        ComplexDenseMatrix? total = null;
        foreach (var section in _sections)
        {
            var t = section.Chain(frequencyHz);
            total = total is null ? t : ComplexMatrixExponential.Multiply(total, t);
        }
        return total!;
    }

    /// <summary>The chain matrix of ONE uniform coupled section — shared with
    /// <see cref="MtlLeadSection"/>, which calls it once per conductor with a 1-conductor
    /// cross-section so a lead block is the same arithmetic a single line would get.</summary>
    internal static ComplexDenseMatrix UniformSectionChain(MtlSection section, double frequencyHz)
    {
        int n = section.ConductorCount;
        double omega = 2 * Math.PI * frequencyHz;
        var rlgc = section.Rlgc;
        var g = rlgc.ConductancePerMeter(frequencyHz);
        var c = rlgc.CapacitancePerMeter(frequencyHz);   // the same matrix when ε is constant

        // The proximity-effect providers (Stage S8) carry the full N×N R(f) and the internal
        // ΔL(f); when absent the generator is bitwise the v1 scalar-diagonal-R + external-L path.
        double[,]? rMatrix = rlgc.ResistanceMatrixOhmsPerMeter?.Invoke(frequencyHz);
        double[,]? internalL = rlgc.InternalInductanceHenriesPerMeter?.Invoke(frequencyHz);

        // Generator ℓ·[[0, Z′], [Y′, 0]]: dV/dx = −Z′I, dI/dx = −Y′V integrated
        // BACKWARD from the far end (the chain convention).
        var generator = new ComplexDenseMatrix(2 * n, 2 * n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double lFull = rlgc.InductanceHenriesPerMeter[i, j] + (internalL?[i, j] ?? 0);
                Complex z = new Complex(0, omega * lFull);
                if (rMatrix is not null) z += rMatrix[i, j];
                else if (i == j) z += rlgc.ResistancePerMeter(i, frequencyHz);
                Complex y = new Complex(g[i, j], omega * c[i, j]);
                generator[i, n + j] = z * section.LengthMeters;
                generator[n + i, j] = y * section.LengthMeters;
            }
        return ComplexMatrixExponential.Exponential(generator);
    }

    /// <summary>
    /// Solves the terminated network at one frequency: Thevenin sources
    /// <paramref name="sourceVolts"/> behind each line's source resistance at the near
    /// end, R∥C receivers at the far end. The 2N unknowns are the far-end [V2; I2];
    /// near-end quantities come back through the chain matrix.
    /// </summary>
    public MtlSolution SolveTerminated(double frequencyHz,
        IReadOnlyList<LineTermination> terminations, IReadOnlyList<Complex> sourceVolts)
    {
        int n = ConductorCount;
        if (terminations.Count != n || sourceVolts.Count != n)
            throw new ArgumentException("One termination and one source value per line.");
        double omega = 2 * Math.PI * frequencyHz;
        var t = ChainMatrix(frequencyHz);

        var system = new ComplexDenseMatrix(2 * n, 2 * n);
        var rhs = new Complex[2 * n];
        for (int i = 0; i < n; i++)
        {
            // Near end: V1_i + Rs_i·I1_i = E_i with [V1; I1] = T·x.
            double rs = terminations[i].SourceResistanceOhms;
            for (int j = 0; j < 2 * n; j++)
                system[i, j] = t[i, j] + rs * t[n + i, j];
            rhs[i] = sourceVolts[i];

            // Far end. The ADMITTANCE row I2_i = Y_L·V2_i expresses an open load naturally
            // (Y = 0) but cannot express a short (Y → ∞). Below a threshold the row is written
            // in IMPEDANCE form instead, V2_i − Z_L·I2_i = 0, where a dead short is simply
            // Z_L = 0. Both rows are algebraically exact — the threshold picks the
            // well-CONDITIONED one, it is not a tolerance, and nothing about the answer depends
            // on where it sits. It is placed low enough (1 Ω) that every previously-accepted
            // termination keeps the byte-for-byte admittance row it has always had.
            double rl = terminations[i].LoadResistanceOhms;
            if (rl < 0)
                throw new ArgumentException(
                    "Receiver resistance cannot be negative (use 0 for a short, "
                    + "PositiveInfinity for an open).", nameof(terminations));
            double cl = terminations[i].LoadCapacitanceFarads;
            if (rl >= 1.0 || double.IsPositiveInfinity(rl))
            {
                Complex yLoad = (double.IsPositiveInfinity(rl) ? Complex.Zero : 1.0 / rl)
                                + new Complex(0, omega * cl);
                system[n + i, n + i] = Complex.One;      // I2_i
                system[n + i, i] = -yLoad;               // −Y_L·V2_i
            }
            else
            {
                // Z_L = R ∥ (1/jωC) computed directly, never as 1/Y: at R = 0 the parallel
                // combination IS zero, while the reciprocal route would divide by infinity.
                Complex zLoad = rl == 0
                    ? Complex.Zero
                    : rl / (Complex.One + new Complex(0, omega * cl * rl));
                system[n + i, i] = Complex.One;          // V2_i
                system[n + i, n + i] = -zLoad;           // −Z_L·I2_i
            }
        }

        var x = ComplexLu.Factor(system).Solve(rhs);
        var far = x;
        var near = t.Multiply(x);
        return new MtlSolution(
            near[..ConductorCount], near[ConductorCount..],
            far[..ConductorCount], far[ConductorCount..]);
    }

    /// <summary>
    /// <summary>
    /// The 2N×2N REFERENCE-TERMINATED transfer impedance: with every port loaded by the same
    /// real conductance <paramref name="referenceSiemens"/>, entry (i, k) is the voltage at
    /// port i per unit current injected at port k. Ports 0..N−1 are near ends, N..2N−1 far.
    ///
    /// <para>This is the reduction the time-domain nonlinear engine needs, and it is
    /// deliberately NOT the bare network's Y-parameters. Those require inverting a chain-matrix
    /// block, which is singular at DC for a lossless line and near-singular at degenerate
    /// lengths — the same reason <see cref="Scattering"/> never inverts the chain's C block.
    /// The reference conductance regularizes exactly those degeneracies: it makes the boundary
    /// system well-conditioned at every bin including DC, and because the node equation adds
    /// its current back (I_ext + g_ref·V), it cancels identically and no physics depends on
    /// its value. It is a CONDITIONING parameter, and it is a named argument rather than a
    /// hidden constant.</para>
    ///
    /// <para>One LU serves all 2N injections, exactly as the scattering path does.</para>
    /// </summary>
    public Complex[,] TransferImpedance(double frequencyHz, double referenceSiemens)
    {
        if (!(referenceSiemens > 0) || double.IsInfinity(referenceSiemens))
            throw new ArgumentOutOfRangeException(nameof(referenceSiemens),
                "The reference conductance must be positive and finite — it exists to keep the "
                + "reduction well-conditioned at DC and at degenerate lengths.");
        int n = ConductorCount;
        int size = 2 * n;
        double g = referenceSiemens;
        var t = ChainMatrix(frequencyHz);

        // Unknowns x = [V2; I2]. Near end: the injected current splits into the reference load
        // and the line, J_i = g·V1_i + I1_i, with [V1; I1] = T·x.
        //
        // Far end: in this chain convention I2 flows OUT of the network toward the load, so the
        // current INTO the network at a far port is −I2 (the same reading Scattering() uses).
        // KCL at the far node is then J = g·V2 + (−I2), i.e. g·V2_i − I2_i = J_{n+i}. Writing
        // +I2 there produces an ANTIsymmetric matrix — which is what the reciprocity gate
        // caught, since a passive network's impedance matrix must be symmetric.
        var system = new ComplexDenseMatrix(size, size);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < size; j++)
                system[i, j] = g * t[i, j] + t[n + i, j];
            system[n + i, i] = g;                  // g·V2_i
            system[n + i, n + i] = -Complex.One;   // − I2_i
        }
        var lu = ComplexLu.Factor(system);

        var z = new Complex[size, size];
        var rhs = new Complex[size];
        for (int k = 0; k < size; k++)
        {
            Array.Clear(rhs);
            rhs[k] = Complex.One;                 // unit current into port k
            var x = lu.Solve(rhs);
            var near = t.Multiply(x);
            for (int i = 0; i < n; i++)
            {
                z[i, k] = near[i];                // V1_i
                z[n + i, k] = x[i];               // V2_i
            }
        }
        return z;
    }

    /// The 2N-port scattering matrix (reference <paramref name="referenceOhms"/>, all
    /// ports resistively terminated). Ports 0..N−1 are near ends, N..2N−1 far ends.
    /// One LU factorization serves all 2N excitations.
    /// </summary>
    public Complex[,] Scattering(double frequencyHz, double referenceOhms = 50)
    {
        int n = ConductorCount;
        int size = 2 * n;
        double z0 = referenceOhms;
        double sqrtZ0 = Math.Sqrt(z0);
        var t = ChainMatrix(frequencyHz);

        // Unknowns x = [V2; I2]. Near rows: V1 + z0·I1 = E_near; far rows:
        // V2 − z0·I2 = E_far (far port current INTO the network is −I2).
        var system = new ComplexDenseMatrix(size, size);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < size; j++)
                system[i, j] = t[i, j] + z0 * t[n + i, j];
            system[n + i, i] = Complex.One;
            system[n + i, n + i] = -z0;
        }
        var lu = ComplexLu.Factor(system);

        var s = new Complex[size, size];
        var rhs = new Complex[size];
        for (int k = 0; k < size; k++)
        {
            Array.Clear(rhs);
            rhs[k] = 2 * sqrtZ0;                     // makes the incident wave a_k = 1
            var x = lu.Solve(rhs);
            var near = t.Multiply(x);
            for (int j = 0; j < n; j++)
            {
                // b = (V − z0·I_in)/(2√z0); near I_in = I1, far I_in = −I2.
                s[j, k] = (near[j] - z0 * near[n + j]) / (2 * sqrtZ0);
                s[n + j, k] = (x[j] + z0 * x[n + j]) / (2 * sqrtZ0);
            }
        }
        return s;
    }
}
