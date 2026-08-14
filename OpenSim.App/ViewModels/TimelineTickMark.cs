namespace OpenSim.App.ViewModels;

/// <summary>
/// One labelled mark under the result timeline, placed at the axis fraction its frame sits
/// at (see <see cref="OpenSim.Core.PostProcessing.FrameTimeline.TickLabels"/> — the values
/// are real frame values, never interpolated).
/// <para>
/// Positions are pre-computed here rather than in a converter because the overlay has a
/// fixed width, the same arrangement <see cref="LegendTick"/> uses.
/// </para>
/// </summary>
/// <param name="Fraction">Position along the axis, 0 at the first frame and 1 at the last.</param>
/// <param name="Left">Left edge of the centred label, in panel pixels.</param>
/// <param name="Label">Formatted axis value including its unit, e.g. "2.5 s".</param>
public sealed record TimelineTickMark(double Fraction, double Left, string Label)
{
    /// <summary>Width of the timeline slider, matching the panel's layout.</summary>
    public const double TrackWidth = 400;

    /// <summary>Default WPF horizontal slider thumb width. The slider's usable travel is
    /// inset by half a thumb at each end, so a label placed at fraction·TrackWidth would
    /// drift from the thumb it labels — worst at the two ends, exactly where the axis
    /// bounds are read.</summary>
    public const double ThumbWidth = 11;

    /// <summary>Width of a tick label; centred on its position.</summary>
    public const double LabelWidth = 64;

    public static TimelineTickMark At(double fraction, string label) =>
        new(fraction,
            ThumbWidth / 2 + fraction * (TrackWidth - ThumbWidth) - LabelWidth / 2,
            label);
}
