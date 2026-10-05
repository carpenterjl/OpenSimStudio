using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Core.Signals;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Channel;

/// <summary>What hangs on one port of a channel: a resistance to ground, and the share of
/// the source waveform driven through it (0 for a plain load).</summary>
public readonly record struct PortTermination(double ResistanceOhms, double Drive = 0);

/// <summary>The bit stream and the terminations of an eye run.</summary>
public sealed record ChannelEyeSetup
{
    public double BitRate { get; init; } = 1e9;

    /// <summary>Edge time as a fraction of the unit interval.</summary>
    public double RiseFractionOfUi { get; init; } = 0.2;

    public int PrbsOrder { get; init; } = 7;

    /// <summary>Samples per bit; 0 = as many as the channel's last frequency allows, up
    /// to 32 (a sample step needs data to its Nyquist frequency).</summary>
    public int SamplesPerUi { get; init; }

    /// <summary>Source swing [V]: single-ended low 0 to high Swing; differential ±Swing
    /// between the two legs.</summary>
    public double Swing { get; init; } = 1.0;

    /// <summary>Per leg, to ground.</summary>
    public double SourceOhms { get; init; } = 50;

    /// <summary>Per leg, to ground.</summary>
    public double LoadOhms { get; init; } = 50;
}

public sealed record ChannelEyeResult
{
    public required EyeDiagram Eye { get; init; }

    /// <summary>One pattern period at the receiver: the single-ended voltage, or the
    /// difference between the two legs.</summary>
    public required double[] Waveform { get; init; }

    /// <summary>Differential runs: the common-mode voltage at the receiver, which a
    /// balanced channel driven differentially does not have.</summary>
    public double[]? CommonMode { get; init; }

    public required double SampleIntervalSeconds { get; init; }
    public required int SamplesPerUi { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            Eye.IsClosed
                ? $"Eye CLOSED (height {Eye.EyeHeight * 1e3:f1} mV)."
                : $"Eye height {Eye.EyeHeight * 1e3:f1} mV, width {Eye.EyeWidthSeconds * 1e12:f1} ps " +
                  $"({Eye.EyeWidthSeconds / Eye.UnitIntervalSeconds * 100:f0} % of the bit), " +
                  $"jitter {Eye.JitterPeakToPeakSeconds * 1e12:f1} ps peak to peak."
        };
        if (CommonMode is { } common)
            lines.Add($"Common mode at the receiver: {(common.Max() - common.Min()) * 1e3:f2} mV peak to peak.");
        lines.AddRange(Notes);
        return lines;
    }
}

/// <summary>
/// The eye of an imported channel between resistive terminations: the periodic steady
/// state of a PRBS pattern, bin by bin through the S-parameters (the same construction as
/// <see cref="TransientLink"/>, with the channel given by its S matrix instead of a line
/// model). Linear only — a driver or receiver model with clamps goes through
/// <see cref="NonlinearLink.SolveNPort(int, Func{double, double, Complex[,]}, IReadOnlyList{INonlinearDriver}, IReadOnlyList{INonlinearDriver}, int, double, int, double, double, int?)"/>
/// with <see cref="ImportedChannel.TransferImpedanceFor"/>.
/// </summary>
public static class ChannelEye
{
    /// <summary>
    /// Port voltages of an S-parameter network with a resistance on every port and a source
    /// behind some of them, per unit source. With Γ the terminations' reflection
    /// coefficients and b_s the waves the sources launch: b = (I − S·Γ)⁻¹·S·b_s, a = Γ·b + b_s,
    /// V = √Z₀·(a + b).
    /// </summary>
    public static Complex[] PortVoltages(Complex[,] s, double referenceOhms, IReadOnlyList<PortTermination> ports)
    {
        int n = s.GetLength(0);
        if (ports.Count != n) throw new ArgumentException("One termination per port.", nameof(ports));
        double root = Math.Sqrt(referenceOhms);
        var gamma = new double[n];
        var launched = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            double r = ports[i].ResistanceOhms;
            if (double.IsPositiveInfinity(r))
            {
                if (ports[i].Drive != 0) throw new ArgumentException("A driven port needs a finite source resistance.");
                gamma[i] = 1;
                continue;
            }
            gamma[i] = (r - referenceOhms) / (r + referenceOhms);
            launched[i] = ports[i].Drive * root / (r + referenceOhms);
        }
        var system = new Complex[n, n];
        var rhs = new Complex[n, 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                system[i, j] = (i == j ? 1 : 0) - s[i, j] * gamma[j];
                rhs[i, 0] += s[i, j] * launched[j];
            }
        }
        var b = NetworkParameters.Multiply(NetworkParameters.Invert(system), rhs);
        var volts = new Complex[n];
        for (int i = 0; i < n; i++)
            volts[i] = root * (gamma[i] * b[i, 0] + launched[i] + b[i, 0]);
        return volts;
    }

    /// <summary>Single-ended: drive one port, read another; every other port in the load.</summary>
    public static ChannelEyeResult SingleEnded(ImportedChannel channel, int drivenPort, int receiverPort,
        ChannelEyeSetup setup)
    {
        var ports = new PortTermination[channel.Ports];
        for (int i = 0; i < ports.Length; i++) ports[i] = new PortTermination(setup.LoadOhms);
        ports[drivenPort] = new PortTermination(setup.SourceOhms, 1);
        return Run(channel, setup, ports, 0, setup.Swing, v => (v[receiverPort], null));
    }

    /// <summary>Differential: the near pair driven in opposition, the difference read at
    /// the far pair. A four-port file.</summary>
    public static ChannelEyeResult Differential(ImportedChannel channel, DifferentialPairing pairing,
        ChannelEyeSetup setup)
    {
        if (channel.Ports != 4)
            throw new InvalidOperationException($"A differential eye needs a four-port file; this one has {channel.Ports}.");
        var ports = new PortTermination[4];
        ports[pairing.Plus1] = new PortTermination(setup.SourceOhms, 0.5);
        ports[pairing.Minus1] = new PortTermination(setup.SourceOhms, -0.5);
        ports[pairing.Plus2] = new PortTermination(setup.LoadOhms);
        ports[pairing.Minus2] = new PortTermination(setup.LoadOhms);
        return Run(channel, setup, ports, -setup.Swing, setup.Swing,
            v => (v[pairing.Plus2] - v[pairing.Minus2], 0.5 * (v[pairing.Plus2] + v[pairing.Minus2])));
    }

    private static ChannelEyeResult Run(ImportedChannel channel, ChannelEyeSetup setup,
        IReadOnlyList<PortTermination> ports, double low, double high,
        Func<Complex[], (Complex Signal, Complex? Common)> read)
    {
        if (!(setup.BitRate > 0)) throw new ArgumentOutOfRangeException(nameof(setup), "The bit rate must be positive.");
        var notes = new List<string>();
        int spu = setup.SamplesPerUi;
        if (spu <= 0)
        {
            // Largest power of two up to 32 whose Nyquist frequency the file reaches.
            spu = 32;
            while (spu > 4 && 0.5 * spu * setup.BitRate > channel.MaxFrequencyHz) spu /= 2;
        }
        if (spu < 4) throw new ArgumentOutOfRangeException(nameof(setup), "At least 4 samples per bit.");
        double dt = 1.0 / (setup.BitRate * spu);
        int bitCount = (int)PrbsGenerator.PeriodBits(setup.PrbsOrder);
        var bits = PrbsGenerator.Generate(setup.PrbsOrder, bitCount);
        var source = SourceWaveform.Trapezoid(bits, spu, setup.RiseFractionOfUi, high, low);
        int samples = source.Length;

        var buffer = new Complex[samples];
        for (int i = 0; i < samples; i++) buffer[i] = source[i];
        var spectrum = Fft.Forward(buffer);
        var signal = new Complex[samples];
        var common = new Complex[samples];
        bool hasCommon = false;
        double baseFrequency = 1.0 / (samples * dt), kept = 0, dropped = 0;
        int half = samples / 2;
        var slots = new (Complex Signal, Complex? Common)?[half + 1];
        Parallel.For(0, half + 1, k =>
        {
            double f = k * baseFrequency;
            if (spectrum[k] == Complex.Zero || !channel.Covers(f)) return;
            slots[k] = read(PortVoltages(channel.At(f), channel.ReferenceOhms, ports));
        });
        for (int k = 0; k <= half; k++)
        {
            double energy = spectrum[k].Magnitude * spectrum[k].Magnitude;
            if (slots[k] is not { } value)
            {
                if (k > 0) dropped += energy;
                continue;
            }
            if (k > 0) kept += energy;
            signal[k] = value.Signal * spectrum[k];
            if (value.Common is { } c) { common[k] = c * spectrum[k]; hasCommon = true; }
            if (k > 0 && k < samples - half)
            {
                signal[samples - k] = Complex.Conjugate(signal[k]);
                common[samples - k] = Complex.Conjugate(common[k]);
            }
        }
        if (dropped > 1e-6 * (kept + dropped))
            notes.Add($"The channel file ends at {channel.MaxFrequencyHz / 1e9:g4} GHz: {dropped / (kept + dropped) * 100:g2} % of the " +
                      "pattern's alternating energy lies above it and is left out.");
        notes.AddRange(channel.Notes);

        var wave = Real(Fft.Inverse(signal));
        return new ChannelEyeResult
        {
            Eye = EyeDiagram.Fold(wave, spu, dt, bits),
            Waveform = wave,
            CommonMode = hasCommon ? Real(Fft.Inverse(common)) : null,
            SampleIntervalSeconds = dt,
            SamplesPerUi = spu,
            Notes = notes
        };
    }

    private static double[] Real(Complex[] values)
    {
        var real = new double[values.Length];
        for (int i = 0; i < real.Length; i++) real[i] = values[i].Real;
        return real;
    }
}
