using OpenSim.Core.PostProcessing;

namespace OpenSim.Tests.PostProcessing;

/// <summary>
/// <see cref="FieldScale.ValueAt"/> — the inverse mapping that puts numbers on a legend —
/// and the identity that lets ONE scale serve both the RF overlays and the FE result
/// views. Before Phase 6 the FE path carried its own affine range type
/// (SceneBuilder.ScalarRange); these tests pin that FieldScale in linear mode is that
/// formula exactly, including its degenerate behaviour, so replacing it changes no pixel.
/// </summary>
public class FieldScaleInverseTests
{
    /// <summary>The retired SceneBuilder.ScalarRange.Normalize, verbatim, as the oracle.
    /// The test project cannot reference the WPF app, so the formula lives here as data.</summary>
    private static double LegacyScalarRangeNormalize(double min, double max, double value)
    {
        double range = max - min;
        if (range <= 0) return 0;
        return Math.Clamp((value - min) / range, 0, 1);
    }

    [Fact]
    public void Linear_IsTheLegacyScalarRangeFormula_AcrossAndOutsideTheRange()
    {
        foreach (var (min, max) in new[]
                 {
                     (0.0, 1.0), (2.0, 10.0), (-40.0, 120.0), (1e-9, 4e-9), (293.15, 373.15),
                     (5.0, 5.0),        // degenerate: both must return 0, not NaN or 1
                     (7.0, 3.0)         // inverted: same
                 })
        {
            var scale = new FieldScale(FieldScaleMode.Linear, min, max);
            foreach (double v in new[] { min - 13.7, min, min + 0.25 * (max - min), 0.5 * (min + max), max, max + 91.3, 0.0 })
                Assert.Equal(LegacyScalarRangeNormalize(min, max, v), scale.Normalize(v), 15);
        }
    }

    [Fact]
    public void Linear_ClampSlider_MatchesTheLegacyClampedRange()
    {
        // The result view's "clamp" spends the whole colormap on the low part of the
        // range: max' = min + fraction * span. That is just another linear FieldScale.
        const double min = -20, max = 180;
        foreach (double fraction in new[] { 0.1, 0.5, 0.9, 1.0 })
        {
            double clampedMax = min + fraction * (max - min);
            var scale = new FieldScale(FieldScaleMode.Linear, min, clampedMax);
            foreach (double v in new[] { -50.0, -20.0, 0.0, 42.0, 180.0, 500.0 })
                Assert.Equal(LegacyScalarRangeNormalize(min, clampedMax, v), scale.Normalize(v), 15);
        }
    }

    [Fact]
    public void ValueAt_InvertsNormalize_Linear()
    {
        var scale = new FieldScale(FieldScaleMode.Linear, 293.15, 421.7);
        foreach (double v in new[] { 293.15, 300.0, 355.4, 421.7 })
            Assert.Equal(v, scale.ValueAt(scale.Normalize(v)), 9);
        for (int i = 0; i <= 10; i++)
        {
            double u = i / 10.0;
            Assert.Equal(u, scale.Normalize(scale.ValueAt(u)), 12);
        }
    }

    [Fact]
    public void ValueAt_InvertsNormalize_Logarithmic()
    {
        var scale = new FieldScale(FieldScaleMode.Logarithmic, 1e-3, 50.0, Decades: 4);
        foreach (double v in new[] { 1e-3, 0.02, 1.7, 50.0 })
            Assert.Equal(v, scale.ValueAt(scale.Normalize(v)), 12);
        for (int i = 0; i <= 10; i++)
        {
            double u = i / 10.0;
            Assert.Equal(u, scale.Normalize(scale.ValueAt(u)), 12);
        }
        Assert.Equal(1e-3, scale.ValueAt(0.0), 12);
        Assert.Equal(50.0, scale.ValueAt(1.0), 12);
    }

    [Fact]
    public void ValueAt_UsesTheEffectiveLowerBound_SoLegendTicksMatchTheColors()
    {
        // Log mode substitutes Max/10^Decades for a non-positive Min; the legend's bottom
        // tick must read that substituted value, not the meaningless 0.
        var scale = new FieldScale(FieldScaleMode.Logarithmic, 0.0, 100.0, Decades: 2);
        Assert.Equal(1.0, scale.ValueAt(0.0), 12);
        Assert.Equal(10.0, scale.ValueAt(0.5), 12);
        Assert.Equal(100.0, scale.ValueAt(1.0), 12);
    }

    [Fact]
    public void ValueAt_ClampsAndStaysFiniteOnDegenerateRanges()
    {
        var scale = new FieldScale(FieldScaleMode.Linear, 2.0, 10.0);
        Assert.Equal(2.0, scale.ValueAt(-1.0), 12);
        Assert.Equal(10.0, scale.ValueAt(7.0), 12);
        Assert.Equal(2.0, scale.ValueAt(double.NaN), 12);

        foreach (var bad in new[]
                 {
                     new FieldScale(FieldScaleMode.Linear, 5.0, 5.0),
                     new FieldScale(FieldScaleMode.Linear, 9.0, 1.0),
                     new FieldScale(FieldScaleMode.Logarithmic, 0.0, 0.0),
                     new FieldScale(FieldScaleMode.Logarithmic, -4.0, -1.0)
                 })
            foreach (double u in new[] { 0.0, 0.5, 1.0 })
                Assert.True(double.IsFinite(bad.ValueAt(u)), $"{bad} at u = {u}");
    }
}
