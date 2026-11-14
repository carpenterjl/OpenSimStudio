using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The system data in and out of project and lens files (plan item OP-10). The file form keeps
/// lengths in the lens unit, as the panel shows them, so a saved value reloads bit for bit and
/// <see cref="BuildSystemData"/> gives the engines the same system it gave before the save.
/// </summary>
public partial class OpticsViewModel
{
    /// <summary>What the last lens save or open did, for the panel.</summary>
    [ObservableProperty] private string _lensFileStatus = "";

    /// <summary>The panel's system data as the files store it.</summary>
    private OpticsSystemSettings SystemDataToSettings() => new()
    {
        Title = SystemTitle,
        Notes = SystemNotes,
        LensUnit = ConventionNames.LensUnits.SavedName(SelectedLensUnit.Value),
        SourceUnit = SourceUnits.Names.SavedName(SelectedSourceUnit.Value),
        AnalysisAreaUnit = ConventionNames.LensUnits.SavedName(SelectedAnalysisAreaUnit.Value),
        MtfUnit = MtfUnits.Names.SavedName(SelectedMtfUnit.Value),
        ApertureType = ApertureTypes.Names.SavedName(SelectedApertureType.Value),
        ApertureValue = ApertureValue,
        Apodization = ApodizationTypes.Names.SavedName(SelectedApodization.Value),
        ApodizationFactor = ApodizationFactor,
        SemiDiameterMargin = SemiDiameterMargin,
        SemiDiameterMarginPercent = SemiDiameterMarginPercent,
        TelecentricObjectSpace = TelecentricObjectSpace,
        AfocalImageSpace = AfocalImageSpace,
        FieldType = FieldTypes.Names.SavedName(SelectedFieldType.Value),
        FieldNormalization = ConventionNames.FieldNormalizations.SavedName(SelectedFieldNormalization.Value),
        Fields = Fields.Select(f => new OpticsFieldSettings
        {
            X = f.X, Y = f.Y, Weight = f.Weight, DecenterX = f.DecenterX, DecenterY = f.DecenterY,
            CompressionX = f.CompressionX, CompressionY = f.CompressionY, TiltDegrees = f.TiltDegrees
        }).ToList(),
        Wavelengths = Wavelengths.Select(w => new OpticsWavelengthSettings { Micrometers = w.Micrometers, Weight = w.Weight }).ToList(),
        PrimaryWavelength = PrimaryWavelength,
        TemperatureCelsius = TemperatureCelsius,
        PressureAtmospheres = PressureAtmospheres,
        AdjustIndexToEnvironment = AdjustIndexToEnvironment
    };

    /// <summary>Sets the panel from a file's system data; null (a file from before OP-10) leaves
    /// the panel as it is. A saved name this build does not know is kept out and named in
    /// <paramref name="notes"/>; null lists leave the panel's rows.</summary>
    private void LoadSystemDataSettings(OpticsSystemSettings? saved, ICollection<string> notes)
    {
        if (saved is null) return;
        _loadingSystem = true;
        try
        {
            // The unit first, with the rescale off: the file's lengths are already in it.
            SelectedLensUnit = Pick(LensUnitChoices, ConventionNames.LensUnits, saved.LensUnit, SelectedLensUnit, "Lens unit", notes);
            SelectedSourceUnit = Pick(SourceUnitChoices, SourceUnits.Names, saved.SourceUnit, SelectedSourceUnit, "Source unit", notes);
            SelectedAnalysisAreaUnit = Pick(LensUnitChoices, ConventionNames.LensUnits, saved.AnalysisAreaUnit, SelectedAnalysisAreaUnit, "Analysis area unit", notes);
            SelectedMtfUnit = Pick(MtfUnitChoices, MtfUnits.Names, saved.MtfUnit, SelectedMtfUnit, "MTF unit", notes);
            SystemTitle = saved.Title ?? SystemTitle;
            SystemNotes = saved.Notes ?? SystemNotes;
            SelectedApertureType = Pick(ApertureTypeChoices, ApertureTypes.Names, saved.ApertureType, SelectedApertureType, "Aperture type", notes);
            ApertureValue = saved.ApertureValue;
            SelectedApodization = Pick(ApodizationChoices, ApodizationTypes.Names, saved.Apodization, SelectedApodization, "Apodization", notes);
            ApodizationFactor = saved.ApodizationFactor;
            SemiDiameterMargin = saved.SemiDiameterMargin;
            SemiDiameterMarginPercent = saved.SemiDiameterMarginPercent;
            TelecentricObjectSpace = saved.TelecentricObjectSpace;
            AfocalImageSpace = saved.AfocalImageSpace;
            SelectedFieldType = Pick(FieldTypeChoices, FieldTypes.Names, saved.FieldType, SelectedFieldType, "Field type", notes);
            SelectedFieldNormalization = Pick(FieldNormalizationChoices, ConventionNames.FieldNormalizations, saved.FieldNormalization,
                SelectedFieldNormalization, "Field normalization", notes);
            if (saved.Fields is { } fields)
                ReplaceFields(fields.Select(f => new OpticsFieldRow
                {
                    X = f.X, Y = f.Y, Weight = f.Weight, DecenterX = f.DecenterX, DecenterY = f.DecenterY,
                    CompressionX = f.CompressionX, CompressionY = f.CompressionY, TiltDegrees = f.TiltDegrees
                }));
            if (saved.Wavelengths is { } wavelengths)
            {
                Wavelengths.Clear();
                foreach (var w in wavelengths) Wavelengths.Add(new OpticsWavelengthRow { Micrometers = w.Micrometers, Weight = w.Weight });
            }
            PrimaryWavelength = saved.PrimaryWavelength;
            TemperatureCelsius = saved.TemperatureCelsius;
            PressureAtmospheres = saved.PressureAtmospheres;
            AdjustIndexToEnvironment = saved.AdjustIndexToEnvironment;
        }
        finally
        {
            _loadingSystem = false;
        }
        RaiseSystemCheck();
    }

    /// <summary>Writes the workspace's optical model to a lens file of its own.</summary>
    public void SaveLensFile(string path)
    {
        string name = string.IsNullOrWhiteSpace(SystemTitle) ? Path.GetFileNameWithoutExtension(path) : SystemTitle;
        new OpticsFileSerializer().Save(new OpticsLensFile { Name = name, Optics = ToSettings() }, path);
        LensFileStatus = $"Lens saved to {path}.";
    }

    /// <summary>Replaces the workspace's optical model with a lens file's. Returns the notes
    /// <see cref="LoadSettings"/> makes; a file that is not a lens throws, naming it.</summary>
    public IReadOnlyList<string> LoadLensFile(string path)
    {
        var lens = new OpticsFileSerializer().Load(path);
        var notes = LoadSettings(lens.Optics);
        LensFileStatus = $"Lens '{lens.Name}' opened from {path}." + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        return notes;
    }

    [RelayCommand]
    private void SaveLens()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = OpticsFileSerializer.FileFilter,
            FileName = (string.IsNullOrWhiteSpace(SystemTitle) ? "lens" : SystemTitle) + OpticsFileSerializer.FileExtension,
            Title = "Save the lens"
        };
        if (dialog.ShowDialog() != true) return;
        try { SaveLensFile(dialog.FileName); }
        catch (Exception ex) { LensFileStatus = $"Lens not saved: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenLens()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = OpticsFileSerializer.FileFilter, Title = "Open a lens" };
        if (dialog.ShowDialog() != true) return;
        try { LoadLensFile(dialog.FileName); }
        catch (Exception ex) { LensFileStatus = $"Lens not opened: {ex.Message}"; }
    }

    private static OpticsChoice<T> Pick<T>(IReadOnlyList<OpticsChoice<T>> choices, SavedNameTable<T> names, string? saved,
        OpticsChoice<T> current, string what, ICollection<string> notes) where T : struct, Enum
    {
        if (saved is null) return current;
        if (names.TryParse(saved, out var value)) return Choice(choices, value);
        notes.Add($"{what} '{saved}' is not one this version knows; kept '{current.Label}'.");
        return current;
    }
}
