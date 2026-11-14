using System.Globalization;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Optics.Polarization;

/// <summary>
/// The bulk (internal) absorption of a medium, as an intensity attenuation coefficient α in
/// 1/m: a path τ through it transmits exp(−α·τ) of the power (Beer's law; OpticStudio help,
/// "Bulk Absorption and Transmission" and "Definition of Polarization Terms", internal
/// transmittance from the path length τ and the absorption α, τ negative for a virtual
/// propagation).
/// <list type="bullet">
/// <item>A catalog glass with an IT table (<see cref="OpticalMaterial.Transmittance"/>): each
/// point (λ_i, t_i, τ_i mm) gives α_i = −ln(t_i)/τ_i per mm (the rule of
/// <see cref="InternalTransmittance.Curve"/>, after AGFFileReader.jl's <c>absorption</c>), and
/// α at λ is linear in λ between the two neighbouring points. That interpolation is this
/// program's choice (OpticStudio's was not read; docs/Optics-Conventions.md §10, item 17). A
/// wavelength outside the table is refused, not extrapolated. Points without a thickness or
/// with t outside 0–1 are left out, as the transmittance plot leaves them out; t = 0 is
/// opaque (α = +∞).</item>
/// <item>A material with no usable IT points but an extinction coefficient k (a
/// refractiveindex.info page): α = 4πk/λ, the decay of |E|² for a wave e^(i2π(n + ik)z/λ)
/// (derived: |e^(i2π(n+ik)z/λ)|² = e^(−4πkz/λ)). Indices and k are relative to the system's
/// air and λ is the air wavelength, so the product k/λ is the absolute one.</item>
/// <item>A constant index, or a material with neither: no absorption.</item>
/// </list>
/// </summary>
public static class BulkAbsorption
{
    /// <summary>α in 1/m of <paramref name="medium"/> at the air wavelength
    /// <paramref name="airMicrometers"/>. Throws, naming the glass and its table's range, when
    /// the wavelength is outside the IT data.</summary>
    public static double Coefficient(ParaxialMedium medium, double airMicrometers)
    {
        ArgumentNullException.ThrowIfNull(medium);
        if (medium.Material is not { } glass) return 0;
        var points = glass.Transmittance
            .Where(p => p.ThicknessMillimeters is double tau && tau > 0 && p.Transmittance >= 0 && p.Transmittance <= 1)
            .OrderBy(p => p.Micrometers)
            .ToArray();
        if (points.Length > 0) return FromTable(glass, points, airMicrometers);
        if (glass.ExtinctionCoefficient(airMicrometers) is double k && k > 0)
            return 4 * Math.PI * k / (airMicrometers * 1e-6);
        return 0;
    }

    private static double FromTable(OpticalMaterial glass, TransmittancePoint[] points, double micrometers)
    {
        double lo = points[0].Micrometers, hi = points[^1].Micrometers;
        if (micrometers < lo || micrometers > hi)
            throw new InvalidOperationException(
                $"{glass.Who}: its internal transmittance data cover {F(lo)}–{F(hi)} µm and {F(micrometers)} µm is outside them; " +
                "bulk absorption is not extrapolated. Use wavelengths inside the data or turn bulk absorption off.");
        for (int i = 0; i < points.Length; i++)
            if (points[i].Micrometers == micrometers) return Alpha(points[i]);
        int k = 1;
        while (points[k].Micrometers < micrometers) k++;
        double a = Alpha(points[k - 1]), b = Alpha(points[k]);
        if (double.IsPositiveInfinity(a) || double.IsPositiveInfinity(b)) return double.PositiveInfinity;
        double u = (micrometers - points[k - 1].Micrometers) / (points[k].Micrometers - points[k - 1].Micrometers);
        return a + u * (b - a);
    }

    /// <summary>α in 1/m of one IT point: −ln(t)/τ per mm, times 1000.</summary>
    private static double Alpha(TransmittancePoint p) =>
        p.Transmittance == 0 ? double.PositiveInfinity : -Math.Log(p.Transmittance) / p.ThicknessMillimeters!.Value * 1000;

    private static string F(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
}
