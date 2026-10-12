using System.Numerics;

namespace OpenSim.Rf.Emi;

/// <summary>A periodic waveform known by its Fourier lines.</summary>
public interface IPeriodicWaveform
{
    double Period { get; }

    /// <summary>Two-sided coefficient c_n = (1/T)∫v(t)e^{−jnω₀t}dt over one period; the
    /// one-sided amplitude of harmonic n ≥ 1 is 2|c_n|.</summary>
    Complex Coefficient(int n);
}

/// <summary>
/// A damped sinusoid g(t) = A·e^{−α(t−t₀)}·sin(ω_d(t−t₀) + φ) for t ≥ t₀, repeated every period
/// — the ringing a switching edge leaves on a node or in a loop. The periodic sum of a transient
/// has lines c_n = G(nω₀)/T with G the transient's Fourier transform, exactly, however slowly
/// the ring decays against the period (the copies that spill into the next period are in the
/// sum), so the coefficients are the closed form of G.
/// </summary>
public sealed class DampedSinusoidTrain : IPeriodicWaveform
{
    public DampedSinusoidTrain(double period, double startTime, double amplitude, double decayPerSecond,
        double angularFrequency, double phase = 0)
    {
        if (!(period > 0)) throw new ArgumentOutOfRangeException(nameof(period));
        if (!(decayPerSecond > 0)) throw new ArgumentOutOfRangeException(nameof(decayPerSecond), "The ring must decay.");
        if (!(angularFrequency > 0)) throw new ArgumentOutOfRangeException(nameof(angularFrequency));
        if (startTime < 0 || startTime >= period) throw new ArgumentOutOfRangeException(nameof(startTime), "The start lies in [0, period).");
        Period = period;
        StartTime = startTime;
        Amplitude = amplitude;
        DecayPerSecond = decayPerSecond;
        AngularFrequency = angularFrequency;
        Phase = phase;
    }

    public double Period { get; }
    public double StartTime { get; }
    public double Amplitude { get; }
    public double DecayPerSecond { get; }
    public double AngularFrequency { get; }
    public double Phase { get; }

    public double FrequencyHz => AngularFrequency / (2 * Math.PI);

    /// <summary>The ring at time t within the period (its own copy only; a copy from the
    /// previous period that has not decayed is left out — <see cref="Coefficient"/> has it).</summary>
    public double Value(double t)
    {
        double tau = t - StartTime;
        return tau < 0 ? 0 : Amplitude * Math.Exp(-DecayPerSecond * tau) * Math.Sin(AngularFrequency * tau + Phase);
    }

    public Complex Coefficient(int n)
    {
        // G(ω) = A·e^{−jωt₀}·[ω_d cos φ + (α + jω) sin φ] / ((α + jω)² + ω_d²).
        double w = 2 * Math.PI * n / Period;
        var s = new Complex(DecayPerSecond, w);
        var numerator = AngularFrequency * Math.Cos(Phase) + s * Math.Sin(Phase);
        var g = Amplitude * Complex.Exp(new Complex(0, -w * StartTime)) * numerator / (s * s + AngularFrequency * AngularFrequency);
        return g / Period;
    }
}

/// <summary>The sum of several waveforms of one period.</summary>
public sealed class CompositeWaveform : IPeriodicWaveform
{
    private readonly IReadOnlyList<IPeriodicWaveform> _parts;

    public CompositeWaveform(IReadOnlyList<IPeriodicWaveform> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("At least one part.", nameof(parts));
        foreach (var p in parts)
            if (Math.Abs(p.Period - parts[0].Period) > 1e-12 * parts[0].Period)
                throw new ArgumentException("All parts must share one period.", nameof(parts));
        _parts = parts;
    }

    public IReadOnlyList<IPeriodicWaveform> Parts => _parts;
    public double Period => _parts[0].Period;

    public Complex Coefficient(int n)
    {
        Complex sum = Complex.Zero;
        foreach (var p in _parts) sum += p.Coefficient(n);
        return sum;
    }
}
