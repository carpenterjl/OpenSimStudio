using OpenSim.Core.PostProcessing;

namespace OpenSim.Tests.PostProcessing;

/// <summary>
/// The field-overlay color scaling (SI Stage S1). The math is display-only but user-
/// facing: a wrong normalization silently misrepresents field levels, so the mapping
/// identities are pinned exactly. The RF back-compat gate matters most — an auto-ranged
/// log overlay must match the arrow/slice visuals' u = 1 + log₁₀(v/max)/decades so one
/// mental legend serves every RF view.
/// </summary>
public class FieldScaleTests
{
    [Fact]
    public void Linear_MapsRangeAffinely_AndClamps()
    {
        var scale = new FieldScale(FieldScaleMode.Linear, 2.0, 10.0);
        Assert.Equal(0.0, scale.Normalize(2.0), 12);
        Assert.Equal(1.0, scale.Normalize(10.0), 12);
        Assert.Equal(0.5, scale.Normalize(6.0), 12);
        Assert.Equal(0.0, scale.Normalize(-5.0), 12);   // below range clamps
        Assert.Equal(1.0, scale.Normalize(1e9), 12);    // above range clamps
    }

    [Fact]
    public void Log_WithMinAtMaxOverDecades_MatchesClassicRfNormalization()
    {
        // u = 1 + log10(v/max)/decades — the arrow/slice visuals' formula.
        const double max = 50.0;
        const int decades = 3;
        var scale = new FieldScale(FieldScaleMode.Logarithmic, max / 1e3, max, decades);
        foreach (double v in new[] { 0.06, 0.5, 3.0, 17.0, 50.0 })
        {
            double classic = Math.Clamp(1 + Math.Log10(v / max) / decades, 0, 1);
            Assert.Equal(classic, scale.Normalize(v), 12);
        }
    }

    [Fact]
    public void Log_UserRange_MapsLogAffinely()
    {
        var scale = new FieldScale(FieldScaleMode.Logarithmic, 1.0, 100.0);
        Assert.Equal(0.0, scale.Normalize(1.0), 12);
        Assert.Equal(0.5, scale.Normalize(10.0), 12);
        Assert.Equal(1.0, scale.Normalize(100.0), 12);
        Assert.Equal(0.0, scale.Normalize(0.0), 12);    // zero/negative → floor, not NaN
        Assert.Equal(0.0, scale.Normalize(-3.0), 12);
        Assert.Equal(1.0, scale.Normalize(1e6), 12);
    }

    [Fact]
    public void Log_NonPositiveOrInvertedMin_FallsBackToDecadesBelowPeak()
    {
        var zeroMin = new FieldScale(FieldScaleMode.Logarithmic, 0.0, 100.0, Decades: 2);
        Assert.Equal(1.0, zeroMin.EffectiveMin, 12);            // 100/10²
        Assert.Equal(0.5, zeroMin.Normalize(10.0), 12);

        var inverted = new FieldScale(FieldScaleMode.Logarithmic, 200.0, 100.0, Decades: 2);
        Assert.Equal(1.0, inverted.EffectiveMin, 12);
        // Degenerate display state must never throw or emit NaN.
        Assert.True(double.IsFinite(inverted.Normalize(5.0)));
    }

    [Fact]
    public void Auto_Linear_SpansDataMinMax()
    {
        var scale = FieldScale.Auto(FieldScaleMode.Linear, new[] { 3.0, 9.0, 6.0 });
        Assert.Equal(3.0, scale.Min, 12);
        Assert.Equal(9.0, scale.Max, 12);
        Assert.Equal(0.5, scale.Normalize(6.0), 12);
    }

    [Fact]
    public void Auto_Log_SpansTopDecadesBelowPeak()
    {
        var scale = FieldScale.Auto(FieldScaleMode.Logarithmic, new[] { 0.001, 2.0, 80.0 }, decades: 4);
        Assert.Equal(80.0, scale.Max, 12);
        Assert.Equal(80.0 / 1e4, scale.Min, 12);
        Assert.Equal(1.0, scale.Normalize(80.0), 12);
        Assert.Equal(0.0, scale.Normalize(80.0 / 1e4), 12);
    }

    [Fact]
    public void Auto_EmptyOrAllZeroData_DegradesToHarmlessRange()
    {
        foreach (var data in new[] { Array.Empty<double>(), new[] { 0.0, 0.0 } })
        {
            var scale = FieldScale.Auto(FieldScaleMode.Logarithmic, data);
            Assert.True(double.IsFinite(scale.Normalize(0.0)));
            Assert.True(double.IsFinite(scale.Normalize(5.0)));
        }
    }
}
