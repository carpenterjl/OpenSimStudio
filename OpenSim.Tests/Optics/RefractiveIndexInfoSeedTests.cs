using OpenSim.Optics.Materials;
using OpenSim.Optics;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-12 gate: the material data shipped in OpenSim.Optics (pages of the refractiveindex.info
/// database, CC0 1.0, embedded with their licence). Every page loads, every material names its
/// source, and every material evaluated densely across its valid range gives a finite n above 1
/// and, where k is given, a finite k ≥ 0. Spot values are copied from the shipped files
/// themselves (OpenSim.Optics/Materials/RefractiveIndexInfo/data/…, identical to the database
/// clone in plans/sources/optics/riidb) or computed in the test from those files' coefficients.
/// </summary>
public sealed class RefractiveIndexInfoSeedTests
{
    private static IEnumerable<OpticalMaterial> All => RefractiveIndexInfoSeed.Catalogs.SelectMany(c => c.Materials);

    private static OpticalMaterial Get(string name) =>
        All.Single(m => m.Name == name);

    [Fact]
    public void EveryListedPage_Loads_IntoTheThreeCatalogs_WithNoWarnings()
    {
        Assert.Empty(RefractiveIndexInfoSeed.Warnings);
        var manifest = RefractiveIndexInfoSeed.Manifest;
        Assert.Equal(manifest.Count, All.Count());
        Assert.Equal(manifest.Select(e => e.Name), All.Select(m => m.Name));
        Assert.Equal(manifest.Count, manifest.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(new[] { "RII-MAIN", "RII-GLASS", "RII-ORGANIC" }, RefractiveIndexInfoSeed.Catalogs.Select(c => c.Name));
        // Nonlinear-index pages (catalog-n2.yml) and vendor datasheet copies (data/specs) are not shipped.
        Assert.DoesNotContain(manifest, e => e.PagePath.Contains("/n2/") || e.PagePath.StartsWith("specs/", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryShippedMaterial_CarriesItsSourceReference()
    {
        foreach (var m in All)
        {
            var s = m.Source;
            Assert.True(s is not null, $"{m.Name} has no source.");
            Assert.False(string.IsNullOrWhiteSpace(s!.PagePath), $"{m.Name} has no page path.");
            Assert.False(string.IsNullOrWhiteSpace(s.PlainReferences), $"{m.Name} ({s.PagePath}) has empty REFERENCES.");
            Assert.False(string.IsNullOrWhiteSpace(s.BookTitle), $"{m.Name} has no book title.");
            Assert.Equal(RefractiveIndexInfoReader.DatabaseName, s.Database);
            Assert.Equal(FormulaNumbering.RefractiveIndexInfo, m.Numbering);
        }
    }

    [Fact]
    public void TheLicence_ShipsBesideTheData()
    {
        Assert.StartsWith("CC0 1.0 Universal", RefractiveIndexInfoSeed.LicenseText);
        Assert.Contains("refractiveindex.info database is in the public domain", RefractiveIndexInfoSeed.PermissionText);
    }

    /// <summary>
    /// The gate. Each material is sampled at 4001 wavelengths spaced evenly in log λ across its
    /// valid range, ends included, plus every table row; k is sampled the same way across its own
    /// range (intersected with the index range, since k is read only where n is). The shipped
    /// pages were chosen so this holds: pages whose n falls below 1 inside an absorption band
    /// (diamond, Phillip; Ta2O5, Bright; Nb2O5, Franta; BK7, Lane; LZOS K108, Bassarab) were left
    /// out for another page of the same material, or left out.
    /// </summary>
    [Fact]
    public void EveryShippedMaterial_GivesFiniteNAboveOne_AndKAtLeastZero_AcrossItsRange()
    {
        foreach (var m in All)
        {
            var (lo, hi) = m.ValidRange!.Value;
            foreach (double w in Samples(lo, hi, m.Tabulated?.Points.Select(p => p.Micrometers)))
            {
                double n = m.RelativeIndex(w);
                Assert.True(double.IsFinite(n) && n > 1, $"{m.Name} ({m.Source!.PagePath}): n = {n} at {w} µm.");
            }
            if (m.ExtinctionRange is not { } kr) continue;
            double klo = Math.Max(kr.Min, lo), khi = Math.Min(kr.Max, hi);
            foreach (double w in Samples(klo, khi, m.Extinction?.Points.Select(p => p.Micrometers)).Where(w => w >= klo && w <= khi))
            {
                double k = m.ExtinctionCoefficient(w)!.Value;
                Assert.True(double.IsFinite(k) && k >= 0, $"{m.Name} ({m.Source!.PagePath}): k = {k} at {w} µm.");
            }
        }
    }

    private static IEnumerable<double> Samples(double lo, double hi, IEnumerable<double>? rows)
    {
        const int count = 4000;
        var grid = Enumerable.Range(0, count + 1).Select(i => i == 0 ? lo : i == count ? hi : lo * Math.Pow(hi / lo, (double)i / count));
        return rows is null ? grid : grid.Concat(rows);
    }

    /// <summary>Fused silica, main/SiO2/nk/Malitson.yml: "type: formula 1", "wavelength_range:
    /// 0.21 6.7", "coefficients: 0 0.6961663 0.0684043 0.4079426 0.1162414 0.8974794 9.896161".
    /// The Sellmeier form is written out here independently of the evaluator. The range matches
    /// the page title in catalog-nk.yml, "Malitson 1965: n 0.21–6.7 µm" (the database's unit is µm).</summary>
    [Fact]
    public void FusedSilica_Malitson_MatchesItsCoefficients_WorkedInTheTest()
    {
        var m = Get("SiO2-Malitson");
        Assert.Equal((0.21, 6.7), m.ValidRange);
        Assert.Equal("Malitson 1965: n 0.21–6.7 µm", m.Source!.PageTitle);
        foreach (double w in new[] { 0.21, SpectralLines.LowerD.Micrometers, 1.0, 3.0, 6.7 })
        {
            double l2 = w * w;
            double expected = Math.Sqrt(1 + 0.6961663 * l2 / (l2 - 0.0684043 * 0.0684043)
                                          + 0.4079426 * l2 / (l2 - 0.1162414 * 0.1162414)
                                          + 0.8974794 * l2 / (l2 - 9.896161 * 9.896161));
            Assert.Equal(expected, m.RelativeIndex(w), 1e-15);
        }
    }

    /// <summary>Cubic ZnS, main/ZnS/nk/Debenham.yml: "type: formula 4", "wavelength_range: 0.405 13",
    /// "coefficients: 8.393 0.14383 0 0.2421 2 4430.99 0 36.71 2", i.e. n² = 8.393 +
    /// 0.14383 λ⁰/(λ² − 0.2421²) + 4430.99 λ⁰/(λ² − 36.71²), written out here.</summary>
    [Fact]
    public void ZincSulfide_Debenham_MatchesItsCoefficients_WorkedInTheTest()
    {
        var m = Get("ZnS-Debenham");
        foreach (double w in new[] { 0.405, 0.6328, 4.0, 10.6, 13.0 })
        {
            double l2 = w * w;
            double expected = Math.Sqrt(8.393 + 0.14383 / (l2 - 0.2421 * 0.2421) + 4430.99 / (l2 - 36.71 * 36.71));
            Assert.Equal(expected, m.RelativeIndex(w), 1e-14);
        }
    }

    /// <summary>Values the shipped tables state. Si at 293 K (main/Si/nk/Li-293K.yml, "tabulated n"):
    /// rows "1.20 3.5167" and "1.22 3.5133", and no k. Clear soda-lime glass
    /// (glass/misc/soda-lime/nk/Rubin-clear.yml): formula 5 "coefficients: 1.5130 -0.003169 2
    /// 0.003962 -2" and a "tabulated k" whose first row is "0.31 4.996E-5".</summary>
    [Fact]
    public void TabulatedRows_AreReturnedAtTheirWavelengths()
    {
        var si = Get("Si-Li-293K");
        Assert.Equal(3.5167, si.RelativeIndex(1.20));
        Assert.Equal(3.5133, si.RelativeIndex(1.22));
        Assert.Null(si.ExtinctionCoefficient(1.21));

        var soda = Get("soda-lime-Rubin-clear");
        Assert.Equal(4.996E-5, soda.ExtinctionCoefficient(0.31)!.Value);
        double w = 0.5;
        Assert.Equal(1.5130 - 0.003169 * w * w + 0.003962 / (w * w), soda.RelativeIndex(w), 1e-15);
    }

    [Fact]
    public void TheGlassLibrary_OffersTheSeed_AsBuiltIns()
    {
        string dir = Directory.CreateTempSubdirectory("opensim-seed-").FullName;
        try
        {
            var library = new GlassCatalogLibrary(dir);
            Assert.Empty(library.LoadWarnings);
            Assert.Equal(new[] { "RII-GLASS", "RII-MAIN", "RII-ORGANIC" }, library.Catalogs.Select(c => c.Name));
            Assert.False(library.IsUserCatalog("RII-MAIN"));
            Assert.Equal("RII-MAIN", library.FindGlass("caf2-li", new[] { "RII-GLASS", "RII-MAIN" })!.Catalog);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
