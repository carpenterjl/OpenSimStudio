using OpenSim.Core.Signals;

namespace OpenSim.Tests.Signals;

/// <summary>Source waveform shaping (SI Stage S2): exact ideal limits, monotone edges,
/// and the periodic wrap the periodic-steady-state transient depends on.</summary>
public class SourceWaveformTests
{
    [Fact]
    public void IdealNrz_ReproducesBitLevelsExactly()
    {
        var bits = new[] { true, false, true, true, false };
        var wave = SourceWaveform.Trapezoid(bits, samplesPerUi: 8, riseFractionOfUi: 0,
            highLevel: 0.9, lowLevel: -0.9);
        Assert.Equal(bits.Length * 8, wave.Length);
        for (int i = 0; i < bits.Length; i++)
            for (int o = 0; o < 8; o++)
                Assert.Equal(bits[i] ? 0.9 : -0.9, wave[i * 8 + o]);
    }

    [Fact]
    public void Edges_AreMonotoneAndReachTheTargetWithinTheRise()
    {
        var bits = new[] { false, true, true, false };
        const int spu = 16;
        var wave = SourceWaveform.Trapezoid(bits, spu, riseFractionOfUi: 0.25);
        int rise = 4; // 0.25 · 16

        // Rising edge into bit 1: monotone non-decreasing, then flat at the level.
        for (int o = 1; o < rise; o++)
            Assert.True(wave[spu + o] >= wave[spu + o - 1]);
        for (int o = rise; o < spu; o++)
            Assert.Equal(1.0, wave[spu + o], 12);
        // No transition into bit 2 (same value): flat throughout.
        for (int o = 0; o < spu; o++)
            Assert.Equal(1.0, wave[2 * spu + o], 12);
        // Falling edge into bit 3 mirrors the rise.
        for (int o = 1; o < rise; o++)
            Assert.True(wave[3 * spu + o] <= wave[3 * spu + o - 1]);
        for (int o = rise; o < spu; o++)
            Assert.Equal(0.0, wave[3 * spu + o], 12);
    }

    [Fact]
    public void PeriodicWrap_TransitionsBitZeroFromTheLastBit()
    {
        // …1|0…: bit 0 is low but the last bit is high, so the pattern start must ramp
        // DOWN from high — the periodic-steady-state signal has no free first edge.
        var bits = new[] { false, false, true };
        var wave = SourceWaveform.Trapezoid(bits, samplesPerUi: 8, riseFractionOfUi: 0.5);
        Assert.True(wave[0] > 0.0, "bit 0 must start mid-fall from the wrapped last bit");
        Assert.Equal(0.0, wave[7], 12);
    }

    [Fact]
    public void Clock_AlternatesEveryUnitInterval()
    {
        var wave = SourceWaveform.Clock(periods: 3, samplesPerUi: 4, riseFractionOfUi: 0);
        for (int i = 0; i < 6; i++)
            for (int o = 0; o < 4; o++)
                Assert.Equal(i % 2 == 0 ? 1.0 : 0.0, wave[i * 4 + o]);
    }

    [Fact]
    public void StepAndPulse_AreExact()
    {
        var step = SourceWaveform.Step(10, stepAt: 4, amplitude: 2.5);
        for (int i = 0; i < 10; i++) Assert.Equal(i >= 4 ? 2.5 : 0.0, step[i]);

        var pulse = SourceWaveform.Pulse(10, startAt: 3, widthSamples: 4);
        for (int i = 0; i < 10; i++) Assert.Equal(i is >= 3 and < 7 ? 1.0 : 0.0, pulse[i]);
    }
}
