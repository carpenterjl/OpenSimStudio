using System.Numerics;

namespace OpenSim.Optics.Diffraction;

/// <summary>Polarization of the incident plane wave relative to the plane of incidence (which
/// contains the grating vector in the planar mounting): TE has E along the grooves, TM has H
/// along the grooves.</summary>
public enum GratingPolarization
{
    TE,
    TM,
}

/// <summary>One piece of a grating layer's period: a fraction <paramref name="Width"/> of the
/// period filled with a medium of complex index <paramref name="Index"/> = n + ik, k ≥ 0 for an
/// absorbing medium (docs/Optics-Conventions.md §61).</summary>
public sealed record GratingSegment(double Width, Complex Index);

/// <summary>
/// One layer of a one-dimensional grating stack: thickness (meters) and the medium across one
/// period, as segments in order from x = 0 whose widths add to 1. A single segment is a
/// homogeneous film.
/// </summary>
public sealed record GratingLayer
{
    public GratingLayer(double thickness, IReadOnlyList<GratingSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!(thickness > 0) || !double.IsFinite(thickness))
            throw new ArgumentException($"A grating layer's thickness is {thickness} m; it must be positive.");
        if (segments.Count == 0) throw new ArgumentException("A grating layer needs at least one segment.");
        double total = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (!(s.Width > 0) || !double.IsFinite(s.Width))
                throw new ArgumentException($"Grating layer segment {i + 1}: the width is {s.Width}; widths are positive fractions of the period.");
            GratingStructure.CheckIndex(s.Index, $"grating layer segment {i + 1}");
            total += s.Width;
        }
        if (Math.Abs(total - 1) > 1e-9)
            throw new ArgumentException($"A grating layer's segment widths add to {total}; they must add to 1 (one period).");
        Thickness = thickness;
        Segments = segments.ToArray();
    }

    /// <summary>Meters.</summary>
    public double Thickness { get; }

    public IReadOnlyList<GratingSegment> Segments { get; }

    /// <summary>True for a single medium across the period.</summary>
    public bool IsHomogeneous => Segments.Count == 1 || Segments.All(s => s.Index == Segments[0].Index);

    /// <summary>A homogeneous film.</summary>
    public static GratingLayer Film(double thickness, Complex index) =>
        new(thickness, new[] { new GratingSegment(1, index) });

    /// <summary>A lamellar layer: <paramref name="ridge"/> over the fraction <paramref name="fill"/>
    /// at the end of the period ([1 − f, 1)), <paramref name="groove"/> over the rest.</summary>
    public static GratingLayer Lamellar(double thickness, double fill, Complex ridge, Complex groove)
    {
        if (!(fill > 0 && fill < 1))
            throw new ArgumentException($"A lamellar layer's fill factor is {fill}; it must lie strictly between 0 and 1.");
        return new(thickness, new[] { new GratingSegment(1 - fill, groove), new GratingSegment(fill, ridge) });
    }
}

/// <summary>
/// A one-dimensional grating stack for <see cref="Rcwa1D"/>: a lossless cover (where light comes
/// from and is reflected into), layers from the cover side down, and a substrate (transmitted
/// light). The period is in meters; indices are relative to the medium in which the wavelength is
/// given.
/// </summary>
public sealed record GratingStructure
{
    public GratingStructure(double period, double coverIndex, IReadOnlyList<GratingLayer> layers, Complex substrateIndex)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (!(period > 0) || !double.IsFinite(period))
            throw new ArgumentException($"The grating period is {period} m; it must be positive.");
        if (!(coverIndex > 0) || !double.IsFinite(coverIndex))
            throw new ArgumentException($"The cover index is {coverIndex}; it must be real and positive (the incident and reflected orders need a lossless medium).");
        CheckIndex(substrateIndex, "the substrate");
        Period = period;
        CoverIndex = coverIndex;
        Layers = layers.ToArray();
        SubstrateIndex = substrateIndex;
    }

    public double Period { get; }
    public double CoverIndex { get; }
    public IReadOnlyList<GratingLayer> Layers { get; }
    public Complex SubstrateIndex { get; }

    /// <summary>True when the substrate absorbs, so transmitted orders carry no defined power.</summary>
    public bool SubstrateAbsorbs => SubstrateIndex.Imaginary > 0;

    internal static void CheckIndex(Complex index, string what)
    {
        if (!double.IsFinite(index.Real) || !double.IsFinite(index.Imaginary))
            throw new ArgumentException($"The index of {what} is {index}; it must be finite.");
        if (index.Imaginary < 0)
            throw new ArgumentException($"The index of {what} is {index.Real} + {index.Imaginary}i; the extinction coefficient must be zero or positive (a gain medium is not modelled).");
        if (index == Complex.Zero)
            throw new ArgumentException($"The index of {what} is zero.");
    }
}

/// <summary>Builders for staircase approximations of continuous relief profiles (OP-47): each
/// returns the layers of a tooth of height <c>depth</c> on the substrate side, cut into slices of
/// equal thickness, each slice lamellar with the tooth material where the profile is above the
/// slice's mid-height.</summary>
public static class GratingProfiles
{
    /// <summary>The sawtooth h(u) = depth·u on [0, 1) (rising toward +x), in <paramref name="slices"/>
    /// slices: slice k from the top (k = 0 … S − 1) holds the ridge on u ∈ [1 − (k + ½)/S, 1).</summary>
    public static IReadOnlyList<GratingLayer> Blazed(double depth, int slices, Complex ridge, Complex groove)
    {
        CheckSlices(depth, slices);
        var layers = new GratingLayer[slices];
        for (int k = 0; k < slices; k++)
        {
            double fill = (k + 0.5) / slices;
            layers[k] = GratingLayer.Lamellar(depth / slices, fill, ridge, groove);
        }
        return layers;
    }

    /// <summary>The exact N-level staircase h(u) = H·⌊Nu⌋/N (<see cref="ScalarGratingEfficiency.Multilevel"/>'s
    /// profile, H the continuous depth): N − 1 layers of thickness H/N, layer j from the top
    /// holding the ridge on u ∈ [(N − 1 − j)/N, 1).</summary>
    public static IReadOnlyList<GratingLayer> Multilevel(double continuousDepth, int levels, Complex ridge, Complex groove)
    {
        if (levels < 2) throw new ArgumentException($"A multilevel profile has at least 2 levels (got {levels}).");
        CheckSlices(continuousDepth, levels);
        var layers = new GratingLayer[levels - 1];
        for (int j = 0; j < levels - 1; j++)
            layers[j] = GratingLayer.Lamellar(continuousDepth / levels, (double)(j + 1) / levels, ridge, groove);
        return layers;
    }

    /// <summary>The sinusoid h(u) = depth·(1 + sin 2πu)/2 in <paramref name="slices"/> slices: at
    /// mid-height y the ridge is where sin 2πu &gt; 2y/depth − 1, the interval from asin(c)/2π to
    /// (π − asin c)/2π (taken modulo 1; the segments are listed from u = 0).</summary>
    public static IReadOnlyList<GratingLayer> Sinusoidal(double depth, int slices, Complex ridge, Complex groove)
    {
        CheckSlices(depth, slices);
        var layers = new GratingLayer[slices];
        for (int k = 0; k < slices; k++)
        {
            double y = 1 - (k + 0.5) / slices;   // mid-height over depth
            double c = 2 * y - 1;
            double a = Math.Asin(c) / (2 * Math.PI);           // −¼ … ¼
            double b = 0.5 - a;                                // ¼ … ¾
            GratingSegment[] segments;
            if (a >= 0)
                segments = new[] { new GratingSegment(a, groove), new GratingSegment(b - a, ridge), new GratingSegment(1 - b, groove) };
            else
                segments = new[] { new GratingSegment(b, ridge), new GratingSegment(1 + a - b, groove), new GratingSegment(-a, ridge) };
            layers[k] = new GratingLayer(depth / slices, segments.Where(g => g.Width > 1e-15).ToArray());
        }
        return layers;
    }

    private static void CheckSlices(double depth, int slices)
    {
        if (!(depth > 0) || !double.IsFinite(depth)) throw new ArgumentException($"The relief depth is {depth} m; it must be positive.");
        if (slices < 1) throw new ArgumentException($"A staircase needs at least one slice (got {slices}).");
    }
}
