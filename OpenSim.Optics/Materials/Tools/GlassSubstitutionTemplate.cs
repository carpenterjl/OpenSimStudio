using System.Globalization;
using OpenSim.Core.Model;

namespace OpenSim.Optics.Materials;

/// <summary>A catalog glass glass substitution may choose, and the catalog it came from.</summary>
public sealed record SubstitutionCandidate(OpticalMaterial Glass, string CatalogName)
{
    /// <summary>The Material cell's text: "catalog:glass", so the lens finds this glass even
    /// when another active catalog has one of the same name.</summary>
    public string MaterialText => CatalogName.Length > 0 ? $"{CatalogName}:{Glass.Name}" : Glass.Name;
}

/// <summary>
/// The Glass Substitution Template applied (plan item OP-59; docs/Optics-Conventions.md §41).
/// Read from OpticStudio 2025 R2's help, saved under <c>plans/sources/optics/op-59-help/</c>:
/// "Glass Substitution Template (global optimizers group)", "Description of Catalog Data",
/// "Using Glass Substitution" and "Restricting Selected Glasses".
/// <list type="bullet">
/// <item>A glass flagged "Exclude Substitution" in its catalog is never chosen ("will not be
/// selected during global optimization, conversion from model to real glasses, or be considered
/// by the RGLA optimization operand").</item>
/// <item>Its valid range must contain every wavelength of the lens ("if the wavelength range is
/// suitable for the current lens"). Table glasses are not used ("Glass substitution does not use
/// table glasses or MIL number glasses"): a tabulated material is left out. MIL-number glasses
/// are not a material kind in this program.</item>
/// <item>With the template in use: the status must be one of the ticked four (a glass with no
/// status, and a Melt glass, whose status is not one of the four boxes, pass this test, as this
/// program's reading); each limit that is set and not zero must hold. A datum the catalog does
/// not record (absent, or −1, which "Description of Catalog Data" says means no data) fails the
/// test only with "Exclude Glasses With Incomplete Data" ("Only data corresponding to non-zero
/// template values are tested"; the status test counts as one when any status box is clear).</item>
/// </list>
/// The AGF catalogs record every quantity the template reads (OD line: relative cost, CR, FR,
/// SR, AR, PR; NM line: status, exclude-substitution; <see cref="AgfReader"/>). A material from the
/// refractiveindex.info seed records none of them, so it passes every test unless incomplete
/// data are excluded.
/// </summary>
public static class GlassSubstitutionTemplate
{
    /// <summary>Why <paramref name="glass"/> fails the template, or null when it passes (or
    /// when there is no template in use). <paramref name="excludeIncomplete"/> overrides the
    /// template's own "Exclude Glasses With Incomplete Data" when given.</summary>
    public static string? Reject(OpticsGlassSubstitutionSettings? template, OpticalMaterial glass, bool? excludeIncomplete = null)
    {
        ArgumentNullException.ThrowIfNull(glass);
        if (template is not { UseTemplate: true } t) return null;
        bool strict = excludeIncomplete ?? t.ExcludeIncompleteData;
        bool statusTested = !(t.AllowStandard && t.AllowPreferred && t.AllowObsolete && t.AllowSpecial);
        if (statusTested)
        {
            switch (glass.Status)
            {
                case GlassStatus.Standard when !t.AllowStandard:
                case GlassStatus.Preferred when !t.AllowPreferred:
                case GlassStatus.Obsolete when !t.AllowObsolete:
                case GlassStatus.Special when !t.AllowSpecial:
                    return $"its status is {glass.Status}, which the template does not allow";
                case null when strict:
                    return "the catalog records no status";
            }
        }
        return Limit("relative cost", glass.RelativeCost, t.MaxRelativeCost, strict)
            ?? Limit("climatic resistance (CR)", glass.ClimaticResistance, t.MaxClimaticResistance, strict)
            ?? Limit("stain resistance (FR)", glass.StainResistance, t.MaxStainResistance, strict)
            ?? Limit("acid resistance (SR)", glass.AcidResistance, t.MaxAcidResistance, strict)
            ?? Limit("alkali resistance (AR)", glass.AlkaliResistance, t.MaxAlkaliResistance, strict)
            ?? Limit("phosphate resistance (PR)", glass.PhosphateResistance, t.MaxPhosphateResistance, strict);
    }

    private static string? Limit(string what, double? value, double? limit, bool strict)
    {
        if (limit is not double max || max == 0) return null;
        if (value is not double v || v < 0) return strict ? $"the catalog records no {what}" : null;
        return v > max ? $"its {what} {F(v)} is above the template's {F(max)}" : null;
    }

    /// <summary>
    /// The glasses substitution may choose from <paramref name="catalogs"/> for a lens at
    /// <paramref name="wavelengths"/> (µm), in catalog order then file order: not excluded in the
    /// catalog, not tabulated, covering every wavelength, and passing the template. When
    /// <paramref name="catalogName"/> is given only that catalog is used; otherwise the template's
    /// <see cref="OpticsGlassSubstitutionSettings.Catalogs"/>, or every catalog when it names none.
    /// A glass of the same catalog and name listed twice is taken once. Throws
    /// <see cref="ArgumentException"/> naming a catalog that is not among <paramref name="catalogs"/>.
    /// </summary>
    public static IReadOnlyList<SubstitutionCandidate> Candidates(IReadOnlyList<OpticalCatalog> catalogs, IReadOnlyList<double> wavelengths,
        OpticsGlassSubstitutionSettings? template, string? catalogName = null, bool? excludeIncomplete = null, bool useTemplate = true)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(wavelengths);
        var names = !string.IsNullOrWhiteSpace(catalogName) ? new[] { catalogName.Trim() }
            : template?.Catalogs is { Count: > 0 } list ? list.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray()
            : null;
        IEnumerable<OpticalCatalog> chosen = catalogs;
        if (names is not null)
        {
            var missing = names.Where(n => !catalogs.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (missing.Length > 0)
                throw new ArgumentException($"Glass substitution names the catalog{(missing.Length == 1 ? "" : "s")} {string.Join(", ", missing.Select(m => $"'{m}'"))}, "
                    + $"which {(missing.Length == 1 ? "is" : "are")} not among the active catalogs ({string.Join(", ", catalogs.Select(c => c.Name))}).");
            chosen = catalogs.Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
        }
        var result = new List<SubstitutionCandidate>();
        var seen = new HashSet<(string, string)>();
        foreach (var catalog in chosen)
            foreach (var glass in catalog.Materials)
            {
                if (glass.ExcludeSubstitution || glass.Tabulated is not null) continue;
                if (!wavelengths.All(glass.Covers)) continue;
                if (useTemplate && Reject(template, glass, excludeIncomplete) is not null) continue;
                if (!seen.Add((catalog.Name.ToUpperInvariant(), glass.Name.ToUpperInvariant()))) continue;
                result.Add(new SubstitutionCandidate(glass, catalog.Name));
            }
        return result;
    }

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
