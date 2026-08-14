namespace OpenSim.App.ViewModels;

/// <summary>
/// One labeled tick on the result legend: the field value a colormap stop actually colors,
/// placed at that stop's position along the gradient.
/// <para>
/// The value comes from <see cref="OpenSim.Core.PostProcessing.FieldScale.ValueAt"/>, which
/// is why the ticks stay honest under a logarithmic scale — they are the inverse of the
/// very mapping the mesh was colored through, not an evenly-spaced guess.
/// </para>
/// </summary>
/// <param name="Fraction">Position along the gradient, 0..1.</param>
/// <param name="Left">Left edge of the centered label, in legend pixels.</param>
/// <param name="Label">Formatted field value (no unit — the min/max row carries it).</param>
public sealed record LegendTick(double Fraction, double Left, string Label)
{
    /// <summary>Inner width of the legend gradient, matching the legend panel's layout.</summary>
    public const double LegendWidth = 144;

    /// <summary>Width of a tick label; centered on its position.</summary>
    public const double LabelWidth = 44;

    public static LegendTick At(double fraction, string label) =>
        new(fraction, fraction * LegendWidth - LabelWidth / 2, label);
}
