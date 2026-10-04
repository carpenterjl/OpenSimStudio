namespace OpenSim.Rf.Si;

/// <summary>
/// An eye diagram folded from one period of a periodic waveform (SI Stage S5): traces
/// of 2 UI starting at every unit interval, a density map for rendering, and the three
/// classic metrics — eye height (vertical opening at the sampling phase), eye width (UI
/// minus the peak-to-peak crossing jitter), and the crossing jitter.
///
/// <para><b>Which samples are ones.</b> The height is the lowest received ONE minus the
/// highest received ZERO, and what makes a sample a one is the bit that was SENT, not the
/// level that arrived. Sorting samples by their received level against a threshold (the
/// earlier rule, still what the overload without bits does) measures the gap around the
/// threshold, which is positive by construction: a channel whose isolated one reaches 0.45
/// and whose isolated zero only falls to 0.55 was reported as a 0.10 V opening when it is a
/// 0.10 V overlap. With the transmitted bits the height is allowed to go negative, and a
/// non-positive height is a CLOSED eye.</para>
/// </summary>
public sealed record EyeDiagram(
    int SamplesPerUi,
    double UnitIntervalSeconds,
    IReadOnlyList<double[]> Traces,
    double EyeHeight,
    double EyeWidthSeconds,
    double JitterPeakToPeakSeconds,
    double Low,
    double High)
{
    /// <summary>True when the height was measured against the transmitted bits. False for the
    /// level-classified overload, whose height cannot be zero or negative and so cannot show
    /// a closed eye.</summary>
    public bool ClassifiedByTransmittedBits { get; init; }

    /// <summary>The eye is closed: at the best sampling phase some received zero is at or above
    /// some received one. Only meaningful when <see cref="ClassifiedByTransmittedBits"/>.</summary>
    public bool IsClosed => ClassifiedByTransmittedBits && EyeHeight <= 0;

    /// <summary>The channel latency found by correlating the received waveform against the
    /// transmitted bits (modulo the pattern period).</summary>
    public double LatencySeconds { get; init; }

    /// <summary>Where in the received unit interval the height was taken, as a fraction of a
    /// UI from the start of the received bit.</summary>
    public double SamplingPhaseUi { get; init; }

    /// <summary>Threshold crossings found in the period.</summary>
    public int CrossingCount { get; init; }

    /// <summary>Folds a periodic waveform of whole unit intervals into an eye, classifying
    /// samples by their RECEIVED level. Kept for waveforms whose transmitted bits are not
    /// known; it cannot report a closed eye (see the class remarks).</summary>
    public static EyeDiagram Fold(IReadOnlyList<double> periodicWaveform, int samplesPerUi,
        double sampleIntervalSeconds) =>
        FoldCore(periodicWaveform, samplesPerUi, sampleIntervalSeconds, null);

    /// <summary>Folds a periodic waveform into an eye and measures it against the bits that
    /// were transmitted (one per unit interval, in order, the pattern the waveform is one
    /// period of). The channel's delay is found by correlation, so the waveform need not be
    /// aligned to the bits.</summary>
    public static EyeDiagram Fold(IReadOnlyList<double> periodicWaveform, int samplesPerUi,
        double sampleIntervalSeconds, IReadOnlyList<bool> transmittedBits)
    {
        ArgumentNullException.ThrowIfNull(transmittedBits);
        return FoldCore(periodicWaveform, samplesPerUi, sampleIntervalSeconds, transmittedBits);
    }

    private static EyeDiagram FoldCore(IReadOnlyList<double> periodicWaveform, int samplesPerUi,
        double sampleIntervalSeconds, IReadOnlyList<bool>? transmittedBits)
    {
        if (samplesPerUi < 4)
            throw new ArgumentOutOfRangeException(nameof(samplesPerUi),
                "At least 4 samples per UI are needed to measure an eye.");
        if (periodicWaveform.Count % samplesPerUi != 0)
            throw new ArgumentException(
                "The waveform must hold a whole number of unit intervals (it is one "
                + "period of the periodic steady state).", nameof(periodicWaveform));
        int bits = periodicWaveform.Count / samplesPerUi;
        if (bits < 2)
            throw new ArgumentException("At least two unit intervals are required.");
        if (transmittedBits is not null && transmittedBits.Count != bits)
            throw new ArgumentException(
                $"The waveform holds {bits} unit intervals but {transmittedBits.Count} transmitted "
                + "bits were given; they must be the same pattern.", nameof(transmittedBits));

        int total = periodicWaveform.Count;
        double ui = samplesPerUi * sampleIntervalSeconds;

        // Fold: one 2-UI trace per unit interval (periodic wrap).
        var traces = new List<double[]>(bits);
        for (int b = 0; b < bits; b++)
        {
            var trace = new double[2 * samplesPerUi];
            int start = b * samplesPerUi;
            for (int s = 0; s < trace.Length; s++)
                trace[s] = periodicWaveform[(start + s) % total];
            traces.Add(trace);
        }

        double low = double.MaxValue, high = double.MinValue;
        foreach (var v in periodicWaveform)
        {
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }
        double threshold = 0.5 * (low + high);

        // Threshold crossings as phases in [0, UI), interpolated between samples, then
        // centered on their circular mean — the crossing cluster of a real eye wraps
        // the phase origin, so a plain min/max spread would misread it. A pair counts when
        // the waveform is below the threshold at one sample and at or above it at the other
        // (half-open), so a sample landing EXACTLY on the threshold is one crossing, not
        // none: the strict test used before dropped both pairs around such a sample.
        var phases = new List<double>();
        for (int n = 0; n < total; n++)
        {
            double a = periodicWaveform[n] - threshold;
            double b = periodicWaveform[(n + 1) % total] - threshold;
            if ((a < 0) == (b < 0)) continue;
            double crossing = (n + a / (a - b)) % samplesPerUi;
            phases.Add(crossing / samplesPerUi);        // fraction of a UI
        }

        double jitterPp = 0, meanPhase = 0;
        if (phases.Count > 0)
        {
            double sx = 0, sy = 0;
            foreach (var p in phases)
            {
                sx += Math.Cos(2 * Math.PI * p);
                sy += Math.Sin(2 * Math.PI * p);
            }
            meanPhase = Math.Atan2(sy, sx) / (2 * Math.PI);
            if (meanPhase < 0) meanPhase += 1;
            double minDev = 0, maxDev = 0;
            foreach (var p in phases)
            {
                double dev = p - meanPhase;
                dev -= Math.Round(dev);                 // wrap to (−0.5, 0.5]
                minDev = Math.Min(minDev, dev);
                maxDev = Math.Max(maxDev, dev);
            }
            jitterPp = (maxDev - minDev) * ui;
        }
        double eyeWidth = Math.Max(0, ui - jitterPp);

        if (transmittedBits is not null)
            return MeasureAgainstBits(periodicWaveform, samplesPerUi, sampleIntervalSeconds,
                transmittedBits, traces, low, high, eyeWidth, jitterPp, phases.Count);

        // Eye height at the sampling phase (mean crossing + UI/2): the vertical gap
        // between the lowest "high" trace and the highest "low" trace there.
        double samplingPhase = (meanPhase + 0.5) % 1.0;
        int samplingIndex = (int)Math.Round(samplingPhase * samplesPerUi) % samplesPerUi;
        double minTop = double.MaxValue, maxBottom = double.MinValue;
        for (int b = 0; b < bits; b++)
        {
            double v = periodicWaveform[(b * samplesPerUi + samplingIndex) % total];
            if (v >= threshold) minTop = Math.Min(minTop, v);
            else maxBottom = Math.Max(maxBottom, v);
        }
        // A constant pattern has one polarity only — the eye is the full swing side.
        double eyeHeight = minTop == double.MaxValue || maxBottom == double.MinValue
            ? high - low
            : Math.Max(0, minTop - maxBottom);

        return new EyeDiagram(samplesPerUi, ui, traces, eyeHeight, eyeWidth, jitterPp, low, high)
        {
            SamplingPhaseUi = samplingPhase, CrossingCount = phases.Count
        };
    }

    /// <summary>Height by transmitted bit. The latency is the circular shift that best lines
    /// the received waveform up with the transmitted pattern (an integrate-and-dump correlation
    /// over every bit shift and every sample offset); the height is then taken at the sampling
    /// offset within the received bit where it is largest, which is what a receiver's clock
    /// recovery would settle on.</summary>
    private static EyeDiagram MeasureAgainstBits(IReadOnlyList<double> wave, int samplesPerUi,
        double dt, IReadOnlyList<bool> sent, List<double[]> traces, double low, double high,
        double eyeWidth, double jitterPp, int crossings)
    {
        int bits = sent.Count, total = wave.Count;
        double ui = samplesPerUi * dt;

        int ones = 0;
        foreach (bool bit in sent) if (bit) ones++;
        if (ones == 0 || ones == bits)
            // One polarity only: there is no eye to close; report the swing, as before.
            return new EyeDiagram(samplesPerUi, ui, traces, high - low, eyeWidth, jitterPp, low, high)
            {
                ClassifiedByTransmittedBits = true, CrossingCount = crossings
            };

        double mean = 0;
        foreach (var v in wave) mean += v;
        mean /= total;

        // S[offset][b] = Σ of the samples of the UI that starts at b·spu + offset.
        // Running sum over the wrapped waveform makes each one O(1).
        var prefix = new double[2 * total + 1];
        for (int i = 0; i < 2 * total; i++) prefix[i + 1] = prefix[i] + (wave[i % total] - mean);

        double best = double.MinValue;
        int bestShift = 0, bestOffset = 0;
        var window = new double[bits];
        for (int offset = 0; offset < samplesPerUi; offset++)
        {
            for (int b = 0; b < bits; b++)
            {
                int start = b * samplesPerUi + offset;
                window[b] = prefix[start + samplesPerUi] - prefix[start];
            }
            for (int shift = 0; shift < bits; shift++)
            {
                double correlation = 0;
                for (int b = 0; b < bits; b++)
                {
                    int received = b + shift;
                    if (received >= bits) received -= bits;
                    correlation += sent[b] ? window[received] : -window[received];
                }
                if (correlation > best)
                    (best, bestShift, bestOffset) = (correlation, shift, offset);
            }
        }
        int latency = bestShift * samplesPerUi + bestOffset;

        double height = double.MinValue;
        int bestPhase = 0;
        for (int phase = 0; phase < samplesPerUi; phase++)
        {
            double minOne = double.MaxValue, maxZero = double.MinValue;
            for (int b = 0; b < bits; b++)
            {
                double v = wave[(b * samplesPerUi + latency + phase) % total];
                if (sent[b]) minOne = Math.Min(minOne, v);
                else maxZero = Math.Max(maxZero, v);
            }
            if (minOne - maxZero > height) (height, bestPhase) = (minOne - maxZero, phase);
        }

        return new EyeDiagram(samplesPerUi, ui, traces, height,
            height > 0 ? eyeWidth : 0, jitterPp, low, high)
        {
            ClassifiedByTransmittedBits = true,
            LatencySeconds = latency * dt,
            SamplingPhaseUi = (double)bestPhase / samplesPerUi,
            CrossingCount = crossings
        };
    }

    /// <summary>The eye as a column-major density map (width 2·SamplesPerUi bins,
    /// <paramref name="heightBins"/> vertical bins over [Low, High] padded 5%) — the
    /// renderer turns this into a persistence bitmap; counts are draw-order-free.</summary>
    public int[,] DensityMap(int heightBins = 128)
    {
        int width = 2 * SamplesPerUi;
        var map = new int[width, heightBins];
        double pad = 0.05 * Math.Max(High - Low, 1e-30);
        double bottom = Low - pad, span = High - Low + 2 * pad;
        foreach (var trace in Traces)
            for (int x = 0; x < width; x++)
            {
                int y = (int)((trace[x] - bottom) / span * (heightBins - 1));
                map[x, Math.Clamp(y, 0, heightBins - 1)]++;
            }
        return map;
    }
}
