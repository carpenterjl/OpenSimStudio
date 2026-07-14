namespace OpenSim.Rf.Si;

/// <summary>
/// An eye diagram folded from one period of a periodic waveform (SI Stage S5): traces
/// of 2 UI starting at every unit interval, a density map for rendering, and the three
/// classic metrics measured on the folded traces themselves — eye height (vertical
/// opening at the sampling phase), eye width (UI minus the peak-to-peak crossing
/// jitter), and the crossing jitter. The threshold is the waveform mid-level; the
/// sampling phase sits half a UI after the mean crossing.
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
    /// <summary>Folds a periodic waveform of whole unit intervals into an eye.</summary>
    public static EyeDiagram Fold(IReadOnlyList<double> periodicWaveform, int samplesPerUi,
        double sampleIntervalSeconds)
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
        // the phase origin, so a plain min/max spread would misread it.
        var phases = new List<double>();
        for (int n = 0; n < total; n++)
        {
            double a = periodicWaveform[n] - threshold;
            double b = periodicWaveform[(n + 1) % total] - threshold;
            if (a == 0 || a * b >= 0) continue;
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

        return new EyeDiagram(samplesPerUi, ui, traces, eyeHeight, eyeWidth, jitterPp, low, high);
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
