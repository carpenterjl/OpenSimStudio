using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Optics;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The system data (plan item OP-18): aperture, fields, wavelengths, environment, units, title
/// and notes, as the setup panel edits them. Lengths are shown in the lens unit, angles in
/// degrees, wavelengths in micrometers; <see cref="BuildSystemData"/> gives the record in
/// meters that the engines use, and <see cref="SystemProblems"/> what is wrong with it.
/// </summary>
public partial class OpticsViewModel
{
    public IReadOnlyList<OpticsChoice<LensUnit>> LensUnitChoices { get; } = OpticsChoice<LensUnit>.From(ConventionNames.LensUnits);
    public IReadOnlyList<OpticsChoice<ApertureType>> ApertureTypeChoices { get; } = OpticsChoice<ApertureType>.From(ApertureTypes.Names);
    public IReadOnlyList<OpticsChoice<ApodizationType>> ApodizationChoices { get; } = OpticsChoice<ApodizationType>.From(ApodizationTypes.Names);
    public IReadOnlyList<OpticsChoice<FieldType>> FieldTypeChoices { get; } = OpticsChoice<FieldType>.From(FieldTypes.Names);
    public IReadOnlyList<OpticsChoice<FieldNormalization>> FieldNormalizationChoices { get; } = OpticsChoice<FieldNormalization>.From(ConventionNames.FieldNormalizations);
    public IReadOnlyList<OpticsChoice<SourceUnit>> SourceUnitChoices { get; } = OpticsChoice<SourceUnit>.From(SourceUnits.Names);
    public IReadOnlyList<OpticsChoice<MtfUnit>> MtfUnitChoices { get; } = OpticsChoice<MtfUnit>.From(MtfUnits.Names);
    public IReadOnlyList<WavelengthPreset> WavelengthPresets => SpectralLines.Presets;

    // Title and notes.
    [ObservableProperty] private string _systemTitle = "";
    [ObservableProperty] private string _systemNotes = "";

    // Units.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthUnitSymbol), nameof(ApertureValueLabel), nameof(FieldUnitLabel))]
    private OpticsChoice<LensUnit> _selectedLensUnit = Choice(ConventionNames.LensUnits, LensUnit.Millimeter);

    [ObservableProperty] private OpticsChoice<SourceUnit> _selectedSourceUnit = Choice(SourceUnits.Names, SourceUnit.Watts);
    [ObservableProperty] private OpticsChoice<LensUnit> _selectedAnalysisAreaUnit = Choice(ConventionNames.LensUnits, LensUnit.Millimeter);
    [ObservableProperty] private OpticsChoice<MtfUnit> _selectedMtfUnit = Choice(MtfUnits.Names, MtfUnit.CyclesPerMillimeter);

    // Aperture.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApertureValueLabel), nameof(ApertureTakesValue))]
    private OpticsChoice<ApertureType> _selectedApertureType = Choice(ApertureTypes.Names, ApertureType.EntrancePupilDiameter);

    /// <summary>The aperture value: a length in lens units, an F-number, an NA or degrees.</summary>
    [ObservableProperty] private double _apertureValue = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApodizationTakesFactor))]
    private OpticsChoice<ApodizationType> _selectedApodization = Choice(ApodizationTypes.Names, ApodizationType.Uniform);

    [ObservableProperty] private double _apodizationFactor;

    /// <summary>The clear semi-diameter margin, lens units.</summary>
    [ObservableProperty] private double _semiDiameterMargin;
    [ObservableProperty] private double _semiDiameterMarginPercent;
    [ObservableProperty] private bool _telecentricObjectSpace;
    [ObservableProperty] private bool _afocalImageSpace;

    // Fields.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FieldUnitLabel))]
    private OpticsChoice<FieldType> _selectedFieldType = Choice(FieldTypes.Names, FieldType.Angle);

    [ObservableProperty] private OpticsChoice<FieldNormalization> _selectedFieldNormalization = Choice(ConventionNames.FieldNormalizations, FieldNormalization.Radial);

    // The field wizard: evenly spread fields along +y.
    [ObservableProperty] private double _wizardMaxField = 10;
    [ObservableProperty] private int _wizardFieldCount = 3;
    [ObservableProperty] private bool _wizardEqualArea = true;

    // Wavelengths.
    /// <summary>The primary wavelength's 1-based number, as the table numbers rows.</summary>
    [ObservableProperty] private int _primaryWavelength = 1;
    [ObservableProperty] private WavelengthPreset? _selectedWavelengthPreset = SpectralLines.Presets[0];
    [ObservableProperty] private double _quadratureMinMicrometers = 0.45;
    [ObservableProperty] private double _quadratureMaxMicrometers = 0.65;
    [ObservableProperty] private int _quadratureCount = 3;

    // Environment.
    [ObservableProperty] private double _temperatureCelsius = 20;
    [ObservableProperty] private double _pressureAtmospheres = 1;
    [ObservableProperty] private bool _adjustIndexToEnvironment;

    private ObservableCollection<OpticsFieldRow>? _fields;
    private ObservableCollection<OpticsWavelengthRow>? _wavelengths;

    /// <summary>Set while a whole system is loaded, so the lens-unit change does not rescale
    /// values that already arrive in the new unit, and the check is raised once at the end.</summary>
    private bool _loadingSystem;

    /// <summary>The field table, field 1 first.</summary>
    public ObservableCollection<OpticsFieldRow> Fields => _fields ??= Watched(new ObservableCollection<OpticsFieldRow> { new() });

    /// <summary>The wavelength table, wavelength 1 first.</summary>
    public ObservableCollection<OpticsWavelengthRow> Wavelengths => _wavelengths ??= Watched(new ObservableCollection<OpticsWavelengthRow> { new() });

    public string LengthUnitSymbol => ConventionNames.Symbol(SelectedLensUnit.Value);

    public bool ApertureTakesValue => !ApertureTypes.HasNoValue(SelectedApertureType.Value);

    public bool ApodizationTakesFactor => ApodizationTypes.TakesFactor(SelectedApodization.Value);

    /// <summary>The aperture value's label with its unit.</summary>
    public string ApertureValueLabel => SelectedApertureType.Value switch
    {
        ApertureType.EntrancePupilDiameter => $"Entrance pupil diameter [{LengthUnitSymbol}]",
        ApertureType.ObjectConeAngle => "Object cone half-angle [°]",
        ApertureType.FloatByStopSize => "Value (set by the stop size)",
        var t => ApertureTypes.Names.Label(t)
    };

    /// <summary>The unit of the field table's x and y.</summary>
    public string FieldUnitLabel => FieldTypes.IsAngle(SelectedFieldType.Value) ? "°" : LengthUnitSymbol;

    /// <summary>What is wrong with the system data, one line each; empty when it can be traced.</summary>
    public IReadOnlyList<string> SystemProblems => BuildSystemData().Validate();

    public bool HasSystemProblems => SystemProblems.Count > 0;

    /// <summary>The panel's one-line summary of the check.</summary>
    public string SystemCheck => SystemProblems is { Count: > 0 } problems
        ? string.Join(Environment.NewLine, problems)
        : "The system data is complete.";

    /// <summary>The system data in meters, degrees and micrometers, as the engines take it.</summary>
    public OpticalSystemData BuildSystemData()
    {
        double perUnit = OpticsConventions.MetersPer(SelectedLensUnit.Value);
        var type = SelectedApertureType.Value;
        double fieldScale = FieldTypes.IsAngle(SelectedFieldType.Value) ? 1.0 : perUnit;
        return new OpticalSystemData
        {
            Title = SystemTitle ?? "",
            Notes = SystemNotes ?? "",
            Units = new SystemUnits
            {
                LensUnit = SelectedLensUnit.Value,
                SourceUnit = SelectedSourceUnit.Value,
                AnalysisAreaUnit = SelectedAnalysisAreaUnit.Value,
                MtfUnit = SelectedMtfUnit.Value
            },
            Aperture = new Aperture
            {
                Type = type,
                Value = ApertureTypes.IsLength(type) ? ApertureValue * perUnit : ApertureValue,
                Apodization = SelectedApodization.Value,
                ApodizationFactor = ApodizationFactor,
                SemiDiameterMargin = SemiDiameterMargin * perUnit,
                SemiDiameterMarginPercent = SemiDiameterMarginPercent,
                TelecentricObjectSpace = TelecentricObjectSpace,
                AfocalImageSpace = AfocalImageSpace
            },
            Fields = new FieldSet
            {
                Type = SelectedFieldType.Value,
                Normalization = SelectedFieldNormalization.Value,
                Points = Fields.Select(f => f.ToFieldPoint(fieldScale)).ToArray()
            },
            Wavelengths = new WavelengthSet
            {
                Wavelengths = Wavelengths.Select(w => new Wavelength { Micrometers = w.Micrometers, Weight = w.Weight }).ToArray(),
                PrimaryIndex = PrimaryWavelength - 1
            },
            Environment = new OpticsEnvironment
            {
                TemperatureCelsius = TemperatureCelsius,
                PressureAtmospheres = PressureAtmospheres,
                AdjustIndexToEnvironment = AdjustIndexToEnvironment
            }
        };
    }

    /// <summary>
    /// A new lens unit keeps every length's meaning: the entrance-pupil diameter, the margin and
    /// height fields are converted, so 10 mm becomes 1 cm rather than 10 cm. (Rescaling a lens
    /// on purpose is a separate tool.)
    /// </summary>
    partial void OnSelectedLensUnitChanged(OpticsChoice<LensUnit>? oldValue, OpticsChoice<LensUnit> newValue)
    {
        if (newValue is null)
        {
            // A picker pushes a transient null while its list rebuilds; the unit is never unset.
            SelectedLensUnit = oldValue ?? LensUnitChoices[0];
            return;
        }
        if (_loadingSystem || oldValue is null || oldValue.Value == newValue.Value) return;
        double from = OpticsConventions.MetersPer(oldValue.Value), to = OpticsConventions.MetersPer(newValue.Value);
        double Convert(double v) => v * from / to;
        if (ApertureTypes.IsLength(SelectedApertureType.Value)) ApertureValue = Convert(ApertureValue);
        SemiDiameterMargin = Convert(SemiDiameterMargin);
        if (!FieldTypes.IsAngle(SelectedFieldType.Value))
            foreach (var f in Fields) { f.X = Convert(f.X); f.Y = Convert(f.Y); }
    }

    // A picker pushes a transient null while its list rebuilds; none of these is ever unset.
    partial void OnSelectedApertureTypeChanged(OpticsChoice<ApertureType> value) { if (value is null) SelectedApertureType = ApertureTypeChoices[0]; }
    partial void OnSelectedApodizationChanged(OpticsChoice<ApodizationType> value) { if (value is null) SelectedApodization = ApodizationChoices[0]; }
    partial void OnSelectedFieldTypeChanged(OpticsChoice<FieldType> value) { if (value is null) SelectedFieldType = FieldTypeChoices[0]; }
    partial void OnSelectedFieldNormalizationChanged(OpticsChoice<FieldNormalization> value) { if (value is null) SelectedFieldNormalization = FieldNormalizationChoices[0]; }
    partial void OnSelectedSourceUnitChanged(OpticsChoice<SourceUnit> value) { if (value is null) SelectedSourceUnit = SourceUnitChoices[0]; }
    partial void OnSelectedAnalysisAreaUnitChanged(OpticsChoice<LensUnit> value) { if (value is null) SelectedAnalysisAreaUnit = LensUnitChoices[0]; }
    partial void OnSelectedMtfUnitChanged(OpticsChoice<MtfUnit> value) { if (value is null) SelectedMtfUnit = MtfUnitChoices[0]; }

    [RelayCommand]
    private void AddField()
    {
        var last = Fields.LastOrDefault();
        Fields.Add(new OpticsFieldRow { X = last?.X ?? 0, Y = last?.Y ?? 0 });
    }

    [RelayCommand]
    private void RemoveField(OpticsFieldRow? row)
    {
        // The last field stays: a system always has one.
        if (row is not null && Fields.Count > 1) Fields.Remove(row);
    }

    /// <summary>Replaces the field table with the wizard's fields along +y.</summary>
    [RelayCommand]
    private void ApplyFieldWizard()
    {
        if (WizardFieldCount < 1 || !double.IsFinite(WizardMaxField)) return;
        ReplaceFields(FieldSet.AlongY(WizardMaxField, WizardFieldCount, WizardEqualArea)
            .Select(p => new OpticsFieldRow { X = p.X, Y = p.Y, Weight = p.Weight }));
    }

    [RelayCommand]
    private void AddWavelength()
    {
        var last = Wavelengths.LastOrDefault();
        Wavelengths.Add(new OpticsWavelengthRow { Micrometers = last?.Micrometers ?? 0.55 });
    }

    [RelayCommand]
    private void RemoveWavelength(OpticsWavelengthRow? row)
    {
        if (row is null || Wavelengths.Count <= 1) return;
        int index = Wavelengths.IndexOf(row);
        Wavelengths.Remove(row);
        // The primary keeps pointing at the same wavelength, or at the last if it was removed.
        if (index >= 0 && index + 1 < PrimaryWavelength) PrimaryWavelength--;
        PrimaryWavelength = Math.Clamp(PrimaryWavelength, 1, Wavelengths.Count);
    }

    /// <summary>Replaces the wavelength table with the selected preset.</summary>
    [RelayCommand]
    private void ApplyWavelengthPreset()
    {
        if (SelectedWavelengthPreset is { } preset) LoadWavelengths(preset.ToWavelengthSet());
    }

    /// <summary>Replaces the wavelength table with Gauss–Legendre wavelengths over the band.</summary>
    [RelayCommand]
    private void ApplyWavelengthQuadrature()
    {
        if (QuadratureCount < 1 || !(QuadratureMinMicrometers > 0) || !(QuadratureMaxMicrometers > QuadratureMinMicrometers)) return;
        LoadWavelengths(WavelengthSet.GaussianQuadrature(QuadratureMinMicrometers, QuadratureMaxMicrometers, QuadratureCount));
    }

    /// <summary>Sets the panel from system data in meters (a preset, a headless lens), showing
    /// lengths in the data's lens unit.</summary>
    public void LoadSystemData(OpticalSystemData data)
    {
        _loadingSystem = true;
        try
        {
            double perUnit = OpticsConventions.MetersPer(data.Units.LensUnit);
            SelectedLensUnit = Choice(LensUnitChoices, data.Units.LensUnit);
            SelectedSourceUnit = Choice(SourceUnitChoices, data.Units.SourceUnit);
            SelectedAnalysisAreaUnit = Choice(LensUnitChoices, data.Units.AnalysisAreaUnit);
            SelectedMtfUnit = Choice(MtfUnitChoices, data.Units.MtfUnit);
            SystemTitle = data.Title;
            SystemNotes = data.Notes;
            var a = data.Aperture;
            SelectedApertureType = Choice(ApertureTypeChoices, a.Type);
            ApertureValue = ApertureTypes.IsLength(a.Type) ? a.Value / perUnit : a.Value;
            SelectedApodization = Choice(ApodizationChoices, a.Apodization);
            ApodizationFactor = a.ApodizationFactor;
            SemiDiameterMargin = a.SemiDiameterMargin / perUnit;
            SemiDiameterMarginPercent = a.SemiDiameterMarginPercent;
            TelecentricObjectSpace = a.TelecentricObjectSpace;
            AfocalImageSpace = a.AfocalImageSpace;
            SelectedFieldType = Choice(FieldTypeChoices, data.Fields.Type);
            SelectedFieldNormalization = Choice(FieldNormalizationChoices, data.Fields.Normalization);
            double fieldScale = FieldTypes.IsAngle(data.Fields.Type) ? 1.0 : perUnit;
            ReplaceFields(data.Fields.Points.Select(p => new OpticsFieldRow
            {
                X = p.X / fieldScale, Y = p.Y / fieldScale, Weight = p.Weight,
                DecenterX = p.DecenterX, DecenterY = p.DecenterY, CompressionX = p.CompressionX,
                CompressionY = p.CompressionY, TiltDegrees = p.TiltDegrees
            }));
            LoadWavelengths(data.Wavelengths);
            TemperatureCelsius = data.Environment.TemperatureCelsius;
            PressureAtmospheres = data.Environment.PressureAtmospheres;
            AdjustIndexToEnvironment = data.Environment.AdjustIndexToEnvironment;
        }
        finally
        {
            _loadingSystem = false;
        }
        RaiseSystemCheck();
    }

    /// <summary>A new project starts with the default system data.</summary>
    private void ResetSystemData() => LoadSystemData(OpticalSystemData.Default);

    private void LoadWavelengths(WavelengthSet set)
    {
        Wavelengths.Clear();
        foreach (var w in set.Wavelengths) Wavelengths.Add(new OpticsWavelengthRow { Micrometers = w.Micrometers, Weight = w.Weight });
        PrimaryWavelength = set.PrimaryIndex + 1;
    }

    private void ReplaceFields(IEnumerable<OpticsFieldRow> rows)
    {
        Fields.Clear();
        foreach (var row in rows) Fields.Add(row);
    }

    private static OpticsChoice<T> Choice<T>(SavedNameTable<T> names, T value) where T : struct, Enum =>
        new(names.Label(value), value);

    private static OpticsChoice<T> Choice<T>(IReadOnlyList<OpticsChoice<T>> choices, T value) where T : struct, Enum =>
        choices.First(c => EqualityComparer<T>.Default.Equals(c.Value, value));

    /// <summary>Numbers the rows and raises the check whenever a table or a row changes.</summary>
    private ObservableCollection<TRow> Watched<TRow>(ObservableCollection<TRow> rows) where TRow : ObservableObject
    {
        void Renumber()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] is OpticsFieldRow f) f.Number = i + 1;
                else if (rows[i] is OpticsWavelengthRow w) w.Number = i + 1;
            }
        }
        void RowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(OpticsFieldRow.Number)) RaiseSystemCheck();
        }
        foreach (var row in rows) row.PropertyChanged += RowChanged;
        Renumber();
        rows.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null) foreach (TRow row in e.OldItems) row.PropertyChanged -= RowChanged;
            if (e.NewItems is not null) foreach (TRow row in e.NewItems) row.PropertyChanged += RowChanged;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                foreach (var row in rows) { row.PropertyChanged -= RowChanged; row.PropertyChanged += RowChanged; }
            Renumber();
            RaiseSystemCheck();
        };
        return rows;
    }

    private static readonly HashSet<string> CheckProperties = new()
    {
        nameof(SystemProblems), nameof(HasSystemProblems), nameof(SystemCheck)
    };

    /// <summary>Any edit of the system data re-runs the check the panel shows.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && !CheckProperties.Contains(name)) RaiseSystemCheck();
    }

    private void RaiseSystemCheck()
    {
        if (_loadingSystem) return;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(SystemProblems)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasSystemProblems)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(SystemCheck)));
    }
}
