using System.Numerics;

namespace OpenSim.Core.Numerics;

/// <summary>
/// First-party complex FFT: iterative radix-2 for power-of-two lengths, Bluestein's
/// chirp-z algorithm for every other length. Bluestein is chosen deliberately over
/// zero-padding: it evaluates the EXACT length-N DFT through power-of-two convolutions,
/// so periodic-signal spectra (a PRBS pattern of 127·2^s samples, an odd harmonic grid)
/// are exact — padding would silently change the frequency grid. Convention:
/// X[k] = Σ x[n]·e^{−j2πnk/N}; the inverse divides by N. Sequential and deterministic.
/// </summary>
public static class Fft
{
    /// <summary>The forward DFT of any length ≥ 1.</summary>
    public static Complex[] Forward(IReadOnlyList<Complex> samples)
        => Transform(samples, inverse: false, normalize: false);

    /// <summary>The inverse DFT (includes the 1/N normalization).</summary>
    public static Complex[] Inverse(IReadOnlyList<Complex> spectrum)
        => Transform(spectrum, inverse: true, normalize: true);

    private static Complex[] Transform(IReadOnlyList<Complex> data, bool inverse, bool normalize)
    {
        int n = data.Count;
        if (n == 0) throw new ArgumentException("FFT input must be non-empty.");
        var work = new Complex[n];
        for (int i = 0; i < n; i++) work[i] = data[i];
        if ((n & (n - 1)) == 0) TransformPow2(work, inverse);
        else TransformBluestein(work, inverse);
        if (normalize)
            for (int i = 0; i < n; i++) work[i] /= n;
        return work;
    }

    /// <summary>Unnormalized in-place radix-2 (bit-reversal + butterflies).</summary>
    private static void TransformPow2(Complex[] a, bool inverse)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j |= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        double sign = inverse ? 1 : -1;
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
                for (int j = 0; j < half; j++)
                {
                    // Twiddles from exact per-index angles (not a recurrence): the
                    // O(N log N) trig cost buys the 1e-12 oracle accuracy the gates pin.
                    double angle = sign * 2 * Math.PI * j / len;
                    var w = new Complex(Math.Cos(angle), Math.Sin(angle));
                    var u = a[i + j];
                    var v = a[i + j + half] * w;
                    a[i + j] = u + v;
                    a[i + j + half] = u - v;
                }
        }
    }

    /// <summary>
    /// Bluestein: nk = (n² + k² − (k−n)²)/2 turns the DFT into a chirp-modulated
    /// convolution, computed by three power-of-two FFTs of length ≥ 2N−1. The chirp
    /// exponent k² is reduced mod 2N BEFORE the trig call — at large N the raw k²·π/N
    /// angle loses the low-order bits that carry all the phase information.
    /// </summary>
    private static void TransformBluestein(Complex[] a, bool inverse)
    {
        int n = a.Length;
        double sign = inverse ? 1 : -1;
        var chirp = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            long m = (long)k * k % (2L * n);
            double angle = sign * Math.PI * m / n;
            chirp[k] = new Complex(Math.Cos(angle), Math.Sin(angle));
        }

        int m2 = 1;
        while (m2 < 2 * n - 1) m2 <<= 1;
        var fa = new Complex[m2];
        var fb = new Complex[m2];
        for (int k = 0; k < n; k++) fa[k] = a[k] * chirp[k];
        // The convolution kernel conj(chirp) is even in its index; the negative half
        // wraps to the top of the cyclic buffer.
        fb[0] = Complex.Conjugate(chirp[0]);
        for (int k = 1; k < n; k++)
            fb[k] = fb[m2 - k] = Complex.Conjugate(chirp[k]);

        TransformPow2(fa, inverse: false);
        TransformPow2(fb, inverse: false);
        for (int i = 0; i < m2; i++) fa[i] *= fb[i];
        TransformPow2(fa, inverse: true);
        for (int k = 0; k < n; k++) a[k] = fa[k] * chirp[k] / m2;
    }
}
