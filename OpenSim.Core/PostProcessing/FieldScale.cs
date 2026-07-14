namespace OpenSim.Core.PostProcessing;

/// <summary>How a field overlay maps a scalar sample onto the 0..1 colormap axis.</summary>
public enum FieldScaleMode
{
    Linear,
    Logarithmic
}

/// <summary>
/// Scalar → [0, 1] colormap normalization for field overlays, with user-settable range.
/// Linear maps [Min, Max] affinely; logarithmic maps [log₁₀ Min, log₁₀ Max] affinely —
/// with Min = Max/10^decades that is exactly the classic u = 1 + log₁₀(v/max)/decades
/// used by the RF arrow/slice visuals, so an auto-ranged log overlay matches them.
/// Values outside the range clamp (the legend marks the ends); a non-positive log Min
/// falls back to Max/10^<see cref="Decades"/> rather than producing NaN — the range is
/// display state and must never make the renderer throw.
/// </summary>
public sealed record FieldScale(FieldScaleMode Mode, double Min, double Max, int Decades = 3)
{
    /// <summary>An auto range from the data: linear spans [min, max] of the samples;
    /// logarithmic spans the top <paramref name="decades"/> decades below the peak
    /// (the RF visuals' convention — the noise floor decades below carries no color).</summary>
    public static FieldScale Auto(FieldScaleMode mode, IReadOnlyList<double> values, int decades = 3)
    {
        double max = 0, min = double.MaxValue;
        foreach (var v in values)
        {
            if (v > max) max = v;
            if (v < min) min = v;
        }
        if (values.Count == 0 || max <= 0) return new FieldScale(mode, 0, 1, decades);
        return mode == FieldScaleMode.Logarithmic
            ? new FieldScale(mode, max / Math.Pow(10, decades), max, decades)
            : new FieldScale(mode, Math.Min(min, max), max, decades);
    }

    /// <summary>The effective lower bound actually used: log mode substitutes
    /// Max/10^Decades when Min is non-positive or not below Max.</summary>
    public double EffectiveMin => Mode == FieldScaleMode.Logarithmic && (Min <= 0 || Min >= Max)
        ? Max / Math.Pow(10, Math.Max(1, Decades))
        : Min;

    /// <summary>Maps one sample to the 0..1 colormap coordinate, clamped.</summary>
    public double Normalize(double value)
    {
        if (Max <= 0 && Mode == FieldScaleMode.Logarithmic) return 0;
        double lo = EffectiveMin, hi = Max;
        if (Mode == FieldScaleMode.Logarithmic)
        {
            if (value <= 0) return 0;
            return Math.Clamp((Math.Log10(value) - Math.Log10(lo))
                              / (Math.Log10(hi) - Math.Log10(lo)), 0, 1);
        }
        if (hi <= lo) return 0;
        return Math.Clamp((value - lo) / (hi - lo), 0, 1);
    }
}
