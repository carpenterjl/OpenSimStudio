using System.Numerics;
using OpenSim.Rf.Network;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Channel;

/// <summary>Which ports of a 2N-port file are the near ends.</summary>
public enum PortOrder
{
    /// <summary>Ports 1, 3, 5… are near ends and 2, 4, 6… their far ends (1→2, 3→4).</summary>
    NearOddFarEven,

    /// <summary>The first half of the ports are near ends, the second half their far ends
    /// (1→N+1, 2→N+2) — the order this program's own exports use.</summary>
    NearFirstHalf
}

/// <summary>
/// A channel known only by its S-parameters at a list of frequencies — a measurement or
/// another tool's result, read from Touchstone.
/// <para>
/// Between the points each entry is interpolated with its delay taken out: a delay line's
/// S21 turns many times between widely spaced points, so its real and imaginary parts
/// cannot be joined by straight lines, but S21·e^{jωτ} hardly moves and can. τ is the
/// entry's own median phase slope. (Magnitude and phase separately would do as well for
/// S21 and badly for a reflection, whose magnitude goes through zero with the phase
/// jumping half a turn.) Below the first point the delay-free value is run out along the
/// first two points to DC and made real there. Above the last point there is no data:
/// <see cref="At"/> holds the last point, and callers that integrate over frequency stop
/// there and say so.
/// </para>
/// </summary>
public sealed class ImportedChannel
{
    private readonly double[] _frequencies;
    private readonly Complex[][] _flat;       // [entry][point]: S·e^{+j2πfτ}
    private readonly double[] _delay;         // τ per entry
    private readonly double[] _dc;            // delay-free value at DC, real
    private readonly List<string> _notes = new();

    public ImportedChannel(TouchstoneData data)
    {
        if (data.FrequenciesHz.Count < 2)
            throw new InvalidOperationException("A channel needs at least two frequency points.");
        Ports = data.Ports;
        ReferenceOhms = data.ReferenceOhms;
        _frequencies = data.FrequenciesHz.ToArray();
        for (int k = 1; k < _frequencies.Length; k++)
            if (!(_frequencies[k] > _frequencies[k - 1]))
                throw new InvalidOperationException("The channel's frequencies must rise.");
        int entries = Ports * Ports, points = _frequencies.Length;
        _flat = new Complex[entries][];
        _delay = new double[entries];
        _dc = new double[entries];
        double coarsest = 0;
        var slopes = new List<double>(points);
        for (int e = 0; e < entries; e++)
        {
            int i = e / Ports, j = e % Ports;
            double largest = 0;
            for (int k = 0; k < points; k++) largest = Math.Max(largest, data.Scattering[k][i, j].Magnitude);
            // Phase steps between neighbouring points where the entry is not near a null.
            slopes.Clear();
            double steps = 0;
            for (int k = 1; k < points; k++)
            {
                Complex a = data.Scattering[k - 1][i, j], b = data.Scattering[k][i, j];
                if (a.Magnitude < 0.05 * largest || b.Magnitude < 0.05 * largest || largest < 1e-6) continue;
                double turn = (b / a).Phase;
                slopes.Add(turn / (_frequencies[k] - _frequencies[k - 1]));
                steps += Math.Abs(turn);
            }
            if (slopes.Count > 0)
            {
                slopes.Sort();
                _delay[e] = Math.Max(0, -slopes[slopes.Count / 2] / (2 * Math.PI));
                if (largest > 0.05) coarsest = Math.Max(coarsest, steps / slopes.Count);
            }
            var flat = new Complex[points];
            for (int k = 0; k < points; k++)
                flat[k] = data.Scattering[k][i, j] * Complex.FromPolarCoordinates(1, 2 * Math.PI * _frequencies[k] * _delay[e]);
            _flat[e] = flat;
            Complex atDc = flat[0] - (flat[1] - flat[0]) * (_frequencies[0] / (_frequencies[1] - _frequencies[0]));
            _dc[e] = Math.Clamp(atDc.Real, -1, 1);
        }
        if (_frequencies[0] > 0)
            _notes.Add($"The file starts at {_frequencies[0] / 1e6:g4} MHz; below that each entry is run out along its first " +
                       "two points to a real value at DC.");
        if (coarsest > 1.0)
            _notes.Add($"The phase turns {coarsest:f1} rad between points on average: the frequency step is too coarse " +
                       "for the channel's delay and the interpolation between points cannot be trusted.");
        Checks = data.Scattering.Select((s, k) => NetworkChecks.Check(_frequencies[k], s)).ToList();
    }

    public int Ports { get; }
    public double ReferenceOhms { get; }
    public IReadOnlyList<double> FrequenciesHz => _frequencies;
    public double MinFrequencyHz => _frequencies[0];
    public double MaxFrequencyHz => _frequencies[^1];
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Reciprocity and passivity at every point of the file.</summary>
    public IReadOnlyList<NetworkCheck> Checks { get; }

    public bool Covers(double frequencyHz) => frequencyHz <= MaxFrequencyHz * (1 + 1e-9);

    /// <summary>Smallest frequency step in the file [Hz].</summary>
    public double FinestStepHz
    {
        get
        {
            double step = double.MaxValue;
            for (int k = 1; k < _frequencies.Length; k++) step = Math.Min(step, _frequencies[k] - _frequencies[k - 1]);
            return step;
        }
    }

    /// <summary>The S matrix at a frequency.</summary>
    public Complex[,] At(double frequencyHz)
    {
        var s = new Complex[Ports, Ports];
        double f = Math.Clamp(frequencyHz, 0, _frequencies[^1]);
        int points = _frequencies.Length;
        int lo, hi;
        double w;
        if (f < _frequencies[0])
        {
            lo = hi = -1;
            w = f / _frequencies[0];
        }
        else
        {
            hi = Array.BinarySearch(_frequencies, f);
            if (hi < 0) hi = ~hi;
            hi = Math.Clamp(hi, 1, points - 1);
            lo = hi - 1;
            w = (f - _frequencies[lo]) / (_frequencies[hi] - _frequencies[lo]);
        }
        for (int e = 0; e < s.Length; e++)
        {
            Complex from = lo < 0 ? _dc[e] : _flat[e][lo];
            Complex to = lo < 0 ? _flat[e][0] : _flat[e][hi];
            s[e / Ports, e % Ports] = (from + w * (to - from))
                                      * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * _delay[e]);
        }
        return s;
    }

    /// <summary>
    /// Which port order the file most likely uses, from where the signal put into port 1
    /// comes out at the lowest frequencies: port 2 (near odd, far even) or port N+1.
    /// </summary>
    public PortOrder DetectOrder()
    {
        if (Ports < 4 || Ports % 2 != 0) return PortOrder.NearOddFarEven;
        int half = Ports / 2, count = Math.Min(5, _frequencies.Length);
        double toSecond = 0, toHalf = 0;
        for (int k = 0; k < count; k++)
        {
            toSecond += _flat[1 * Ports + 0][k].Magnitude;
            toHalf += _flat[half * Ports + 0][k].Magnitude;
        }
        return toSecond >= toHalf ? PortOrder.NearOddFarEven : PortOrder.NearFirstHalf;
    }

    /// <summary>File port index (0-based) of each port in the order near 1…N, far 1…N.</summary>
    public int[] NearFarPorts(PortOrder order)
    {
        if (Ports % 2 != 0)
            throw new InvalidOperationException($"A {Ports}-port file has no near and far ends to pair.");
        int lines = Ports / 2;
        var map = new int[Ports];
        for (int i = 0; i < lines; i++)
        {
            map[i] = order == PortOrder.NearOddFarEven ? 2 * i : i;
            map[lines + i] = order == PortOrder.NearOddFarEven ? 2 * i + 1 : lines + i;
        }
        return map;
    }

    /// <summary>
    /// The channel with every port loaded by the conductance g: voltage at port i per unit
    /// current into port k, ports ordered near 1…N, far 1…N — what the nonlinear time-domain
    /// engine takes. Z′ = Z₀(I + S)·[(I − S) + gZ₀(I + S)]⁻¹, which needs neither Z nor Y to
    /// exist on their own.
    /// </summary>
    public Complex[,] TransferImpedance(double frequencyHz, double referenceSiemens, PortOrder order)
    {
        var map = NearFarPorts(order);
        var s = At(frequencyHz);
        int n = Ports;
        double gz = referenceSiemens * ReferenceOhms;
        var plus = new Complex[n, n];
        var system = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Complex value = s[map[i], map[j]];
                plus[i, j] = (i == j ? 1 : 0) + value;
                system[i, j] = (i == j ? 1 + gz : 0) - value + gz * value;
            }
        var z = NetworkParameters.Multiply(plus, NetworkParameters.Invert(system));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                z[i, j] *= ReferenceOhms;
        return z;
    }

    /// <summary>The same function, refusing a time step whose Nyquist frequency the file
    /// does not reach — the engine needs the channel at every bin up to 1/(2·dt).</summary>
    public Func<double, double, Complex[,]> TransferImpedanceFor(double timeStepSeconds, PortOrder order)
    {
        double nyquist = 0.5 / timeStepSeconds;
        if (!Covers(nyquist))
            throw new InvalidOperationException(
                $"The channel file ends at {MaxFrequencyHz / 1e9:g4} GHz; a time step of {timeStepSeconds * 1e12:g4} ps " +
                $"needs it to {nyquist / 1e9:g4} GHz. Use a step of at least {0.5 / MaxFrequencyHz * 1e12:g4} ps " +
                "(fewer samples per bit), or a file measured to a higher frequency.");
        return (f, g) => TransferImpedance(f, g, order);
    }

    /// <summary>What the file is, in a few lines.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            $"{Ports}-port, {_frequencies.Length} points from {MinFrequencyHz / 1e6:g4} MHz to {MaxFrequencyHz / 1e9:g4} GHz, " +
            $"reference {ReferenceOhms:g4} Ω."
        };
        var passive = Checks.MaxBy(c => c.LargestSingularValue)!;
        lines.Add(passive.IsPassive()
            ? $"Passive: the largest singular value of S is {passive.LargestSingularValue:f4}."
            : $"NOT PASSIVE: the largest singular value of S reaches {passive.LargestSingularValue:f4} at " +
              $"{passive.FrequencyHz / 1e6:g4} MHz — the data returns more power than it is sent there.");
        if (Ports > 1)
        {
            var reciprocal = Checks.MaxBy(c => c.ReciprocityError)!;
            lines.Add(reciprocal.IsReciprocal()
                ? $"Reciprocal: |S_ij − S_ji| is at most {reciprocal.ReciprocityError:e1}."
                : $"NOT RECIPROCAL: |S_ij − S_ji| reaches {reciprocal.ReciprocityError:e1} at {reciprocal.FrequencyHz / 1e6:g4} MHz.");
        }
        lines.AddRange(_notes);
        return lines;
    }
}
