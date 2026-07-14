namespace OpenSim.Core.Signals;

/// <summary>
/// Sampled source waveforms for the SI time-domain path. The bit-pattern generator is
/// PERIODIC by construction (the transition into bit 0 comes from the last bit) because
/// the transient engine computes the exact periodic steady state through the pattern's
/// discrete spectrum — a one-shot waveform with free ends would not be the same signal.
/// </summary>
public static class SourceWaveform
{
    /// <summary>
    /// A trapezoidal NRZ waveform: <paramref name="samplesPerUi"/> samples per bit,
    /// linear edges spanning <paramref name="riseFractionOfUi"/> of a unit interval at
    /// the start of each differing bit (rise and fall share the shape — the Thevenin
    /// driver model's symmetric edge). riseFraction = 0 is the ideal square NRZ.
    /// </summary>
    public static double[] Trapezoid(IReadOnlyList<bool> bits, int samplesPerUi,
        double riseFractionOfUi, double highLevel = 1.0, double lowLevel = 0.0)
    {
        if (bits.Count == 0) throw new ArgumentException("At least one bit is required.");
        if (samplesPerUi < 1) throw new ArgumentOutOfRangeException(nameof(samplesPerUi));
        if (riseFractionOfUi is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(riseFractionOfUi));

        int riseSamples = (int)Math.Round(riseFractionOfUi * samplesPerUi);
        var wave = new double[bits.Count * samplesPerUi];
        for (int i = 0; i < bits.Count; i++)
        {
            double target = bits[i] ? highLevel : lowLevel;
            double previous = bits[(i + bits.Count - 1) % bits.Count] ? highLevel : lowLevel;
            for (int o = 0; o < samplesPerUi; o++)
            {
                // Samples sit at the centers of their intervals (o + 0.5), so an edge of
                // riseSamples covers exactly that many samples and the ideal limit
                // (riseSamples = 0) lands every sample on its bit level.
                double ramp = riseSamples == 0
                    ? 1
                    : Math.Clamp((o + 0.5) / riseSamples, 0, 1);
                wave[i * samplesPerUi + o] = previous + (target - previous) * ramp;
            }
        }
        return wave;
    }

    /// <summary>A 101010… clock pattern through the same trapezoid shaping.</summary>
    public static double[] Clock(int periods, int samplesPerUi, double riseFractionOfUi,
        double highLevel = 1.0, double lowLevel = 0.0)
    {
        if (periods < 1) throw new ArgumentOutOfRangeException(nameof(periods));
        var bits = new bool[2 * periods];
        for (int i = 0; i < bits.Length; i += 2) bits[i] = true;
        return Trapezoid(bits, samplesPerUi, riseFractionOfUi, highLevel, lowLevel);
    }

    /// <summary>An ideal step: 0 before <paramref name="stepAt"/>, amplitude from it on.</summary>
    public static double[] Step(int totalSamples, int stepAt, double amplitude = 1.0)
    {
        if (totalSamples < 1) throw new ArgumentOutOfRangeException(nameof(totalSamples));
        var wave = new double[totalSamples];
        for (int i = Math.Max(0, stepAt); i < totalSamples; i++) wave[i] = amplitude;
        return wave;
    }

    /// <summary>An ideal rectangular pulse of <paramref name="widthSamples"/> samples.</summary>
    public static double[] Pulse(int totalSamples, int startAt, int widthSamples,
        double amplitude = 1.0)
    {
        if (totalSamples < 1) throw new ArgumentOutOfRangeException(nameof(totalSamples));
        if (widthSamples < 0) throw new ArgumentOutOfRangeException(nameof(widthSamples));
        var wave = new double[totalSamples];
        int end = Math.Min(totalSamples, startAt + widthSamples);
        for (int i = Math.Max(0, startAt); i < end; i++) wave[i] = amplitude;
        return wave;
    }
}
