using System.Text.Json;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Catalogs;

// OP-16: the stock lens catalog folder (docs/Optics-Conventions.md section 53).

/// <summary>
/// The stock lens catalogs of one folder, the counterpart of OpticStudio's <c>&lt;data&gt;\Stockcat</c>
/// folder ("The vendor files must be placed in the &lt;data&gt;\Stockcat folder"; help, Lens
/// Catalogs): each private catalog file (<see cref="LensCatalog.FileExtension"/>) in it is one
/// catalog, and each sub-folder holding .zmx or .zar files is one catalog named after the
/// sub-folder (its files are read when the library opens). No vendor catalog ships with the
/// program; the user supplies the vendor files.
/// </summary>
public sealed class LensCatalogLibrary
{
    private LensCatalogLibrary(string folder, IReadOnlyList<LensCatalog> catalogs, IReadOnlyList<string> problems)
    {
        Folder = folder;
        Catalogs = catalogs;
        Problems = problems;
    }

    /// <summary>The library folder.</summary>
    public string Folder { get; }

    /// <summary>The catalogs, sorted by name (ordinal, case ignored).</summary>
    public IReadOnlyList<LensCatalog> Catalogs { get; }

    /// <summary>Catalogs not loaded and lens files left out, one line each naming the file.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The catalog of that name (case ignored), or null.</summary>
    public LensCatalog? Find(string name) =>
        Catalogs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Opens the library at <paramref name="folder"/>. A folder that does not exist gives an empty
    /// library with a line saying so; a private catalog that does not load is named in the
    /// problems with the reason and left out, as are the lens files a folder catalog leaves out.
    /// Two catalogs of the same name are both kept, the second named in the problems.
    /// </summary>
    public static LensCatalogLibrary Open(string folder, IReadOnlyList<OpticalCatalog> glassCatalogs, ZemaxImportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(glassCatalogs);
        var catalogs = new List<LensCatalog>();
        var problems = new List<string>();
        if (!Directory.Exists(folder))
        {
            problems.Add($"The stock lens catalog folder '{folder}' does not exist; it has no catalogs.");
            return new LensCatalogLibrary(Path.GetFullPath(folder), catalogs, problems);
        }
        foreach (string file in Directory.EnumerateFiles(folder, "*" + LensCatalog.FileExtension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try { catalogs.Add(LensCatalog.Load(file)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(file)}: not loaded: {ex.Message}");
            }
        }
        foreach (string dir in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.EnumerateFiles(dir).Any(LensCatalog.IsLensFile)) continue;
            var catalog = LensCatalog.FromFolder(dir, glassCatalogs, null, options);
            catalogs.Add(catalog);
            problems.AddRange(catalog.Problems.Select(p => $"{catalog.Name}: {p}"));
        }
        foreach (var g in catalogs.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"{g.Count()} catalogs are named '{g.Key}'; a search by vendor name finds the first.");
        var sorted = catalogs.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new LensCatalogLibrary(Path.GetFullPath(folder), sorted, problems);
    }

    // ---------------------------------------------------------------- the folder setting

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string UserDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSimStudio");

    /// <summary>The folder used when the user has not chosen one (beside the glass catalogs').</summary>
    public static string DefaultFolder => Path.Combine(UserDirectory, "StockLensCatalogs");

    private static string SettingsFile(string? userDirectory) => Path.Combine(userDirectory ?? UserDirectory, "stock-lens-catalogs.json");

    /// <summary>The folder the user chose, or <see cref="DefaultFolder"/>; a setting file that
    /// does not read gives the default and a line in <paramref name="notes"/>.</summary>
    public static string ChosenFolder(ICollection<string>? notes = null, string? userDirectory = null)
    {
        string file = SettingsFile(userDirectory);
        string fallback = userDirectory is null ? DefaultFolder : Path.Combine(userDirectory, "StockLensCatalogs");
        if (!File.Exists(file)) return fallback;
        try
        {
            var setting = JsonSerializer.Deserialize<FolderSetting>(File.ReadAllText(file), JsonOptions);
            return string.IsNullOrWhiteSpace(setting?.Folder) ? fallback : setting.Folder;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            notes?.Add($"Stock lens catalog setting '{file}' was not read ({e.Message}); the default folder is used.");
            return fallback;
        }
    }

    /// <summary>Saves the folder choice.</summary>
    public static void ChooseFolder(string folder, string? userDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string file = SettingsFile(userDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(new FolderSetting { Folder = Path.GetFullPath(folder) }, JsonOptions));
    }

    private sealed record FolderSetting
    {
        public string? Folder { get; init; }
    }
}
