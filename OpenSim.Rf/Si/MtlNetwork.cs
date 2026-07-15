using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Si;

/// <summary>One uniform coupled-line section: RLGC matrices over a length.</summary>
public sealed record MtlSection(RlgcResult Rlgc, double LengthMeters)
{
    public int ConductorCount => Rlgc.ConductorCount;
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
    private readonly IReadOnlyList<MtlSection> _sections;

    public MtlNetwork(IReadOnlyList<MtlSection> sections)
    {
        if (sections is null || sections.Count == 0)
            throw new ArgumentException("At least one section is required.", nameof(sections));
        int n = sections[0].ConductorCount;
        foreach (var section in sections)
        {
            if (section.ConductorCount != n)
                throw new ArgumentException(
                    "Every cascaded section must carry the same number of conductors "
                    + "(uncoupled leads are 1-line sections per line — cascade per line, "
                    + "or model leads as an N-line section with wide gaps).",
                    nameof(sections));
            if (section.LengthMeters <= 0)
                throw new ArgumentException("Section lengths must be positive.", nameof(sections));
        }
        _sections = sections.ToArray();
        ConductorCount = n;
    }

    public int ConductorCount { get; }

    /// <summary>The 2N×2N chain (ABCD) matrix of the whole cascade at one frequency:
    /// [V_near; I_near] = T·[V_far; I_far].</summary>
    public ComplexDenseMatrix ChainMatrix(double frequencyHz)
    {
        ComplexDenseMatrix? total = null;
        foreach (var section in _sections)
        {
            var t = SectionChain(section, frequencyHz);
            total = total is null ? t : ComplexMatrixExponential.Multiply(total, t);
        }
        return total!;
    }

    private static ComplexDenseMatrix SectionChain(MtlSection section, double frequencyHz)
    {
        int n = section.ConductorCount;
        double omega = 2 * Math.PI * frequencyHz;
        var rlgc = section.Rlgc;
        var g = rlgc.ConductancePerMeter(frequencyHz);

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
                Complex y = new Complex(g[i, j], omega * rlgc.CapacitanceFaradsPerMeter[i, j]);
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

            // Far end: I2_i = Y_L·V2_i (admittance form — an open load is just Y = 0;
            // a shorted receiver would need the impedance form and is rejected loudly).
            double rl = terminations[i].LoadResistanceOhms;
            if (rl <= 0 && !double.IsPositiveInfinity(rl))
                throw new ArgumentException(
                    "Receiver resistance must be positive (or PositiveInfinity for open).",
                    nameof(terminations));
            Complex yLoad = (double.IsPositiveInfinity(rl) ? Complex.Zero : 1.0 / rl)
                            + new Complex(0, omega * terminations[i].LoadCapacitanceFarads);
            system[n + i, n + i] = Complex.One;      // I2_i
            system[n + i, i] = -yLoad;               // −Y_L·V2_i
        }

        var x = ComplexLu.Factor(system).Solve(rhs);
        var far = x;
        var near = t.Multiply(x);
        return new MtlSolution(
            near[..ConductorCount], near[ConductorCount..],
            far[..ConductorCount], far[ConductorCount..]);
    }

    /// <summary>
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
