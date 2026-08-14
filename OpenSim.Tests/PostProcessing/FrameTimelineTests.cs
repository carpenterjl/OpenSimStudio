using OpenSim.Core.PostProcessing;

namespace OpenSim.Tests.PostProcessing;

/// <summary>
/// Timeline arithmetic behind the draggable result timeline (Phase 6). The scrubber drags
/// a continuous axis value, so the mapping back to frames has to be exact at the ends and
/// reproducible in the middle: a tie that resolved differently on two consecutive playback
/// ticks would make the animation stutter between two frames.
/// </summary>
public class FrameTimelineTests
{
    // Deliberately NON-uniform: a transient solve that halves its step mid-run still has
    // to scrub correctly, which an index-proportional slider could never do.
    private static readonly double[] Frames = { 0.0, 1.0, 3.0, 4.0, 8.0 };

    [Fact]
    public void NearestFrameIndex_SnapsToTheClosestFrame()
    {
        Assert.Equal(0, FrameTimeline.NearestFrameIndex(Frames, 0.0));
        Assert.Equal(0, FrameTimeline.NearestFrameIndex(Frames, 0.4));
        Assert.Equal(1, FrameTimeline.NearestFrameIndex(Frames, 0.6));
        Assert.Equal(1, FrameTimeline.NearestFrameIndex(Frames, 1.9));
        Assert.Equal(2, FrameTimeline.NearestFrameIndex(Frames, 2.1));
        Assert.Equal(3, FrameTimeline.NearestFrameIndex(Frames, 4.0));
        Assert.Equal(3, FrameTimeline.NearestFrameIndex(Frames, 5.9));
        Assert.Equal(4, FrameTimeline.NearestFrameIndex(Frames, 6.1));
        Assert.Equal(4, FrameTimeline.NearestFrameIndex(Frames, 8.0));
    }

    [Fact]
    public void NearestFrameIndex_ResolvesExactTiesToTheLowerFrame()
    {
        Assert.Equal(0, FrameTimeline.NearestFrameIndex(Frames, 0.5));   // between 0 and 1
        Assert.Equal(1, FrameTimeline.NearestFrameIndex(Frames, 2.0));   // between 1 and 3
        Assert.Equal(3, FrameTimeline.NearestFrameIndex(Frames, 6.0));   // between 4 and 8
    }

    [Fact]
    public void NearestFrameIndex_ClampsOutsideTheAxis()
    {
        Assert.Equal(0, FrameTimeline.NearestFrameIndex(Frames, -1e6));
        Assert.Equal(4, FrameTimeline.NearestFrameIndex(Frames, 1e6));
        Assert.Equal(0, FrameTimeline.NearestFrameIndex(Frames, double.NaN));
        Assert.Equal(0, FrameTimeline.NearestFrameIndex(new[] { 2.5 }, 99.0));
    }

    [Fact]
    public void NearestFrameIndex_RejectsEmptyOrOutOfOrderAxes()
    {
        Assert.Throws<ArgumentException>(() => FrameTimeline.NearestFrameIndex(Array.Empty<double>(), 0));
        Assert.Throws<ArgumentException>(() => FrameTimeline.NearestFrameIndex(new[] { 0.0, 2.0, 1.0 }, 0));
    }

    [Fact]
    public void Advance_MovesInsideTheRangeWithoutWrapping()
    {
        double t = FrameTimeline.Advance(2.0, 0.5, 0.0, 8.0, loop: false, out bool wrapped);
        Assert.Equal(2.5, t, 12);
        Assert.False(wrapped);
    }

    [Fact]
    public void Advance_ClampsAtTheEndWhenNotLooping()
    {
        double t = FrameTimeline.Advance(7.9, 0.5, 0.0, 8.0, loop: false, out bool wrapped);
        Assert.Equal(8.0, t, 12);
        Assert.True(wrapped);      // the caller stops playback on this flag

        double back = FrameTimeline.Advance(0.1, -0.5, 0.0, 8.0, loop: false, out bool wrappedBack);
        Assert.Equal(0.0, back, 12);
        Assert.True(wrappedBack);
    }

    [Fact]
    public void Advance_WrapsAroundWhenLooping_InBothDirections()
    {
        double t = FrameTimeline.Advance(7.5, 1.0, 0.0, 8.0, loop: true, out bool wrapped);
        Assert.Equal(0.5, t, 12);
        Assert.True(wrapped);

        double back = FrameTimeline.Advance(0.5, -1.0, 0.0, 8.0, loop: true, out _);
        Assert.Equal(7.5, back, 12);

        // A stalled UI thread delivers one enormous tick — it must still land in range.
        double huge = FrameTimeline.Advance(1.0, 250.0, 0.0, 8.0, loop: true, out _);
        Assert.InRange(huge, 0.0, 8.0);
        Assert.Equal(3.0, huge, 12);   // (1 + 250) mod 8 = 3
    }

    [Fact]
    public void Advance_OnADegenerateAxisStaysPut()
    {
        double t = FrameTimeline.Advance(2.5, 1.0, 2.5, 2.5, loop: true, out bool wrapped);
        Assert.Equal(2.5, t, 12);
        Assert.True(wrapped);
    }

    [Fact]
    public void TickLabels_TakeRealFrameValues_SpanTheAxis_AndStayOrdered()
    {
        var ticks = FrameTimeline.TickLabels(Frames, "s", maxLabels: 3);
        Assert.Equal(3, ticks.Count);
        Assert.Equal(0.0, ticks[0].Fraction, 12);
        Assert.Equal(1.0, ticks[^1].Fraction, 12);
        for (int i = 1; i < ticks.Count; i++)
            Assert.True(ticks[i].Fraction > ticks[i - 1].Fraction);
        Assert.All(ticks, t => Assert.InRange(t.Fraction, 0.0, 1.0));
        Assert.All(ticks, t => Assert.EndsWith("s", t.Label));

        // Every label is one of the actual frame values, never an interpolated number.
        var labels = Frames.Select(v => FrameTimeline.Format(v, "s")).ToHashSet();
        Assert.All(ticks, t => Assert.Contains(t.Label, labels));
    }

    [Fact]
    public void TickLabels_NeverExceedTheFrameCount_AndHandleASingleFrame()
    {
        var few = FrameTimeline.TickLabels(new[] { 0.0, 1.0 }, "s", maxLabels: 8);
        Assert.Equal(2, few.Count);

        var one = FrameTimeline.TickLabels(new[] { 4.2 }, "s");
        Assert.Single(one);
        Assert.Equal(0.0, one[0].Fraction, 12);

        Assert.Empty(FrameTimeline.TickLabels(Array.Empty<double>(), "s"));
    }

    [Fact]
    public void Format_OmitsTheUnitOnDimensionlessAxes()
    {
        Assert.Equal("3", FrameTimeline.Format(3, null));
        Assert.Equal("3", FrameTimeline.Format(3, ""));
        Assert.Equal("3 Hz", FrameTimeline.Format(3, "Hz"));
    }
}
