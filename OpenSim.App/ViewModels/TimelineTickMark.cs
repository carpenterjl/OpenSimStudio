namespace OpenSim.App.ViewModels;

/// <summary>
/// One labelled mark under the result timeline, placed at the axis fraction its frame sits
/// at (see <see cref="OpenSim.Core.PostProcessing.FrameTimeline.TickLabels"/> — the values
/// are real frame values, never interpolated).
/// </summary>
/// <param name="Fraction">Position along the axis, 0 at the first frame and 1 at the last.</param>
/// <param name="Label">Formatted axis value including its unit, e.g. "2.5 s".</param>
public sealed record TimelineTickMark(double Fraction, string Label)
{
    /// <summary>Default WPF horizontal slider thumb width. The slider's usable travel is
    /// inset by half a thumb at each end, so a label placed at fraction·width would
    /// drift from the thumb it labels — worst at the two ends, exactly where the axis
    /// bounds are read.</summary>
    public const double ThumbWidth = 11;

    /// <summary>Width of a tick label; centred on its position.</summary>
    public const double LabelWidth = 64;

    /// <summary>How far a label's box may overhang the track at each end before it is held
    /// back: the panel's padding, the room there is before the pane cuts it.</summary>
    public const double EndOverhang = 8;

    /// <summary>The left edge of a label at <paramref name="fraction"/> over a track this wide.</summary>
    public static double LeftAt(double fraction, double trackWidth)
    {
        double centred = ThumbWidth / 2 + fraction * (trackWidth - ThumbWidth) - LabelWidth / 2;
        return Math.Clamp(centred, -EndOverhang, Math.Max(-EndOverhang, trackWidth + EndOverhang - LabelWidth));
    }
}
