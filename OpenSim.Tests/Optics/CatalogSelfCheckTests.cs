using OpenSim.Optics.Materials;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics;

/// <summary>OP-11's gate: a catalog checks itself, nd and Vd from the coefficients against
/// the NM line's printed values, to the printed digits.</summary>
public sealed class CatalogSelfCheckTests
{
    private readonly ITestOutputHelper _out;

    public CatalogSelfCheckTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheFixture_ChecksItself_GlassByGlass()
    {
        var checks = CatalogSelfCheck.Run(AgfReader.Read(AgfReaderTests.FixturePath("OP11_FIXTURE.agf")))
            .ToDictionary(c => c.Material.Name + (c.Material.Comment.StartsWith("A second") ? " (2)" : ""));

        // Every formula 1–13, the E-notation copy and the duplicate pass on nd and Vd.
        foreach (var name in new[] { "SCHOTT-FORM", "SELLMEIER1-BK7", "HERZBERGER-FORM", "SELLMEIER2-FORM", "CONRADY-FORM", "SELLMEIER3-FORM",
                     "HANDBOOK1-FORM", "HANDBOOK2-FORM", "SELLMEIER4-FORM", "EXTENDED1-FORM", "SELLMEIER5-FORM", "EXTENDED2-FORM",
                     "EXTENDED3-FORM", "BK7-ENOTATION", "SELLMEIER1-BK7 (2)" })
        {
            var c = checks[name];
            Assert.True(c.Outcome == GlassCheckOutcome.Passed, $"{name}: {c.Reason}");
            Assert.True(c.NdPassed && c.VdChecked && c.VdPassed, name);
        }

        // The tolerance is half the last printed digit: "1.516372" → 5e−7, "1.51680E+00" → 5e−6.
        Assert.Equal(5e-7, checks["SCHOTT-FORM"].NdTolerance!.Value, 12);
        Assert.Equal(5e-6, checks["BK7-ENOTATION"].NdTolerance!.Value, 12);
        Assert.Equal(5e-5, checks["BK7-ENOTATION"].VdTolerance!.Value, 12);

        var bad = checks["BADND"];
        Assert.Equal(GlassCheckOutcome.Failed, bad.Outcome);
        Assert.False(bad.NdPassed);
        Assert.True(bad.VdPassed);
        Assert.Equal(1e-4, bad.NdError!.Value, 7);
        Assert.Contains("printed 1.516900", bad.Reason);

        var vdPlaceholder = checks["VD-PLACEHOLDER"];
        Assert.Equal(GlassCheckOutcome.Passed, vdPlaceholder.Outcome);
        Assert.False(vdPlaceholder.VdChecked);
        Assert.Contains("only nd was checked", vdPlaceholder.Reason);

        AssertNotChecked(checks["IR-PLACEHOLDER"], "placeholder (absent or ≤ 1)");
        AssertNotChecked(checks["UV-ONLY"], "does not contain the F, d and C lines");
        AssertNotChecked(checks["UNITY-PLACEHOLDER"], "n = 1 at d");
        AssertNotChecked(checks["CAUCHY-FORM"], "-2");

        Assert.Equal("16 of 17 checked glasses match the printed nd and Vd to the printed digits; 1 do not; 4 not checked.",
            CatalogSelfCheck.Summarize(checks.Values.ToList()));
    }

    private static void AssertNotChecked(GlassCheck check, string reason)
    {
        Assert.Equal(GlassCheckOutcome.NotChecked, check.Outcome);
        Assert.Contains(reason, check.Reason);
        Assert.Null(check.ComputedNd);
    }

    /// <summary>The repository root (the folder holding OpenSimStudio.sln), or null.</summary>
    private static string? RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OpenSimStudio.sln"))) return dir.FullName;
        return null;
    }

    /// <summary>
    /// The gate over real catalogs: the 23 AGF files downloaded with ZemaxGlass into
    /// <c>plans/sources/optics/zemaxglass/AGF_files</c>. They are vendor files that may not be
    /// redistributed, so they are not in the repository (plans/ is gitignored); where the folder
    /// is absent this test does nothing and says so in its output.
    /// <para>
    /// <b>Clean catalogs</b> (determined from the data, 2026-11-14): every checked glass of
    /// archer, arton, birefringent, corning, heraeus, hikari, hoya, infrared, lightpath, misc,
    /// nikon, ohara, rad_hard, rpo and topas matches the printed nd and Vd to the printed digits
    /// (the largest miss in each is under half a printed digit: hoya 0.997 of it, ohara 0.9998).
    /// isuzu and umicore have no glass the rules check (placeholder nd = 1.0).
    /// </para>
    /// <para>
    /// <b>Why every catalog gets only 5e−5 on nd.</b> Some vendors print the measured nd of a
    /// melt on the NM line and a least-squares fit over the whole spectrum as the CD
    /// coefficients. The fit does not pass exactly through the measured d-line value, so its
    /// residual at d can exceed half a printed digit; that is the vendor's fit, not this
    /// program's arithmetic (the clean catalogs, evaluated the same way, all match). Observed
    /// largest |computed − printed nd| over checked glasses: cdgm 1.09e−5 (144 of 162 glasses
    /// beyond the printed digits), sumita 3.77e−5 (45 of 192), zeon 2.10e−5 (3 of 3), schott
    /// 4.77e−6 (none beyond its five printed decimals), lzos 4.99e−7 (none). Vd misses, the
    /// same residual divided by the small nF − nC: cdgm 0.0915 (104 glasses), sumita 0.229 (43),
    /// zeon 0.126 (2), schott 0.00875 (5 glasses whose Vd is printed to two decimals or in
    /// E-notation), lzos 1.16e−6 (12 glasses whose Vd is printed to six decimals). 5e−5 is the
    /// observed worst (sumita 3.77e−5) with a margin, and still ten times finer than the 5e−4 a
    /// three-decimal nd claims.
    /// </para>
    /// <para>
    /// <b>liebetraut is a defective file</b> and is checked for its defect instead: every glass
    /// is a one-term Sellmeier with its resonance printed in C3 where formula 2 reads C2 (C2,
    /// C4, C5, C6 are all 0.0). As printed, every term is λ-independent, so the coefficients
    /// carry no dispersion at all (nF = nC exactly) and nd misses by up to 0.048. With the C3
    /// value moved to C2, nd matches the printed value within 1.7e−4, the limit of the three to
    /// five significant digits the coefficients are printed with.
    /// </para>
    /// </summary>
    [Fact]
    public void RealCatalogs_WhenPresent_CheckThemselves()
    {
        string? root = RepositoryRoot();
        string folder = root is null ? "" : Path.Combine(root, "plans", "sources", "optics", "zemaxglass", "AGF_files");
        if (root is null || !Directory.Exists(folder))
        {
            _out.WriteLine($"DID NOTHING: the real-catalog folder '{folder}' is not on this machine; only the committed fixtures were checked.");
            return;
        }

        string[] clean = { "archer", "arton", "birefringent", "corning", "heraeus", "hikari", "hoya", "infrared", "lightpath", "misc",
                           "nikon", "ohara", "rad_hard", "rpo", "topas" };
        var files = Directory.GetFiles(folder, "*.agf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.NotEmpty(files);
        int checkedGlasses = 0;
        foreach (string file in files)
        {
            var catalog = AgfReader.Read(file);
            // Every NM line became a glass: counted on the decoded text independently of the parser.
            int nmLines = AgfReader.Decode(File.ReadAllBytes(file)).Split('\n').Count(l => l.StartsWith("NM "));
            Assert.Equal(nmLines, catalog.Materials.Count);

            var checks = CatalogSelfCheck.Run(catalog);
            var done = checks.Where(c => c.Outcome != GlassCheckOutcome.NotChecked).ToList();
            checkedGlasses += done.Count;
            double ndMax = done.Select(c => c.NdError ?? double.PositiveInfinity).DefaultIfEmpty(0).Max();
            double vdMax = done.Where(c => c.VdChecked).Select(c => c.VdError ?? double.PositiveInfinity).DefaultIfEmpty(0).Max();
            _out.WriteLine($"{catalog.Name,-13} {catalog.Materials.Count,4} glasses, {done.Count,4} checked, " +
                           $"{done.Count(c => c.Outcome == GlassCheckOutcome.Failed),3} beyond the printed digits; max |Δnd| {ndMax:E2}, max |ΔVd| {vdMax:E2}");

            if (catalog.Name.Equals("liebetraut", StringComparison.OrdinalIgnoreCase))
            {
                AssertTheLiebetrautDefect(catalog, checks);
                continue;
            }
            if (clean.Contains(catalog.Name, StringComparer.OrdinalIgnoreCase))
                foreach (var c in done)
                    Assert.True(c.Outcome == GlassCheckOutcome.Passed, $"{catalog.Name} {c.Material.Name}: {c.Reason}");
            foreach (var c in done)
                Assert.True(c.NdError <= 5e-5, $"{catalog.Name} {c.Material.Name}: nd off by {c.NdError:E2}, beyond the vendors' fit residual. {c.Reason}");
        }
        Assert.True(checkedGlasses > 2000, $"only {checkedGlasses} glasses were checked across {files.Count} catalogs");
    }

    private static void AssertTheLiebetrautDefect(OpticalCatalog catalog, IReadOnlyList<GlassCheck> checks)
    {
        Assert.All(checks, c => Assert.Equal(GlassCheckOutcome.Failed, c.Outcome));
        foreach (var glass in catalog.Materials)
        {
            var c = glass.Coefficients;
            Assert.Equal(DispersionFormula.Sellmeier1, glass.Formula);
            Assert.True(c[1] == 0 && c[3] == 0 && c[4] == 0 && c[5] == 0, glass.Name);
            Assert.Equal(glass.RelativeIndex(0.4861327), glass.RelativeIndex(0.6562725));   // no dispersion as printed
            var moved = glass with { Coefficients = new[] { c[0], c[2], 0.0, 0.0, 0.0, 0.0 } };
            var check = CatalogSelfCheck.Check(moved);
            Assert.True(check.NdError < 2e-4, $"liebetraut {glass.Name}: with the resonance in C2, nd is off by {check.NdError:E2}");
        }
    }
}
