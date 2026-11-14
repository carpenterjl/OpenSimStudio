using System.Globalization;

namespace OpenSim.Optics.Materials;

/// <summary>A glass on the nd–Vd map: its catalog, its computed numbers.</summary>
public sealed record GlassMapPoint(string Catalog, GlassProperties Properties)
{
    public OpticalMaterial Material => Properties.Material;
}

/// <summary>A glass that could not be placed on a map, and why.</summary>
public sealed record GlassLeftOut(string Catalog, OpticalMaterial Material, string Reason);

/// <summary>A glass on the athermal map.</summary>
/// <param name="ExpansionPerKelvin">α, the thermal expansion coefficient over −30…+70 °C (the
/// ED line's first number, printed in 10⁻⁶/K), per K.</param>
/// <param name="RelativeIndex">n_rel at the wavelength and the lower temperature.</param>
/// <param name="RelativeDnDT">Δn_rel/ΔT over the temperature interval, per K.</param>
/// <param name="ThermoOpticConstant">G = α·(n_rel − 1) + dn_rel/dT (SCHOTT TIE-19 equation (10)), per K.</param>
public sealed record AthermalMapPoint(string Catalog, OpticalMaterial Material, double ExpansionPerKelvin,
    double RelativeIndex, double RelativeDnDT, double ThermoOpticConstant);

/// <summary>
/// The glass maps of plan item OP-14 as data: the nd–Vd map (with P_g,F for the partial
/// dispersion diagram) and the athermal map. Every glass of every catalog given is either placed
/// or listed as left out with its reason; nothing is dropped silently. Positions are always the
/// values computed from the glass's coefficients (<see cref="GlassProperties"/>), never the
/// printed NM values.
/// </summary>
public static class GlassMaps
{
    /// <summary>The nd–Vd map of <paramref name="catalogs"/>; <paramref name="nameFilter"/>
    /// keeps only glasses whose name contains it (any case; empty keeps all).</summary>
    public static (IReadOnlyList<GlassMapPoint> Points, IReadOnlyList<GlassLeftOut> LeftOut) GlassMap(
        IEnumerable<OpticalCatalog> catalogs, string? nameFilter = null, CancellationToken cancellation = default)
    {
        var points = new List<GlassMapPoint>();
        var left = new List<GlassLeftOut>();
        foreach (var catalog in catalogs)
        foreach (var glass in catalog.Materials)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Matches(glass, nameFilter)) continue;
            if (GlassProperties.TryCompute(glass, out var p, out string reason)) points.Add(new GlassMapPoint(catalog.Name, p!));
            else left.Add(new GlassLeftOut(catalog.Name, glass, reason));
        }
        return (points, left);
    }

    /// <summary>
    /// The athermal map: for each glass with a TD line (the dn/dT model) and an ED expansion
    /// coefficient, α against G (or dn_rel/dT) at <paramref name="airMicrometers"/>, a
    /// wavelength in air. dn_rel/dT is the mean over the interval,
    /// (n_rel(T₂) − n_rel(T₁))/(T₂ − T₁), with n_rel relative to air at each temperature and
    /// <paramref name="pressurePascals"/> (<see cref="ThermalIndex.AtAirWavelength"/>, TIE-19
    /// equations (2)–(5)): the way TIE-19's Table 2 quotes its "relative temperature
    /// coefficients of refractive index [+20/+40 deg. °C]". G is TIE-19 equation (10),
    /// G = α·(n_rel(λ, T) − 1) + dn_rel/dT, with n_rel at T₁.
    /// </summary>
    public static (IReadOnlyList<AthermalMapPoint> Points, IReadOnlyList<GlassLeftOut> LeftOut) AthermalMap(
        IEnumerable<OpticalCatalog> catalogs, double airMicrometers, double lowerCelsius, double upperCelsius,
        AirIndexModel airModel, double pressurePascals, string? nameFilter = null, CancellationToken cancellation = default)
    {
        if (!(upperCelsius > lowerCelsius))
            throw new ArgumentException($"The temperature interval {lowerCelsius}–{upperCelsius} °C must have its upper end above its lower.");
        var points = new List<AthermalMapPoint>();
        var left = new List<GlassLeftOut>();
        foreach (var catalog in catalogs)
        foreach (var glass in catalog.Materials)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Matches(glass, nameFilter)) continue;
            if (glass.Thermal is null) { left.Add(new GlassLeftOut(catalog.Name, glass, $"{glass.Who} has no TD line: no dn/dT model.")); continue; }
            if (glass.ThermalExpansion is not double tce || !double.IsFinite(tce) || tce <= 0)
            {
                left.Add(new GlassLeftOut(catalog.Name, glass, $"{glass.Who} states no thermal expansion coefficient (ED line)."));
                continue;
            }
            try
            {
                var (n1, dndt) = RelativeCoefficient(glass, airMicrometers, lowerCelsius, upperCelsius, airModel, pressurePascals);
                double alpha = tce * 1e-6;
                points.Add(new AthermalMapPoint(catalog.Name, glass, alpha, n1, dndt, ThermoOpticConstant(alpha, n1, dndt)));
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                left.Add(new GlassLeftOut(catalog.Name, glass, e.Message.Split(" (Parameter")[0]));
            }
        }
        return (points, left);
    }

    /// <summary>TIE-19 equation (10): G = α·(n_rel − 1) + dn_rel/dT.</summary>
    public static double ThermoOpticConstant(double expansionPerKelvin, double relativeIndex, double relativeDnDT) =>
        expansionPerKelvin * (relativeIndex - 1) + relativeDnDT;

    /// <summary>n_rel at T₁ and the mean Δn_rel/ΔT over T₁…T₂, at an air wavelength.</summary>
    public static (double IndexAtLower, double DnDT) RelativeCoefficient(OpticalMaterial glass, double airMicrometers,
        double lowerCelsius, double upperCelsius, AirIndexModel airModel, double pressurePascals)
    {
        double n1 = ThermalIndex.AtAirWavelength(glass, airMicrometers, airModel, new AirConditions(lowerCelsius, pressurePascals)).Relative;
        double n2 = ThermalIndex.AtAirWavelength(glass, airMicrometers, airModel, new AirConditions(upperCelsius, pressurePascals)).Relative;
        return (n1, (n2 - n1) / (upperCelsius - lowerCelsius));
    }

    private static bool Matches(OpticalMaterial glass, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || glass.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Internal transmittance at a chosen thickness from a glass's IT table.</summary>
public static class InternalTransmittance
{
    /// <summary>
    /// The IT table scaled to <paramref name="thicknessMillimeters"/>, at the table's own
    /// wavelengths (nothing is interpolated or extrapolated). A point measured on a sample of
    /// thickness τ with internal transmittance t has absorption −ln(t)/τ per mm (AGFFileReader.jl,
    /// <c>src/utilities.jl</c>, <c>absorption</c>, its docstring), so at thickness d the
    /// transmittance is exp(−d·ln(t)/τ) = t^(d/τ). Points without a thickness, or with a
    /// transmittance outside 0–1, are left out and counted in <c>Skipped</c>.
    /// </summary>
    public static (IReadOnlyList<(double Micrometers, double Transmittance)> Points, int Skipped) Curve(
        OpticalMaterial glass, double thicknessMillimeters)
    {
        ArgumentNullException.ThrowIfNull(glass);
        if (!double.IsFinite(thicknessMillimeters) || thicknessMillimeters < 0)
            throw new ArgumentOutOfRangeException(nameof(thicknessMillimeters), thicknessMillimeters, "The thickness must be zero or more millimeters.");
        var points = new List<(double, double)>();
        int skipped = 0;
        foreach (var p in glass.Transmittance.OrderBy(p => p.Micrometers))
        {
            if (p.ThicknessMillimeters is not double tau || !(tau > 0) || !(p.Transmittance >= 0) || p.Transmittance > 1) { skipped++; continue; }
            double t = p.Transmittance == 0 ? (thicknessMillimeters == 0 ? 1 : 0) : Math.Exp(thicknessMillimeters / tau * Math.Log(p.Transmittance));
            points.Add((p.Micrometers, t));
        }
        return (points, skipped);
    }
}

/// <summary>Finding glasses by name in an ordered list of catalogs, and the active set.</summary>
public static class GlassLookup
{
    /// <summary>
    /// The catalogs a lens draws from: <paramref name="active"/> names in order (a name not
    /// loaded is skipped), or every loaded catalog when it is null, as
    /// <c>OpticsMaterialsSettings.ActiveCatalogs</c> defines.
    /// </summary>
    public static IReadOnlyList<OpticalCatalog> ActiveCatalogs(IReadOnlyList<OpticalCatalog> loaded, IReadOnlyList<string>? active)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (active is null) return loaded.ToArray();
        return active.Select(name => loaded.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c is not null).Select(c => c!).Distinct().ToArray();
    }

    /// <summary>
    /// Parses a list of glass names separated by commas or semicolons and finds each in the
    /// catalogs, searched in order (first match). A name may be written "catalog:glass" to pick a
    /// catalog. Names not found are returned in <c>Missing</c>.
    /// </summary>
    public static (IReadOnlyList<OpticalMaterial> Found, IReadOnlyList<string> Missing) Find(string? names, IReadOnlyList<OpticalCatalog> catalogs)
    {
        var found = new List<OpticalMaterial>();
        var missing = new List<string>();
        foreach (string raw in (names ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            OpticalMaterial? glass = null;
            int colon = raw.IndexOf(':');
            if (colon > 0)
            {
                string catalog = raw[..colon].Trim(), name = raw[(colon + 1)..].Trim();
                glass = catalogs.FirstOrDefault(c => string.Equals(c.Name, catalog, StringComparison.OrdinalIgnoreCase))?.Find(name);
            }
            else
                foreach (var c in catalogs)
                    if (c.Find(raw) is { } g) { glass = g; break; }
            if (glass is null) missing.Add(raw);
            else found.Add(glass);
        }
        return (found, missing);
    }

    internal static string N(double v, string format = "G6") => v.ToString(format, CultureInfo.InvariantCulture);
}
