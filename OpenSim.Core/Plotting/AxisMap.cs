namespace OpenSim.Core.Plotting;

/// <summary>
/// One plot axis: the data interval [<see cref="Min"/>, <see cref="Max"/>] mapped affinely (or
/// affinely in log10 when <see cref="Log"/>) onto the pixel interval from
/// <see cref="PixelStart"/> to <see cref="PixelEnd"/>. A vertical axis passes the bottom pixel
/// as the start, so larger values sit higher on screen.
/// </summary>
public readonly record struct AxisMap(double Min, double Max, double PixelStart, double PixelEnd, bool Log = false)
{
    private double T(double v) => Log ? Math.Log10(v) : v;
    private double Inverse(double t) => Log ? Math.Pow(10, t) : t;

    /// <summary>Pixel position of a data value. Non-positive values on a log axis map to
    /// negative infinity (below the start), never to a misleading finite pixel.</summary>
    public double ToPixel(double value)
    {
        if (Log && !(value > 0)) return double.NegativeInfinity;
        return PixelStart + (T(value) - T(Min)) / (T(Max) - T(Min)) * (PixelEnd - PixelStart);
    }

    /// <summary>Data value at a pixel position: the exact inverse of <see cref="ToPixel"/>.</summary>
    public double ToData(double pixel) =>
        Inverse(T(Min) + (pixel - PixelStart) / (PixelEnd - PixelStart) * (T(Max) - T(Min)));

    /// <summary>Data units per pixel on a linear axis (always positive).</summary>
    public double DataPerPixel => Math.Abs((Max - Min) / (PixelEnd - PixelStart));

    /// <summary>The axis magnified by <paramref name="factor"/> (2 shows half the range) about the
    /// data value <paramref name="anchor"/>, which stays at the same pixel. Log axes zoom in log10.</summary>
    public AxisMap ZoomedAbout(double anchor, double factor)
    {
        double c = T(anchor);
        return this with
        {
            Min = Inverse(c + (T(Min) - c) / factor),
            Max = Inverse(c + (T(Max) - c) / factor)
        };
    }

    /// <summary>The axis with its content moved by <paramref name="pixels"/> on screen (the data
    /// under the cursor follows a drag).</summary>
    public AxisMap Panned(double pixels)
    {
        double shift = -pixels / (PixelEnd - PixelStart) * (T(Max) - T(Min));
        return this with { Min = Inverse(T(Min) + shift), Max = Inverse(T(Max) + shift) };
    }
}
