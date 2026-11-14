namespace OpenSim.Optics.Materials;

/// <summary>One row of an extinction table: k at one wavelength (µm).</summary>
public sealed record ExtinctionPoint(double Micrometers, double K);

/// <summary>
/// The extinction coefficient k given as its own table, apart from the index: a
/// refractiveindex.info page that gives n by a formula (or its own n table) and k as a
/// "tabulated k" block on another wavelength grid. Interpolated linearly in wavelength, as
/// <see cref="TabulatedIndex"/> is and for the same reasons (no overshoot, so k ≥ 0 stays so);
/// nothing is extrapolated past the first and last rows.
/// </summary>
public sealed record ExtinctionTable
{
    /// <summary>The rows, wavelength strictly increasing.</summary>
    public required IReadOnlyList<ExtinctionPoint> Points { get; init; }

    public double MinMicrometers => Points[0].Micrometers;
    public double MaxMicrometers => Points[^1].Micrometers;

    /// <summary>What is wrong with the table, one line each; empty when it can be used.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Points is null || Points.Count < 2)
        {
            problems.Add("An extinction table needs at least two rows.");
            return problems;
        }
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (!double.IsFinite(p.Micrometers) || p.Micrometers <= 0)
                problems.Add($"k row {i + 1}: the wavelength must be positive (it is {p.Micrometers} µm).");
            if (!double.IsFinite(p.K) || p.K < 0)
                problems.Add($"k row {i + 1}: k must be zero or positive (it is {p.K}).");
            if (i > 0 && !(p.Micrometers > Points[i - 1].Micrometers))
                problems.Add($"k row {i + 1}: the wavelengths must increase from row to row ({Points[i - 1].Micrometers} µm, then {p.Micrometers} µm).");
        }
        return problems;
    }

    /// <summary>k at <paramref name="micrometers"/>, which the caller has checked lies in the table.</summary>
    public double Interpolate(double micrometers)
    {
        int hi = 1;
        while (hi < Points.Count - 1 && Points[hi].Micrometers < micrometers) hi++;
        var a = Points[hi - 1];
        var b = Points[hi];
        if (micrometers == b.Micrometers) return b.K;
        if (micrometers == a.Micrometers) return a.K;
        double t = (micrometers - a.Micrometers) / (b.Micrometers - a.Micrometers);
        return a.K + t * (b.K - a.K);
    }
}
