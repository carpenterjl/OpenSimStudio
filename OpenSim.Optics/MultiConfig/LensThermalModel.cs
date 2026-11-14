using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.MultiConfig;

/// <summary>A linear thermal expansion coefficient (per K) and where it came from, for messages.</summary>
/// <param name="Alpha">α, 1/K: a length L becomes L(1 + αΔT).</param>
/// <param name="Source">"the glass N-BK7 (catalog TCE 7.1e-6/K)", "the TCE column of surface 3" …</param>
public readonly record struct ExpansionCoefficient(double Alpha, string Source);

// OP-91
/// <summary>
/// How a lens's dimensions follow a change of temperature, as OpticStudio's thermal pickup solves
/// define it (OpticStudio 2025 R2 help, System Explorer › Environment, pages saved in
/// <c>plans/sources/optics/op-91-help/</c>): "the linear change in size is given by the
/// expression L' = L(1 + αΔT), where L is a linear dimension, α is the TCE, and ΔT is the change
/// in temperature" (Defining Multiple Temperature and Pressure Values). Which α applies:
/// <list type="bullet">
/// <item><b>Radii</b> (Radius of Curvature (CRVT) Values): a surface made of a catalog glass uses
/// that glass's TCE; an air or MIRROR surface uses the previous surface's glass when that is a
/// catalog glass, and its own TCE column otherwise. A glass whose catalog sets "ignore thermal
/// expansion" is not a solid (Modeling Gases and Liquids), and a surface without a catalog glass
/// (a constant index, a model glass) uses its TCE column (Adding TCE data).</item>
/// <item><b>Thicknesses</b> (Thickness (THIC) Values): a catalog glass expands with its TCE; any
/// other space with its TCE column (the mount), measured not on axis but "along a length of
/// material which extends from the edge of the surface to the edge of the next surface", at
/// each surface's mechanical semi-diameter (<see cref="AirSpaceThickness"/>).</item>
/// </list>
/// A catalog TCE is in 10⁻⁶/K (help: TCE, "the thermal coefficient of expansion in 1e-6/K";
/// ZemaxGlass manual, ED line, "in units of 10⁻⁶"); so is the TCE column. Owner decision OQ-12
/// (2026-11-12): a glass may name a mechanical <see cref="Material"/>
/// (<see cref="OpticsThermalSettings.GlassMaterials"/>); its
/// <see cref="Material.ThermalExpansionCoefficient"/> (per K) then replaces the catalog TCE.
/// docs/Optics-Conventions.md section 37.
/// </summary>
public static class LensThermalModel
{
    /// <summary>The TCE columns' and catalogs' unit: 10⁻⁶ per K.</summary>
    public const double TceUnit = 1e-6;

    /// <summary>The catalog glass a surface is made of, or null when its medium is air, a mirror,
    /// a constant index or a model glass. Throws <see cref="ArgumentException"/> naming the glass
    /// when it is not in <paramref name="catalogs"/>.</summary>
    public static OpticalMaterial? CatalogGlass(OpticsSurfaceSettings surface, IReadOnlyList<OpticalCatalog> catalogs, string where)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(catalogs);
        if (SavedSurfaces.IsMirror(surface) || string.IsNullOrWhiteSpace(surface.Material) || surface.Index is not null || surface.ModelGlass is not null)
            return null;
        return ProjectLens.FindGlass(surface.Material!.Trim(), catalogs)
            ?? throw new ArgumentException($"{where}: the glass '{surface.Material!.Trim()}' is not in the active catalogs, so its TCE is not known.");
    }

    /// <summary>The catalog glass of a surface when it is a solid: a catalog glass whose catalog
    /// does not set "ignore thermal expansion" (help: Modeling Gases and Liquids). Null otherwise.</summary>
    public static OpticalMaterial? SolidGlass(OpticsSurfaceSettings surface, IReadOnlyList<OpticalCatalog> catalogs, string where) =>
        CatalogGlass(surface, catalogs, where) is { IgnoreThermalExpansion: false } glass ? glass : null;

    /// <summary>
    /// A glass's expansion: the linked mechanical material's (OQ-12) when
    /// <paramref name="thermal"/> links one to the glass, else the catalog's TCE (ED line, the
    /// −30…+70 °C value, which OpticStudio "will use ... for any temperature range", help: TCE).
    /// Throws <see cref="ArgumentException"/> when neither gives a number.
    /// </summary>
    public static ExpansionCoefficient GlassExpansion(OpticalMaterial glass, OpticsThermalSettings? thermal, string where)
    {
        ArgumentNullException.ThrowIfNull(glass);
        if (LinkedMaterial(glass, thermal) is { } material)
        {
            return material.ThermalExpansionCoefficient is double a && double.IsFinite(a)
                ? new ExpansionCoefficient(a, $"the material '{material.Name}' linked to the glass {glass.Name} ({N(a)}/K)")
                : throw new ArgumentException($"{where}: the glass {glass.Name} is linked to the material '{material.Name}', which has no thermal expansion coefficient; enter one in the material library or remove the link.");
        }
        return glass.ThermalExpansion is double tce && double.IsFinite(tce)
            ? new ExpansionCoefficient(tce * TceUnit, $"the glass {glass.Name} (catalog TCE {N(tce)}e-6/K)")
            : throw new ArgumentException($"{where}: {glass.Who} has no TCE (no ED line in its catalog); link it to a mechanical material or add the TCE to the catalog.");
    }

    /// <summary>The mechanical material linked to a glass (OQ-12), or null. A link names the
    /// glass as "GLASS" or "CATALOG:GLASS", any case.</summary>
    public static Material? LinkedMaterial(OpticalMaterial glass, OpticsThermalSettings? thermal)
    {
        ArgumentNullException.ThrowIfNull(glass);
        if (thermal?.GlassMaterials is not { Count: > 0 } links) return null;
        string full = glass.Catalog.Length > 0 ? $"{glass.Catalog}:{glass.Name}" : glass.Name;
        foreach (var link in links)
        {
            string? name = link?.Glass?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (string.Equals(name, glass.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(name, full, StringComparison.OrdinalIgnoreCase))
                return link!.Material;
        }
        return null;
    }

    /// <summary>The TCE column of surface <paramref name="j"/>, per K.</summary>
    public static ExpansionCoefficient ColumnExpansion(OpticsSettings system, int j)
    {
        var s = Surface(system, j);
        return new ExpansionCoefficient(s.Tce * TceUnit, $"the TCE column of surface {j} ({N(s.Tce)}e-6/K)");
    }

    /// <summary>
    /// The expansion a radius (and every other length on the surface: semi-diameters) of surface
    /// <paramref name="j"/> follows, by the rule of the help's "Radius of Curvature (CRVT) Values":
    /// its own catalog glass; for an air or MIRROR surface the previous surface's catalog glass,
    /// else its own TCE column; for any other medium its TCE column.
    /// </summary>
    public static ExpansionCoefficient SurfaceExpansion(OpticsSettings system, int j, IReadOnlyList<OpticalCatalog> catalogs)
    {
        var s = Surface(system, j);
        string where = Where(system, j);
        if (SolidGlass(s, catalogs, where) is { } glass) return GlassExpansion(glass, system.Thermal, where);
        if ((SavedSurfaces.IsAir(s) || SavedSurfaces.IsMirror(s)) && j >= 1
            && SolidGlass(Surface(system, j - 1), catalogs, Where(system, j - 1)) is { } before)
            return GlassExpansion(before, system.Thermal, Where(system, j - 1));
        return ColumnExpansion(system, j);
    }

    /// <summary>The scale 1 + αΔT; throws when it is not positive (a dimension cannot shrink to
    /// nothing).</summary>
    public static double Scale(ExpansionCoefficient e, double deltaT, string where)
    {
        double s = 1 + e.Alpha * deltaT;
        return s > 0 && double.IsFinite(s) ? s
            : throw new ArgumentException($"{where}: 1 + αΔT = {N(s)} with α from {e.Source} and ΔT = {N(deltaT)} K; a length cannot scale by that.");
    }

    /// <summary>
    /// The thickness after surface <paramref name="n"/> at the new temperature, by the help's
    /// "Thickness (THIC) Values": surface <paramref name="n"/> made of a solid catalog glass
    /// scales by that glass's expansion; any other space is a mount (<see cref="AirSpaceThickness"/>).
    /// <paramref name="thickness"/> is the reference value in lens units; +infinity stays so.
    /// </summary>
    public static double Thickness(OpticsSettings reference, int n, double thickness, double deltaT,
        IReadOnlyList<OpticalCatalog> catalogs, ThermalLensCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (double.IsPositiveInfinity(thickness) || deltaT == 0) return thickness;
        string where = Where(reference, n);
        if (SolidGlass(Surface(reference, n), catalogs, where) is { } glass)
            return thickness * Scale(GlassExpansion(glass, reference.Thermal, where), deltaT, where);
        return AirSpaceThickness(reference, n, thickness, deltaT, catalogs, cache);
    }

    /// <summary>
    /// The thickness of a space that is not a solid glass (air, a liquid, a gas, a model glass),
    /// at the new temperature (help: Thickness (THIC) Values). The mount is a spacer whose length
    /// is the edge thickness E = T − Z_n(h_n) + Z_{n+1}(h_{n+1}) (help: Edge Thickness, with the
    /// sags Z taken at each surface's mechanical semi-diameter h, as the THIC page says), and which
    /// expands with the TCE column of surface n, α_m: E' = E(1 + α_mΔT). Each surface j grows by
    /// its own s_j = 1 + α_jΔT (<see cref="SurfaceExpansion"/>), so its sag becomes
    /// Z'_j(r) = s_j Z_j(r/s_j); the mount's contact moves radially to
    /// h'_j = min(h_j(1 + α_mΔT), h_j s_j) — the mount's own radial growth, but never past the
    /// lens's expanded mechanical edge (the help's two cases). The new centre thickness is
    /// T' = E' + Z'_n(h'_n) − Z'_{n+1}(h'_{n+1}). When every α is equal this is T(1 + αΔT).
    /// The mechanical semi-diameters are those of the reference configuration
    /// (<see cref="SemiDiameterSizing"/>: fixed ones as entered, automatic ones sized by rays).
    /// </summary>
    public static double AirSpaceThickness(OpticsSettings reference, int n, double thickness, double deltaT,
        IReadOnlyList<OpticalCatalog> catalogs, ThermalLensCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(catalogs);
        int count = reference.Surfaces?.Count ?? 0;
        string where = Where(reference, n);
        if (n < 0 || n + 1 >= count)
            throw new ArgumentException($"{where}: an air space's thermal thickness needs the next surface, and surface {n} is the last.");
        if (double.IsPositiveInfinity(thickness) || deltaT == 0) return thickness;
        var mount = ColumnExpansion(reference, n);
        var ea = SurfaceExpansion(reference, n, catalogs);
        var eb = SurfaceExpansion(reference, n + 1, catalogs);
        double sm = Scale(mount, deltaT, where), sa = Scale(ea, deltaT, where), sb = Scale(eb, deltaT, Where(reference, n + 1));

        cache ??= new ThermalLensCache(reference, catalogs);
        var (lens, mechanical, perUnit) = cache.Get();
        double ha = mechanical[n], hb = mechanical[n + 1];
        double za = Sag(lens, n, ha, where), zb = Sag(lens, n + 1, hb, Where(reference, n + 1));
        double t = thickness * perUnit;
        double edge = t - za + zb;
        double haNew = Math.Min(ha * sm, ha * sa), hbNew = Math.Min(hb * sm, hb * sb);
        double zaNew = sa * Sag(lens, n, haNew / sa, where), zbNew = sb * Sag(lens, n + 1, hbNew / sb, Where(reference, n + 1));
        double result = (edge * sm + zaNew - zbNew) / perUnit;
        return double.IsFinite(result) ? result
            : throw new ArgumentException($"{where}: the thermal air-space thickness is not finite (edge thickness {N(edge / perUnit)} lens units).");
    }

    private static double Sag(SequentialLens lens, int j, double r, string where)
    {
        double z = ElementVolume.Sag(lens.Surfaces[j], 0, r);
        return double.IsFinite(z) ? z
            : throw new ArgumentException($"{where}: the surface is not defined at its mechanical semi-diameter ({N(r)} m), so its edge thickness has no meaning.");
    }

    private static OpticsSurfaceSettings Surface(OpticsSettings system, int j)
    {
        ArgumentNullException.ThrowIfNull(system);
        var surfaces = system.Surfaces;
        if (surfaces is null || j < 0 || j >= surfaces.Count)
            throw new ArgumentException($"There is no surface {j}; the lens has surfaces 0 to {(surfaces?.Count ?? 0) - 1}.");
        return surfaces[j] ?? new OpticsSurfaceSettings();
    }

    private static string Where(OpticsSettings system, int j) => $"Surface {j}";

    private static string N(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>
/// The reference configuration's solved lens and mechanical semi-diameters, built once on first
/// use: an air space's thermal thickness reads its sags and edges
/// (<see cref="LensThermalModel.AirSpaceThickness"/>).
/// </summary>
public sealed class ThermalLensCache
{
    private readonly OpticsSettings _reference;
    private readonly IReadOnlyList<OpticalCatalog> _catalogs;
    private (SequentialLens Lens, IReadOnlyList<double> Mechanical, double PerUnit)? _built;

    public ThermalLensCache(OpticsSettings reference, IReadOnlyList<OpticalCatalog> catalogs)
    {
        _reference = reference ?? throw new ArgumentNullException(nameof(reference));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
    }

    /// <summary>The lens (meters), each surface's mechanical semi-diameter (meters) and the
    /// meters per lens unit.</summary>
    public (SequentialLens Lens, IReadOnlyList<double> Mechanical, double PerUnit) Get()
    {
        if (_built is { } b) return b;
        SequentialLens lens;
        try { lens = ProjectLens.From(_reference, _catalogs) ?? throw new ArgumentException("the reference configuration has no surfaces."); }
        catch (ArgumentException ex) { throw new ArgumentException($"the reference configuration's lens cannot be built for its edge thicknesses: {ex.Message}", ex); }
        var data = SystemDataSettings.FromSettings(_reference.SystemData, new List<string>());
        if (data.Validate() is { Count: > 0 } invalid)
            throw new ArgumentException($"the reference configuration's mechanical semi-diameters need valid system data: {string.Join(" ", invalid)}");
        SemiDiameterReport report;
        try { report = SemiDiameterSizing.Compute(lens, data); }
        catch (InvalidOperationException ex) { throw new ArgumentException($"the reference configuration's mechanical semi-diameters could not be sized: {ex.Message}", ex); }
        double perUnit = OpticsConventions.MetersPer(ProjectLens.LensUnitOf(_reference.SystemData));
        _built = (lens, report.Mechanical, perUnit);
        return _built.Value;
    }
}
