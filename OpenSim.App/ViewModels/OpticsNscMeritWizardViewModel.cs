// OP-88
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis.ImageSimulation;
using OpenSim.Optics.Merit;
using OpenSim.Optics.NonSequential.Detectors;

namespace OpenSim.App.ViewModels;

/// <summary>A choice of the non-sequential wizards' lists.</summary>
public sealed record NscWizardChoice<T>(string Label, T Value);

/// <summary>
/// The non-sequential Optimization Wizard and Bitmap Wizard dialog (plan item OP-88; help,
/// "Optimization Wizard (optimize tab, non-sequential)" and "Bitmap Wizard (optimize tab,
/// non-sequential)"): the clear-data, Start At and raytrace settings both share, then either a
/// criterion on a detector or an image file (BMP or PNG) with its total flux. Apply writes the
/// rows into the Merit Function Editor as one undo step (<see cref="NscMeritWizards"/>) and, as
/// the help's dialog does, turns the clear-data and raytrace rows off for the next Apply and moves
/// Start At past what it wrote; OK applies and closes. Scatter Rays sets the NSTR row's Scat?
/// (OP-80's surface scattering); LightningTrace is refused by name; JPG images are refused (only
/// BMP and PNG are read first-party).
/// </summary>
public sealed partial class OpticsNscMeritWizardViewModel : ObservableObject
{
    private readonly OpticsMeritEditorViewModel _editor;
    private readonly Func<(IReadOnlyList<OpticsNscObjectSettings> Rows, int Configurations)> _system;
    private readonly Func<int, int, int, string?>? _resample;

    /// <param name="editor">The Merit Function Editor the rows go into.</param>
    /// <param name="system">The project's non-sequential objects and configuration count now.</param>
    /// <param name="resampleDetector">Sets detector object n's pixel counts (Resample Detector);
    /// returns why it was refused, or null. Null: Resample Detector is refused.</param>
    public OpticsNscMeritWizardViewModel(OpticsMeritEditorViewModel editor,
        Func<(IReadOnlyList<OpticsNscObjectSettings> Rows, int Configurations)> system, Func<int, int, int, string?>? resampleDetector = null)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _resample = resampleDetector;
        RefreshObjects();
    }

    /// <summary>Raised when OK has applied, so the window closes.</summary>
    public event EventHandler? CloseRequested;

    public IReadOnlyList<NscWizardChoice<bool>> Modes { get; } = new[]
    {
        new NscWizardChoice<bool>("Optimization Wizard", false),
        new NscWizardChoice<bool>("Bitmap Wizard", true),
    };

    /// <summary>True for the Bitmap Wizard.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptimization))]
    private bool _isBitmap;

    public bool IsOptimization => !IsBitmap;

    /// <summary>The detector objects ("2: Detector Rectangle").</summary>
    [ObservableProperty] private IReadOnlyList<NscWizardChoice<int>> _detectors = Array.Empty<NscWizardChoice<int>>();

    /// <summary>"All" and each source object.</summary>
    [ObservableProperty] private IReadOnlyList<NscWizardChoice<int>> _sources = Array.Empty<NscWizardChoice<int>>();

    /// <summary>"All" and each configuration.</summary>
    [ObservableProperty] private IReadOnlyList<NscWizardChoice<int>> _configurationChoices = Array.Empty<NscWizardChoice<int>>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Criteria))]
    private int _detector;

    [ObservableProperty] private int _clearDetector;
    [ObservableProperty] private bool _includeClearAndTrace = true;
    [ObservableProperty] private int _startAt;
    [ObservableProperty] private bool _overwrite;
    [ObservableProperty] private double _overallWeight = 1;
    [ObservableProperty] private int _configuration;
    [ObservableProperty] private int _source;
    [ObservableProperty] private bool _splitRays;
    [ObservableProperty] private bool _scatterRays;
    [ObservableProperty] private bool _usePolarization;
    [ObservableProperty] private bool _ignoreErrors;
    [ObservableProperty] private bool _useLightningTrace;

    // Optimization Wizard
    [ObservableProperty] private NscWizardCriterion _criterion = NscWizardCriterion.TotalFlux;
    [ObservableProperty] private NscWizardComparison _comparison = NscWizardComparison.Equal;
    [ObservableProperty] private double _target;
    [ObservableProperty] private double _minimumFlux;

    // Bitmap Wizard
    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private double _totalFlux = 1;
    [ObservableProperty] private bool _colorTargets;
    [ObservableProperty] private bool _resampleDetector;

    [ObservableProperty] private string _status = "";

    /// <summary>The preview of the last bitmap targets ([iy, ix], iy 0 the detector's −y edge), or null.</summary>
    [ObservableProperty] private double[,]? _preview;

    public IReadOnlyList<NscWizardChoice<NscWizardComparison>> Comparisons { get; } = new[]
    {
        new NscWizardChoice<NscWizardComparison>("Equal to", NscWizardComparison.Equal),
        new NscWizardChoice<NscWizardComparison>("Greater than", NscWizardComparison.GreaterThan),
        new NscWizardChoice<NscWizardComparison>("Less than", NscWizardComparison.LessThan),
    };

    /// <summary>The criteria the selected detector's type offers.</summary>
    public IReadOnlyList<NscWizardChoice<NscWizardCriterion>> Criteria
    {
        get
        {
            var (rows, _) = _system();
            string type = Detector >= 1 && Detector <= rows.Count ? (rows[Detector - 1]?.Type ?? "") : NscDetectorTypes.Rectangle;
            return NscMeritWizards.CriteriaFor(type).Select(c => new NscWizardChoice<NscWizardCriterion>(NscMeritWizards.Name(c), c)).ToArray();
        }
    }

    /// <summary>Reads the objects and configurations again (the detector and source lists).</summary>
    [RelayCommand]
    public void RefreshObjects()
    {
        var (rows, configurations) = _system();
        Detectors = rows.Select((r, i) => (r, i))
            .Where(p => p.r is not null && NscDetectorTypes.IsDetectorType((p.r.Type ?? "").Trim()))
            .Select(p => new NscWizardChoice<int>($"{p.i + 1}: {p.r.Type}", p.i + 1)).ToArray();
        Sources = new[] { new NscWizardChoice<int>("All", 0) }
            .Concat(rows.Select((r, i) => (r, i))
                .Where(p => OpenSim.Optics.NonSequential.Sources.NscSourceTypes.Find(p.r?.Type) is not null)
                .Select(p => new NscWizardChoice<int>($"{p.i + 1}: {p.r.Type}", p.i + 1))).ToArray();
        ConfigurationChoices = new[] { new NscWizardChoice<int>("All", 0) }
            .Concat(Enumerable.Range(1, Math.Max(1, configurations)).Select(k => new NscWizardChoice<int>(k.ToString(CultureInfo.InvariantCulture), k))).ToArray();
        if (Detector == 0 || Detectors.All(d => d.Value != Detector)) Detector = Detectors.FirstOrDefault()?.Value ?? 0;
        if (Detectors.Count == 0) Status = "The system has no detector objects: add a Detector Rectangle, Color or Polar in the Non-Sequential Component Editor.";
    }

    /// <summary>Writes the rows; returns what was written, or null with <see cref="Status"/> saying why not.</summary>
    public NscWizardResult? ApplyRows()
    {
        var (rows, configurations) = _system();
        var existing = _editor.ToFunction();
        NscWizardResult result;
        try
        {
            if (!IsBitmap)
            {
                result = NscMeritWizards.Optimization(existing, rows, Math.Max(1, configurations), new NscOptimizationWizardOptions
                {
                    Detector = Detector, Criterion = Criterion, Comparison = Comparison, Target = Target, MinimumFlux = MinimumFlux,
                    ClearDetector = ClearDetector, IncludeClearAndTrace = IncludeClearAndTrace, StartAt = StartAt, Overwrite = Overwrite,
                    OverallWeight = OverallWeight, Configuration = Configuration, Source = Source, SplitRays = SplitRays,
                    ScatterRays = ScatterRays, UsePolarization = UsePolarization, IgnoreErrors = IgnoreErrors, UseLightningTrace = UseLightningTrace,
                }, _editor.Registry);
            }
            else
            {
                var image = ReadImage(ImagePath);
                var options = new NscBitmapWizardOptions
                {
                    Detector = Detector, TotalFlux = TotalFlux, ColorTargets = ColorTargets, ResampleDetector = ResampleDetector,
                    ClearDetector = ClearDetector, IncludeClearAndTrace = IncludeClearAndTrace, StartAt = StartAt, Overwrite = Overwrite,
                    OverallWeight = OverallWeight, Configuration = Configuration, Source = Source, SplitRays = SplitRays,
                    ScatterRays = ScatterRays, UsePolarization = UsePolarization, IgnoreErrors = IgnoreErrors, UseLightningTrace = UseLightningTrace,
                };
                if (ResampleDetector && _resample is null)
                {
                    Status = "Resample Detector: this editor cannot change the detector's pixels; scale the image to the detector instead.";
                    return null;
                }
                result = NscMeritWizards.Bitmap(existing, rows, Math.Max(1, configurations), options, image, _editor.Registry);
                if (result.DetectorPixels is { } px && _resample!(Detector, px.X, px.Y) is { } refusal)
                {
                    Status = $"Resample Detector refused: {refusal}";
                    return null;
                }
                Preview = result.Preview;
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Status = e.Message;
            return null;
        }
        string? refused = _editor.ApplyFunction(result.Function, IsBitmap ? "Bitmap Wizard" : "Optimization Wizard (non-sequential)");
        if (refused is not null) { Status = refused; return null; }
        // As the help's dialog: "after Apply is clicked the first time, the clear data and raytrace settings are deactivated".
        IncludeClearAndTrace = false;
        StartAt = result.NextStartAt;
        Overwrite = false;
        Status = string.Join(" ", result.Notes);
        return result;
    }

    [RelayCommand]
    private void Apply() => ApplyRows();

    [RelayCommand]
    private void Ok()
    {
        if (ApplyRows() is not null) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A BMP or PNG file (first-party readers, OP-42); JPG and others are refused by name.</summary>
    public static SceneImage ReadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Bitmap Wizard: choose an image file (BMP or PNG).");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".bmp" => BmpFile.Read(path),
            ".png" => PngFile.Read(path),
            ".jpg" or ".jpeg" => throw new NotSupportedException("Bitmap Wizard: JPG images are not read (this build decodes BMP and PNG first-party); save the image as PNG."),
            _ => throw new NotSupportedException($"Bitmap Wizard: '{ext}' is not an image type the wizard reads (BMP or PNG)."),
        };
    }
}
