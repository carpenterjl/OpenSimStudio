using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Emi;

/// <summary>
/// A small linear AC circuit solved by modified nodal analysis at one frequency at a time:
/// resistors, inductors, capacitors (each with optional series parasitics through
/// <see cref="Branch"/>), independent current sources and voltage sources. Node "0" is ground.
/// It exists for the LISN and filter networks of the conducted-emission estimate — a few dozen
/// nodes — and is not a SPICE replacement (no nonlinear parts, no transient).
/// </summary>
public sealed class AcCircuit
{
    private readonly Dictionary<string, int> _nodes = new() { ["0"] = -1 };
    private readonly List<Element> _elements = new();

    private abstract record Element(string Name, string A, string B);
    private sealed record Impedance(string Name, string A, string B, Func<double, Complex> Z) : Element(Name, A, B);
    private sealed record CurrentSource(string Name, string A, string B, Func<double, Complex> I) : Element(Name, A, B);
    private sealed record VoltageSource(string Name, string A, string B, Func<double, Complex> V) : Element(Name, A, B);

    private int Node(string name)
    {
        if (!_nodes.TryGetValue(name, out int index))
        {
            index = _nodes.Count - 1;
            _nodes[name] = index;
        }
        return index;
    }

    /// <summary>A two-terminal impedance Z(f) between nodes a and b.</summary>
    public AcCircuit Branch(string name, string a, string b, Func<double, Complex> z)
    {
        Node(a); Node(b);
        _elements.Add(new Impedance(name, a, b, z));
        return this;
    }

    public AcCircuit Resistor(string name, string a, string b, double ohms) => Branch(name, a, b, _ => ohms);

    public AcCircuit Inductor(string name, string a, string b, double henries, double seriesOhms = 0) =>
        Branch(name, a, b, f => new Complex(seriesOhms, 2 * Math.PI * f * henries));

    /// <summary>A capacitor with optional ESR and ESL in series.</summary>
    public AcCircuit Capacitor(string name, string a, string b, double farads, double esr = 0, double esl = 0) =>
        Branch(name, a, b, f => new Complex(esr, 2 * Math.PI * f * esl - 1 / (2 * Math.PI * f * farads)));

    /// <summary>A current source pushing I(f) from node a through the source into node b.</summary>
    public AcCircuit Current(string name, string a, string b, Func<double, Complex> current)
    {
        Node(a); Node(b);
        _elements.Add(new CurrentSource(name, a, b, current));
        return this;
    }

    /// <summary>A voltage source holding V(a) − V(b) = V(f).</summary>
    public AcCircuit Voltage(string name, string a, string b, Func<double, Complex> voltage)
    {
        Node(a); Node(b);
        _elements.Add(new VoltageSource(name, a, b, voltage));
        return this;
    }

    public IReadOnlyCollection<string> Nodes => _nodes.Keys;

    /// <summary>Node voltages at <paramref name="frequencyHz"/>. When
    /// <paramref name="sourceValues"/> is given, each source takes the value listed for it and a
    /// source not listed is switched off (a current source open, a voltage source shorted);
    /// otherwise every source takes its own function's value.</summary>
    public AcSolution Solve(double frequencyHz, IReadOnlyDictionary<string, Complex>? sourceValues = null)
    {
        Complex Value(string name, Func<double, Complex> own) =>
            sourceValues is null ? own(frequencyHz) : sourceValues.TryGetValue(name, out var v) ? v : Complex.Zero;
        int n = _nodes.Count - 1;
        var sources = _elements.OfType<VoltageSource>().ToList();
        int size = n + sources.Count;
        var m = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        foreach (var e in _elements)
        {
            int a = _nodes[e.A], b = _nodes[e.B];
            switch (e)
            {
                case Impedance z:
                    var value = z.Z(frequencyHz);
                    if (value == Complex.Zero) throw new InvalidOperationException($"'{z.Name}' is a zero impedance; use a node merge or a small resistance.");
                    var y = 1 / value;
                    if (a >= 0) m[a, a] += y;
                    if (b >= 0) m[b, b] += y;
                    if (a >= 0 && b >= 0) { m[a, b] -= y; m[b, a] -= y; }
                    break;
                case CurrentSource s:
                    var i = Value(s.Name, s.I);
                    if (a >= 0) rhs[a] -= i;
                    if (b >= 0) rhs[b] += i;
                    break;
            }
        }
        for (int k = 0; k < sources.Count; k++)
        {
            var s = sources[k];
            int a = _nodes[s.A], b = _nodes[s.B], row = n + k;
            if (a >= 0) { m[a, row] += 1; m[row, a] += 1; }
            if (b >= 0) { m[b, row] -= 1; m[row, b] -= 1; }
            rhs[row] = Value(s.Name, s.V);
        }
        var x = ComplexLu.Factor(m).Solve(rhs);
        var voltages = new Dictionary<string, Complex>(StringComparer.Ordinal);
        foreach (var (name, index) in _nodes) voltages[name] = index < 0 ? Complex.Zero : x[index];
        return new AcSolution(frequencyHz, voltages);
    }
}

public sealed record AcSolution(double FrequencyHz, IReadOnlyDictionary<string, Complex> Voltages)
{
    public Complex this[string node] => Voltages[node];
    public Complex Between(string a, string b) => Voltages[a] - Voltages[b];
}

/// <summary>
/// The Fourier lines of a periodic piecewise-linear waveform, exact: over each straight segment
/// the integral of v(t)·e^{−jnω₀t} has a closed form, so a trapezoid's edges are resolved however
/// short they are against the period.
/// </summary>
public sealed class PiecewiseLinearWaveform
{
    private readonly (double T, double V)[] _points;

    /// <summary>Points (t, v) over one period [0, <paramref name="period"/>]; the waveform runs
    /// straight between them and wraps from the last point to the first one period later.</summary>
    public PiecewiseLinearWaveform(double period, IReadOnlyList<(double T, double V)> points)
    {
        if (!(period > 0)) throw new ArgumentOutOfRangeException(nameof(period));
        if (points.Count < 2) throw new ArgumentException("At least two points.", nameof(points));
        for (int i = 1; i < points.Count; i++)
            if (!(points[i].T > points[i - 1].T)) throw new ArgumentException("Times must increase.", nameof(points));
        if (points[0].T < 0 || points[^1].T >= period) throw new ArgumentException("Points must lie in [0, period).", nameof(points));
        Period = period;
        _points = points.ToArray();
    }

    public double Period { get; }
    public double Fundamental => 1 / Period;

    /// <summary>A trapezoidal pulse train: 0 → <paramref name="amplitude"/> in
    /// <paramref name="rise"/>, held so the 50 % width is <paramref name="duty"/>·T, back to 0 in
    /// <paramref name="fall"/>.</summary>
    public static PiecewiseLinearWaveform Trapezoid(double amplitude, double frequencyHz, double duty, double rise, double fall)
    {
        double period = 1 / frequencyHz;
        double width = duty * period;
        if (!(rise > 0 && fall > 0)) throw new ArgumentException("Rise and fall times must be positive.");
        if (width - rise / 2 - fall / 2 < 0 || period - width - rise / 2 - fall / 2 < 0)
            throw new ArgumentException("The edges do not fit in the pulse and the gap.");
        return new PiecewiseLinearWaveform(period, new[]
        {
            (0.0, 0.0), (rise, amplitude), (rise / 2 + width - fall / 2, amplitude), (rise / 2 + width + fall / 2, 0.0)
        });
    }

    /// <summary>The mean value (n = 0).</summary>
    public double Mean => Coefficient(0).Real;

    /// <summary>Two-sided coefficient c_n = (1/T)∫v(t)e^{−jnω₀t}dt; the one-sided amplitude of
    /// harmonic n ≥ 1 is 2|c_n|.</summary>
    public Complex Coefficient(int n)
    {
        double w = 2 * Math.PI * n / Period;
        Complex sum = Complex.Zero;
        for (int i = 0; i < _points.Length; i++)
        {
            var (t0, v0) = _points[i];
            var (t1, v1) = i + 1 < _points.Length ? _points[i + 1] : (_points[0].T + Period, _points[0].V);
            double dt = t1 - t0;
            if (n == 0) { sum += 0.5 * (v0 + v1) * dt; continue; }
            double slope = (v1 - v0) / dt;
            // ∫(v0 + s(t − t0))e^{−jwt}dt = [e^{−jwt}((v0 + s(t−t0))/(−jw) + s/w²)] from t0 to t1.
            Complex E(double t) => Complex.Exp(new Complex(0, -w * t));
            Complex jw = new Complex(0, w);
            Complex F(double t, double v) => E(t) * (-v / jw + slope / (w * w));
            sum += F(t1, v1) - F(t0, v0);
        }
        return sum / Period;
    }

    /// <summary>One-sided harmonic amplitudes (peak) up to <paramref name="maxFrequencyHz"/>.</summary>
    public IReadOnlyList<(double FrequencyHz, Complex Amplitude)> Harmonics(double maxFrequencyHz, double minFrequencyHz = 0)
    {
        var list = new List<(double, Complex)>();
        int first = Math.Max(1, (int)Math.Ceiling(minFrequencyHz * Period - 1e-9));
        for (int n = first; n * Fundamental <= maxFrequencyHz * (1 + 1e-12); n++)
            list.Add((n * Fundamental, 2 * Coefficient(n)));
        return list;
    }
}
