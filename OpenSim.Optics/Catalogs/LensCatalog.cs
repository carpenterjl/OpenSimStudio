using System.Text.Json;
using System.Text.Json.Serialization;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Catalogs;

// OP-16: a stock lens catalog (docs/Optics-Conventions.md section 53).

/// <summary>
/// A stock lens catalog: the lenses of one vendor (or of the user's own files), each with the
/// search data the Lens Catalogs tool lists (OpticStudio 2025 R2 help, Libraries tab › Stock
/// Parts Group › Lens Catalogs and Make Private Lens Catalog;
/// plans/sources/optics/op-16-help/).
/// <para>
/// A catalog is built from a folder of Zemax lens files (.zmx, or the lens in a .zar), each read
/// with OP-94's reader (<see cref="ZemaxLensFiles"/>, section 52) as it is (<see cref="FromFolder"/>),
/// or loaded from a private catalog file (<see cref="Load"/>), which <see cref="Save"/> writes
/// with every lens file's bytes inside it — the help's "convert a folder full of ZMX files to a
/// stock lens catalog file". OpticStudio's own .ZMF format has no public description and is not
/// read or written; the private catalog is this program's JSON file (<see cref="FileExtension"/>).
/// </para>
/// <para>
/// A file that cannot be read, or whose lens cannot be traced, is left out of the catalog and
/// named in <see cref="Problems"/> with the reason; nothing is dropped without a line saying so.
/// </para>
/// </summary>
public sealed class LensCatalog
{
    /// <summary>The private catalog file's extension.</summary>
    public const string FileExtension = ".osslcat";

    /// <summary>The open and save dialogs' filter for private catalogs.</summary>
    public const string FileFilter = "Stock lens catalogs (*.osslcat)|*.osslcat|All files (*.*)|*.*";

    /// <summary>The first line of a private catalog's "format" field.</summary>
    public const string FormatName = "OpenSim Studio stock lens catalog";

    /// <summary>The private catalog format version written.</summary>
    public const int FormatVersion = 1;

    private readonly IReadOnlyDictionary<string, byte[]>? _contents;

    private LensCatalog(string name, string? folder, string? file, IReadOnlyList<StockLens> lenses,
        IReadOnlyList<string> problems, IReadOnlyDictionary<string, byte[]>? contents)
    {
        Name = name;
        Folder = folder;
        FilePath = file;
        Lenses = lenses;
        Problems = problems;
        _contents = contents;
    }

    /// <summary>The catalog's name, shown as the vendor.</summary>
    public string Name { get; }

    /// <summary>The folder the lens files are in (a folder catalog), or null.</summary>
    public string? Folder { get; }

    /// <summary>The private catalog file it was loaded from, or null.</summary>
    public string? FilePath { get; }

    /// <summary>The lenses, sorted by part name (ordinal, case ignored), then file name.</summary>
    public IReadOnlyList<StockLens> Lenses { get; }

    /// <summary>Files left out and why, one line each naming the file.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The part name of a lens file: the first space-delimited word of its name without
    /// the extension (help, Make Private Lens Catalog: "a file with the name 'Lens number 5.ZMX'
    /// will be assigned a part name of 'Lens'").</summary>
    public static string PartNameOf(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        int space = stem.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? stem : stem[..space];
    }

    /// <summary>True for a file this catalog reads (.zmx or .zar, case ignored).</summary>
    public static bool IsLensFile(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ZemaxLensFiles.LensExtension, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ZemaxLensFiles.ArchiveExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The catalog of every .zmx and .zar file directly in <paramref name="folder"/> (sub-folders
    /// are not read), named <paramref name="name"/> or after the folder. Glass names are checked
    /// against <paramref name="glassCatalogs"/> as the import does; <paramref name="options"/> are
    /// the import's (strict by default: a file with content the reader does not hold is left out
    /// and named). Throws <see cref="DirectoryNotFoundException"/> naming the folder when it is not there.
    /// </summary>
    public static LensCatalog FromFolder(string folder, IReadOnlyList<OpticalCatalog> glassCatalogs, string? name = null, ZemaxImportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(glassCatalogs);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The lens folder '{folder}' does not exist.");
        string catalogName = string.IsNullOrWhiteSpace(name) ? new DirectoryInfo(folder).Name : name.Trim();
        var files = Directory.EnumerateFiles(folder).Where(IsLensFile).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        var lenses = new List<StockLens>();
        var problems = new List<string>();
        foreach (string path in files)
        {
            string file = Path.GetFileName(path);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{file}: not read ({ex.Message}).");
                continue;
            }
            if (Index(catalogName, file, bytes, glassCatalogs, options, problems) is { } lens) lenses.Add(lens);
        }
        if (files.Count == 0) problems.Add($"The folder '{folder}' has no .zmx or .zar files.");
        NameDuplicates(lenses, problems);
        return new LensCatalog(catalogName, Path.GetFullPath(folder), null, Sorted(lenses), problems, null);
    }

    /// <summary>The lens of one file's bytes, or null with the reason added to <paramref name="problems"/>.</summary>
    private static StockLens? Index(string catalogName, string file, byte[] bytes, IReadOnlyList<OpticalCatalog> glassCatalogs,
        ZemaxImportOptions? options, List<string> problems)
    {
        try
        {
            var result = Read(file, bytes, glassCatalogs, options);
            var data = StockLensProperties.Of(result.Optics, glassCatalogs, result.ArchiveCatalogs);
            if (result.Warnings.Count > 0)
                problems.Add($"{file}: indexed with {result.Warnings.Count} item{(result.Warnings.Count == 1 ? "" : "s")} not read ({string.Join(" ", result.Warnings)}).");
            return new StockLens
            {
                PartName = PartNameOf(file),
                Vendor = catalogName,
                Source = file,
                Title = string.IsNullOrWhiteSpace(result.Optics.SystemData?.Title) ? null : result.Optics.SystemData!.Title,
                EffectiveFocalLengthMm = data.EffectiveFocalLengthMm,
                EntrancePupilDiameterMm = data.EntrancePupilDiameterMm,
                Shape = data.Shape,
                Type = data.Type,
                Elements = data.Elements,
                LensUnit = result.Optics.SystemData?.LensUnit,
            };
        }
        catch (Exception ex) when (ex is ZemaxFileException or InvalidDataException or ArgumentException or InvalidOperationException or FormatException)
        {
            problems.Add($"{file}: left out: {ex.Message.Replace(Environment.NewLine, " ")}");
            return null;
        }
    }

    /// <summary>Reads a lens file's bytes, a .zmx or a .zar by the file name's extension.</summary>
    private static ZemaxImportResult Read(string file, byte[] bytes, IReadOnlyList<OpticalCatalog> glassCatalogs, ZemaxImportOptions? options)
    {
        if (string.Equals(Path.GetExtension(file), ZemaxLensFiles.ArchiveExtension, StringComparison.OrdinalIgnoreCase))
            return ZemaxLensFiles.ImportArchive(bytes, file, glassCatalogs, options);
        var (text, encoding) = ZemaxText.Decode(bytes);
        return ZemaxLensReader.Read(text, glassCatalogs, options, encoding);
    }

    /// <summary>The raw bytes of a lens's file: from the folder, or from inside the private catalog.</summary>
    public byte[] FileBytes(StockLens lens)
    {
        ArgumentNullException.ThrowIfNull(lens);
        if (!Lenses.Contains(lens)) throw new ArgumentException($"The lens '{lens.PartName}' ({lens.Source}) is not in the catalog '{Name}'.");
        if (_contents is not null)
            return _contents.TryGetValue(lens.Source, out var b) ? b
                : throw new InvalidDataException($"The catalog '{Name}' holds no file '{lens.Source}'.");
        string path = Path.Combine(Folder!, lens.Source);
        if (!File.Exists(path)) throw new FileNotFoundException($"The lens file '{path}' of catalog '{Name}' is no longer there; reload the catalog.", path);
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// The lens as an optical model, read again from its file with <paramref name="glassCatalogs"/>
    /// (the Lens Catalogs tool's Load and Insert read the file, not the index).
    /// </summary>
    public ZemaxImportResult Import(StockLens lens, IReadOnlyList<OpticalCatalog> glassCatalogs, ZemaxImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(glassCatalogs);
        return Read(lens.Source, FileBytes(lens), glassCatalogs, options);
    }

    /// <summary>
    /// Writes the catalog as a private catalog file: its name, every lens's search data and the
    /// bytes of every lens file, so the file stands on its own (Make Private Lens Catalog: "All
    /// lens files in this folder will be placed in the ZMF or ZLC file"). The problems are kept.
    /// </summary>
    public void Save(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new CatalogFile
        {
            Format = FormatName,
            Version = FormatVersion,
            Name = string.IsNullOrWhiteSpace(name) ? Name : name.Trim(),
            Problems = Problems.ToList(),
            Lenses = Lenses.Select(l => new CatalogEntry
            {
                PartName = l.PartName, Source = l.Source, Title = l.Title,
                EffectiveFocalLengthMm = l.EffectiveFocalLengthMm, EntrancePupilDiameterMm = l.EntrancePupilDiameterMm,
                Shape = l.ShapeCode, Type = l.TypeCode, Elements = l.Elements, LensUnit = l.LensUnit,
                Content = Convert.ToBase64String(FileBytes(l)),
            }).ToList(),
        };
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(file, Json));
    }

    /// <summary>
    /// Make Private Lens Catalog: the catalog of a folder of lens files (<see cref="FromFolder"/>)
    /// written to <paramref name="path"/> and returned as loaded from there.
    /// </summary>
    public static LensCatalog MakePrivate(string sourceFolder, string path, IReadOnlyList<OpticalCatalog> glassCatalogs, string? name = null,
        ZemaxImportOptions? options = null)
    {
        string catalogName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name.Trim();
        var catalog = FromFolder(sourceFolder, glassCatalogs, catalogName, options);
        if (catalog.Lenses.Count == 0)
            throw new InvalidDataException($"Make Private Lens Catalog: no lens of '{sourceFolder}' could be read, so no catalog was written:"
                + Environment.NewLine + string.Join(Environment.NewLine, catalog.Problems));
        catalog.Save(path, catalogName);
        return Load(path);
    }

    /// <summary>
    /// Loads a private catalog file. Throws <see cref="InvalidDataException"/> naming the file and
    /// the entry when it is not one, is a newer version, or an entry is incomplete or has a code
    /// that is not a shape or type code.
    /// </summary>
    public static LensCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        CatalogFile? file;
        try { file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(path), Json); }
        catch (JsonException ex) { throw new InvalidDataException($"The stock lens catalog '{path}' is not valid JSON: {ex.Message}", ex); }
        if (file is null || !string.Equals(file.Format, FormatName, StringComparison.Ordinal))
            throw new InvalidDataException($"'{path}' is not a stock lens catalog (its format field is not '{FormatName}').");
        if (file.Version > FormatVersion)
            throw new InvalidDataException($"The stock lens catalog '{path}' is format version {file.Version}; this build reads version {FormatVersion} and earlier.");
        string catalogName = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileNameWithoutExtension(path) : file.Name!;
        var lenses = new List<StockLens>();
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int i = 0;
        foreach (var e in file.Lenses ?? new List<CatalogEntry>())
        {
            i++;
            string where = $"The stock lens catalog '{path}', lens {i}";
            if (string.IsNullOrWhiteSpace(e.PartName) || string.IsNullOrWhiteSpace(e.Source) || string.IsNullOrEmpty(e.Content))
                throw new InvalidDataException($"{where}: the part name, file name and content are all needed.");
            if (e.Shape is not { Length: 1 } || e.Type is not { Length: 1 })
                throw new InvalidDataException($"{where} ({e.PartName}): the shape and type must be one-letter codes.");
            if (contents.ContainsKey(e.Source!))
                throw new InvalidDataException($"{where} ({e.PartName}): the file name '{e.Source}' appears twice.");
            try
            {
                contents[e.Source!] = Convert.FromBase64String(e.Content!);
                lenses.Add(new StockLens
                {
                    PartName = e.PartName!, Vendor = catalogName, Source = e.Source!, Title = e.Title,
                    EffectiveFocalLengthMm = e.EffectiveFocalLengthMm, EntrancePupilDiameterMm = e.EntrancePupilDiameterMm,
                    Shape = StockLensCodes.ParseShape(e.Shape[0]), Type = StockLensCodes.ParseType(e.Type[0]),
                    Elements = e.Elements, LensUnit = e.LensUnit,
                });
            }
            catch (FormatException ex) { throw new InvalidDataException($"{where} ({e.PartName}): {ex.Message}", ex); }
        }
        return new LensCatalog(catalogName, null, Path.GetFullPath(path), Sorted(lenses), file.Problems?.ToArray() ?? Array.Empty<string>(), contents);
    }

    private static IReadOnlyList<StockLens> Sorted(IEnumerable<StockLens> lenses) => lenses
        .OrderBy(l => l.PartName, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Source, StringComparer.OrdinalIgnoreCase).ToArray();

    private static void NameDuplicates(List<StockLens> lenses, List<string> problems)
    {
        foreach (var g in lenses.GroupBy(l => l.PartName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Part name '{g.Key}' is given by {g.Count()} files ({string.Join(", ", g.Select(l => l.Source))}); all are listed, told apart by file name.");
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class CatalogFile
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Name { get; set; }
        public List<CatalogEntry>? Lenses { get; set; }
        public List<string>? Problems { get; set; }
    }

    private sealed class CatalogEntry
    {
        public string? PartName { get; set; }
        public string? Source { get; set; }
        public string? Title { get; set; }
        public double? EffectiveFocalLengthMm { get; set; }
        public double? EntrancePupilDiameterMm { get; set; }
        public string? Shape { get; set; }
        public string? Type { get; set; }
        public int Elements { get; set; }
        public string? LensUnit { get; set; }
        public string? Content { get; set; }
    }
}
