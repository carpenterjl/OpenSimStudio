namespace OpenSim.Core.Plotting;

/// <summary>The length of a scale bar: the longest round value that fits a pixel budget.</summary>
public static class ScaleBar
{
    /// <summary>The largest 1, 2 or 5 times a power of ten whose length on screen is at most
    /// <paramref name="maxPixels"/>, at <paramref name="dataPerPixel"/> data units per pixel. NaN
    /// when either argument is not positive and finite.</summary>
    public static double Length(double dataPerPixel, double maxPixels)
    {
        double budget = dataPerPixel * maxPixels;
        if (!(budget > 0) || !double.IsFinite(budget)) return double.NaN;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(budget)));
        // Guard the floor against log10 landing a hair below an exact power of ten.
        if (magnitude * 10 <= budget) magnitude *= 10;
        double unit = budget / magnitude;
        return (unit >= 5 ? 5 : unit >= 2 ? 2 : 1) * magnitude;
    }
}
