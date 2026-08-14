namespace OpenSim.Core.PostProcessing;

/// <summary>One labelled mark on a timeline, positioned as a fraction of the axis span.</summary>
/// <param name="Fraction">Position along the axis, 0 at the first frame and 1 at the last.</param>
/// <param name="Label">Formatted axis value, e.g. "2.5 s".</param>
public readonly record struct TimelineTick(double Fraction, string Label);

/// <summary>
/// Timeline arithmetic for scrubbing and playing back a multi-frame result.
/// <para>
/// The UI drags a continuous TIME, not a frame index: a transient solve's frames are
/// spaced by Δt on a real axis, so a thumb placed by index would move at a rate that has
/// nothing to do with the physics. Everything here maps between that continuous axis
/// (<see cref="Results.ResultFrame.Value"/>) and the discrete frames, keeping playback
/// speed proportional to simulated time.
/// </para>
/// Pure math with no WPF and no state — the view model owns the current time; this owns
/// the rules.
/// </summary>
public static class FrameTimeline
{
    /// <summary>The frame whose axis value is closest to <paramref name="time"/>, clamped
    /// to the ends. Exact halfway ties resolve to the LOWER frame so a scrub that lands
    /// between frames is reproducible (and so playback never skips a frame by rounding up
    /// on one tick and down on the next).</summary>
    /// <param name="values">Frame axis values, non-decreasing (the solvers emit them in order).</param>
    public static int NearestFrameIndex(IReadOnlyList<double> values, double time)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            throw new ArgumentException("A timeline needs at least one frame.", nameof(values));
        RequireAscending(values);
        if (double.IsNaN(time) || time <= values[0]) return 0;
        if (time >= values[^1]) return values.Count - 1;

        // Binary search for the first frame at or after `time`.
        int lo = 0, hi = values.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (values[mid] <= time) lo = mid; else hi = mid;
        }
        double dLo = time - values[lo], dHi = values[hi] - time;
        return dHi < dLo ? hi : lo;   // strict `<` ⇒ ties keep the lower frame
    }

    /// <summary>Advances a playback head by <paramref name="delta"/> axis units within
    /// [<paramref name="t0"/>, <paramref name="t1"/>].
    /// <paramref name="wrapped"/> reports that the head reached an end — the caller loops
    /// (the returned time has wrapped around) or stops playback (the time is clamped to
    /// the end it hit). Reverse playback is symmetric.</summary>
    public static double Advance(double time, double delta, double t0, double t1,
        bool loop, out bool wrapped)
    {
        double span = t1 - t0;
        if (!(span > 0))            // a single frame (or a degenerate axis) has nowhere to go
        {
            wrapped = true;
            return t0;
        }
        double next = time + delta;
        if (next >= t0 && next <= t1)
        {
            wrapped = false;
            return next;
        }
        wrapped = true;
        if (!loop) return next > t1 ? t1 : t0;
        // Modulo into the span; IEEERemainder-style folding keeps a large delta (a stalled
        // UI thread delivering one huge tick) inside the range instead of overshooting.
        double offset = (next - t0) % span;
        if (offset < 0) offset += span;
        return t0 + offset;
    }

    /// <summary>Up to <paramref name="maxLabels"/> evenly spaced ticks taken from the
    /// frames themselves — the first and last frame always among them, so the axis ends
    /// are labelled with real frame values rather than interpolated ones.</summary>
    /// <param name="unit">Axis unit appended to each label ("s", "Hz"); null or empty for
    /// dimensionless axes such as mode number.</param>
    public static IReadOnlyList<TimelineTick> TickLabels(IReadOnlyList<double> values,
        string? unit, int maxLabels = 5)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return Array.Empty<TimelineTick>();
        RequireAscending(values);
        if (maxLabels < 2) maxLabels = 2;

        double t0 = values[0], t1 = values[^1], span = t1 - t0;
        int count = Math.Min(maxLabels, values.Count);
        var ticks = new List<TimelineTick>(count);
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            // Sample frame indices evenly; count == 1 (a single frame) takes index 0.
            int index = count == 1 ? 0 : (int)Math.Round(i * (values.Count - 1.0) / (count - 1));
            if (!seen.Add(index)) continue;
            double v = values[index];
            double fraction = span > 0 ? (v - t0) / span : 0;
            ticks.Add(new TimelineTick(fraction, Format(v, unit)));
        }
        return ticks;
    }

    /// <summary>The axis-value label shown for the playback head.</summary>
    public static string Format(double value, string? unit) =>
        string.IsNullOrEmpty(unit) ? $"{value:g4}" : $"{value:g4} {unit}";

    private static void RequireAscending(IReadOnlyList<double> values)
    {
        for (int i = 1; i < values.Count; i++)
            if (values[i] < values[i - 1])
                throw new ArgumentException(
                    $"Frame axis values must be non-decreasing; frame {i} = {values[i]} follows " +
                    $"{values[i - 1]}.", nameof(values));
    }
}
