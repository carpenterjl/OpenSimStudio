using System.Text;

namespace OpenSim.Optics.Materials;

/// <summary>One shipped material as the manifest lists it.</summary>
public sealed record SeedEntry(string Catalog, string Name, string PagePath, string BookTitle, string PageTitle);

/// <summary>
/// The material data shipped with the program (OP-12): a selection of pages from the
/// refractiveindex.info database, which is CC0 1.0, embedded in this assembly unchanged together
/// with the database's LICENSE and permission statement. The selection and its reasons are in
/// docs/Optics-Conventions.md (materials section); the list is
/// <c>Materials/RefractiveIndexInfo/manifest.tsv</c>.
/// <para>
/// The pages become three built-in catalogs named after the database's shelves: RII-MAIN
/// (crystals, IR materials and coating oxides), RII-GLASS (non-vendor glasses) and RII-ORGANIC
/// (polymers). A material is named book-page ("SiO2-Malitson") and carries its page path and the
/// page's REFERENCES in <see cref="OpticalMaterial.Source"/>.
/// </para>
/// <para>
/// Loading never throws: a page that does not read is left out and named in
/// <see cref="Warnings"/> (the gate test requires none). The data are read once, on first use.
/// </para>
/// </summary>
public static class RefractiveIndexInfoSeed
{
    /// <summary>The manifest resource names of the shipped files start with this, followed by
    /// the file's path under Materials/RefractiveIndexInfo/ (with '/' or '\').</summary>
    internal const string ResourcePrefix = "riidb/";

    private static readonly Lazy<(IReadOnlyList<OpticalCatalog> Catalogs, IReadOnlyList<string> Warnings)> Loaded = new(Load);

    /// <summary>The shipped catalogs, in manifest order.</summary>
    public static IReadOnlyList<OpticalCatalog> Catalogs => Loaded.Value.Catalogs;

    /// <summary>Pages that did not load, one line each; empty when all did.</summary>
    public static IReadOnlyList<string> Warnings => Loaded.Value.Warnings;

    /// <summary>The materials the manifest lists, in its order.</summary>
    public static IReadOnlyList<SeedEntry> Manifest => ParseManifest(ReadResource("manifest.tsv"));

    /// <summary>The database's licence (CC0 1.0 Universal), as shipped beside the data.</summary>
    public static string LicenseText => ReadResource("LICENSE");

    /// <summary>The database author's statement waiving copyright, as shipped beside the data.</summary>
    public static string PermissionText => ReadResource("permission.txt");

    /// <summary>The text of a shipped page, by its database path ("main/SiO2/nk/Malitson.yml").</summary>
    public static string PageText(string pagePath) => ReadResource("data/" + pagePath);

    private static (IReadOnlyList<OpticalCatalog>, IReadOnlyList<string>) Load()
    {
        var warnings = new List<string>();
        IReadOnlyList<SeedEntry> manifest;
        try
        {
            manifest = Manifest;
        }
        catch (Exception e) when (e is InvalidDataException or FileNotFoundException)
        {
            warnings.Add($"The shipped material list could not be read: {e.Message}");
            return (Array.Empty<OpticalCatalog>(), warnings);
        }

        var byCatalog = new List<(string Name, List<OpticalMaterial> Materials)>();
        foreach (var entry in manifest)
        {
            OpticalMaterial material;
            try
            {
                material = RefractiveIndexInfoReader.Read(PageText(entry.PagePath), entry.PagePath, entry.Name, entry.Catalog,
                    entry.BookTitle, entry.PageTitle);
            }
            catch (Exception e) when (e is InvalidDataException or FileNotFoundException)
            {
                warnings.Add($"Shipped material '{entry.Name}' ({entry.PagePath}) was not loaded: {e.Message}");
                continue;
            }
            var group = byCatalog.FirstOrDefault(g => g.Name == entry.Catalog);
            if (group.Materials is null)
            {
                group = (entry.Catalog, new List<OpticalMaterial>());
                byCatalog.Add(group);
            }
            group.Materials.Add(material);
        }
        var catalogs = byCatalog.Select(g => new OpticalCatalog
        {
            Name = g.Name,
            Comments = new[]
            {
                $"Shipped with OpenSim Studio from the {RefractiveIndexInfoReader.DatabaseName}; each material names its page and references.",
            },
            Materials = g.Materials
        }).ToArray();
        return (catalogs, warnings);
    }

    internal static IReadOnlyList<SeedEntry> ParseManifest(string text)
    {
        var entries = new List<SeedEntry>();
        int lineNo = 0;
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            if (raw.Trim().Length == 0 || raw.StartsWith('#')) continue;
            var f = raw.Split('\t');
            if (f.Length != 5 || f.Take(3).Any(s => s.Trim().Length == 0))
                throw new InvalidDataException($"manifest.tsv line {lineNo}: expected five tab-separated fields (catalog, name, page path, book title, page title).");
            entries.Add(new SeedEntry(f[0].Trim(), f[1].Trim(), f[2].Trim(), f[3].Trim(), f[4].Trim()));
        }
        return entries;
    }

    private static readonly Lazy<Dictionary<string, string>> ResourceNames = new(() =>
        typeof(RefractiveIndexInfoSeed).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(n => n[ResourcePrefix.Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal));

    private static string ReadResource(string relativePath)
    {
        if (!ResourceNames.Value.TryGetValue(relativePath, out string? resource))
            throw new FileNotFoundException($"The shipped file '{relativePath}' is not embedded in {typeof(RefractiveIndexInfoSeed).Assembly.GetName().Name}.");
        using var stream = typeof(RefractiveIndexInfoSeed).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
