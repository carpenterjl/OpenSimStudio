using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Channel;

/// <summary>An impedance profile against round-trip time.</summary>
/// <param name="TimeSeconds">Round-trip time of each sample; the port is at 0.</param>
/// <param name="Reflection">The reflected step, as a fraction of the incident one.</param>
/// <param name="ImpedanceOhms">Z₀(1 + ρ)/(1 − ρ): what a TDR instrument shows. Past the
/// first discontinuity it is an apparent impedance — it still holds the earlier
/// reflections and re-reflections.</param>
/// <param name="PeeledOhms">The profile with those taken out, for a lossless line; null
/// when not asked for.</param>
/// <param name="RiseTimeSeconds">10–90 % rise time of the step.</param>
public sealed record TdrProfile(double[] TimeSeconds, double[] Reflection, double[] ImpedanceOhms,
    double[]? PeeledOhms, double RiseTimeSeconds, double ReferenceOhms, IReadOnlyList<string> Notes)
{
    /// <summary>Impedance at a round-trip time, between samples by straight line.</summary>
    public double At(double roundTripSeconds, bool peeled = false)
    {
        var values = peeled ? PeeledOhms ?? throw new InvalidOperationException("No peeled profile was computed.") : ImpedanceOhms;
        double dt = TimeSeconds[1] - TimeSeconds[0];
        double x = (roundTripSeconds - TimeSeconds[0]) / dt;
        int i = Math.Clamp((int)Math.Floor(x), 0, values.Length - 2);
        double w = Math.Clamp(x - i, 0, 1);
        return values[i] + w * (values[i + 1] - values[i]);
    }
}

/// <summary>
/// The loss of a line, per second of TDR round-trip time: α(f) = a₀ + a₁√f + a₂f [Np/s] —
/// resistance, skin effect and dielectric loss. A round trip of Δt covers Δt of one-way
/// travel, so a line whose one-way loss is A(f) over a one-way delay τ has α = A/τ.
/// </summary>
public sealed record TdrLoss(double ConstantNepersPerSecond, double SkinNepersPerSecondRootHz,
    double DielectricNepersPerSecondHz)
{
    public double At(double frequencyHz) =>
        ConstantNepersPerSecond + SkinNepersPerSecondRootHz * Math.Sqrt(frequencyHz)
                                + DielectricNepersPerSecondHz * frequencyHz;

    /// <summary>
    /// The loss of a through path of the channel, as if it were spread evenly along it:
    /// A(f) = −½·ln(|S21|²/(1 − |S11|²)) — the insertion loss with the mismatch at the port
    /// taken out — over the path's delay, fitted by non-negative least squares to the three
    /// terms. Points where the port reflects most of the power, or nothing comes through,
    /// are left out.
    /// </summary>
    public static TdrLoss FromThrough(ImportedChannel channel, int from, int to)
    {
        double delay = channel.DelaySeconds(to, from);
        if (!(delay > 0)) throw new InvalidOperationException("The through path has no delay to spread its loss over.");
        var rows = new List<(double F, double Alpha)>();
        foreach (double f in channel.FrequenciesHz)
        {
            var s = channel.At(f);
            double through = s[to, from].Magnitude, reflected = s[from, from].Magnitude;
            double kept = 1 - reflected * reflected;
            if (kept < 0.1 || through < 1e-4) continue;
            double loss = -0.5 * Math.Log(through * through / kept);
            rows.Add((f, Math.Max(0, loss) / delay));
        }
        if (rows.Count < 3) throw new InvalidOperationException("Too few usable points on the through path to fit its loss.");
        // Non-negative least squares over three terms: the best of the subsets whose
        // unconstrained solution is non-negative.
        double[]? best = null;
        double bestError = double.MaxValue;
        for (int mask = 1; mask < 8; mask++)
        {
            var columns = Enumerable.Range(0, 3).Where(c => (mask & (1 << c)) != 0).ToArray();
            var a = new double[rows.Count, columns.Length];
            var b = new double[rows.Count];
            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < columns.Length; c++)
                    a[r, c] = columns[c] switch { 0 => 1, 1 => Math.Sqrt(rows[r].F), _ => rows[r].F };
                b[r] = rows[r].Alpha;
            }
            var x = HouseholderLeastSquares.Solve(a, b);
            if (x.Any(v => v < 0)) continue;
            var full = new double[3];
            for (int c = 0; c < columns.Length; c++) full[columns[c]] = x[c];
            double error = rows.Sum(r => Math.Pow(full[0] + full[1] * Math.Sqrt(r.F) + full[2] * r.F - r.Alpha, 2));
            if (error < bestError) { bestError = error; best = full; }
        }
        best ??= new double[3];
        return new TdrLoss(best[0], best[1], best[2]);
    }
}

/// <summary>
/// Time-domain reflectometry from a reflection coefficient known against frequency: the
/// response to a step of finite rise time, by inverse Fourier transform. The step is a
/// Gaussian-edged one, because the data ends at some frequency and a sharper edge would ask
/// for content that is not there; by default its rise time is the fastest the data
/// supports (the edge's spectrum 40 dB down at the last frequency).
/// <para>
/// The peeled profile runs the layer-peeling recursion on the same band-limited response:
/// each time sample is a short piece of line, its reflection is read off the first arrival,
/// and that piece's effect is removed from everything later. Without a loss model it
/// assumes a lossless line, and on a lossy one the later sections read progressively off.
/// With a <see cref="TdrLoss"/>, each piece also takes back its share of the loss: both
/// waves are referred to the next interface with the forward wave's accumulated loss taken
/// out of both, which leaves the forward wave as it was and multiplies the backward one by
/// z/H², H the one-way loss of a piece — minimum phase, so causal, as a passive line's is.
/// </para>
/// </summary>
public static class Tdr
{
    /// <summary>10–90 % rise time of a Gaussian edge of standard deviation 1.</summary>
    private const double RisePerSigma = 2.5631031311;

    /// <summary>Most samples a profile holds (the peeling is quadratic in it).</summary>
    private const int MaxSamples = 8192;

    /// <param name="reflection">S11 (or S_dd11) at a frequency.</param>
    /// <param name="maxFrequencyHz">Where the data ends.</param>
    /// <param name="frequencyStepHz">The data's frequency step; it sets how long a response
    /// is seen before it wraps (1 / step).</param>
    /// <param name="riseTimeSeconds">Null = the fastest the data supports.</param>
    /// <param name="maxTimeSeconds">Round-trip span to return; null = half the unambiguous range.</param>
    /// <param name="loss">The line's loss for the peeled profile; null peels it as lossless.</param>
    public static TdrProfile Profile(Func<double, Complex> reflection, double maxFrequencyHz, double frequencyStepHz,
        double referenceOhms, double? riseTimeSeconds = null, bool peel = true, double? maxTimeSeconds = null,
        TdrLoss? loss = null)
    {
        if (!(maxFrequencyHz > 0) || !(frequencyStepHz > 0) || frequencyStepHz >= maxFrequencyHz)
            throw new ArgumentException("TDR needs a frequency range of many steps.");
        var notes = new List<string>();
        double fastest = RisePerSigma * Math.Sqrt(2 * Math.Log(100)) / (2 * Math.PI * maxFrequencyHz);
        double rise = riseTimeSeconds ?? fastest;
        if (rise < 0.999 * fastest)
            notes.Add($"A {rise * 1e12:g3} ps edge has content beyond the data's {maxFrequencyHz / 1e9:g4} GHz " +
                      $"(the data supports {fastest * 1e12:g3} ps); the profile rings at that rate.");
        double sigma = rise / RisePerSigma;

        // Time step a quarter of what the data's bandwidth alone would give; the spectrum is
        // zero above the data, and the Gaussian has already taken it down there.
        int size = 1;
        while (size < 8 * maxFrequencyHz / frequencyStepHz) size <<= 1;
        size = Math.Min(size, 1 << 18);
        double step = Math.Min(frequencyStepHz, 8 * maxFrequencyHz / size);
        double dt = 1.0 / (size * step);
        int shift = Math.Min(size / 8, (int)Math.Ceiling(4.5 * sigma / dt));

        var spectrum = new Complex[size];
        for (int k = 0; k <= size / 2; k++)
        {
            double f = k * step;
            if (f > maxFrequencyHz) break;
            double window = Math.Exp(-0.5 * Math.Pow(2 * Math.PI * f * sigma, 2));
            Complex value = reflection(f) * window * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * shift * dt);
            if (k == 0) value = value.Real;
            spectrum[k] = value;
            if (k > 0 && k < size / 2) spectrum[size - k] = Complex.Conjugate(value);
        }
        var impulse = Fft.Inverse(spectrum);

        double span = maxTimeSeconds ?? 0.5 / step;
        int count = Math.Min(size - shift, shift + (int)Math.Ceiling(span / dt) + 1);
        if (count > MaxSamples)
        {
            count = MaxSamples;
            notes.Add($"The profile is given to {(count - shift) * dt * 1e9:g4} ns round trip ({MaxSamples} samples); " +
                      "ask for a slower edge to see further.");
        }
        if (span > 0.5 / step)
            notes.Add($"The data's {step / 1e6:g4} MHz step shows {1e9 / step:g4} ns before the response wraps; " +
                      "anything later than that is folded back into the profile.");
        var time = new double[count];
        var rho = new double[count];
        var impedance = new double[count];
        var arrivals = new double[count];
        double sum = 0;
        for (int n = 0; n < count; n++)
        {
            arrivals[n] = impulse[n].Real;
            sum += arrivals[n];
            time[n] = (n - shift) * dt;
            rho[n] = sum;
            impedance[n] = Impedance(referenceOhms, sum);
        }
        double[]? peeled = !peel ? null
            : loss is null ? Peel(arrivals, referenceOhms)
            : PeelLossy(arrivals, referenceOhms, loss, dt, maxFrequencyHz);
        if (peel && loss is null)
            notes.Add("The peeled profile assumes a lossless line: on a lossy one the sections far from the port read off.");
        if (peel && loss is not null)
            notes.Add($"The peeled profile takes out a loss of {loss.At(maxFrequencyHz) * 1e-9 * 20 / Math.Log(10):g3} dB per ns of " +
                      $"round trip at {maxFrequencyHz / 1e9:g4} GHz, as if spread evenly along the line.");
        return new TdrProfile(time, rho, impedance, peeled, rise, referenceOhms, notes);
    }

    /// <summary>TDR looking into one port of a channel, the others in their reference.</summary>
    public static TdrProfile Profile(ImportedChannel channel, int port, double? riseTimeSeconds = null,
        bool peel = true, double? maxTimeSeconds = null, TdrLoss? loss = null) =>
        Profile(f => channel.At(f)[port, port], channel.MaxFrequencyHz, channel.FinestStepHz,
            channel.ReferenceOhms, riseTimeSeconds, peel, maxTimeSeconds, loss);

    /// <summary>Differential TDR looking into the near pair of a four-port channel
    /// (S_dd11, referred to twice the single-ended reference).</summary>
    public static TdrProfile DifferentialProfile(ImportedChannel channel, DifferentialPairing pairing,
        double? riseTimeSeconds = null, bool peel = true, double? maxTimeSeconds = null, TdrLoss? loss = null) =>
        Profile(f => MixedMode.FromSingleEnded(channel.At(f), pairing)[0, 0], channel.MaxFrequencyHz,
            channel.FinestStepHz, 2 * channel.ReferenceOhms, riseTimeSeconds, peel, maxTimeSeconds, loss);

    private static double Impedance(double reference, double rho) =>
        reference * (1 + rho) / Math.Max(1e-9, 1 - Math.Min(rho, 1 - 1e-9));

    /// <summary>
    /// Layer peeling on the arrivals of an impulse: the first arrival over the first
    /// incident sample is the first interface's reflection coefficient; with it the waves
    /// are carried across that interface and one sample further, and the next interface is
    /// read the same way.
    /// </summary>
    internal static double[] Peel(double[] arrivals, double referenceOhms)
    {
        int n = arrivals.Length;
        var forward = new double[n];
        var backward = (double[])arrivals.Clone();
        forward[0] = 1;
        var impedance = new double[n];
        double z = referenceOhms;
        for (int k = 0; k < n; k++)
        {
            int length = n - k;
            double gamma = forward[0] != 0 ? Math.Clamp(backward[0] / forward[0], -0.999, 0.999) : 0;
            z *= (1 + gamma) / (1 - gamma);
            impedance[k] = z;
            double scale = 1 / (1 - gamma);
            for (int i = 0; i < length; i++)
            {
                double a = forward[i], b = backward[i];
                forward[i] = (a - gamma * b) * scale;
                backward[i] = (b - gamma * a) * scale;
            }
            // The backward wave is seen one round-trip sample earlier in the next layer.
            for (int i = 0; i + 1 < length; i++) backward[i] = backward[i + 1];
            if (length > 0) backward[length - 1] = 0;
        }
        return impedance;
    }

    /// <summary>
    /// Layer peeling with each piece's loss taken back (see the class remarks). Both waves
    /// are kept as spectra on a grid at least twice the profile's length, so the per-piece
    /// filter z/H² is one multiplication and the first time samples the recursion reads are
    /// sums. |1/H²| = e^{α(f)·dt} per piece (half the round trip each way), held at its value
    /// at the top of the data's band above it, with the minimum phase that goes with it
    /// (folded cepstrum).
    /// </summary>
    internal static double[] PeelLossy(double[] arrivals, double referenceOhms, TdrLoss loss, double dt, double bandHz)
    {
        int n = arrivals.Length;
        int size = 1;
        while (size < 2 * n) size <<= 1;

        var logMagnitude = new Complex[size];
        for (int k = 0; k <= size / 2; k++)
        {
            double f = Math.Min(k / (size * dt), bandHz);
            logMagnitude[k] = loss.At(f) * dt;
            if (k > 0 && k < size / 2) logMagnitude[size - k] = logMagnitude[k];
        }
        var cepstrum = Fft.Inverse(logMagnitude);
        var folded = new Complex[size];
        folded[0] = cepstrum[0].Real;
        for (int k = 1; k < size / 2; k++) folded[k] = 2 * cepstrum[k].Real;
        folded[size / 2] = cepstrum[size / 2].Real;
        var logFilter = Fft.Forward(folded);
        var step = new Complex[size];
        for (int k = 0; k < size; k++)
            step[k] = Complex.Exp(logFilter[k]) * Complex.FromPolarCoordinates(1, 2 * Math.PI * k / size);

        var backwardTime = new Complex[size];
        for (int k = 0; k < n; k++) backwardTime[k] = arrivals[k];
        var backward = Fft.Forward(backwardTime);
        var forward = new Complex[size];
        for (int k = 0; k < size; k++) forward[k] = 1;   // a unit impulse at t = 0

        var impedance = new double[n];
        double z = referenceOhms;
        for (int layer = 0; layer < n; layer++)
        {
            Complex f0 = 0, b0 = 0;
            for (int k = 0; k < size; k++) { f0 += forward[k]; b0 += backward[k]; }
            double gamma = f0.Real != 0 ? Math.Clamp(b0.Real / f0.Real, -0.999, 0.999) : 0;
            z *= (1 + gamma) / (1 - gamma);
            impedance[layer] = z;
            double scale = 1 / (1 - gamma);
            for (int k = 0; k < size; k++)
            {
                Complex a = forward[k], b = backward[k];
                forward[k] = (a - gamma * b) * scale;
                backward[k] = (b - gamma * a) * scale * step[k];
            }
        }
        return impedance;
    }
}
