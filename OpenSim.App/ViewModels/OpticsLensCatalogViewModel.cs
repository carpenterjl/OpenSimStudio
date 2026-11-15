using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.App.ViewModels;

// OP-16: the Lens Catalogs, Make Private Lens Catalog and Design Templates dialog.

/// <summary>One line of the search results.</summary>
public sealed record StockLensRow(StockLens Lens, LensCatalog Catalog, string Line)
{
    public override string ToString() => Line;
}

/// <summary>One input of the selected design template, edited as text.</summary>
public sealed partial class DesignTemplateInputRow : ObservableObject
{
    public DesignTemplateInputRow(DesignTemplateParameter parameter)
    {
        Parameter = parameter;
        _value = parameter.Default;
    }

    public DesignTemplateParameter Parameter { get; }
    public string Label => string.IsNullOrEmpty(Parameter.Unit) ? Parameter.Name : $"{Parameter.Name} ({Parameter.Unit})";
    public string Description => Parameter.Description;

    [ObservableProperty] private string _value;
}

/// <summary>
/// Lens Catalogs (plan item OP-16; OpticStudio 2025 R2 help, "Lens Catalogs", "Make Private Lens
/// Catalog", "Insert Lens", "Design Templates"): the library folder's catalogs
/// (<see cref="LensCatalogLibrary"/>), the search criteria, Search, Catalog Report, Load (replaces
/// the workspace's lens), Insert (one undoable edit of the Lens Data Editor, with the insert
/// options), default ranges from a surface group, Make Private Lens Catalog, and the design
/// templates with their inputs and Load.
/// </summary>
public sealed partial class OpticsLensCatalogViewModel : ObservableObject
{
    public const string AllCatalogs = "All catalogs";

    private readonly Func<OpticsSettings> _system;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _glass;
    private readonly Func<OpticsSettings, bool, bool, string, string?> _apply;
    private readonly Func<OpticsSettings, IReadOnlyList<string>> _load;
    private readonly Action<string> _log;
    private LensCatalogLibrary? _library;

    /// <param name="system">The project's optics record now.</param>
    /// <param name="glass">Every loaded glass catalog (lens files name their own).</param>
    /// <param name="apply">Writes a record as one undoable edit (flags: the multi-configuration
    /// table changed, the merit function changed; then the edit's name); returns why it was refused, or null.</param>
    /// <param name="load">Replaces the workspace's optical model; returns its notes.</param>
    /// <param name="log">A line for the log.</param>
    /// <param name="libraryFolder">The library folder; null reads the user's choice.</param>
    public OpticsLensCatalogViewModel(Func<OpticsSettings> system, Func<IReadOnlyList<OpticalCatalog>> glass,
        Func<OpticsSettings, bool, bool, string, string?> apply, Func<OpticsSettings, IReadOnlyList<string>> load, Action<string> log,
        string? libraryFolder = null)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _glass = glass ?? throw new ArgumentNullException(nameof(glass));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _libraryFolder = libraryFolder ?? LensCatalogLibrary.ChosenFolder();
        _selectedElements = ElementChoices[0];
        RefreshTemplates();
    }

    // ---------------------------------------------------------------- library

    /// <summary>The folder whose catalogs are listed (private catalog files and vendor sub-folders).</summary>
    [ObservableProperty] private string _libraryFolder;

    public ObservableCollection<string> Vendors { get; } = new() { AllCatalogs };

    [ObservableProperty] private string _selectedVendor = AllCatalogs;

    /// <summary>When true, a lens file with content the reader does not hold is indexed with
    /// that content listed instead of being left out.</summary>
    [ObservableProperty] private bool _readUnsupportedAsWarnings;

    /// <summary>The open library's catalogs (empty until <see cref="ReloadCommand"/>).</summary>
    public IReadOnlyList<LensCatalog> Catalogs => _library?.Catalogs ?? Array.Empty<LensCatalog>();

    /// <summary>Uses another library folder, remembered for next time when <paramref name="save"/>, and reloads.</summary>
    public void SetLibraryFolder(string folder, bool save)
    {
        LibraryFolder = folder;
        if (save) LensCatalogLibrary.ChooseFolder(folder);
        Reload();
    }

    /// <summary>Reads the library folder again.</summary>
    [RelayCommand]
    private void Reload()
    {
        _library = LensCatalogLibrary.Open(LibraryFolder, _glass(), Options());
        string keep = SelectedVendor;
        Vendors.Clear();
        Vendors.Add(AllCatalogs);
        foreach (var c in _library.Catalogs) Vendors.Add(c.Name);
        SelectedVendor = Vendors.Contains(keep) ? keep : AllCatalogs;
        foreach (var p in _library.Problems) _log("Lens catalogs: " + p);
        int lenses = _library.Catalogs.Sum(c => c.Lenses.Count);
        Status = $"{_library.Catalogs.Count} catalog{(_library.Catalogs.Count == 1 ? "" : "s")}, {lenses} lens{(lenses == 1 ? "" : "es")} in {LibraryFolder}"
            + (_library.Problems.Count > 0 ? $"; {_library.Problems.Count} problem{(_library.Problems.Count == 1 ? "" : "s")}, listed below and in the log." : ".");
        Details = string.Join(Environment.NewLine, _library.Problems);
        OnPropertyChanged(nameof(Catalogs));
    }

    private ZemaxImportOptions Options() => new() { UnsupportedAsWarnings = ReadUnsupportedAsWarnings };

    private IEnumerable<LensCatalog> Searched() =>
        SelectedVendor == AllCatalogs || string.IsNullOrEmpty(SelectedVendor) ? Catalogs
        : Catalogs.Where(c => string.Equals(c.Name, SelectedVendor, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- criteria

    [ObservableProperty] private bool _useFocalLength;
    [ObservableProperty] private double _focalLengthMin = 0;
    [ObservableProperty] private double _focalLengthMax = 100;
    [ObservableProperty] private bool _useDiameter;
    [ObservableProperty] private double _diameterMin = 0;
    [ObservableProperty] private double _diameterMax = 25;
    [ObservableProperty] private bool _shapeEqui;
    [ObservableProperty] private bool _shapeBi;
    [ObservableProperty] private bool _shapePlano;
    [ObservableProperty] private bool _shapeMeniscus;
    [ObservableProperty] private bool _typeSpherical;
    [ObservableProperty] private bool _typeGrin;
    [ObservableProperty] private bool _typeAspheric;
    [ObservableProperty] private bool _typeToroidal;
    [ObservableProperty] private string _partText = "";

    public IReadOnlyList<InteractiveChoice<int?>> ElementChoices { get; } =
        new[] { new InteractiveChoice<int?>("Any #", null) }
            .Concat(Enumerable.Range(1, 6).Select(n => new InteractiveChoice<int?>(n.ToString(CultureInfo.InvariantCulture), n))).ToArray();

    [ObservableProperty] private InteractiveChoice<int?> _selectedElements;

    /// <summary>The criteria as set.</summary>
    public LensSearchCriteria Criteria()
    {
        var shapes = new HashSet<StockLensShape>();
        if (ShapeEqui) shapes.Add(StockLensShape.Equi);
        if (ShapeBi) shapes.Add(StockLensShape.Bi);
        if (ShapePlano) shapes.Add(StockLensShape.Plano);
        if (ShapeMeniscus) shapes.Add(StockLensShape.Meniscus);
        var types = new HashSet<StockLensType>();
        if (TypeSpherical) types.Add(StockLensType.Spherical);
        if (TypeGrin) types.Add(StockLensType.Grin);
        if (TypeAspheric) types.Add(StockLensType.Aspheric);
        if (TypeToroidal) types.Add(StockLensType.Toroidal);
        return new LensSearchCriteria
        {
            UseEffectiveFocalLength = UseFocalLength, FocalLengthMinMm = FocalLengthMin, FocalLengthMaxMm = FocalLengthMax,
            UseEntrancePupilDiameter = UseDiameter, DiameterMinMm = DiameterMin, DiameterMaxMm = DiameterMax,
            Shapes = shapes, Types = types, Elements = SelectedElements?.Value, Text = string.IsNullOrWhiteSpace(PartText) ? null : PartText,
        };
    }

    /// <summary>First surface of the group the default ranges are taken from.</summary>
    [ObservableProperty] private int _groupFirst = 1;

    /// <summary>Last surface of that group.</summary>
    [ObservableProperty] private int _groupLast = 2;

    /// <summary>Sets the focal-length and diameter ranges to the surface group's values ±5%
    /// (help, Lens Catalogs).</summary>
    [RelayCommand]
    private void RangesFromSurfaces()
    {
        try
        {
            var system = _system();
            var context = new LensEditContext(system.SystemData, GlassLookup.ActiveCatalogs(_glass(), system.Materials?.ActiveCatalogs));
            var group = StockLensProperties.OfGroup(system.Surfaces ?? new List<OpticsSurfaceSettings>(), GroupFirst, GroupLast, context);
            var c = LensSearchCriteria.Around(group);
            UseFocalLength = c.UseEffectiveFocalLength;
            if (c.UseEffectiveFocalLength) { FocalLengthMin = c.FocalLengthMinMm; FocalLengthMax = c.FocalLengthMaxMm; }
            UseDiameter = c.UseEntrancePupilDiameter;
            if (c.UseEntrancePupilDiameter) { DiameterMin = c.DiameterMinMm; DiameterMax = c.DiameterMaxMm; }
            Status = string.Create(CultureInfo.InvariantCulture,
                $"Surfaces {GroupFirst} to {GroupLast}: EFL {(group.EffectiveFocalLengthMm is double f ? f.ToString("G8", CultureInfo.InvariantCulture) + " mm" : "afocal")}, diameter {group.DiameterMm:G8} mm; ranges set to ±5%.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Status = ex.Message; }
    }

    // ---------------------------------------------------------------- search and results

    public ObservableCollection<StockLensRow> Results { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadLensCommand))]
    [NotifyCanExecuteChangedFor(nameof(InsertLensCommand))]
    private StockLensRow? _selectedResult;

    [ObservableProperty] private string _status = "Choose a library folder holding private catalogs (.osslcat) or one sub-folder of .zmx/.zar/.seq/.len files per vendor, then Search.";

    /// <summary>The report, the problems, or the notes of the last action, one line each.</summary>
    [ObservableProperty] private string _details = "";

    [RelayCommand]
    private void Search()
    {
        if (_library is null) Reload();
        try
        {
            var found = new List<StockLensRow>();
            var criteria = Criteria();
            foreach (var catalog in Searched())
                foreach (var lens in LensCatalogSearch.Find(new[] { catalog }, criteria))
                    found.Add(new StockLensRow(lens, catalog, (SelectedVendor == AllCatalogs ? catalog.Name + ": " : "") + LensCatalogSearch.ResultLine(lens)));
            Results.Clear();
            foreach (var r in found) Results.Add(r);
            SelectedResult = Results.FirstOrDefault();
            Status = $"{found.Count} lens{(found.Count == 1 ? "" : "es")} found.";
        }
        catch (ArgumentException ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void Report()
    {
        if (_library is null) Reload();
        try
        {
            var criteria = Criteria();
            var lenses = LensCatalogSearch.Find(Searched(), criteria);
            Details = string.Join(Environment.NewLine, LensCatalogSearch.Report(SelectedVendor, criteria, lenses));
            Status = $"Catalog report of {lenses.Count} lens{(lenses.Count == 1 ? "" : "es")}.";
        }
        catch (ArgumentException ex) { Status = ex.Message; }
    }

    private bool HasSelection() => SelectedResult is not null;

    /// <summary>Loads the selected lens into the workspace, replacing the current lens (help: "Loads
    /// the currently selected lens file into the Lens Data Editor").</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void LoadLens()
    {
        var row = SelectedResult!;
        try
        {
            var result = row.Catalog.Import(row.Lens, _glass(), Options());
            var lines = result.Warnings.Select(w => "Not read: " + w).Concat(result.Notes).Concat(_load(result.Optics)).ToList();
            foreach (var l in lines) _log($"Lens catalogs ({row.Lens.PartName}): {l}");
            Status = $"{row.Lens.PartName} loaded from {row.Catalog.Name}." + (lines.Count > 0 ? " Notes are in the log." : "");
            Details = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is ZemaxFileException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"{row.Lens.PartName} not loaded: {ex.Message}";
        }
    }

    /// <summary>The surface the inserted lens goes before.</summary>
    [ObservableProperty] private int _insertAt = 1;

    [ObservableProperty] private bool _ignoreObject;
    [ObservableProperty] private bool _changeSignInMirrorSpace = true;
    [ObservableProperty] private bool _reverseOnInsert;

    /// <summary>Inserts the selected lens before surface <see cref="InsertAt"/> as one undoable edit.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void InsertLens()
    {
        var row = SelectedResult!;
        try
        {
            var system = _system();
            var file = row.Catalog.Import(row.Lens, _glass(), Options());
            var result = StockLensInsertion.Insert(system, file.Optics, InsertAt, new InsertLensOptions
            {
                IgnoreObject = IgnoreObject, ChangeSignInMirrorSpace = ChangeSignInMirrorSpace, Reverse = ReverseOnInsert,
            });
            string? refusal = _apply(result.Settings, !ReferenceEquals(result.Settings.MultiConfiguration, system.MultiConfiguration),
                !ReferenceEquals(result.Settings.MeritFunction, system.MeritFunction), $"Insert Lens {row.Lens.PartName}");
            if (refusal is not null) { Status = $"Not inserted: {refusal}"; return; }
            var lines = file.Warnings.Select(w => "Not read: " + w).Concat(result.Notes).ToList();
            foreach (var l in lines) _log($"Insert Lens ({row.Lens.PartName}): {l}");
            Status = $"{row.Lens.PartName} inserted as surfaces {result.First} to {result.Last}. The Lens Data Editor's Undo takes it back.";
            Details = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is ZemaxFileException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Status = $"{row.Lens.PartName} not inserted: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- Make Private Lens Catalog

    /// <summary>The folder of .zmx/.zar/.seq/.len files to convert.</summary>
    [ObservableProperty] private string _privateSource = "";

    /// <summary>The catalog's name; the file is &lt;name&gt;.osslcat in the library folder.</summary>
    [ObservableProperty] private string _privateName = "";

    [RelayCommand]
    private void MakePrivateCatalog()
    {
        if (string.IsNullOrWhiteSpace(PrivateSource)) { Status = "Choose the source folder of .zmx/.zar/.seq/.len files first."; return; }
        string name = string.IsNullOrWhiteSpace(PrivateName) ? new DirectoryInfo(PrivateSource).Name : PrivateName.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Status = $"The catalog name '{name}' cannot be a file name."; return; }
        string path = Path.Combine(LibraryFolder, name + LensCatalog.FileExtension);
        try
        {
            var catalog = LensCatalog.MakePrivate(PrivateSource, path, _glass(), name, Options());
            foreach (var p in catalog.Problems) _log($"Make Private Lens Catalog ({name}): {p}");
            Reload();
            SelectedVendor = Vendors.FirstOrDefault(v => string.Equals(v, catalog.Name, StringComparison.OrdinalIgnoreCase)) ?? AllCatalogs;
            Status = $"Catalog '{catalog.Name}' written to {path}: {catalog.Lenses.Count} lens{(catalog.Lenses.Count == 1 ? "" : "es")}"
                + (catalog.Problems.Count > 0 ? $", {catalog.Problems.Count} problem{(catalog.Problems.Count == 1 ? "" : "s")} listed below." : ".");
            Details = string.Join(Environment.NewLine, catalog.Problems);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"No catalog written: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- design templates

    /// <summary>Text the template's name, kind or description must contain.</summary>
    [ObservableProperty] private string _templateFilter = "";

    partial void OnTemplateFilterChanged(string value) => RefreshTemplates();

    public ObservableCollection<DesignTemplate> Templates { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadTemplateCommand))]
    private DesignTemplate? _selectedTemplate;

    partial void OnSelectedTemplateChanged(DesignTemplate? value)
    {
        TemplateInputs.Clear();
        if (value is null) return;
        foreach (var p in value.Parameters) TemplateInputs.Add(new DesignTemplateInputRow(p));
    }

    public ObservableCollection<DesignTemplateInputRow> TemplateInputs { get; } = new();

    private void RefreshTemplates()
    {
        var keep = SelectedTemplate;
        Templates.Clear();
        foreach (var t in DesignTemplates.Filter(TemplateFilter)) Templates.Add(t);
        SelectedTemplate = keep is not null && Templates.Contains(keep) ? keep : Templates.FirstOrDefault();
    }

    private bool HasTemplate() => SelectedTemplate is not null;

    /// <summary>Builds the selected template from its inputs and loads it, replacing the current lens.</summary>
    [RelayCommand(CanExecute = nameof(HasTemplate))]
    private void LoadTemplate()
    {
        var t = SelectedTemplate!;
        try
        {
            var values = TemplateInputs.ToDictionary(r => r.Parameter.Name, r => r.Value, StringComparer.OrdinalIgnoreCase);
            var result = t.Build(values, _glass());
            var lines = result.Notes.Concat(_load(result.Optics)).ToList();
            foreach (var l in lines) _log($"Design template ({t.Name}): {l}");
            Status = $"Template '{t.Name}' loaded.";
            Details = string.Join(Environment.NewLine, lines);
        }
        catch (ArgumentException ex) { Status = ex.Message; }
    }
}
