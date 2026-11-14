using System.Text.Json;

namespace OpenSim.Optics.Materials;

/// <summary>
/// The glass catalogs on offer: a built-in set overlaid by the AGF files in the user's catalog
/// folder, on the pattern of <c>MaterialLibrary</c> (built-ins plus a user file, a user entry
/// overriding a built-in of the same name). The built-in set is the CC0 seed from the
/// refractiveindex.info database (<see cref="RefractiveIndexInfoSeed"/>, OP-12: catalogs RII-MAIN,
/// RII-GLASS, RII-ORGANIC); vendor catalogs are not redistributable and enter by import only.
/// <para>
/// The folder is a per-user setting, kept in <c>%AppData%/OpenSimStudio/glass-catalogs.json</c>;
/// by default it is <c>%AppData%/OpenSimStudio/GlassCatalogs</c>. Which catalogs a lens uses is
/// the lens's (<c>OpticsMaterialsSettings.ActiveCatalogs</c>), not the library's. Load problems
/// never fail startup and are never silent: a file that does not read is left out and named in
/// <see cref="LoadWarnings"/>, for the log.
/// </para>
/// </summary>
public sealed class GlassCatalogLibrary
{
    /// <summary>The catalogs shipped with the program: the refractiveindex.info seed (OP-12).</summary>
    public static IReadOnlyList<OpticalCatalog> BuiltInCatalogs => RefractiveIndexInfoSeed.Catalogs;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _userDirectory;
    private readonly IReadOnlyList<OpticalCatalog> _builtIn;
    private readonly List<OpticalCatalog> _catalogs = new();
    private readonly HashSet<string> _userNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loadWarnings = new();
    private string? _settingWarning;
    private readonly IReadOnlyList<string> _builtInWarnings;

    public GlassCatalogLibrary() : this(DefaultUserDirectory) { }

    /// <summary>Test seam: a library rooted at a private directory, with its own built-ins.</summary>
    internal GlassCatalogLibrary(string userDirectory, IReadOnlyList<OpticalCatalog>? builtIn = null)
    {
        _userDirectory = userDirectory;
        _builtIn = builtIn ?? BuiltInCatalogs;
        _builtInWarnings = builtIn is null ? RefractiveIndexInfoSeed.Warnings : Array.Empty<string>();
        CatalogFolder = ReadFolderSetting() ?? DefaultFolder;
        Reload();
    }

    private static string DefaultUserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSimStudio");

    private string SettingsFile => Path.Combine(_userDirectory, "glass-catalogs.json");

    /// <summary>The folder used when the user has not chosen one.</summary>
    public string DefaultFolder => Path.Combine(_userDirectory, "GlassCatalogs");

    /// <summary>The folder whose *.agf files are loaded as user catalogs.</summary>
    public string CatalogFolder { get; private set; }

    /// <summary>Built-in catalogs first, then the folder's, each set in name order.</summary>
    public IReadOnlyList<OpticalCatalog> Catalogs => _catalogs;

    /// <summary>Files that did not load and catalogs overridden, from the last load, for the log.</summary>
    public IReadOnlyList<string> LoadWarnings => _loadWarnings;

    /// <summary>True when the catalog came from the user's folder.</summary>
    public bool IsUserCatalog(string name) => _userNames.Contains(name);

    /// <summary>The catalog of that name, ignoring case; null when none is loaded.</summary>
    public OpticalCatalog? FindCatalog(string name) =>
        _catalogs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first glass of that name in <paramref name="catalogNames"/>, searched in the
    /// order given (OpticStudio's rule for a glass found in several active catalogs is not read
    /// at a source; first-listed is this program's choice). Null when none has it.</summary>
    public OpticalMaterial? FindGlass(string glassName, IEnumerable<string> catalogNames)
    {
        foreach (var name in catalogNames)
            if (FindCatalog(name)?.Find(glassName) is { } glass) return glass;
        return null;
    }

    /// <summary>Re-reads the folder. Built-ins are kept; a user file overrides a built-in of the
    /// same name, with a warning.</summary>
    public void Reload()
    {
        _catalogs.Clear();
        _userNames.Clear();
        _loadWarnings.Clear();
        if (_settingWarning is not null) _loadWarnings.Add(_settingWarning);
        _loadWarnings.AddRange(_builtInWarnings);
        _catalogs.AddRange(_builtIn.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));
        if (!Directory.Exists(CatalogFolder)) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(CatalogFolder, "*.agf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _loadWarnings.Add($"Glass catalog folder '{CatalogFolder}' could not be read: {e.Message}");
            return;
        }
        foreach (string file in files)
        {
            OpticalCatalog catalog;
            try
            {
                catalog = AgfReader.Read(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _loadWarnings.Add($"Glass catalog '{file}' was not loaded: {e.Message}");
                continue;
            }
            int existing = _catalogs.FindIndex(c => string.Equals(c.Name, catalog.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                _loadWarnings.Add($"Glass catalog '{file}' replaces the built-in catalog '{_catalogs[existing].Name}'.");
                _catalogs[existing] = catalog;
            }
            else _catalogs.Add(catalog);
            _userNames.Add(catalog.Name);
        }
    }

    /// <summary>Uses another folder for the user catalogs, saves the choice and reloads.</summary>
    public void SetCatalogFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("The catalog folder needs a path.", nameof(folder));
        CatalogFolder = Path.GetFullPath(folder);
        Directory.CreateDirectory(_userDirectory);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new FolderSetting { Folder = CatalogFolder }, JsonOptions));
        Reload();
    }

    /// <summary>
    /// Copies an AGF file into the catalog folder and loads it. The file is read first, so a
    /// file that does not parse throws (<see cref="InvalidDataException"/>, naming the line) and
    /// nothing is copied. A user catalog of the same name is replaced only when
    /// <paramref name="replace"/> is true; otherwise the import is refused, naming it.
    /// </summary>
    public OpticalCatalog Import(string agfPath, bool replace = false)
    {
        var parsed = AgfReader.Read(agfPath);
        string target = Path.Combine(CatalogFolder, Path.GetFileName(agfPath));
        if (!string.Equals(Path.GetFullPath(agfPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(target) && !replace)
                throw new InvalidOperationException($"The catalog folder already has '{Path.GetFileName(agfPath)}'; import it again with replace to overwrite it.");
            Directory.CreateDirectory(CatalogFolder);
            File.Copy(agfPath, target, overwrite: true);
        }
        Reload();
        return FindCatalog(parsed.Name) ?? throw new InvalidOperationException($"Catalog '{parsed.Name}' was copied to '{target}' but did not load: {string.Join(" ", _loadWarnings)}");
    }

    private string? ReadFolderSetting()
    {
        if (!File.Exists(SettingsFile)) return null;
        try
        {
            var setting = JsonSerializer.Deserialize<FolderSetting>(File.ReadAllText(SettingsFile), JsonOptions);
            return string.IsNullOrWhiteSpace(setting?.Folder) ? null : setting.Folder;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _settingWarning = $"Glass catalog setting '{SettingsFile}' was not read ({e.Message}); the default folder is used.";
            return null;
        }
    }

    private sealed record FolderSetting
    {
        public string? Folder { get; init; }
    }
}
