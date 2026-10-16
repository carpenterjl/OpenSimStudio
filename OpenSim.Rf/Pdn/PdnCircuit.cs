using System.Numerics;

namespace OpenSim.Rf.Pdn;

public enum BoardSide { Top, Bottom }

/// <summary>A node of the mounting network on one side of the board.</summary>
public readonly record struct MountNode(BoardSide Side, int Node);

/// <summary>A via's foot on a plate other than the side's outer one: the network node sits on
/// stack port <paramref name="Port"/> (the via's current goes on through the stack there).</summary>
public sealed record PortFoot(MountNode Node, int Port);

/// <summary>A capacitor between two pads.</summary>
public sealed record MountedCapacitor(string Name, MountNode A, MountNode B, CapacitorModel Model);

/// <summary>The load: its power pads tied at one node, its ground pads at another, behind the
/// package's resistance and inductance (one lumped series branch).</summary>
public sealed record MountedLoad(string Name, MountNode Plus, MountNode Minus)
{
    public double PackageOhms { get; init; }
    public double PackageHenries { get; init; }

    /// <summary>The stack ports its vias reach, for the report (which plane resonances it sees).</summary>
    public IReadOnlyList<int> Ports { get; init; } = Array.Empty<int>();
}

/// <summary>
/// The regulator's control loop as the gain A(s) from the error at its sense point to its
/// internal source: A(s) = K/sᵐ·Π(1 + s/ω_z)/Π(1 + s/ω_p), with m = 1 and K = 2π·<see cref="GainHz"/>
/// for an integrating compensator (|A| = 1 at GainHz with no zeros or poles), or m = 0 and
/// K = <see cref="DcGain"/>. For a voltage-mode converter A is the compensator times V_in/V_ramp;
/// the output filter is the inductance of the regulator model and the board's capacitors.
/// </summary>
public sealed record RegulatorLoop
{
    public bool Integrator { get; init; } = true;
    public double GainHz { get; init; }
    public double DcGain { get; init; }
    public IReadOnlyList<double> ZerosHz { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> PolesHz { get; init; } = Array.Empty<double>();

    public Complex Gain(double frequencyHz)
    {
        var s = new Complex(0, 2 * Math.PI * frequencyHz);
        Complex a = Integrator ? 2 * Math.PI * GainHz / s : DcGain;
        foreach (double z in ZerosHz) a *= 1 + s / (2 * Math.PI * z);
        foreach (double p in PolesHz) a /= 1 + s / (2 * Math.PI * p);
        return a;
    }

    public void Validate()
    {
        if (Integrator ? !(GainHz > 0) : !(DcGain > 0))
            throw new ArgumentException(Integrator
                ? "An integrating regulator loop needs its gain frequency above zero."
                : "A regulator loop needs a DC gain above zero.");
        if (ZerosHz.Concat(PolesHz).Any(f => !(f > 0)))
            throw new ArgumentException("The regulator loop's zeros and poles must lie above zero frequency.");
    }
}

/// <summary>A regulator on its output pads: a source behind R + L. With a loop the source is
/// −A(s)·(V_sense+ − V_sense−); the sense nodes default to its own pads.</summary>
public sealed record MountedRegulator(string Name, MountNode Plus, MountNode Minus, double ResistanceOhms, double InductanceHenries)
{
    public RegulatorLoop? Loop { get; init; }
    public MountNode? SensePlus { get; init; }
    public MountNode? SenseMinus { get; init; }
}

/// <summary>The parts on their pads and vias above the stack (FU-32).</summary>
public sealed record PdnMounting
{
    public MountingNetwork? Top { get; init; }
    public MountingNetwork? Bottom { get; init; }
    public IReadOnlyList<PortFoot> Feet { get; init; } = Array.Empty<PortFoot>();
    public IReadOnlyList<MountedCapacitor> Capacitors { get; init; } = Array.Empty<MountedCapacitor>();
    public required MountedLoad Load { get; init; }
    public IReadOnlyList<MountedRegulator> Regulators { get; init; } = Array.Empty<MountedRegulator>();
}

/// <summary>
/// The circuit the plane stack sits in, solved by modified nodal analysis at each frequency:
/// node voltages, the stack's port currents and each controlled regulator's branch current.
/// <code>
///   node rows:      Σ Y·V + Σ I_port + Σ i_reg = J            (J: 1 A into the load's plus node)
///   port rows:      V(foot) − Σ Z_stack·I_port = 0
///   regulator rows: V+ − V− + A·(V_s+ − V_s−) − Z_ol·i_reg = 0
/// </code>
/// Each side's nodes are referred to that side's outer plate; the two sides meet only through
/// the stack's ports.
/// </summary>
internal sealed class PdnCircuit
{
    private readonly List<string> _names = new();
    private readonly List<(int[] Terminals, double[,] Gamma)> _networks = new();
    private readonly List<(int A, int B, Func<double, Complex> Impedance, bool Part)> _branches = new();
    private readonly List<(int Node, int Port)> _feet = new();
    private readonly List<(int Plus, int Minus, Func<double, Complex> Open, RegulatorLoop Loop, int SensePlus, int SenseMinus)> _controlled = new();

    public int LoadPlus { get; set; } = -1;
    public int LoadMinus { get; set; } = -1;

    public int NodeCount => _names.Count;

    public int AddNode(string name)
    {
        _names.Add(name);
        return _names.Count - 1;
    }

    /// <summary>A reduced inductive network: currents Γ·V/(jω) into its terminals.</summary>
    public void AddNetwork(int[] terminals, double[,] gamma) => _networks.Add((terminals, gamma));

    /// <summary>A two-terminal impedance (−1 is the reference). Parts are left out of the bare
    /// planes' impedance.</summary>
    public void AddImpedance(int a, int b, Func<double, Complex> impedance, bool part) => _branches.Add((a, b, impedance, part));

    public void AddFoot(int node, int port) => _feet.Add((node, port));

    public void AddControlled(int plus, int minus, Func<double, Complex> open, RegulatorLoop loop, int sensePlus, int senseMinus) =>
        _controlled.Add((plus, minus, open, loop, sensePlus, senseMinus));

    public bool HasLoop => _controlled.Count > 0;

    /// <summary>
    /// The impedance the load sees at one frequency, with the parts (or without them: the
    /// stack and the mountings alone), and with a loop its return ratio T = A·V_sense/E with the
    /// source driven alone.
    /// </summary>
    public (Complex Impedance, Complex? LoopGain) Solve(double frequencyHz, Complex[,] stack, bool withParts)
    {
        int nodes = _names.Count, ports = stack.GetLength(0);
        var usedPorts = _feet.Select(f => f.Port).Distinct().OrderBy(p => p).ToArray();
        var portIndex = new Dictionary<int, int>();
        for (int k = 0; k < usedPorts.Length; k++) portIndex[usedPorts[k]] = k;
        int controlled = withParts ? _controlled.Count : 0;
        int size = nodes + usedPorts.Length + controlled;
        double omega = 2 * Math.PI * frequencyHz;

        var matrix = new DenseComplexSystem(size);
        foreach (var (terminals, gamma) in _networks)
        {
            Complex scale = 1 / new Complex(0, omega);
            for (int i = 0; i < terminals.Length; i++)
                for (int j = 0; j < terminals.Length; j++)
                    if (gamma[i, j] != 0) matrix.Add(terminals[i], terminals[j], scale * gamma[i, j]);
        }
        foreach (var (a, b, impedance, part) in _branches)
        {
            if (part && !withParts) continue;
            Complex y = 1 / impedance(frequencyHz);
            if (a >= 0) matrix.Add(a, a, y);
            if (b >= 0) matrix.Add(b, b, y);
            if (a >= 0 && b >= 0) { matrix.Add(a, b, -y); matrix.Add(b, a, -y); }
        }
        foreach (var (node, port) in _feet)
        {
            int row = nodes + portIndex[port];
            matrix.Add(node, row, 1);                          // the port current leaves the node
            matrix.Add(row, node, 1);
        }
        for (int p = 0; p < usedPorts.Length; p++)
            for (int q = 0; q < usedPorts.Length; q++)
                matrix.Add(nodes + p, nodes + q, -stack[usedPorts[p], usedPorts[q]]);
        for (int r = 0; r < controlled; r++)
        {
            var (plus, minus, open, loop, sp, sm) = _controlled[r];
            int row = nodes + usedPorts.Length + r;
            Complex a = loop.Gain(frequencyHz);
            if (plus >= 0) { matrix.Add(plus, row, 1); matrix.Add(row, plus, 1); }
            if (minus >= 0) { matrix.Add(minus, row, -1); matrix.Add(row, minus, -1); }
            if (sp >= 0) matrix.Add(row, sp, a);
            if (sm >= 0) matrix.Add(row, sm, -a);
            matrix.Add(row, row, -open(frequencyHz));
        }

        var rhs = new Complex[size];
        if (LoadPlus >= 0) rhs[LoadPlus] += 1;
        if (LoadMinus >= 0) rhs[LoadMinus] -= 1;
        var solved = matrix.Copy().Solve(rhs);
        Complex z = (LoadPlus >= 0 ? solved[LoadPlus] : 0) - (LoadMinus >= 0 ? solved[LoadMinus] : 0);

        Complex? loopGain = null;
        if (controlled > 0)
        {
            // The first regulator's loop broken at its source: E driven at 1 V with no A terms.
            var (plus, minus, open, loop, sp, sm) = _controlled[0];
            int row = nodes + usedPorts.Length;
            Complex a = loop.Gain(frequencyHz);
            if (sp >= 0) matrix.Add(row, sp, -a);
            if (sm >= 0) matrix.Add(row, sm, a);
            var drive = new Complex[size];
            drive[row] = 1;                                    // V+ − V− − Z·i = E, with E = 1 V
            var open1 = matrix.Solve(drive);
            Complex sense = (sp >= 0 ? open1[sp] : 0) - (sm >= 0 ? open1[sm] : 0);
            loopGain = a * sense;
        }
        return (z, loopGain);
    }
}

/// <summary>A dense complex system solved by LU with partial pivoting, real and imaginary parts
/// kept apart (complex structs in the inner loop are slow).</summary>
internal sealed class DenseComplexSystem
{
    private readonly int _n;
    private readonly double[] _re, _im;

    public DenseComplexSystem(int n)
    {
        _n = n;
        _re = new double[n * n];
        _im = new double[n * n];
    }

    private DenseComplexSystem(int n, double[] re, double[] im)
    {
        _n = n;
        _re = re;
        _im = im;
    }

    public DenseComplexSystem Copy() => new(_n, (double[])_re.Clone(), (double[])_im.Clone());

    public void Add(int row, int column, Complex value)
    {
        _re[row * _n + column] += value.Real;
        _im[row * _n + column] += value.Imaginary;
    }

    /// <summary>Solves in place (the matrix is overwritten by its factors).</summary>
    public Complex[] Solve(Complex[] b)
    {
        int n = _n;
        var xr = b.Select(v => v.Real).ToArray();
        var xi = b.Select(v => v.Imaginary).ToArray();
        for (int k = 0; k < n; k++)
        {
            int pivot = k;
            double best = -1;
            for (int i = k; i < n; i++)
            {
                double m = _re[i * n + k] * _re[i * n + k] + _im[i * n + k] * _im[i * n + k];
                if (m > best) { best = m; pivot = i; }
            }
            if (!(best > 0)) throw new InvalidOperationException("The PDN circuit is singular: a node has no path to the planes.");
            if (pivot != k)
            {
                for (int j = 0; j < n; j++)
                {
                    (_re[k * n + j], _re[pivot * n + j]) = (_re[pivot * n + j], _re[k * n + j]);
                    (_im[k * n + j], _im[pivot * n + j]) = (_im[pivot * n + j], _im[k * n + j]);
                }
                (xr[k], xr[pivot]) = (xr[pivot], xr[k]);
                (xi[k], xi[pivot]) = (xi[pivot], xi[k]);
            }
            double pr = _re[k * n + k], pi = _im[k * n + k], scale = 1 / (pr * pr + pi * pi);
            for (int i = k + 1; i < n; i++)
            {
                double ar = _re[i * n + k], ai = _im[i * n + k];
                if (ar == 0 && ai == 0) continue;
                double fr = (ar * pr + ai * pi) * scale, fi = (ai * pr - ar * pi) * scale;
                for (int j = k + 1; j < n; j++)
                {
                    double sr = _re[k * n + j], si = _im[k * n + j];
                    _re[i * n + j] -= fr * sr - fi * si;
                    _im[i * n + j] -= fr * si + fi * sr;
                }
                xr[i] -= fr * xr[k] - fi * xi[k];
                xi[i] -= fr * xi[k] + fi * xr[k];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double sr = xr[i], si = xi[i];
            for (int j = i + 1; j < n; j++)
            {
                double ar = _re[i * n + j], ai = _im[i * n + j];
                sr -= ar * xr[j] - ai * xi[j];
                si -= ar * xi[j] + ai * xr[j];
            }
            double dr = _re[i * n + i], di = _im[i * n + i], scale = 1 / (dr * dr + di * di);
            xr[i] = (sr * dr + si * di) * scale;
            xi[i] = (si * dr - sr * di) * scale;
        }
        return Enumerable.Range(0, n).Select(i => new Complex(xr[i], xi[i])).ToArray();
    }
}
