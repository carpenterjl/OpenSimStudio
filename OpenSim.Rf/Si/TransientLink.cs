using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Si;

/// <summary>One period of the link's periodic steady state, per line and end.</summary>
public sealed record TransientResult(
    double SampleIntervalSeconds,
    double[][] NearVoltages,
    double[][] FarVoltages);

/// <summary>
/// The SI time-domain engine (Stage S5): EXACT periodic steady state through the
/// pattern's discrete spectrum. The source waveforms are periodic by construction
/// (see <see cref="OpenSim.Core.Signals.SourceWaveform"/>), so multiplying their FFT by
/// the network's frequency response at the EXACT bin frequencies and inverting IS the
/// answer — circular convolution is the periodic convolution, with no windowing or
/// truncation approximations anywhere. Bins are independent MTL solves and run in
/// parallel into ordered slots (the Stage G recipe — bitwise-deterministic at any
/// thread count); the spectrum is mirrored conjugate-symmetric so the output is real.
/// </summary>
public static class TransientLink
{
    /// <summary>
    /// Solves the terminated link with one periodic source per line (null = quiet
    /// line). All sources share the sample grid; superposition is exact (linear
    /// network), which is how victim/aggressor crosstalk runs — each line gets its own
    /// PRBS/seed and the victim's waveform carries the coupled sum.
    /// </summary>
    public static TransientResult SolvePeriodic(MtlNetwork network,
        IReadOnlyList<LineTermination> terminations,
        IReadOnlyList<double[]?> sourcePeriodsPerLine,
        double sampleIntervalSeconds,
        int? maxDegreeOfParallelism = null)
    {
        int lines = network.ConductorCount;
        if (terminations.Count != lines || sourcePeriodsPerLine.Count != lines)
            throw new ArgumentException("One termination and one (possibly null) source per line.");
        if (sampleIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleIntervalSeconds));

        int samples = 0;
        for (int i = 0; i < lines; i++)
        {
            var source = sourcePeriodsPerLine[i];
            if (source is null) continue;
            if (samples == 0) samples = source.Length;
            else if (source.Length != samples)
                throw new ArgumentException(
                    "All driven lines must share one pattern period (same sample count) — "
                    + "the periodic spectrum is a single frequency grid.");
        }
        if (samples == 0)
            throw new ArgumentException("At least one line must carry a source waveform.");

        // Source spectra.
        var spectra = new Complex[lines][];
        for (int i = 0; i < lines; i++)
        {
            var source = sourcePeriodsPerLine[i];
            if (source is null) continue;
            var buffer = new Complex[samples];
            for (int n = 0; n < samples; n++) buffer[n] = source[n];
            spectra[i] = Fft.Forward(buffer);
        }

        // Independent MTL solves at the positive bins, ordered slots. Negative bins by
        // conjugate symmetry (real sources ⇒ real waveforms).
        int half = samples / 2;
        var nearSpectra = new Complex[lines][];
        var farSpectra = new Complex[lines][];
        for (int i = 0; i < lines; i++)
        {
            nearSpectra[i] = new Complex[samples];
            farSpectra[i] = new Complex[samples];
        }
        double baseFrequency = 1.0 / (samples * sampleIntervalSeconds);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism ?? Environment.ProcessorCount
        };
        Parallel.For(0, half + 1, options, k =>
        {
            double frequency = k * baseFrequency;
            // Superpose the driven lines at this bin: E_i = X_i[k].
            var drive = new Complex[lines];
            bool any = false;
            for (int i = 0; i < lines; i++)
            {
                if (spectra[i] is null) continue;
                drive[i] = spectra[i][k];
                any = any || drive[i] != Complex.Zero;
            }
            if (!any) return;
            var solution = network.SolveTerminated(frequency, terminations, drive);
            for (int i = 0; i < lines; i++)
            {
                nearSpectra[i][k] = solution.NearVoltages[i];
                farSpectra[i][k] = solution.FarVoltages[i];
            }
        });

        for (int i = 0; i < lines; i++)
            for (int k = 1; k < samples - half; k++)
            {
                nearSpectra[i][samples - k] = Complex.Conjugate(nearSpectra[i][k]);
                farSpectra[i][samples - k] = Complex.Conjugate(farSpectra[i][k]);
            }

        var near = new double[lines][];
        var far = new double[lines][];
        for (int i = 0; i < lines; i++)
        {
            near[i] = RealInverse(nearSpectra[i]);
            far[i] = RealInverse(farSpectra[i]);
        }
        return new TransientResult(sampleIntervalSeconds, near, far);
    }

    /// <summary>Single-driver convenience: PRBS/step/clock on one line, others quiet.</summary>
    public static TransientResult SolvePeriodic(MtlNetwork network,
        IReadOnlyList<LineTermination> terminations, int drivenLine,
        double[] sourcePeriod, double sampleIntervalSeconds,
        int? maxDegreeOfParallelism = null)
    {
        var sources = new double[network.ConductorCount][];
        sources[drivenLine] = sourcePeriod;
        return SolvePeriodic(network, terminations, sources, sampleIntervalSeconds,
            maxDegreeOfParallelism);
    }

    private static double[] RealInverse(Complex[] spectrum)
    {
        var samples = Fft.Inverse(spectrum);
        var real = new double[samples.Length];
        for (int n = 0; n < samples.Length; n++) real[n] = samples[n].Real;
        return real;
    }
}
