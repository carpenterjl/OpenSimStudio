using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Merit;

namespace OpenSim.App.ViewModels;

/// <summary>Axial symmetry: automatic (the help's default), on or off.</summary>
public enum MeritWizardSymmetry
{
    Automatic = 0,
    On = 1,
    Off = 2,
}

/// <summary>
/// The Optimization Wizard dialog (plan item OP-56; OpticStudio 2025 R2 help, Optimization
/// Wizard). Its fields are <see cref="DefaultMeritOptions"/>; Apply generates the default merit
/// function (<see cref="DefaultMeritWizard.Generate"/>) on the project's configurations and inserts
/// it into the Merit Function Editor at Start At as one undo step; OK applies and closes. Save,
/// Load and Reset Settings are the help's "Button Functions": the saved settings travel with the
/// project (<see cref="OpticsMeritEditorViewModel.WizardSettings"/>). The contrast (MTF) criterion
/// writes the Moore–Elliott contrast rows at the Spatial Frequency.
/// </summary>
public sealed partial class OpticsMeritWizardViewModel : ObservableObject
{
    private readonly OpticsMeritEditorViewModel _editor;

    public OpticsMeritWizardViewModel(OpticsMeritEditorViewModel editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        var notes = new List<string>();
        Show(DefaultMeritWizardSettings.FromSettings(editor.WizardSettings, notes));
        Status = notes.Count > 0 ? string.Join(Environment.NewLine, notes) : "";
    }

    /// <summary>Raised when OK has applied the rows and the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    public IReadOnlyList<OpticsChoice<MeritWizardCriterion>> Criteria { get; } = new[]
    {
        new OpticsChoice<MeritWizardCriterion>("Wavefront", MeritWizardCriterion.Wavefront),
        new OpticsChoice<MeritWizardCriterion>("Spot radius", MeritWizardCriterion.SpotRadius),
        new OpticsChoice<MeritWizardCriterion>("Spot x", MeritWizardCriterion.SpotX),
        new OpticsChoice<MeritWizardCriterion>("Spot y", MeritWizardCriterion.SpotY),
        new OpticsChoice<MeritWizardCriterion>("Spot x + y", MeritWizardCriterion.SpotXY),
        new OpticsChoice<MeritWizardCriterion>("Angular", MeritWizardCriterion.Angular),
        new OpticsChoice<MeritWizardCriterion>("Contrast (MTF)", MeritWizardCriterion.Contrast),
    };

    public IReadOnlyList<OpticsChoice<MeritWizardReference>> References { get; } = new[]
    {
        new OpticsChoice<MeritWizardReference>("Centroid", MeritWizardReference.Centroid),
        new OpticsChoice<MeritWizardReference>("Chief ray", MeritWizardReference.ChiefRay),
        new OpticsChoice<MeritWizardReference>("Unreferenced (wavefront)", MeritWizardReference.Unreferenced),
    };

    public IReadOnlyList<OpticsChoice<PupilIntegration>> Integrations { get; } = new[]
    {
        new OpticsChoice<PupilIntegration>("Gaussian quadrature", PupilIntegration.GaussianQuadrature),
        new OpticsChoice<PupilIntegration>("Rectangular array", PupilIntegration.RectangularArray),
    };

    public IReadOnlyList<OpticsChoice<MeritWizardSymmetry>> SymmetryChoices { get; } = new[]
    {
        new OpticsChoice<MeritWizardSymmetry>("Automatic (when the lens is rotationally symmetric)", MeritWizardSymmetry.Automatic),
        new OpticsChoice<MeritWizardSymmetry>("Assume axial symmetry", MeritWizardSymmetry.On),
        new OpticsChoice<MeritWizardSymmetry>("Do not assume symmetry", MeritWizardSymmetry.Off),
    };

    public IReadOnlyList<OpticsChoice<MeritWizardConfigurations>> ConfigurationChoices { get; } = new[]
    {
        new OpticsChoice<MeritWizardConfigurations>("Current", MeritWizardConfigurations.Current),
        new OpticsChoice<MeritWizardConfigurations>("All", MeritWizardConfigurations.All),
    };

    public IReadOnlyList<int> ArmsChoices => DefaultMeritWizard.AllowedArms;

    [ObservableProperty] private MeritWizardCriterion _criterion;
    [ObservableProperty] private MeritWizardReference _reference;
    [ObservableProperty] private PupilIntegration _integration;
    [ObservableProperty] private int _rings;
    [ObservableProperty] private int _arms;
    [ObservableProperty] private int _grid;
    [ObservableProperty] private bool _deleteVignetted;
    [ObservableProperty] private MeritWizardSymmetry _symmetry;
    [ObservableProperty] private bool _ignoreLateralColor;

    /// <summary>The row the generated rows start at; 0 for the end.</summary>
    [ObservableProperty] private int _startAt;

    [ObservableProperty] private double _overallWeight;
    [ObservableProperty] private double _relativeXWeight;
    [ObservableProperty] private double _relativeYWeight;
    [ObservableProperty] private double _relativeSWeight;
    [ObservableProperty] private double _relativeTWeight;

    /// <summary>Contrast: the spatial frequency, cycles/mm.</summary>
    [ObservableProperty] private double _spatialFrequency;
    [ObservableProperty] private MeritWizardConfigurations _configurations;
    [ObservableProperty] private bool _glassBoundaries;
    [ObservableProperty] private double _glassMinimum;
    [ObservableProperty] private double _glassMaximum;
    [ObservableProperty] private double _glassEdge;
    [ObservableProperty] private bool _airBoundaries;
    [ObservableProperty] private double _airMinimum;
    [ObservableProperty] private double _airMaximum;
    [ObservableProperty] private double _airEdge;

    /// <summary>What the last command did, the wizard's notes, or why nothing was done.</summary>
    [ObservableProperty] private string _status = "";

    public bool IsGaussian => Integration == PupilIntegration.GaussianQuadrature;

    partial void OnIntegrationChanged(PupilIntegration value) => OnPropertyChanged(nameof(IsGaussian));

    /// <summary>The dialog's fields as options.</summary>
    public DefaultMeritOptions ToOptions() => new()
    {
        Criterion = Criterion, Reference = Reference, Integration = Integration,
        Rings = Rings, Arms = Arms, Grid = Grid, DeleteVignetted = DeleteVignetted,
        AssumeAxialSymmetry = Symmetry switch { MeritWizardSymmetry.On => true, MeritWizardSymmetry.Off => false, _ => null },
        IgnoreLateralColor = IgnoreLateralColor, StartAt = StartAt,
        OverallWeight = OverallWeight, RelativeXWeight = RelativeXWeight, RelativeYWeight = RelativeYWeight,
        RelativeSWeight = RelativeSWeight, RelativeTWeight = RelativeTWeight, SpatialFrequency = SpatialFrequency,
        Configurations = Configurations,
        GlassBoundaries = GlassBoundaries, GlassMinimum = GlassMinimum, GlassMaximum = GlassMaximum, GlassEdge = GlassEdge,
        AirBoundaries = AirBoundaries, AirMinimum = AirMinimum, AirMaximum = AirMaximum, AirEdge = AirEdge,
    };

    private void Show(DefaultMeritOptions o)
    {
        Criterion = o.Criterion;
        Reference = o.Reference;
        Integration = o.Integration;
        Rings = o.Rings;
        Arms = o.Arms;
        Grid = o.Grid;
        DeleteVignetted = o.DeleteVignetted;
        Symmetry = o.AssumeAxialSymmetry switch { true => MeritWizardSymmetry.On, false => MeritWizardSymmetry.Off, null => MeritWizardSymmetry.Automatic };
        IgnoreLateralColor = o.IgnoreLateralColor;
        StartAt = o.StartAt ?? DefaultMeritWizard.DefaultStartAt(_editor.ToFunction());
        OverallWeight = o.OverallWeight;
        RelativeXWeight = o.RelativeXWeight;
        RelativeYWeight = o.RelativeYWeight;
        RelativeSWeight = o.RelativeSWeight;
        RelativeTWeight = o.RelativeTWeight;
        SpatialFrequency = o.SpatialFrequency;
        Configurations = o.Configurations;
        GlassBoundaries = o.GlassBoundaries;
        GlassMinimum = o.GlassMinimum;
        GlassMaximum = o.GlassMaximum;
        GlassEdge = o.GlassEdge;
        AirBoundaries = o.AirBoundaries;
        AirMinimum = o.AirMinimum;
        AirMaximum = o.AirMaximum;
        AirEdge = o.AirEdge;
    }

    /// <summary>Generates the rows and inserts them (the help's Apply). False, with the reason in
    /// <see cref="Status"/>, when nothing was inserted.</summary>
    [RelayCommand]
    private void Apply() => TryApply();

    /// <summary>Applies, then closes the dialog when the rows went in (the help's OK).</summary>
    [RelayCommand]
    private void Ok()
    {
        if (TryApply()) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool TryApply()
    {
        var options = ToOptions();
        var problems = DefaultMeritWizard.Validate(options);
        if (problems.Count > 0)
        {
            Status = string.Join(Environment.NewLine, problems);
            return false;
        }
        try
        {
            var result = _editor.ApplyDefaultMerit(options);
            Status = $"Inserted {result.Rows.Count} rows: {result.Summary}"
                + (result.Notes.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, result.Notes) : "");
            return true;
        }
        catch (ArgumentException e)
        {
            Status = e.Message;
            return false;
        }
    }

    /// <summary>Saves the dialog's settings with the project (the help's Save Settings).</summary>
    [RelayCommand]
    private void SaveSettings()
    {
        _editor.WizardSettings = DefaultMeritWizardSettings.ToSettings(ToOptions());
        Status = "Settings saved with the project.";
    }

    /// <summary>Restores the last saved settings (the help's Load Settings).</summary>
    [RelayCommand]
    private void LoadSettings()
    {
        var notes = new List<string>();
        Show(DefaultMeritWizardSettings.FromSettings(_editor.WizardSettings, notes));
        Status = _editor.WizardSettings is null ? "No settings were saved; the defaults are shown."
            : notes.Count > 0 ? string.Join(Environment.NewLine, notes) : "Saved settings loaded.";
    }

    /// <summary>The defaults (the help's Reset Settings).</summary>
    [RelayCommand]
    private void ResetSettings()
    {
        Show(new DefaultMeritOptions());
        Status = "Settings reset to the defaults.";
    }
}
