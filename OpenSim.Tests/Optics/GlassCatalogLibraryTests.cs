using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>OP-11: the catalog manager — built-ins plus the user's folder, import, the folder
/// setting, and glass lookup through the active catalogs.</summary>
public sealed class GlassCatalogLibraryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("opensim-glass-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>A library over the test directory without the shipped catalogs, so a test sees
    /// only the user folder (the shipped seed has its own tests, RefractiveIndexInfoSeedTests).</summary>
    private GlassCatalogLibrary UserOnly() => new(_directory, Array.Empty<OpticalCatalog>());

    private static OpticalCatalog Catalog(string name, params string[] glasses) => new()
    {
        Name = name,
        Materials = glasses.Select(g => new OpticalMaterial { Name = g, Catalog = name, FormulaNumber = 2 }).ToArray()
    };

    [Fact]
    public void AFreshLibrary_HasOnlyTheBuiltIns_AndNoWarnings()
    {
        var library = new GlassCatalogLibrary(_directory);
        Assert.Equal(GlassCatalogLibrary.BuiltInCatalogs.Count, library.Catalogs.Count);    // the OP-12 seed
        Assert.NotEmpty(library.Catalogs);
        Assert.All(library.Catalogs, c => Assert.False(library.IsUserCatalog(c.Name)));
        Assert.Empty(library.LoadWarnings);
        Assert.Equal(Path.Combine(_directory, "GlassCatalogs"), library.CatalogFolder);

        var withBuiltIns = new GlassCatalogLibrary(_directory, new[] { Catalog("SEED", "A") });
        Assert.Equal("SEED", withBuiltIns.Catalogs.Single().Name);
        Assert.False(withBuiltIns.IsUserCatalog("SEED"));
    }

    [Fact]
    public void Import_CopiesTheFile_AndLoadsIt_RefusingASecondCopyUnlessReplacing()
    {
        var library = UserOnly();
        var imported = library.Import(AgfReaderTests.FixturePath("OP11_FIXTURE.agf"));
        Assert.Equal("OP11_FIXTURE", imported.Name);
        Assert.True(File.Exists(Path.Combine(library.CatalogFolder, "OP11_FIXTURE.agf")));
        Assert.True(library.IsUserCatalog("op11_fixture"));
        Assert.Same(imported, library.FindCatalog("op11_fixture"));

        var again = Assert.Throws<InvalidOperationException>(() => library.Import(AgfReaderTests.FixturePath("OP11_FIXTURE.agf")));
        Assert.Contains("already has 'OP11_FIXTURE.agf'", again.Message);
        library.Import(AgfReaderTests.FixturePath("OP11_FIXTURE.agf"), replace: true);
        Assert.Single(library.Catalogs);

        // A new library over the same folder finds it again.
        Assert.Equal(21, UserOnly().FindCatalog("OP11_FIXTURE")!.Materials.Count);
    }

    [Fact]
    public void AFileThatDoesNotRead_IsRefusedOnImport_AndNamedOnLoad()
    {
        string bad = Path.Combine(_directory, "BROKEN.agf");
        File.WriteAllText(bad, "NM A 2 0 1.5 60\nCD 1 x 3\n");
        var library = UserOnly();
        Assert.Throws<InvalidDataException>(() => library.Import(bad));
        Assert.False(File.Exists(Path.Combine(library.CatalogFolder, "BROKEN.agf")));

        Directory.CreateDirectory(library.CatalogFolder);
        File.Copy(bad, Path.Combine(library.CatalogFolder, "BROKEN.agf"));
        library.Reload();
        Assert.Empty(library.Catalogs);
        Assert.Contains(library.LoadWarnings, w => w.Contains("BROKEN.agf") && w.Contains("line 2"));
    }

    [Fact]
    public void TheFolderSetting_IsSaved_AndReadByTheNextLibrary()
    {
        string folder = Path.Combine(_directory, "my catalogs");
        Directory.CreateDirectory(folder);
        File.Copy(AgfReaderTests.FixturePath("TEST_CAT.agf"), Path.Combine(folder, "TEST_CAT.agf"));

        var library = UserOnly();
        library.SetCatalogFolder(folder);
        Assert.Equal("TEST_CAT", library.Catalogs.Single().Name);

        var next = UserOnly();
        Assert.Equal(Path.GetFullPath(folder), next.CatalogFolder);
        Assert.Equal("TEST_CAT", next.Catalogs.Single().Name);

        File.WriteAllText(Path.Combine(_directory, "glass-catalogs.json"), "{ not json");
        var broken = UserOnly();
        Assert.Equal(broken.DefaultFolder, broken.CatalogFolder);
        Assert.Contains(broken.LoadWarnings, w => w.Contains("glass-catalogs.json"));
    }

    [Fact]
    public void AUserCatalog_OverridesABuiltInOfTheSameName_WithAWarning()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "GlassCatalogs"));
        File.Copy(AgfReaderTests.FixturePath("TEST_CAT.agf"), Path.Combine(_directory, "GlassCatalogs", "TEST_CAT.agf"));
        var library = new GlassCatalogLibrary(_directory, new[] { Catalog("test_cat", "OLD") });
        Assert.Single(library.Catalogs);
        Assert.NotNull(library.FindCatalog("TEST_CAT")!.Find("MG523"));
        Assert.Contains(library.LoadWarnings, w => w.Contains("replaces the built-in"));
    }

    [Fact]
    public void FindGlass_SearchesTheActiveCatalogsInTheOrderGiven()
    {
        var library = new GlassCatalogLibrary(_directory, new[] { Catalog("A", "X", "Y"), Catalog("B", "Y", "Z") });
        Assert.Equal("A", library.FindGlass("y", new[] { "A", "B" })!.Catalog);
        Assert.Equal("B", library.FindGlass("Y", new[] { "B", "A" })!.Catalog);
        Assert.Equal("B", library.FindGlass("Z", new[] { "missing", "B" })!.Catalog);
        Assert.Null(library.FindGlass("Z", new[] { "A" }));
    }
}
