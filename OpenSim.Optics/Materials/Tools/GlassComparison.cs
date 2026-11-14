using System.Globalization;

namespace OpenSim.Optics.Materials;

/// <summary>A side-by-side table: one column per glass after the property name.</summary>
public sealed record GlassComparisonTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Glasses side by side (OpticStudio's Glass Compare; plan item OP-14): catalog, formula and
/// range; the computed nd, Vd, ne, Ve, nF − nC, P_g,F and ΔP_g,F (<see cref="GlassProperties"/>)
/// beside the printed nd, Vd and ΔP_g,F; the index at each requested wavelength; the thermal,
/// cost and resistance data as the catalog prints them. A value a glass does not have prints
/// as "—"; an index outside its valid range prints "out of range", never an extrapolation.
/// </summary>
public static class GlassComparison
{
    public const string Absent = "—";

    public static GlassComparisonTable Build(IReadOnlyList<OpticalMaterial> glasses, IReadOnlyList<double> wavelengths)
    {
        ArgumentNullException.ThrowIfNull(glasses);
        ArgumentNullException.ThrowIfNull(wavelengths);
        var headers = new List<string> { "Property" };
        headers.AddRange(glasses.Select(g => g.Name));
        var props = glasses.Select(g => GlassProperties.TryCompute(g, out var p, out _) ? p : null).ToArray();
        var rows = new List<IReadOnlyList<string>>();

        void Row(string label, Func<int, string> value) =>
            rows.Add(new[] { label }.Concat(Enumerable.Range(0, glasses.Count).Select(value)).ToArray());
        string Num(double? v, string format) => v is double x && double.IsFinite(x) ? x.ToString(format, CultureInfo.InvariantCulture) : Absent;

        Row("Catalog", i => glasses[i].Catalog.Length > 0 ? glasses[i].Catalog : Absent);
        Row("Formula", i => glasses[i].Formula is { } f ? DispersionFormulas.Name(f) : glasses[i].Tabulated is not null ? "Tabulated" : Absent);
        Row("Valid range, µm", i => glasses[i].ValidRange is { } r ? $"{Num(r.Min, "G6")}–{Num(r.Max, "G6")}" : Absent);
        Row("Status", i => glasses[i].Status?.ToString() ?? Absent);
        Row("nd (computed)", i => Num(props[i]?.Nd, "F6"));
        Row("nd (printed)", i => glasses[i].Nd?.Text ?? Absent);
        Row("Vd (computed)", i => Num(props[i]?.Vd, "F4"));
        Row("Vd (printed)", i => glasses[i].Vd?.Text ?? Absent);
        Row("ne", i => Num(props[i]?.Ne, "F6"));
        Row("Ve", i => Num(props[i]?.Ve, "F4"));
        Row("nF − nC", i => Num(props[i]?.PrincipalDispersion, "F6"));
        Row("PgF", i => Num(props[i]?.PgF, "F4"));
        Row("ΔPgF (computed)", i => Num(props[i]?.DeltaPgF, "F4"));
        Row("ΔPgF (printed)", i => Num(glasses[i].DeltaPgF, "G6"));
        foreach (double w in wavelengths)
            Row($"n at {w.ToString("G7", CultureInfo.InvariantCulture)} µm", i => Index(glasses[i], w));
        Row("TCE −30/70 °C, 10⁻⁶/K", i => Num(glasses[i].ThermalExpansion, "G6"));
        Row("dn/dT data (TD)", i => glasses[i].Thermal is null ? "no" : "yes");
        Row("Density, g/cm³", i => Num(glasses[i].Density, "G6"));
        Row("Relative cost", i => Num(glasses[i].RelativeCost, "G6"));
        Row("Climatic resistance CR", i => Num(glasses[i].ClimaticResistance, "G6"));
        Row("Stain resistance FR", i => Num(glasses[i].StainResistance, "G6"));
        Row("Acid resistance SR", i => Num(glasses[i].AcidResistance, "G6"));
        Row("Alkali resistance AR", i => Num(glasses[i].AlkaliResistance, "G6"));
        Row("Phosphate resistance PR", i => Num(glasses[i].PhosphateResistance, "G6"));
        Row("Transmittance points (IT)", i => glasses[i].Transmittance.Count.ToString(CultureInfo.InvariantCulture));
        return new GlassComparisonTable(headers, rows);
    }

    private static string Index(OpticalMaterial glass, double micrometers)
    {
        if (!glass.Covers(micrometers)) return "out of range";
        try { return glass.RelativeIndex(micrometers).ToString("F6", CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException) { return "not evaluable"; }
    }
}
