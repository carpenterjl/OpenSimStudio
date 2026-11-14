using System.Globalization;

namespace OpenSim.Core.Plotting;

/// <summary>
/// Tick positions and labels for plot axes: round steps of 1, 2 or 5 times a power of ten on
/// a linear axis, the decades on a logarithmic one. The step rule is the one
/// <c>CurvePlot</c> and <c>FieldMapPlot</c> already use, so the interactive plots tick their
/// axes the same way as the static ones.
/// </summary>
public static class AxisTicks
{
    /// <summary>The round step (1, 2, 5 or 10 times a power of ten) nearest above
    /// <paramref name="span"/> / <paramref name="target"/>. NaN when the span is not a positive
    /// finite number.</summary>
    public static double NiceStep(double span, int target)
    {
        if (!(span > 0) || !double.IsFinite(span) || target < 1) return double.NaN;
        double raw = span / target;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return (raw / magnitude) switch
        {
            < 1.5 => magnitude,
            < 3.5 => 2 * magnitude,
            < 7.5 => 5 * magnitude,
            _ => 10 * magnitude
        };
    }

    /// <summary>
    /// The multiples of <see cref="NiceStep"/> inside [<paramref name="from"/>, <paramref name="to"/>]
    /// (either order), ascending. Each tick is an integer times the step rather than a running sum,
    /// so a long axis does not accumulate rounding, and a tick within 1e-9 steps of zero is exactly
    /// zero. A zero-width range gives the single value; a non-finite one gives none.
    /// </summary>
    public static IReadOnlyList<double> Linear(double from, double to, int target = 6)
    {
        if (!double.IsFinite(from) || !double.IsFinite(to)) return Array.Empty<double>();
        if (from > to) (from, to) = (to, from);
        if (from == to) return new[] { from };
        double step = NiceStep(to - from, target);
        long first = (long)Math.Ceiling(from / step - 1e-9);
        long last = (long)Math.Floor(to / step + 1e-9);
        var ticks = new List<double>((int)Math.Max(0, Math.Min(last - first + 1, 1000)));
        for (long k = first; k <= last && ticks.Count < 1000; k++)
            ticks.Add(k == 0 ? 0 : k * step);
        return ticks;
    }

    /// <summary>The powers of ten inside [<paramref name="from"/>, <paramref name="to"/>] (data
    /// units, both positive), ascending. Empty when either bound is not positive.</summary>
    public static IReadOnlyList<double> Decades(double from, double to)
    {
        if (from > to) (from, to) = (to, from);
        if (!(from > 0) || !double.IsFinite(to)) return Array.Empty<double>();
        int first = (int)Math.Ceiling(Math.Log10(from) - 1e-9);
        int last = (int)Math.Floor(Math.Log10(to) + 1e-9);
        var ticks = new List<double>();
        for (int k = first; k <= last && ticks.Count < 400; k++) ticks.Add(Math.Pow(10, k));
        return ticks;
    }

    /// <summary>
    /// A tick label with as many decimals as the step needs and no more (step 0.25 → "0.25",
    /// step 2 → "4"). Very large or very small magnitudes switch to scientific notation so a label
    /// never runs to a dozen digits. Invariant culture: plots are read beside exported CSV.
    /// </summary>
    public static string Format(double value, double step)
    {
        if (!double.IsFinite(value)) return value.ToString(CultureInfo.InvariantCulture);
        if (value == 0) return "0";
        double magnitude = Math.Abs(value);
        if (!(step > 0) || !double.IsFinite(step)) step = magnitude;
        if (magnitude >= 1e6 || step < 1e-4)
            return value.ToString("0.###E+0", CultureInfo.InvariantCulture);
        int decimals = (int)Math.Clamp(-Math.Floor(Math.Log10(step) + 1e-9), 0, 10);
        // Steps of 2.5 x 10^n (never produced by NiceStep, but passed by callers with their own
        // steps) need one more digit than their power of ten.
        if (Math.Abs(Math.Round(step / Math.Pow(10, -decimals)) * Math.Pow(10, -decimals) - step) > 1e-9 * step)
            decimals = Math.Min(decimals + 1, 10);
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    /// <summary>A readout number: six significant figures, invariant culture.</summary>
    public static string Readout(double value) =>
        value.ToString("g6", CultureInfo.InvariantCulture);
}
