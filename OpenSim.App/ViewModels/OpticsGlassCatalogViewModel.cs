using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Materials;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Optics workspace's materials panel (plan item OP-11): the glass catalogs on offer, which
/// of them the lens uses, and a view of one glass's catalog data with the catalog's self-check.
/// It belongs to <see cref="OpticsViewModel"/>, which saves the active catalogs with the lens
/// and publishes them with the system; browsing (the selected catalog and glass, the filter,
/// the probe wavelength) is not part of the system and publishes nothing. The glass data are
/// shown, not edited: an editor that writes catalogs back is not built.
/// </summary>
public sealed partial class OpticsGlassCatalogViewModel : ObservableObject
{
    private readonly GlassCatalogLibrary _library;
    private readonly Action _activeCatalogsChanged;
    private readonly Func<OpticsEnvironment>? _environment;

    /// <summary>True when the lens names its catalogs; false means every loaded catalog
    /// (the saved form is then null).</summary>
    private bool _explicitSelection;

    /// <summary>Set while rows are rebuilt, so a programmatic check-box change is not taken
    /// for the user's.</summary>
    private bool _rebuilding;

    /// <param name="environment">The system's environment, for the probe's adjusted index
    /// (OP-13); null shows the catalog index only.</param>
    public OpticsGlassCatalogViewModel(GlassCatalogLibrary library, Action activeCatalogsChanged, Func<OpticsEnvironment>? environment = null)
    {
        _library = library;
        _activeCatalogsChanged = activeCatalogsChanged;
        _environment = environment;
        RebuildRows(activeNames: null);
        SelectedCatalog = Catalogs.FirstOrDefault();
    }

    /// <summary>Every catalog loaded, then any saved active catalog this machine lacks.</summary>
    public ObservableCollection<OpticsCatalogRow> Catalogs { get; } = new();

    /// <summary>The folder the user catalogs are read from.</summary>
    public string CatalogFolder => _library.CatalogFolder;

    /// <summary>What the last import, reload or folder change did.</summary>
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glasses), nameof(CatalogNotes))]
    private OpticsCatalogRow? _selectedCatalog;

    /// <summary>Shows only the glasses whose name contains this text (any case).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glasses))]
    private string _glassFilter = "";

    [ObservableProperty] private OpticalMaterial? _selectedGlass;

    /// <summary>A wavelength to evaluate the selected glass at, µm; the d line to start with.</summary>
    [ObservableProperty] private double _probeMicrometers = SpectralLines.LowerD.Micrometers;

    /// <summary>The self-check of the selected catalog, glass by glass.</summary>
    [ObservableProperty] private IReadOnlyList<GlassCheck> _catalogChecks = Array.Empty<GlassCheck>();

    /// <summary>The active catalogs' names in search order: what a glass name is looked up in.</summary>
    public IReadOnlyList<string> ActiveCatalogNames => Catalogs.Where(r => r.IsActive).Select(r => r.Name).ToArray();

    /// <summary>The selected catalog as read, or null (none selected, or not loaded).</summary>
    public OpticalCatalog? SelectedCatalogData => SelectedCatalog is null ? null : _library.FindCatalog(SelectedCatalog.Name);

    /// <summary>The selected catalog's glasses that pass the filter.</summary>
    public IReadOnlyList<OpticalMaterial> Glasses =>
        SelectedCatalogData?.Materials.Where(m => GlassFilter.Length == 0 || m.Name.Contains(GlassFilter, StringComparison.OrdinalIgnoreCase)).ToArray()
        ?? (IReadOnlyList<OpticalMaterial>)Array.Empty<OpticalMaterial>();

    public string CatalogSummary => SelectedCatalogData is null
        ? (SelectedCatalog is null ? "" : $"Catalog '{SelectedCatalog.Name}' is not loaded on this machine; import its AGF file to use it.")
        : CatalogSelfCheck.Summarize(CatalogChecks);

    /// <summary>The reader's notes on the selected catalog, one per line.</summary>
    public string CatalogNotes => SelectedCatalogData is { Notes.Count: > 0 } c ? string.Join("\n", c.Notes) : "";

    /// <summary>The selected glass's self-check.</summary>
    public GlassCheck? SelectedCheck => SelectedGlass is null ? null : CatalogChecks.FirstOrDefault(c => ReferenceEquals(c.Material, SelectedGlass));

    public bool HasSelectedGlass => SelectedGlass is not null;

    // ---- The selected glass, as the form shows it ----

    public string GlassFormula => SelectedGlass switch
    {
        null => "",
        { Tabulated: not null } => "Tabulated n, k",
        { FormulaNumber: int n } g => DispersionFormulas.NameOf(n) + (g.Formula is { } f && !DispersionFormulas.IsVerified(f) ? " — unverified form" : ""),
        _ => "none"
    };

    public string GlassCoefficients => SelectedGlass is null ? "" : string.Join("  ", SelectedGlass.Coefficients.Select(Number));

    public string GlassRange => SelectedGlass?.ValidRange is { } r ? $"{Number(r.Min)} – {Number(r.Max)}" : SelectedGlass is null ? "" : "not stated";

    public string GlassNdVd => SelectedGlass is null ? "" : $"{SelectedGlass.Nd?.Text ?? "—"} / {SelectedGlass.Vd?.Text ?? "—"}";

    public string GlassComputedNdVd => SelectedCheck is { ComputedNd: double nd, ComputedVd: double vd } ? $"{nd:F6} / {vd:F4}" : SelectedGlass is null ? "" : "—";

    public string GlassCheckResult => SelectedCheck switch
    {
        null => "",
        { Outcome: GlassCheckOutcome.Passed } c => "Matches the printed nd and Vd to the printed digits. " + c.Reason,
        { Outcome: GlassCheckOutcome.Failed } c => "Does not match: " + c.Reason,
        var c => "Not checked: " + c.Reason
    };

    public string GlassStatusText => SelectedGlass is null ? "" :
        $"{SelectedGlass.Status?.ToString() ?? SelectedGlass.StatusNumber?.ToString(CultureInfo.InvariantCulture) ?? "—"}" +
        $"; melt frequency {Optional(SelectedGlass.MeltFrequency)}" + (SelectedGlass.ExcludeSubstitution ? "; excluded from substitution" : "");

    public string GlassCode => SelectedGlass?.GlassCode ?? "";

    public string GlassThermal => SelectedGlass switch
    {
        null => "",
        { Thermal: { } t } => $"D0 {Number(t.D0)}  D1 {Number(t.D1)}  D2 {Number(t.D2)}  E0 {Number(t.E0)}  E1 {Number(t.E1)}  λtk {Number(t.LambdaTk)} µm",
        _ => "none"
    };

    public string GlassReferenceTemperature => SelectedGlass is null ? "" : SelectedGlass.Thermal is { } t ? Number(t.ReferenceTemperatureCelsius) : "not stated";

    public string GlassExpansion => SelectedGlass is null ? "" :
        $"{Optional(SelectedGlass.ThermalExpansion)} (−30…70 °C), {Optional(SelectedGlass.ThermalExpansionHigh)} (100…300 °C)" +
        (SelectedGlass.IgnoreThermalExpansion ? "; ignored" : "");

    public string GlassDensity => SelectedGlass is null ? "" : Optional(SelectedGlass.Density);

    public string GlassDeltaPgF => SelectedGlass is null ? "" : Optional(SelectedGlass.DeltaPgF);

    public string GlassCostAndResistance => SelectedGlass is not { } g ? "" :
        $"cost {Optional(g.RelativeCost)}; CR {Optional(g.ClimaticResistance)}  FR {Optional(g.StainResistance)}  SR {Optional(g.AcidResistance)}  " +
        $"AR {Optional(g.AlkaliResistance)}  PR {Optional(g.PhosphateResistance)}";

    public string GlassTransmittance => SelectedGlass is not { } g ? "" : g.Transmittance.Count == 0 ? "none" :
        $"{g.Transmittance.Count} points, {Number(g.Transmittance.Min(p => p.Micrometers))} – {Number(g.Transmittance.Max(p => p.Micrometers))} µm";

    public string GlassComment => SelectedGlass?.Comment ?? "";

    /// <summary>The selected glass's index at <see cref="ProbeMicrometers"/>, or why there is none.</summary>
    public string ProbeResult
    {
        get
        {
            if (SelectedGlass is null) return "";
            try
            {
                return $"n = {SelectedGlass.RelativeIndex(ProbeMicrometers):F7} (relative to the catalog's air, at its reference temperature)";
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return e.Message.Split(" (Parameter")[0];
            }
        }
    }

    /// <summary>
    /// With "Adjust indices to the environment" on, the selected glass's index at
    /// <see cref="ProbeMicrometers"/> (taken as measured in the system's air) at the system's
    /// temperature and pressure (OP-13), naming the air model; otherwise "".
    /// </summary>
    public string ProbeEnvironmentResult
    {
        get
        {
            if (SelectedGlass is not { } glass || _environment?.Invoke() is not { AdjustIndexToEnvironment: true } env) return "";
            try
            {
                var r = ThermalIndex.AtAirWavelength(glass, ProbeMicrometers, env.AirModel, env.Air);
                string thermal = r.HasThermalData ? "" : " The glass has no TD line, so no dn/dT is applied (reference taken as 20 °C).";
                return $"n = {r.Relative:F7} relative to the system's air at {Number(env.TemperatureCelsius)} °C and {Number(env.PressureAtmospheres)} atm; " +
                       $"absolute {r.Absolute:F7}.{thermal} {env.AirModelDescription}";
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return e.Message.Split(" (Parameter")[0];
            }
        }
    }

    /// <summary>The system's environment changed: the adjusted probe is recomputed.</summary>
    public void EnvironmentChanged() => OnPropertyChanged(nameof(ProbeEnvironmentResult));

    private static readonly string[] GlassProperties =
    {
        nameof(SelectedCheck), nameof(HasSelectedGlass), nameof(GlassFormula), nameof(GlassCoefficients), nameof(GlassRange), nameof(GlassNdVd),
        nameof(GlassComputedNdVd), nameof(GlassCheckResult), nameof(GlassStatusText), nameof(GlassCode), nameof(GlassThermal),
        nameof(GlassReferenceTemperature), nameof(GlassExpansion), nameof(GlassDensity), nameof(GlassDeltaPgF),
        nameof(GlassCostAndResistance), nameof(GlassTransmittance), nameof(GlassComment), nameof(ProbeResult),
        nameof(ProbeEnvironmentResult)
    };

    partial void OnSelectedGlassChanged(OpticalMaterial? value)
    {
        foreach (var name in GlassProperties) OnPropertyChanged(name);
    }

    partial void OnProbeMicrometersChanged(double value)
    {
        OnPropertyChanged(nameof(ProbeResult));
        OnPropertyChanged(nameof(ProbeEnvironmentResult));
    }

    partial void OnSelectedCatalogChanged(OpticsCatalogRow? value)
    {
        CatalogChecks = SelectedCatalogData is { } catalog ? CatalogSelfCheck.Run(catalog) : Array.Empty<GlassCheck>();
        OnPropertyChanged(nameof(SelectedCatalogData));
        OnPropertyChanged(nameof(CatalogSummary));
        SelectedGlass = Glasses.FirstOrDefault();
    }

    partial void OnGlassFilterChanged(string value)
    {
        if (SelectedGlass is null || !Glasses.Contains(SelectedGlass)) SelectedGlass = Glasses.FirstOrDefault();
    }

    // ---- Catalog actions ----

    /// <summary>Imports an AGF file into the user's catalog folder (replacing a copy of the same
    /// name) and selects it. Returns the status line; a file that does not read is named there
    /// and nothing changes.</summary>
    internal string ImportCatalogFile(string path)
    {
        try
        {
            bool replacing = _library.IsUserCatalog(Path.GetFileNameWithoutExtension(path));
            var catalog = _library.Import(path, replace: true);
            RebuildRows(CurrentActiveNamesOrNull());
            SelectedCatalog = Catalogs.FirstOrDefault(r => r.Name == catalog.Name);
            Status = $"{(replacing ? "Replaced" : "Imported")} catalog '{catalog.Name}': {catalog.Materials.Count} glasses. " + CatalogSummary +
                     (catalog.Notes.Count > 0 ? $" {catalog.Notes.Count} reader notes below." : "");
            _activeCatalogsChanged();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Status = $"Catalog not imported: {e.Message}";
        }
        return Status;
    }

    /// <summary>Reads the user catalogs from another folder, saving the choice for next time.</summary>
    internal void UseCatalogFolder(string folder)
    {
        try
        {
            _library.SetCatalogFolder(folder);
            AfterReload($"Catalog folder set to {_library.CatalogFolder}.");
            OnPropertyChanged(nameof(CatalogFolder));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Status = $"Catalog folder not changed: {e.Message}";
        }
    }

    [RelayCommand]
    private void ImportAgf()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "AGF glass catalog (*.agf)|*.agf|All files (*.*)|*.*", Title = "Import a glass catalog" };
        if (dialog.ShowDialog() != true) return;
        ImportCatalogFile(dialog.FileName);
    }

    [RelayCommand]
    private void ChooseCatalogFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder of AGF glass catalogs" };
        if (dialog.ShowDialog() != true) return;
        UseCatalogFolder(dialog.FolderName);
    }

    [RelayCommand]
    private void ReloadCatalogs()
    {
        _library.Reload();
        AfterReload("Catalogs reloaded.");
    }

    private void AfterReload(string what)
    {
        string? selected = SelectedCatalog?.Name;
        RebuildRows(CurrentActiveNamesOrNull());
        SelectedCatalog = Catalogs.FirstOrDefault(r => r.Name == selected) ?? Catalogs.FirstOrDefault();
        Status = what + (_library.LoadWarnings.Count > 0 ? " " + string.Join(" ", _library.LoadWarnings) : "");
        // The catalogs' content changed: the material analyses (OP-14) recompute from the new set.
        _activeCatalogsChanged();
    }

    /// <summary>Problems from the library's load, for the log at startup.</summary>
    public IReadOnlyList<string> LoadWarnings => _library.LoadWarnings;

    // ---- The active catalogs: saved with the lens ----

    private List<string>? CurrentActiveNamesOrNull() => _explicitSelection ? ActiveCatalogNames.ToList() : null;

    /// <summary>The rows for the loaded catalogs plus any named active catalog not loaded;
    /// <paramref name="activeNames"/> null makes every loaded catalog active.</summary>
    private void RebuildRows(IReadOnlyList<string>? activeNames)
    {
        _rebuilding = true;
        try
        {
            foreach (var row in Catalogs) row.PropertyChanged -= OnRowChanged;
            Catalogs.Clear();
            var active = activeNames is null ? null : new HashSet<string>(activeNames, StringComparer.OrdinalIgnoreCase);
            // Active catalogs first, in their saved order (the search order), then the rest.
            var ordered = activeNames is null
                ? _library.Catalogs.Select(c => c.Name).ToList()
                : activeNames.Concat(_library.Catalogs.Select(c => c.Name).Where(n => !active!.Contains(n))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string name in ordered)
            {
                var catalog = _library.FindCatalog(name);
                var row = new OpticsCatalogRow(catalog?.Name ?? name, catalog?.Materials.Count ?? 0,
                    catalog is null ? "not loaded" : _library.IsUserCatalog(name) ? "user folder" : "built in")
                {
                    IsActive = active is null || active.Contains(name)
                };
                row.PropertyChanged += OnRowChanged;
                Catalogs.Add(row);
            }
        }
        finally
        {
            _rebuilding = false;
        }
        OnPropertyChanged(nameof(ActiveCatalogNames));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rebuilding || e.PropertyName != nameof(OpticsCatalogRow.IsActive)) return;
        _explicitSelection = true;
        OnPropertyChanged(nameof(ActiveCatalogNames));
        _activeCatalogsChanged();
    }

    /// <summary>The saved form: null while every loaded catalog is active and the user has not
    /// chosen, else the active names in search order (including any not loaded here).</summary>
    public OpticsMaterialsSettings? ToSettings() =>
        _explicitSelection ? new OpticsMaterialsSettings { ActiveCatalogs = ActiveCatalogNames.ToList() } : null;

    /// <summary>Restores the active catalogs from a file. Null (a file from before OP-11) means
    /// every loaded catalog. A named catalog this machine does not have is kept, shown as not
    /// loaded, and noted.</summary>
    public void LoadSettings(OpticsMaterialsSettings? saved, ICollection<string> notes)
    {
        string? selected = SelectedCatalog?.Name;
        if (saved?.ActiveCatalogs is not { } names)
        {
            _explicitSelection = false;
            RebuildRows(null);
        }
        else
        {
            _explicitSelection = true;
            foreach (var missing in names.Where(n => _library.FindCatalog(n) is null))
                notes.Add($"Glass catalog '{missing}' is active in the file but not loaded on this machine; it is kept, " +
                          "and its glasses cannot be found until its AGF file is imported.");
            RebuildRows(names);
        }
        SelectedCatalog = Catalogs.FirstOrDefault(r => r.Name == selected) ?? Catalogs.FirstOrDefault();
    }

    /// <summary>A new lens: every loaded catalog active.</summary>
    public void Reset() => LoadSettings(null, new List<string>());

    private static string Number(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
    private static string Optional(double? v) => v is double d ? Number(d) : "—";
    private static string Optional(int? v) => v is int i ? i.ToString(CultureInfo.InvariantCulture) : "—";
}
