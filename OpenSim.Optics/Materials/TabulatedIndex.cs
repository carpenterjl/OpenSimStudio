namespace OpenSim.Optics.Materials;

/// <summary>
/// A material given as a table of n and k against wavelength (OpticStudio's "Tabulated"
/// material type) instead of a dispersion formula. The AGF reader does not produce these; the
/// shipped data of OP-12 and the user's own measurements do.
/// <para>
/// <b>Interpolation (our choice).</b> n and k are interpolated linearly in wavelength between
/// the two neighbouring rows. Linear interpolation cannot overshoot between rows, so a table of
/// k ≥ 0 never interpolates to a negative k and a monotone n stays monotone; a spline would be
/// smoother but can ring between widely spaced rows. How OpticStudio interpolates its tabulated
/// materials has not been read at a source, so results between rows may differ from it by the
/// curvature of the data over one row spacing. Outside the first and last rows nothing is
/// extrapolated: <see cref="OpticalMaterial.RelativeIndex"/> refuses the wavelength.
/// </para>
/// </summary>
public sealed record TabulatedIndex
{
    /// <summary>The rows, wavelength strictly increasing.</summary>
    public required IReadOnlyList<TabulatedIndexPoint> Points { get; init; }

    /// <summary>False for a table of n only (a refractiveindex.info "tabulated n" block): the
    /// rows' K is then 0 as a placeholder, not data, and <see cref="OpticalMaterial.ExtinctionCoefficient"/>
    /// reports no k from it.</summary>
    public bool HasExtinction { get; init; } = true;

    public double MinMicrometers => Points[0].Micrometers;
    public double MaxMicrometers => Points[^1].Micrometers;

    /// <summary>What is wrong with the table, one line each; empty when it can be used.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Points is null || Points.Count < 2)
        {
            problems.Add("A tabulated material needs at least two rows.");
            return problems;
        }
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (!double.IsFinite(p.Micrometers) || p.Micrometers <= 0)
                problems.Add($"Row {i + 1}: the wavelength must be positive (it is {p.Micrometers} µm).");
            if (!double.IsFinite(p.N) || p.N <= 0)
                problems.Add($"Row {i + 1}: n must be positive (it is {p.N}).");
            if (!double.IsFinite(p.K) || p.K < 0)
                problems.Add($"Row {i + 1}: k must be zero or positive (it is {p.K}).");
            if (i > 0 && !(p.Micrometers > Points[i - 1].Micrometers))
                problems.Add($"Row {i + 1}: the wavelengths must increase from row to row ({Points[i - 1].Micrometers} µm, then {p.Micrometers} µm).");
        }
        return problems;
    }

    /// <summary>n and k at <paramref name="micrometers"/>, which the caller has checked lies in
    /// [<see cref="MinMicrometers"/>, <see cref="MaxMicrometers"/>].</summary>
    public (double N, double K) Interpolate(double micrometers)
    {
        int hi = 1;
        while (hi < Points.Count - 1 && Points[hi].Micrometers < micrometers) hi++;
        var a = Points[hi - 1];
        var b = Points[hi];
        if (micrometers == b.Micrometers) return (b.N, b.K);
        if (micrometers == a.Micrometers) return (a.N, a.K);
        double t = (micrometers - a.Micrometers) / (b.Micrometers - a.Micrometers);
        return (a.N + t * (b.N - a.N), a.K + t * (b.K - a.K));
    }
}
