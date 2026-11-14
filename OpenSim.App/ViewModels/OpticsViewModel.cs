using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.App.ViewModels;

/// <summary>An optics mode as the picker shows it.</summary>
public sealed record OpticsModeOption(string Label, OpticsMode Mode)
{
    public static readonly IReadOnlyList<OpticsModeOption> All =
        OpticsModes.All.Select(m => new OpticsModeOption(OpticsModes.Label(m), m)).ToArray();
}

/// <summary>
/// The Optics workspace (plan item OP-1). So far it holds the one choice the workspace has —
/// sequential or non-sequential — and carries it to and from the project file. The system
/// editors, the ray trace and the analyses are later plan items; like the antenna study,
/// optics will keep its own result records rather than go through the FE solver path.
/// </summary>
public partial class OpticsViewModel : ObservableObject
{
    private readonly Services.ProjectSession _session;
    private readonly GlassCatalogLibrary _glassLibrary;
    private readonly Services.ILogService _log;

    public OpticsViewModel(Services.ProjectSession session, GlassCatalogLibrary glassCatalogs, Services.ILogService log)
    {
        // The materials panel first: publishing reads its active catalogs.
        GlassCatalogs = new OpticsGlassCatalogViewModel(glassCatalogs, PublishSystem, () => BuildSystemData().Environment);
        foreach (var warning in GlassCatalogs.LoadWarnings)
            log.Append($"Glass catalogs: {warning}");
        Coatings = new OpticsCoatingCatalogViewModel(PublishSystem);
        _glassLibrary = glassCatalogs;
        // OP-31: the lens data editor, after the catalogs and coatings its check reads.
        LensEditor = new OpticsLensEditorViewModel(PublishFromLensEditor, BuildLensEditContext);
        // OP-54: the merit function editor, evaluated over the project's configurations (OP-30).
        MeritEditor = new OpticsMeritEditorViewModel(PublishFromLensEditor,
            () => new OpenSim.Optics.Merit.ProjectMeritConfigurations(ToSettings(), BuildLensEditContext().Catalogs));
        // OP-30: the multi-configuration editor, applied to the entered system.
        MultiConfig = new OpticsMultiConfigEditorViewModel(PublishFromMultiConfig, () => BuildSettings(withTable: false), ReplaceSystem);
        _log = log;
        _session = session;
        PublishSystem();
    }

    /// <summary>The thin-film coating catalog (OP-15), saved with the lens and published with
    /// the system so the coating plots recompute after every edit.</summary>
    public OpticsCoatingCatalogViewModel Coatings { get; }

    /// <summary>The glass catalogs and the active ones (OP-11). Its own property changes do not
    /// reach this view model's, so browsing a catalog publishes nothing; changing which catalogs
    /// are active does.</summary>
    public OpticsGlassCatalogViewModel GlassCatalogs { get; }

    /// <summary>Hands the session a snapshot of the system after an edit, so the analysis
    /// windows recompute (OP-9). Field initialisers raise property changes before the constructor has set the session,
    /// so there is nothing to publish to until then.</summary>
    private void PublishSystem()
    {
        if (_session is null) return;
        // The lens editor's check and automatic semi-diameters read the system data and catalogs.
        LensEditor?.Refresh();
        MultiConfig?.Refresh();
        _session.PublishOptics(ToSettings());
    }

    /// <summary>The lens editor's own edits: it has already checked the lens.</summary>
    private void PublishFromLensEditor()
    {
        if (_session is null) return;
        MultiConfig?.Refresh();   // OP-30: the configurations apply to the entered lens
        _session.PublishOptics(ToSettings());
    }

    /// <summary>The multi-configuration editor's own edits (OP-30): it has already checked them.</summary>
    private void PublishFromMultiConfig()
    {
        if (_session is null) return;
        _session.PublishOptics(ToSettings());
    }

    /// <summary>Make Single Configuration (OP-30): the whole system replaced by one configuration.</summary>
    private void ReplaceSystem(OpticsSettings settings)
    {
        foreach (var note in LoadSettings(settings)) _log.Append($"Make single configuration: {note}");
    }

    /// <summary>The multi-configuration table (OP-30), saved with the lens.</summary>
    public OpticsMultiConfigEditorViewModel MultiConfig { get; }

    /// <summary>The sequential surfaces (OP-31), saved with the lens.</summary>
    public OpticsLensEditorViewModel LensEditor { get; }

    // OP-54
    /// <summary>The merit function (OP-54), saved with the lens.</summary>
    public OpticsMeritEditorViewModel MeritEditor { get; }

    /// <summary>What the lens editor's tools and check read: the system data as saved, the
    /// active glass catalogs and the coating names.</summary>
    private LensEditContext BuildLensEditContext() =>
        new(SystemDataToSettings(), GlassLookup.ActiveCatalogs(_glassLibrary.Catalogs, GlassCatalogs?.ActiveCatalogNames))
        {
            CoatingNames = Coatings?.ToSettings()?.Coatings?.Select(c => c.Name ?? "").ToArray()
        };

    public IReadOnlyList<OpticsModeOption> Modes => OpticsModeOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mode))]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    private OpticsModeOption _selectedMode = OpticsModeOption.All[0];

    partial void OnSelectedModeChanged(OpticsModeOption value)
    {
        // A picker pushes a transient null while its list rebuilds; the mode is never unset.
        if (value is null) SelectedMode = OpticsModeOption.All[0];
        else PublishSystem();
    }

    public OpticsMode Mode => SelectedMode.Mode;

    /// <summary>What the selected mode means, for the panel.</summary>
    public string ModeDescription => Mode switch
    {
        OpticsMode.Sequential =>
            "Surfaces in a fixed order: every ray meets surface 1, then 2, and so on. For lens design.",
        OpticsMode.NonSequential =>
            "Objects in space with no set order: a ray meets whatever lies in its path. For illumination and stray light.",
        _ => ""
    };

    /// <summary>The workspace's inputs as the project file (and a lens file) stores them.</summary>
    public OpticsSettings ToSettings() => BuildSettings(withTable: true);

    /// <summary>The inputs, with or without the multi-configuration table (OP-30; without it is
    /// the system as entered, which the configurations are applied to).</summary>
    private OpticsSettings BuildSettings(bool withTable) => new()
    {
        Mode = OpticsModes.SavedName(Mode),
        SystemData = SystemDataToSettings(),
        Materials = GlassCatalogs?.ToSettings(),
        Coatings = Coatings?.ToSettings(),
        Surfaces = LensEditor?.ToSettings(),
        MeritFunction = MeritEditor?.ToSettings(),   // OP-54
        MultiConfiguration = withTable ? MultiConfig?.ToSettings() : null
    };

    /// <summary>Restores the workspace from a project or lens file; null (every older file)
    /// leaves it as it is, as does each part a file does not carry. Returns notes for the log: a
    /// mode or choice this build does not know is kept out and named, never opened as another
    /// without a word.</summary>
    public IReadOnlyList<string> LoadSettings(OpticsSettings? saved)
    {
        var notes = new List<string>();
        if (saved is null) return notes;
        if (saved.Mode is { } name)
        {
            if (OpticsModes.TryParseSavedName(name, out var mode))
                SelectedMode = OpticsModeOption.All.First(o => o.Mode == mode);
            else
                notes.Add($"Optics mode '{name}' is not one this version knows; kept '{SelectedMode.Label}'.");
        }
        LoadSystemDataSettings(saved.SystemData, notes);
        GlassCatalogs.LoadSettings(saved.Materials, notes);
        Coatings.LoadSettings(saved.Coatings, notes);
        LensEditor.LoadSettings(saved.Surfaces, notes);
        MeritEditor.LoadSettings(saved.MeritFunction, notes);   // OP-54

        MultiConfig.LoadSettings(saved.MultiConfiguration, notes);   // OP-30: null is one configuration
        PublishSystem();
        return notes;
    }

    /// <summary>A new project starts in the default mode, with the default system data.</summary>
    public void Reset()
    {
        SelectedMode = OpticsModeOption.All[0];
        ResetSystemData();
        GlassCatalogs.Reset();
        Coatings.Reset();
        LensEditor.Reset();
        MeritEditor.Reset();   // OP-54

        MultiConfig.Reset();
        PublishSystem();
    }
}
